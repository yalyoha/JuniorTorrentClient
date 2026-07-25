using System.Globalization;

namespace JTC.Services;

/// <summary>
/// Minimal append-only debug log to <c>%LocalAppData%\JTC\debug.log</c>.
/// Non-blocking (best-effort); never throws to callers.
/// </summary>
public static class DebugLog
{
    private static readonly object _lock = new();
    private static string LogPath => Path.Combine(AppPaths.Root, "debug.log");

    public static void Info(string message)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            var line = $"{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)}  {message}{Environment.NewLine}";
            lock (_lock)
            {
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 1_000_000)
                    File.Delete(LogPath); // simple rotation: nuke when > 1 MB
                File.AppendAllText(LogPath, line);
            }
        }
        catch
        {
            // Logging must never propagate errors.
        }
    }

    public static void Error(string message, Exception ex) =>
        Info($"ERROR: {message}: {ex.GetType().Name}: {ex.Message}");
}
