using System.Text.Json;
using System.Text.Json.Serialization;

namespace JTC.Services;

public sealed class StateStore
{
    private const string FileName = "torrents.json";
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _directory;
    // Serialises overlapping SaveAsync calls. Two concurrent writers used to race on the
    // shared "torrents.json.tmp" path (from OnManagerStateChanged bursts firing multiple
    // SaveStateAsync calls at once) → IOException and, worst case, a torn JSON left behind
    // after a mid-serialize crash. Instance-scoped: one StateStore per app process.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public StateStore(string directory)
    {
        _directory = directory;
    }

    private string FilePath => Path.Combine(_directory, FileName);

    public async Task<List<PersistedTorrent>> LoadAsync()
    {
        if (!File.Exists(FilePath))
            return [];

        try
        {
            await using var stream = File.OpenRead(FilePath);
            var result = await JsonSerializer.DeserializeAsync<List<PersistedTorrent>>(stream, Options);
            return result ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task SaveAsync(IReadOnlyList<PersistedTorrent> items)
    {
        Directory.CreateDirectory(_directory);
        await _writeLock.WaitAsync();
        try
        {
            // Unique per-write temp file. The shared "*.tmp" path caused overlapping
            // writers to clobber each other's in-flight streams; a fresh random name
            // per call means each writer owns its own temp until the atomic Move.
            var tmp = Path.Combine(_directory, Path.GetRandomFileName() + ".tmp");
            try
            {
                await using (var stream = File.Create(tmp))
                {
                    await JsonSerializer.SerializeAsync(stream, items, Options);
                }
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch
            {
                // Ensure we don't litter the directory with orphaned temps if the
                // serialize step blew up before Move. Swallow best-effort — deletion
                // failure of a temp file must not mask the real error.
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
