using System.Globalization;

namespace JTC.Services;

/// <summary>
/// Minimal append-only debug log to <c>%LocalAppData%\JTC\debug.log</c>.
/// Non-blocking (best-effort); never throws to callers.
///
/// Every Info/Error also forwards to <see cref="CloudLogSink"/> so all activity
/// from every installed client lands in one place on the VPS. LocalOnly is the
/// escape hatch the sink itself uses to report its own failures without looping.
/// </summary>
public static class DebugLog
{
    private static readonly object _lock = new();
    private static string LogPath => Path.Combine(AppPaths.Root, "debug.log");

    public static void Info(string message)
    {
        WriteLocal(message);
        CloudLogSink.Enqueue("info", message);
    }

    public static void Error(string message, Exception ex)
    {
        var text = $"ERROR: {message}: {ex.GetType().Name}: {ex.Message}";
        WriteLocal(text);
        CloudLogSink.Enqueue("error", text, new
        {
            source = message,
            exception = ex.GetType().FullName,
            ex_message = ex.Message,
            stack = ex.StackTrace,
        });
    }

    /// <summary>Local-only write. Used by CloudLogSink to log its own send failures
    /// without recursing back into itself.</summary>
    public static void LocalOnly(string message) => WriteLocal(message);

    private static void WriteLocal(string message)
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
}
