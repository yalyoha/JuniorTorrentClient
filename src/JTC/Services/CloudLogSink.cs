using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace JTC.Services;

/// <summary>
/// Ships log lines to jtc.alekseylosev.ru so activity from all installed clients
/// lands in one place for debugging. Best-effort: any HTTP / serialisation failure
/// is swallowed silently — the local <see cref="DebugLog"/> stays the source of
/// truth, cloud is a convenience layer.
///
/// Events are buffered in memory and flushed on a fixed interval (or when the
/// buffer hits a hard cap) so short bursts don't turn into one HTTP request per
/// line. The install-id is an anonymous GUID persisted in AppPaths.Root so the
/// server can group events per-machine without ever seeing a torrent name or
/// infohash.
/// </summary>
public static class CloudLogSink
{
    private const string IngestUrl   = "https://jtc.alekseylosev.ru/api/ingest";
    // Shared secret. Test-only server, so this lives in source. Match to the
    // value in /srv/jtc/backend/.env on the VPS (JTC_INGEST_TOKEN).
    private const string IngestToken = "jtc-test-2026-shared-token-change-if-you-care";
    private const int    MaxBuffered = 500;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly object _lock = new();
    private static readonly List<Event> _buffer = new(capacity: 64);
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };

    private static Timer? _timer;
    private static string _installId = "";
    private static string _version   = "";
    private static string _os        = "";
    private static bool   _started;
    private static bool   _enabled;

    public static void Start(bool enabled, string version)
    {
        lock (_lock)
        {
            _enabled = enabled;
            _version = version ?? "?";
            _os      = TryReadOsString();
            _installId = ResolveInstallId();
            if (_started) return;
            _started = true;
            _timer = new Timer(_ => FlushSafe(), null, FlushInterval, FlushInterval);
        }
        Enqueue("info", $"CloudLogSink started (enabled={enabled}, version={_version})");
    }

    public static void SetEnabled(bool enabled)
    {
        lock (_lock)
        {
            _enabled = enabled;
            if (!enabled) _buffer.Clear();
        }
    }

    public static void Enqueue(string kind, string message, object? data = null)
    {
        if (!_started || !_enabled) return;
        var ev = new Event
        {
            install_id = _installId,
            version    = _version,
            os         = _os,
            ts         = DateTime.UtcNow.ToString("o"),
            kind       = kind,
            message    = message,
            data       = data,
        };
        lock (_lock)
        {
            _buffer.Add(ev);
            // Hard cap: if the server is unreachable for a long time, drop the oldest
            // half so we keep recent context and don't grow unbounded in memory.
            if (_buffer.Count > MaxBuffered)
                _buffer.RemoveRange(0, _buffer.Count / 2);
        }
    }

    /// <summary>
    /// Flush without waiting for the timer. Called during graceful shutdown so the
    /// last few seconds of activity aren't lost. Bounded by the HttpClient timeout.
    /// </summary>
    public static void FlushNow() => FlushSafe();

    private static void FlushSafe()
    {
        Event[] toSend;
        lock (_lock)
        {
            if (_buffer.Count == 0) return;
            toSend = _buffer.ToArray();
            _buffer.Clear();
        }
        _ = SendAsync(toSend);
    }

    private static async Task SendAsync(Event[] events)
    {
        try
        {
            var sb = new StringBuilder(events.Length * 128);
            foreach (var ev in events)
            {
                sb.AppendLine(JsonSerializer.Serialize(ev, _json));
            }
            using var req = new HttpRequestMessage(HttpMethod.Post, IngestUrl)
            {
                Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/x-ndjson"),
            };
            req.Headers.TryAddWithoutValidation("X-JTC-Token", IngestToken);
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            // Discard body — success/failure is enough for the local log.
            if (!resp.IsSuccessStatusCode)
                DebugLog.LocalOnly($"CloudLogSink: server returned {(int)resp.StatusCode} for {events.Length} events");
        }
        catch (Exception ex)
        {
            DebugLog.LocalOnly($"CloudLogSink: send failed ({ex.GetType().Name}: {ex.Message})");
        }
    }

    private static string ResolveInstallId()
    {
        try
        {
            AppPaths.EnsureExists();
            var path = Path.Combine(AppPaths.Root, "install-id.txt");
            if (File.Exists(path))
            {
                var s = File.ReadAllText(path).Trim();
                if (s.Length > 0) return s;
            }
            var id = Guid.NewGuid().ToString("N");
            File.WriteAllText(path, id);
            return id;
        }
        catch
        {
            // Fall back to an ephemeral id — losing grouping across restarts is
            // better than crashing the sink init because the disk was momentarily
            // unavailable.
            return "ephemeral-" + Guid.NewGuid().ToString("N");
        }
    }

    private static string TryReadOsString()
    {
        try { return System.Runtime.InteropServices.RuntimeInformation.OSDescription; }
        catch { return "?"; }
    }

    private sealed class Event
    {
        public string install_id { get; set; } = "";
        public string version    { get; set; } = "";
        public string os         { get; set; } = "";
        public string ts         { get; set; } = "";
        public string kind       { get; set; } = "";
        public string message    { get; set; } = "";
        public object? data      { get; set; }
    }
}
