using System;
using Microsoft.UI.Xaml;
using JTC.Services;

namespace JTC;

public partial class App : Application
{
    public static TorrentService? Service { get; private set; }

    private MainWindow? _mainWindow;

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var cliArgs = Environment.GetCommandLineArgs();
        // Log BOTH activation sources so we can diagnose "double-click doesn't add" reports:
        //   Environment.GetCommandLineArgs() — the process's raw CLI (works in unpackaged
        //     WinUI 3 for shell-launched files, but not always for other activation kinds)
        //   LaunchActivatedEventArgs.Arguments — the WinUI activation payload (a single
        //     string; empty for a plain launch, populated for some activation kinds)
        // If GetCommandLineArgs is missing the file path, we still have Arguments as a
        // fallback. Prior to this change we only trusted GetCommandLineArgs.
        var winuiArgs = args?.Arguments ?? "";
        DebugLog.Info($"OnLaunched: cliArgs=[{string.Join(" | ", cliArgs)}]");
        DebugLog.Info($"OnLaunched: winui.Arguments='{winuiArgs}'");

        // If another TClient instance is already running, hand off any .torrent arg to it
        // via the inbox file, then bail out. The primary picks it up.
        if (SingleInstance.TryClaimOrHandOff(cliArgs))
        {
            Exit();
            return;
        }

        Service = new TorrentService();
        _mainWindow = new MainWindow(Service);
        _mainWindow.Activate();

        // Idempotent — writes to HKCU only if a value actually changed.
        FileAssociation.EnsureRegistered();

        // Restore persisted torrents first, then let subsequent activations land afterwards.
        await Service.LoadStateAsync();

        // Route any inbox file-drops (from secondary "Open with" launches) into the UI.
        // An empty drop means "primary is in the tray, please show yourself" — this covers
        // the case where the user re-launches JTC while it's minimized.
        var window = _mainWindow;
        SingleInstance.TorrentPathReceived += path =>
            window.DispatcherQueue.TryEnqueue(async () => await window.OpenTorrentPathAsync(path));
        SingleInstance.MagnetReceived += magnet =>
            window.DispatcherQueue.TryEnqueue(async () => await window.OpenMagnetAsync(magnet));
        SingleInstance.ShowWindowRequested += () =>
            window.DispatcherQueue.TryEnqueue(window.RestoreFromTray);
        // Installer signals shutdown via the inbox marker file so it can safely overwrite
        // JTC.exe and its DLLs. Hard-kill matches the tray "Выход" path — torrent state is
        // persisted after every add/remove/pause/resume, so nothing important is lost.
        SingleInstance.ShutdownRequested += () =>
        {
            DebugLog.Info("SingleInstance shutdown marker received — killing process");
            System.Diagnostics.Process.GetCurrentProcess().Kill();
        };
        SingleInstance.StartWatching();

        // Handle the launch argument this very process was given, if any — either a
        // .torrent file path (from Explorer / file association) or a magnet: URI (from
        // a browser via the URL scheme handler).
        //
        // Fall back to LaunchActivatedEventArgs.Arguments (winuiArgs) when
        // Environment.GetCommandLineArgs() doesn't reveal the file path — some activation
        // paths in WinUI 3 unpackaged put the argument there and leave GetCommandLineArgs
        // with just the exe path. We keep GetCommandLineArgs as first source because it's
        // reliably CommandLineToArgvW-tokenised (respects quoted paths with spaces);
        // winuiArgs is a raw single string that may need trimming and quote-stripping.
        var initial = SingleInstance.ExtractLaunchSource(cliArgs);
        if (initial is null && !string.IsNullOrWhiteSpace(winuiArgs))
        {
            var trimmed = winuiArgs.Trim().Trim('"');
            if (trimmed.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
                || (trimmed.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) && File.Exists(trimmed)))
            {
                initial = trimmed;
                DebugLog.Info($"OnLaunched: recovered initial source from winui.Arguments: '{initial}'");
            }
        }
        DebugLog.Info($"OnLaunched: initial source for primary = '{initial ?? "<none>"}'");
        if (initial is not null)
        {
            if (initial.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                _ = _mainWindow.OpenMagnetAsync(initial);
            else
                _ = _mainWindow.OpenTorrentPathAsync(initial);
        }
    }
}
