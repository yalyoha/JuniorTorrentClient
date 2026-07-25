using System.Diagnostics;

namespace JTC.Services;

/// <summary>
/// Cross-process single-instance guard for the current user session, with a file-based
/// "inbox" that lets subsequent launches hand off their .torrent arguments to the primary
/// instance and exit cleanly.
///
/// Usage in <c>App.OnLaunched</c>:
/// <code>
///   if (SingleInstance.TryClaimOrHandOff(args)) return; // we're a secondary, already handed off
///   // else we're primary — proceed with UI setup, later call StartWatching()
/// </code>
/// </summary>
public static class SingleInstance
{
    // Local\ prefix scopes the mutex to the current user session — no admin needed,
    // and doesn't collide with other Windows users on the same machine.
    private const string MutexName = @"Local\JTC-SingleInstance-yalyoha";

    private static Mutex? _mutex;
    private static FileSystemWatcher? _watcher;
    private static string InboxDir => Path.Combine(AppPaths.Root, "inbox");

    // Sentinel content the installer drops into the inbox before overwriting files,
    // asking the running instance to exit cleanly. The mutex it holds gets released as
    // a side-effect of process exit, which is what the installer's PrepareToInstall
    // hook actually waits on.
    public const string ShutdownMarker = "@shutdown";

    public static event Action<string>? TorrentPathReceived;
    public static event Action<string>? MagnetReceived;
    public static event Action? ShowWindowRequested;
    public static event Action? ShutdownRequested;

    /// <summary>
    /// Attempts to claim the primary-instance mutex. If another instance already holds it,
    /// writes the caller's CLI arguments into the inbox for the primary to pick up, then
    /// returns <c>true</c> — the caller should exit its process immediately.
    /// Returns <c>false</c> if we're the primary and should continue normal startup.
    /// </summary>
    public static bool TryClaimOrHandOff(string[] args)
    {
        var source = ExtractLaunchSource(args);
        DebugLog.Info($"SingleInstance: TryClaimOrHandOff cliArgs=[{string.Join(" | ", args)}] extractedSource='{source ?? "<null>"}'");

        _mutex = new Mutex(initiallyOwned: false, MutexName, out bool createdNew);
        if (createdNew)
        {
            // First instance — take ownership and stay running.
            try { _mutex.WaitOne(0); } catch { /* already owned, fine */ }
            DebugLog.Info("SingleInstance: primary — took mutex, staying");
            return false;
        }

        // Secondary instance — drop a note in the inbox and exit. Empty content means
        // "just bring the primary's window back from the tray"; a torrent path or a
        // magnet URI means "open this in the primary". The primary detects which by
        // checking the "magnet:" prefix.
        //
        // Atomic staging pattern: write to a .tmp file first (not matched by the primary's
        // FileSystemWatcher filter of *.txt), then rename to .txt. Without this, the naive
        // File.WriteAllText race allowed the primary's watcher to fire on the open-for-write
        // event while the file was still empty on disk → ReadAllText either returned empty
        // or threw (file locked), catch swallowed it, empty content dispatched as
        // ShowWindowRequested, and the torrent path was silently lost. Bug repro: double-click
        // a .torrent while JTC is running — reported "~20 % of clicks the dialog never appears".
        try
        {
            Directory.CreateDirectory(InboxDir);
            var stem = Guid.NewGuid().ToString("N");
            var stagingPath = Path.Combine(InboxDir, stem + ".tmp");
            var finalPath   = Path.Combine(InboxDir, stem + ".txt");
            File.WriteAllText(stagingPath, source ?? string.Empty);
            File.Move(stagingPath, finalPath);
            DebugLog.Info($"SingleInstance: secondary wrote {Path.GetFileName(finalPath)} content='{(source is null ? "<empty>" : (source.Length > 80 ? source.Substring(0,77) + "..." : source))}'");
        }
        catch (Exception ex)
        {
            // best-effort — user can just add manually
            DebugLog.Error("SingleInstance: secondary inbox write", ex);
        }
        _mutex.Dispose();
        _mutex = null;
        return true;
    }

    /// <summary>
    /// Called once on the primary instance after the main window is ready. Drains any
    /// pre-existing inbox files (silently discarding stale @shutdown markers), then
    /// watches for new drops from later "Open with" launches.
    /// </summary>
    public static void StartWatching()
    {
        Directory.CreateDirectory(InboxDir);
        DebugLog.Info($"SingleInstance: StartWatching at {InboxDir}");
        // Any @shutdown marker present at startup was left there by an installer that
        // already succeeded — the process it was meant to kill is dead (installer ran
        // taskkill /F as fallback in v0.4.2+). Acting on it here would make the fresh
        // JTC kill itself the moment it starts. Silently discard those; process all
        // other pre-existing files normally so a "double-click torrent while app was
        // starting" hand-off still works.
        DrainInbox(ignoreShutdownMarker: true);
        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(InboxDir, "*.txt")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true,
        };
        // Subsequent DrainInbox calls (from FileSystemWatcher events) DO act on
        // @shutdown — that's the intended path when an installer runs while the
        // primary instance is alive.
        //
        // Subscribe to BOTH Created and Renamed events — secondary instances now write
        // to <stem>.tmp then rename to <stem>.txt, and on Windows FileSystemWatcher fires
        // Renamed (not Created) when the destination filename appears via rename. Without
        // the Renamed handler, the primary would miss all handoffs from the atomic write path.
        _watcher.Created += (_, e) => { DebugLog.Info($"SingleInstance: watcher Created {e.Name}"); DrainInbox(); };
        _watcher.Renamed += (_, e) => { DebugLog.Info($"SingleInstance: watcher Renamed {e.OldName} → {e.Name}"); DrainInbox(); };
    }

    private static void DrainInbox(bool ignoreShutdownMarker = false)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(InboxDir, "*.txt"))
            {
                // Retry a few times if the file is still locked by the writer. The atomic
                // .tmp → .txt rename in TryClaimOrHandOff makes this rare now, but keep the
                // retry as defence for exotic timing. Critical: on failure DO NOT delete —
                // the next watcher event will retry. Previous code silently caught the
                // read exception, treated content as empty (→ ShowWindowRequested), and
                // deleted the file — that lost the torrent path on ~20 % of double-clicks.
                string? content = null;
                Exception? lastReadEx = null;
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    try { content = File.ReadAllText(file).Trim(); break; }
                    catch (Exception ex) { lastReadEx = ex; Thread.Sleep(50); }
                }
                if (content is null)
                {
                    DebugLog.Info($"inbox: {Path.GetFileName(file)} locked after 4 tries ({lastReadEx?.GetType().Name}) — leaving for next event");
                    continue; // do not delete; wait for next watcher tick
                }

                var preview = content.Length > 80 ? content.Substring(0, 77) + "..." : content;
                var kind =
                    string.Equals(content, ShutdownMarker, StringComparison.OrdinalIgnoreCase) ? "shutdown" :
                    string.IsNullOrEmpty(content)                                               ? "show-window" :
                    content.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)           ? "magnet" :
                                                                                                  "torrent-path";
                DebugLog.Info($"inbox: {Path.GetFileName(file)} → {kind} '{preview}' (ignoreShutdown={ignoreShutdownMarker})");

                if (string.Equals(content, ShutdownMarker, StringComparison.OrdinalIgnoreCase))
                {
                    if (!ignoreShutdownMarker)
                        ShutdownRequested?.Invoke();
                    // else: fall through to File.Delete below so we drop the stale marker.
                }
                else if (string.IsNullOrEmpty(content))
                    ShowWindowRequested?.Invoke();
                else if (content.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                    MagnetReceived?.Invoke(content);
                else
                    TorrentPathReceived?.Invoke(content);

                try { File.Delete(file); } catch { /* fine */ }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Error("SingleInstance.DrainInbox", ex);
        }
    }

    /// <summary>
    /// Extracts either a .torrent file path OR a magnet URI from the process's launch
    /// arguments — whichever comes first. Returns null if neither is present. Callers
    /// distinguish which they got by checking the "magnet:" prefix.
    /// </summary>
    public static string? ExtractLaunchSource(string[] args)
    {
        // args[0] is the exe path when launched via Environment.GetCommandLineArgs().
        for (int i = 1; i < args.Length; i++)
        {
            var a = args[i]?.Trim();
            if (string.IsNullOrWhiteSpace(a)) continue;
            if (a.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                return a;
            if (a.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) && File.Exists(a))
                return a;
        }
        return null;
    }
}
