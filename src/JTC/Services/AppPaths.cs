namespace JTC.Services;

public static class AppPaths
{
    private const string OldFolderName = "TClient"; // pre-rebrand
    private const string NewFolderName = "JTC";

    public static string Root { get; } = ResolveRoot();

    public static string CacheDir { get; } = Path.Combine(Root, "cache");

    public static void EnsureExists()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(CacheDir);
    }

    /// <summary>
    /// One-time migration: if the old %LocalAppData%\TClient folder exists and the new
    /// %LocalAppData%\JTC folder does not, move everything over so the user's torrents,
    /// settings and cache survive the rebrand. Falls back to just using the new name
    /// if the old folder is missing.
    /// </summary>
    private static string ResolveRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var newRoot = Path.Combine(localAppData, NewFolderName);
        var oldRoot = Path.Combine(localAppData, OldFolderName);

        if (!Directory.Exists(newRoot) && Directory.Exists(oldRoot))
        {
            try
            {
                Directory.Move(oldRoot, newRoot);
            }
            catch
            {
                // Filesystem couldn't rename atomically (across-volume, locked file, etc.) —
                // don't crash startup; just point at the old location so we don't lose data.
                if (!Directory.Exists(newRoot))
                    return oldRoot;
            }
        }
        return newRoot;
    }
}
