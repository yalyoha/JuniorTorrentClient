using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using MonoTorrent.Client;
using Windows.UI;
using JTC.Helpers;
using JTC.Services;
using JTC.ViewModels;

namespace JTC;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    private readonly TorrentService _service;

    // True while the window is hidden to the notification-area icon (X-close or explicit
    // Hide). Tracked explicitly instead of asking Win32 — AppWindow.Hide() clears
    // WS_VISIBLE, but that state can also be cleared by OS-level animation transitions,
    // and IsIconic vs IsWindowVisible give ambiguous answers when the window is animating
    // between visible/minimized/hidden. Our own flag has one owner (Hide / RestoreFromTray),
    // so there is no ambiguity.
    private bool _isHiddenToTray;

    // Update-check state. Every window Activation triggers a check (v0.6.1+ — the
    // previous 30-min throttle meant users had to restart the app to force a check
    // after a release landed). Re-entry is still blocked by _updateFlowInProgress so
    // rapid alt-tab while the update dialog is open doesn't stack calls. GitHub's
    // 60 req/hr unauthenticated limit is generous enough for realistic focus patterns
    // — worst case is a 429 the try/catch swallows.
    // _snoozedVersion records a version the user dismissed with "Позже" so we don't
    // re-prompt for the same release until they restart.
    private Version? _snoozedVersion;
    private bool _updateFlowInProgress;

    public MainWindow(TorrentService service)
    {
        _service = service;
        InitializeComponent();
        ViewModel = new MainViewModel(service, DispatcherQueue);

        // Paint the window background + set element theme from the user's saved choice.
        // For the Colored theme, restore all user-picked colours: gradient top/bottom,
        // plashka bg/fg, and every status colour (which affects both the 4 px stripe
        // and the 17 %-opacity row-fill tint).
        var initialSettings = SettingsStore.Load();
        var initialTheme    = initialSettings.Theme;
        var initialTop      = ThemeHelper.TryParseHex(initialSettings.ColoredTopHex);
        var initialBottom   = ThemeHelper.TryParseHex(initialSettings.ColoredBottomHex);
        var (initPlBg, initPlFg) = ThemeHelper.PlashkaColorsFrom(initialSettings);
        var initStatus      = ThemeHelper.StatusColorsFrom(initialSettings);
        ThemeHelper.SetStatusColors(initStatus.Idle, initStatus.Downloading, initStatus.Seeding, initStatus.Hashing, initStatus.Error);
        ThemeHelper.SetCornerRadii(initialSettings.ButtonCornerRadius, initialSettings.PlashkaCornerRadius);
        // Cache the status-indicator style BEFORE LoadStateAsync fires TorrentAdded —
        // TorrentViewModel's field initializer for _statusAsCircle reads
        // ThemeHelper.CurrentStatusIndicatorStyle, so without this call the first-run
        // rows always come up as Circle regardless of what settings.json says.
        ThemeHelper.SetStatusIndicatorStyle(initialSettings.StatusIndicatorStyle);
        ThemeHelper.Apply(RootGrid, initialTheme, initialTop, initialBottom, initPlBg, initPlFg);
        // Push button rounding to the six toolbar buttons up-front (row rounding is
        // picked up by each TorrentViewModel's PlashkaCornerRadius default reading
        // ThemeHelper.CurrentPlashkaCornerRadius set just above).
        ApplyButtonCornerRadius(initialSettings.ButtonCornerRadius);

        // Merge title bar into the client area so Acrylic reads through it.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ThemeHelper.ApplyToTitleBar(AppWindow.TitleBar, initialTheme);

        // Reasonable initial size.
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 640));

        // Prevent the user from shrinking the window past the point where the toolbar
        // buttons ("Добавить magnet") and header labels ("Прогресс") get squeezed into
        // a vertical character-per-line stack — see screenshots/img_22.png.
        InstallMinSizeSubclass();

        // Window icon (taskbar + Alt-Tab). The .exe icon is set separately via <ApplicationIcon> in csproj.
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "tclient.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = v is null
            ? ""
            : v.Revision > 0
                ? $"v{v.Major}.{v.Minor}.{v.Build}.{v.Revision}"
                : $"v{v.Major}.{v.Minor}.{v.Build}";

        // Torrent client stays alive when the user closes the window — engine keeps
        // seeding/leeching in the background. Real quit is only via the tray "Выход" item.
        AppWindow.Closing += OnAppWindowClosing;
        TrayIcon.LeftClickCommand = new RelayCommand(ToggleFromTray);

        // Auto-update check: fires whenever focus lands on the window (initial launch,
        // alt-tab back in, restore from tray). Throttled to once per 30 min per session
        // to stay well under GitHub's 60 req/hr unauthenticated rate limit.
        Activated += OnWindowActivated;

        // Wire tray context-flyout items. Use Command instead of Click event because
        // clicks on MenuFlyoutItems inside TaskbarIcon.ContextFlyout haven't been
        // firing in this H.NotifyIcon.WinUI version (verified via empty debug.log).
        TrayShowItem.Command = new RelayCommand(() =>
        {
            DebugLog.Info("Tray 'Показать' command fired");
            RestoreFromTray();
        });
        TrayExitItem.Command = new RelayCommand(() =>
        {
            DebugLog.Info("Tray 'Выход' command fired — killing process");
            System.Diagnostics.Process.GetCurrentProcess().Kill();
        });

        // Diagnostic: log whenever the flyout opens so we can tell if the XAML menu
        // is even showing, vs Windows drawing its own menu we can't intercept.
        if (TrayIcon.ContextFlyout is Microsoft.UI.Xaml.Controls.MenuFlyout mf)
        {
            mf.Opened += (_, _) => DebugLog.Info("Tray context flyout opened");
        }
    }

    private void ApplyButtonCornerRadius(int radius)
    {
        var cr = new CornerRadius(radius);
        OpenTorrentButton.CornerRadius = cr;
        AddMagnetButton.CornerRadius   = cr;
        ResumeButton.CornerRadius      = cr;
        PauseButton.CornerRadius       = cr;
        RemoveButton.CornerRadius      = cr;
        SettingsButton.CornerRadius    = cr;
    }

    private async void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        // Only react to activation, not deactivation.
        if (args.WindowActivationState == WindowActivationState.Deactivated) return;
        // Guard against re-entry — one update flow at a time. No time throttle: every
        // focus event triggers a check so a fresh release doesn't need an app restart
        // to notice (v0.6.1). See _updateFlowInProgress comment for rate-limit thinking.
        if (_updateFlowInProgress) return;

        try
        {
            var info = await UpdateService.CheckLatestVersionAsync();
            if (info is null) return;
            var current = UpdateService.CurrentVersion();
            if (current is null) return;
            if (!UpdateService.IsNewer(info.Version, current)) return;
            if (_snoozedVersion is not null && _snoozedVersion.Equals(info.Version)) return;

            _updateFlowInProgress = true;
            try
            {
                // Silent auto-update path (default in v0.5.4+): skip the "Обновить /
                // Позже" prompt, go straight to download-with-progress + silent install.
                // User only sees the download progress bar and the fresh JTC launching.
                if (SettingsStore.Load().AutoUpdateEnabled)
                    await DownloadAndRunInstallerAsync(info, silent: true);
                else
                    await ShowUpdatePromptAsync(info);
            }
            finally { _updateFlowInProgress = false; }
        }
        catch (Exception ex) { DebugLog.Error("OnWindowActivated update check", ex); }
    }

    private async Task ShowUpdatePromptAsync(UpdateInfo info)
    {
        var currentVerText = UpdateService.CurrentVersion()?.ToString(3) ?? "?";
        var headline = new TextBlock
        {
            Text = $"Обнаружена новая версия {info.TagName}.\nСейчас установлена v{currentVerText}.",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        var notesHeader = new TextBlock
        {
            Text = "Что изменилось:",
            Margin = new Thickness(0, 12, 0, 4),
            Opacity = 0.8,
        };
        var notesBlock = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(info.ReleaseNotes)
                ? "(автор релиза не оставил описания)"
                : info.ReleaseNotes,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        var notesScroll = new Microsoft.UI.Xaml.Controls.ScrollViewer
        {
            Content = notesBlock,
            MaxHeight = 320,
            VerticalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Disabled,
        };
        var stack = new Microsoft.UI.Xaml.Controls.StackPanel { Spacing = 0, MinWidth = 460, MaxWidth = 640 };
        stack.Children.Add(headline);
        stack.Children.Add(notesHeader);
        stack.Children.Add(notesScroll);

        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = "Доступно обновление",
            Content = stack,
            PrimaryButtonText = "Обновить",
            CloseButtonText = "Позже",
            DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        // Give the notes area room to breathe on wide releases like v0.4.0.
        dialog.Resources["ContentDialogMaxWidth"] = 720.0;
        ThemeHelper.ApplyToDialog(dialog, ThemeHelper.CurrentTheme);
        Microsoft.UI.Xaml.Controls.ContentDialogResult res;
        try { res = await dialog.ShowAsync(); }
        catch (Exception ex)
        {
            // Another dialog is already open (settings, delete-confirm, etc.). Swallow
            // and try again on the next Activated tick — no need to snooze the version.
            DebugLog.Error("ShowUpdatePromptAsync", ex);
            return;
        }
        if (res != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
        {
            _snoozedVersion = info.Version;
            return;
        }

        await DownloadAndRunInstallerAsync(info, silent: false);
    }

    private async Task DownloadAndRunInstallerAsync(UpdateInfo info, bool silent)
    {
        var status = new TextBlock { Text = "Скачивание установщика…" };
        var bar = new Microsoft.UI.Xaml.Controls.ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Width = 380,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var content = new Microsoft.UI.Xaml.Controls.StackPanel { Spacing = 4, MinWidth = 400 };
        content.Children.Add(status);
        content.Children.Add(bar);

        var progressDialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = $"Обновление до {info.TagName}",
            Content = content,
            // No Cancel button — we don't have a cancellation token wired into the download.
            // Closing the dialog wouldn't actually stop the network transfer, so hiding the
            // option is more honest than showing one that lies.
            XamlRoot = Content.XamlRoot,
        };
        ThemeHelper.ApplyToDialog(progressDialog, ThemeHelper.CurrentTheme);

        var showTask = progressDialog.ShowAsync();

        string? path = null;
        try
        {
            var progress = new Progress<double>(pct =>
            {
                var v = pct * 100;
                bar.Value = v;
                status.Text = $"Скачивание установщика… {v:F0}%";
            });
            path = await UpdateService.DownloadInstallerAsync(info, progress);
        }
        catch (Exception ex) { DebugLog.Error("DownloadAndRunInstallerAsync", ex); }

        progressDialog.Hide();
        try { await showTask; } catch { }

        if (path is null)
        {
            await ShowErrorAsync("Не удалось скачать обновление",
                $"Проверьте подключение к сети и попробуйте снова.\n\nПрямая ссылка: {info.HtmlUrl}");
            return;
        }
        // Hand off to the installer, then die so it can freely overwrite our files.
        // Silent path uses /VERYSILENT and the installer's [Run] step relaunches JTC
        // once the copy is done (skipifsilent was dropped in v0.5.4).
        if (silent)
            UpdateService.LaunchInstallerSilentAndExit(path);
        else
            UpdateService.LaunchInstallerAndExit(path);
    }

    private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender,
                                    Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        // Real quit is only via the tray "Выход" (Process.Kill) — X-close always hides
        // to tray. No _reallyExiting bypass: we don't want an accidental Close() call to
        // shut the engine down mid-download.
        args.Cancel = true;
        AppWindow.Hide();
        _isHiddenToTray = true;
        DebugLog.Info("Window closing → hidden to tray");
    }

    public void RestoreFromTray()
    {
        AppWindow.Show();
        AppWindow.MoveInZOrderAtTop();
        Activate();
        _isHiddenToTray = false;
        DebugLog.Info("Window restored from tray");
    }

    /// <summary>
    /// Tray left-click toggle: hide the window if it's currently shown, restore it if
    /// hidden. Uses our own tracked flag (see _isHiddenToTray comment) rather than
    /// Win32 IsWindowVisible so the state is unambiguous during animation transitions.
    /// A minimized window (user pressed the minimize caption button) counts as "shown"
    /// and click will hide it — matches how Discord / Telegram tray icons behave.
    /// </summary>
    public void ToggleFromTray()
    {
        DebugLog.Info($"Tray left-click fired; hidden={_isHiddenToTray}");
        if (_isHiddenToTray)
        {
            RestoreFromTray();
        }
        else
        {
            AppWindow.Hide();
            _isHiddenToTray = true;
            DebugLog.Info("  → hidden to tray");
        }
    }

    private void TrayShow_Click(object sender, RoutedEventArgs e) => RestoreFromTray();

    private void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        // Kill immediately, no cleanup, no await, nothing that could throw or block
        // before we reach TerminateProcess. Torrent state is persisted after every
        // add/remove/pause/resume, so a hard-kill loses nothing important. Log first
        // so we can confirm the handler even fires (previous versions with graceful
        // shutdown appeared to never reach the exit call at all).
        DebugLog.Info("TrayExit_Click fired — killing process");
        System.Diagnostics.Process.GetCurrentProcess().Kill();
    }

    private async void OpenTorrentButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".torrent");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        await OpenTorrentPathAsync(file.Path);
    }

    /// <summary>
    /// Adds a torrent from a known file path (used by file-association activation from Windows
    /// Explorer). Reuses the same folder-picking logic as the manual button.
    /// </summary>
    public async Task OpenTorrentPathAsync(string torrentPath)
    {
        DebugLog.Info($"OpenTorrentPathAsync ENTER path='{torrentPath}' exists={System.IO.File.Exists(torrentPath)}");
        var downloadDir = await GetOrPickDownloadDirAsync();
        if (downloadDir is null) { DebugLog.Info("OpenTorrentPathAsync: user cancelled download dir picker"); return; }

        // For multi-file torrents (typical case: a TV season / album), let the user pick
        // which files to actually download. Parse the .torrent locally first — this doesn't
        // engage the engine and won't create a manager, so a Cancel here leaves no state
        // behind. Single-file torrents skip the dialog entirely (nothing to choose).
        IReadOnlySet<int>? skipIndices = null;
        try
        {
            var parsed = await MonoTorrent.Torrent.LoadAsync(torrentPath);
            if (parsed.Files.Count > 1)
            {
                var entries = parsed.Files
                    .Select((f, i) => new FileSelectionDialog.Entry(i, f.Path, f.Length))
                    .ToList();
                var selection = await FileSelectionDialog.ShowAsync(Content.XamlRoot, parsed.Name, entries);
                if (selection is null)
                    return; // user cancelled
                skipIndices = selection;
            }
        }
        catch (Exception ex)
        {
            // Parse failure is an add failure too — surface the same error.
            await ShowErrorAsync("Не удалось прочитать .torrent", ex.Message);
            return;
        }

        try
        {
            await _service.AddTorrentFileAsync(torrentPath, downloadDir, startImmediately: true, skipIndices);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Не удалось открыть торрент", ex.Message);
        }
    }

    /// <summary>
    /// Adds a magnet URI (used by URL-scheme activation from browsers — "magnet:..." link
    /// in a webpage). Reuses the same folder-picking logic as the manual button.
    /// </summary>
    public async Task OpenMagnetAsync(string magnetUri)
    {
        var downloadDir = await GetOrPickDownloadDirAsync();
        if (downloadDir is null) return;

        try
        {
            await _service.AddMagnetAsync(magnetUri, downloadDir, startImmediately: true);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Не удалось добавить magnet", ex.Message);
        }
    }

    private async void AddMagnetButton_Click(object sender, RoutedEventArgs e)
    {
        var input = new Microsoft.UI.Xaml.Controls.TextBox
        {
            PlaceholderText = "magnet:?xt=urn:btih:...",
            AcceptsReturn = false,
        };
        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = "Добавить magnet-ссылку",
            Content = input,
            PrimaryButtonText = "Добавить",
            CloseButtonText = "Отмена",
            DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        ThemeHelper.ApplyToDialog(dialog, ThemeHelper.CurrentTheme);
        var result = await dialog.ShowAsync();
        if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
            return;

        var magnet = input.Text?.Trim();
        if (string.IsNullOrEmpty(magnet) || !magnet.StartsWith("magnet:"))
        {
            await ShowErrorAsync("Неверная magnet-ссылка", "Текст должен начинаться с \"magnet:\".");
            return;
        }

        var downloadDir = await GetOrPickDownloadDirAsync();
        if (downloadDir is null) return;

        try
        {
            await _service.AddMagnetAsync(magnet, downloadDir, startImmediately: true);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Не удалось добавить magnet", ex.Message);
        }
    }

    private async Task<Windows.Storage.StorageFolder?> PickDownloadFolderAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return await picker.PickSingleFolderAsync();
    }

    /// <summary>
    /// Returns the saved download directory if set and still exists on disk; otherwise prompts the user
    /// for a folder and saves that choice in settings.json for next time.
    /// </summary>
    private async Task<string?> GetOrPickDownloadDirAsync()
    {
        var settings = SettingsStore.Load();
        if (!string.IsNullOrEmpty(settings.LastDownloadDir) && Directory.Exists(settings.LastDownloadDir))
            return settings.LastDownloadDir;

        var folder = await PickDownloadFolderAsync();
        if (folder is null) return null;

        SettingsStore.Save(settings with { LastDownloadDir = folder.Path });
        return folder.Path;
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = Content.XamlRoot,
        };
        ThemeHelper.ApplyToDialog(dialog, ThemeHelper.CurrentTheme);
        await dialog.ShowAsync();
    }

    /// <summary>
    /// Click on empty space inside the ListView (below the last row, header area, scrollbar
    /// margin) clears selection. WinUI's Extended selection mode has no built-in equivalent —
    /// once you click a row you can only deselect via Ctrl-click on that same row, which most
    /// users don't discover.
    /// </summary>
    private void TorrentList_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        var node = e.OriginalSource as DependencyObject;
        while (node is not null)
        {
            if (node is Microsoft.UI.Xaml.Controls.ListViewItem)
                return; // tap landed on a row — let normal selection logic run
            node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
        }
        TorrentList.SelectedItems.Clear();
    }

    /// <summary>
    /// Multi-select support: update every VM's IsSelected flag so the row-background brush
    /// reflects the full selection set, not just the primary SelectedItem.
    /// </summary>
    private void TorrentList_SelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        var selected = new HashSet<JTC.ViewModels.TorrentViewModel>(
            TorrentList.SelectedItems.Cast<JTC.ViewModels.TorrentViewModel>());
        foreach (var vm in ViewModel.Torrents)
            vm.IsSelected = selected.Contains(vm);
    }

    private List<JTC.ViewModels.TorrentViewModel> SelectedVMs() =>
        TorrentList.SelectedItems.Cast<JTC.ViewModels.TorrentViewModel>().ToList();

    private async void ResumeButton_Click(object sender, RoutedEventArgs e)
    {
        var vms = SelectedVMs();
        if (vms.Count == 0) return;
        foreach (var vm in vms)
        {
            try { await _service.ResumeAsync(vm.Manager); }
            catch (Exception ex) { await ShowErrorAsync("Не удалось возобновить", ex.Message); break; }
        }
    }

    private async void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        var vms = SelectedVMs();
        if (vms.Count == 0) return;
        foreach (var vm in vms)
        {
            try { await _service.PauseAsync(vm.Manager); }
            catch (Exception ex) { await ShowErrorAsync("Не удалось приостановить", ex.Message); break; }
        }
    }

    private async void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        var vms = SelectedVMs();
        if (vms.Count == 0)
        {
            await ShowErrorAsync("Ничего не выбрано", "Сначала выделите торрент в списке.");
            return;
        }
        await DeleteWithConfirmationAsync(vms);
    }

    /// <summary>
    /// Shared delete flow — used by the toolbar (multi-select) and the row context menu
    /// (single row). Shows the "delete files / keep files / cancel" dialog, then optimistically
    /// removes the rows from the UI and lets the service work through them serially.
    /// </summary>
    private async Task DeleteWithConfirmationAsync(List<TorrentViewModel> vms)
    {
        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = vms.Count == 1 ? "Удалить торрент" : $"Удалить торренты ({vms.Count})",
            Content = vms.Count == 1
                ? $"Также удалить скачанные файлы «{vms[0].Name}»?"
                : $"Также удалить скачанные файлы для {vms.Count} выбранных торрентов?",
            PrimaryButtonText = "Удалить файлы",
            SecondaryButtonText = "Оставить файлы",
            CloseButtonText = "Отмена",
            DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Secondary,
            XamlRoot = Content.XamlRoot,
        };
        ThemeHelper.ApplyToDialog(dialog, ThemeHelper.CurrentTheme);
        var result = await dialog.ShowAsync();
        if (result == Microsoft.UI.Xaml.Controls.ContentDialogResult.None)
            return; // Отмена

        var deleteFiles = result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary;

        // Optimistic UI: pull every row out of the list right away, then let the service
        // work through them sequentially in the background (RemoveAsync is serialized by
        // TorrentService's internal semaphore).
        var toRemove = vms.Select(v => (vm: v, manager: v.Manager)).ToList();
        foreach (var (v, _) in toRemove)
        {
            v.Detach();
            ViewModel.Torrents.Remove(v);
        }
        ViewModel.SelectedTorrent = null;

        foreach (var (_, manager) in toRemove)
        {
            try { await _service.RemoveAsync(manager, deleteFiles); }
            catch { /* row already gone; engine failure is not user-actionable */ }
        }
    }

    /// <summary>
    /// Double-click a row → reveal the torrent's file or container folder in Explorer.
    /// </summary>
    private void TorrentRow_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is TorrentViewModel vm)
            RevealInExplorer(vm.Manager);
    }

    private void RowOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is TorrentViewModel vm)
            RevealInExplorer(vm.Manager);
    }

    private async void RowRecheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not TorrentViewModel vm)
            return;
        try
        {
            await _service.RecheckAsync(vm.Manager);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Не удалось обновить", ex.Message);
        }
    }

    private async void RowDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not TorrentViewModel vm)
            return;
        await DeleteWithConfirmationAsync(new List<TorrentViewModel> { vm });
    }

    /// <summary>
    /// Opens the download folder in Windows Explorer with the torrent's file (single-
    /// file torrent) or its container folder (multi-file torrent) highlighted. Uses
    /// <c>explorer.exe /select,</c> so the user doesn't have to visually scan among
    /// unrelated files. Falls back to just opening the download folder when metadata
    /// hasn't arrived yet (magnet still resolving).
    /// </summary>
    private void RevealInExplorer(TorrentManager manager)
    {
        string? target = null;
        try
        {
            if (manager.Files is { Count: 1 } files && !string.IsNullOrEmpty(files[0].FullPath))
            {
                target = files[0].FullPath;
            }
            else if (manager.Torrent is not null && !string.IsNullOrEmpty(manager.Torrent.Name))
            {
                target = Path.Combine(manager.SavePath, manager.Torrent.Name);
            }
        }
        catch { /* fall through to fallback */ }

        if (string.IsNullOrEmpty(target))
        {
            if (Directory.Exists(manager.SavePath))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = manager.SavePath,
                        UseShellExecute = true,
                    });
                }
                catch { /* best-effort */ }
            }
            return;
        }

        // /select, opens Explorer at the parent folder with the given item selected.
        // Quote the path so spaces work; commas are impossible inside a Windows path.
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{target}\"",
                UseShellExecute = true,
            });
        }
        catch { /* best-effort */ }
    }

    // --- Minimum window size (WM_GETMINMAXINFO subclass) ---------------------------
    // WinUI 3 / WindowsAppSDK 2.2 has no AppWindow.MinSize; enforce it via a Comctl32
    // subclass that clamps ptMinTrackSize on every WM_GETMINMAXINFO. Values are in
    // DIPs and scaled by the window's current DPI so the limit tracks per-monitor DPI
    // changes automatically (WM_GETMINMAXINFO fires again after WM_DPICHANGED).
    private const int MinWindowWidthDip  = 780;
    private const int MinWindowHeightDip = 360;

    private const uint WM_GETMINMAXINFO = 0x0024;

    private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, IntPtr dwRefData);

    [DllImport("Comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, uint uIdSubclass, IntPtr dwRefData);

    [DllImport("Comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("User32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    // Held as a field so the GC doesn't collect the delegate while native code still
    // holds the function pointer via SetWindowSubclass.
    private SUBCLASSPROC? _subclassProc;

    private void InstallMinSizeSubclass()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _subclassProc = MinSizeWndProc;
        SetWindowSubclass(hwnd, _subclassProc, 0, IntPtr.Zero);
    }

    private IntPtr MinSizeWndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == WM_GETMINMAXINFO)
        {
            var scale = GetDpiForWindow(hWnd) / 96.0;
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            mmi.ptMinTrackSize.x = (int)Math.Ceiling(MinWindowWidthDip  * scale);
            mmi.ptMinTrackSize.y = (int)Math.Ceiling(MinWindowHeightDip * scale);
            Marshal.StructureToPtr(mmi, lParam, false);
            return IntPtr.Zero;
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var current = SettingsStore.Load();

        // Snapshot the live state so we can revert on Cancel — the color-picker flyouts
        // apply live-preview to the window and title bar as the user drags, so we need
        // a way back to "exactly what was on screen before the dialog opened".
        var snapshotTheme       = ThemeHelper.CurrentTheme;
        var snapshotTop         = ThemeHelper.CurrentTop;
        var snapshotBottom      = ThemeHelper.CurrentBottom;
        var snapshotPlashkaBg   = ThemeHelper.CurrentPlashkaBg;
        var snapshotPlashkaFg   = ThemeHelper.CurrentPlashkaFg;
        var snapshotStatusIdle        = RowBrushes.StatusIdle;
        var snapshotStatusDownloading = RowBrushes.StatusDownloading;
        var snapshotStatusSeeding     = RowBrushes.StatusSeeding;
        var snapshotStatusHashing     = RowBrushes.StatusHashing;
        var snapshotStatusError       = RowBrushes.StatusError;
        var snapshotBtnRadius       = ThemeHelper.CurrentButtonCornerRadius;
        var snapshotPlashkaRadius   = ThemeHelper.CurrentPlashkaCornerRadius;
        var snapshotIndicatorStyle  = ThemeHelper.CurrentStatusIndicatorStyle;

        // Forward-declared so ApplyLiveTheme can close over the dialog reference — the
        // dialog itself is constructed further down (needs all the child controls first).
        Microsoft.UI.Xaml.Controls.ContentDialog? dialog = null;
        // Tracks which theme the dialog is currently styled with, so ApplyLiveTheme can
        // pick the fast RepaintColoredDialog path (colour drag inside Colored) vs the
        // full ApplyToDialog reset path (any theme switch).
        var lastDialogTheme = current.Theme;

        // Current working colors — start from settings, fall back to the first built-in
        // preset if the stored hex is unparseable or missing (fresh install).
        var workingTop = ThemeHelper.TryParseHex(current.ColoredTopHex)
                         ?? ThemeHelper.TryParseHex(BuiltInColorPresets.PinkTopHex)!.Value;
        var workingBottom = ThemeHelper.TryParseHex(current.ColoredBottomHex)
                            ?? ThemeHelper.TryParseHex(BuiltInColorPresets.PinkBottomHex)!.Value;
        // Plashka bg/fg and status colours — start from settings (with legacy fallback via
        // ThemeHelper.PlashkaColorsFrom / StatusColorsFrom), applied live as user tweaks.
        var (workingPlashkaBg, workingPlashkaFg) = ThemeHelper.PlashkaColorsFrom(current);
        var (workingStatusIdle, workingStatusDownloading, workingStatusSeeding,
             workingStatusHashing, workingStatusError) = ThemeHelper.StatusColorsFrom(current);
        var workingButtonRadius    = current.ButtonCornerRadius;
        var workingPlashkaRadius   = current.PlashkaCornerRadius;
        var workingIndicatorStyle  = current.StatusIndicatorStyle;

        // ---- LEFT column: existing settings ---------------------------------------
        var pathBox = new Microsoft.UI.Xaml.Controls.TextBox
        {
            Header = "Папка загрузок",
            Text = current.LastDownloadDir ?? "",
            IsReadOnly = true,
            MinWidth = 220,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        var browseBtn = new Microsoft.UI.Xaml.Controls.Button
        {
            Content = "Обзор…",
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(8, 0, 0, 0),
        };
        browseBtn.Click += async (_, _) =>
        {
            var folder = await PickDownloadFolderAsync();
            if (folder is not null) pathBox.Text = folder.Path;
        };
        var pathRow = new Microsoft.UI.Xaml.Controls.Grid();
        pathRow.ColumnDefinitions.Add(new Microsoft.UI.Xaml.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathRow.ColumnDefinitions.Add(new Microsoft.UI.Xaml.Controls.ColumnDefinition { Width = GridLength.Auto });
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(pathBox, 0);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(browseBtn, 1);
        pathRow.Children.Add(pathBox);
        pathRow.Children.Add(browseBtn);

        var maxBox = new Microsoft.UI.Xaml.Controls.NumberBox
        {
            Header = "Одновременных закачек",
            Value = current.MaxSimultaneousDownloads,
            Minimum = 1,
            Maximum = 20,
            SmallChange = 1,
            LargeChange = 5,
            SpinButtonPlacementMode = Microsoft.UI.Xaml.Controls.NumberBoxSpinButtonPlacementMode.Inline,
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        // Theme picker — indices match themeItems below.
        var themeItems = new[]
        {
            (Label: "Цветная", Theme: AppTheme.Colored),
            (Label: "Чёрная",  Theme: AppTheme.Dark),
            (Label: "Белая",   Theme: AppTheme.Light),
        };
        var themeBox = new Microsoft.UI.Xaml.Controls.ComboBox
        {
            Header = "Тема",
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        foreach (var t in themeItems) themeBox.Items.Add(t.Label);
        themeBox.SelectedIndex = System.Array.FindIndex(themeItems, t => t.Theme == current.Theme);
        if (themeBox.SelectedIndex < 0) themeBox.SelectedIndex = 0;

        // All non-path controls live in one vertical stack under the full-width path row.
        // v0.5 collapsed the earlier two-column layout — with plashka bg/fg + five status
        // colour swatches added, everything reads more clearly stacked top-to-bottom, and
        // ContentScrollViewer (see ThemeHelper.ApplyToDialog) handles overflow.

        // ---- RIGHT column: color pickers + presets (only when Colored) ------------
        // Working copy of the preset list so Save-as-preset / Delete stay local until
        // the user hits Сохранить at the dialog level.
        var workingPresets = new List<ColorPreset>(current.CustomPresets);

        var presetBox = new Microsoft.UI.Xaml.Controls.ComboBox
        {
            Header = "Пресет",
            Width = 220,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        void RebuildPresetItems(int? selectIndex = null)
        {
            var selectedName = presetBox.SelectedItem as string;
            presetBox.Items.Clear();
            foreach (var p in BuiltInColorPresets.All) presetBox.Items.Add(p.Name);
            foreach (var p in workingPresets)          presetBox.Items.Add(p.Name);
            if (selectIndex is int idx && idx >= 0 && idx < presetBox.Items.Count)
                presetBox.SelectedIndex = idx;
            else if (selectedName is not null)
            {
                for (int i = 0; i < presetBox.Items.Count; i++)
                    if ((string)presetBox.Items[i] == selectedName) { presetBox.SelectedIndex = i; break; }
            }
        }

        ColorPreset? PresetAt(int i)
        {
            if (i < 0) return null;
            if (i < BuiltInColorPresets.All.Count) return BuiltInColorPresets.All[i];
            var j = i - BuiltInColorPresets.All.Count;
            return (j >= 0 && j < workingPresets.Count) ? workingPresets[j] : null;
        }

        bool IsBuiltInIndex(int i) => i >= 0 && i < BuiltInColorPresets.All.Count;

        int FindPresetIndexMatchingColors(Color top, Color bottom)
        {
            var topHex    = ThemeHelper.ToHex(top);
            var bottomHex = ThemeHelper.ToHex(bottom);
            for (int i = 0; i < BuiltInColorPresets.All.Count; i++)
            {
                var p = BuiltInColorPresets.All[i];
                if (string.Equals(p.TopHex, topHex, System.StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(p.BottomHex, bottomHex, System.StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            for (int j = 0; j < workingPresets.Count; j++)
            {
                var p = workingPresets[j];
                if (string.Equals(p.TopHex, topHex, System.StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(p.BottomHex, bottomHex, System.StringComparison.OrdinalIgnoreCase))
                    return BuiltInColorPresets.All.Count + j;
            }
            return -1;
        }

        // Color swatches — small colored rectangles that pop a WinUI ColorPicker on click.
        // Kept as Border rather than Button because Button carries the default WinUI hover
        // overlay (a translucent grey lift) which reads as visual noise on a pure-colour
        // sample — the swatch should look identical whether the pointer is over it or not.
        Border MakeSwatchBorder(Color initial)
        {
            return new Border
            {
                Width = 48,
                Height = 28,
                Background = new SolidColorBrush(initial),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
            };
        }

        var topSwatch    = MakeSwatchBorder(workingTop);
        var bottomSwatch = MakeSwatchBorder(workingBottom);

        Microsoft.UI.Xaml.Controls.Flyout MakeColorFlyout(Color initial, System.Action<Color> onChanged)
        {
            var picker = new Microsoft.UI.Xaml.Controls.ColorPicker
            {
                Color = initial,
                IsAlphaEnabled = false,
                IsAlphaSliderVisible = false,
                IsAlphaTextInputVisible = false,
                IsHexInputVisible = true,
                IsColorChannelTextInputVisible = false,
                IsMoreButtonVisible = false,
                IsColorPreviewVisible = true,
                IsColorSliderVisible = true,
                ColorSpectrumShape = Microsoft.UI.Xaml.Controls.ColorSpectrumShape.Box,
            };
            picker.ColorChanged += (_, args) => onChanged(args.NewColor);
            return new Microsoft.UI.Xaml.Controls.Flyout
            {
                Content = picker,
                Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom,
            };
        }

        void ApplyLiveTheme()
        {
            var themeIdx = System.Math.Max(0, themeBox.SelectedIndex);
            var theme = themeItems[themeIdx].Theme;
            // Push status colours first so row VMs pick them up in the following Refresh loop.
            ThemeHelper.SetStatusColors(workingStatusIdle, workingStatusDownloading,
                workingStatusSeeding, workingStatusHashing, workingStatusError);
            ThemeHelper.SetCornerRadii(workingButtonRadius, workingPlashkaRadius);
            ThemeHelper.SetStatusIndicatorStyle(workingIndicatorStyle);
            ThemeHelper.Apply(RootGrid, theme, workingTop, workingBottom, workingPlashkaBg, workingPlashkaFg);
            ThemeHelper.ApplyToTitleBar(AppWindow.TitleBar, theme);
            ApplyButtonCornerRadius(workingButtonRadius);
            var newPlashkaCorner = new Microsoft.UI.Xaml.CornerRadius(workingPlashkaRadius);
            var newAsCircle = workingIndicatorStyle == StatusIndicatorStyle.Circle;
            foreach (var vm in ViewModel.Torrents)
            {
                vm.PlashkaCornerRadius = newPlashkaCorner;
                vm.StatusAsCircle = newAsCircle;
                vm.RefreshBrushes();
                vm.Refresh();
            }
            if (dialog is not null)
            {
                // Colour-only tweak (theme stayed Colored, only top/bottom hex shifted)
                // uses the fast RepaintColoredDialog path so ColorPicker drag frames
                // don't re-set 50+ resource keys per frame. Any theme *switch* goes
                // through ApplyToDialog which clears prior Colored-scope overrides
                // and applies the new theme's defaults — otherwise switching
                // Colored → Dark left the dialog with a stale gradient background
                // (screenshots/img_25 / img_26).
                if (theme == AppTheme.Colored && lastDialogTheme == AppTheme.Colored)
                    ThemeHelper.RepaintColoredDialog(dialog, workingTop, workingBottom);
                else
                    ThemeHelper.ApplyToDialog(dialog, theme);
                lastDialogTheme = theme;
            }
        }

        var topFlyout = MakeColorFlyout(workingTop, c =>
        {
            workingTop = c;
            topSwatch.Background = new SolidColorBrush(c);
            // Manual color pick means the preset selection may no longer match.
            var match = FindPresetIndexMatchingColors(workingTop, workingBottom);
            presetBox.SelectedIndex = match;
            ApplyLiveTheme();
        });
        FlyoutBase.SetAttachedFlyout(topSwatch, topFlyout);
        topSwatch.Tapped += (_, _) => FlyoutBase.ShowAttachedFlyout(topSwatch);

        var bottomFlyout = MakeColorFlyout(workingBottom, c =>
        {
            workingBottom = c;
            bottomSwatch.Background = new SolidColorBrush(c);
            var match = FindPresetIndexMatchingColors(workingTop, workingBottom);
            presetBox.SelectedIndex = match;
            ApplyLiveTheme();
        });
        FlyoutBase.SetAttachedFlyout(bottomSwatch, bottomFlyout);
        bottomSwatch.Tapped += (_, _) => FlyoutBase.ShowAttachedFlyout(bottomSwatch);

        // Rows for the two swatches — [Label] [Swatch]. Kept as narrow grids so both
        // rows align visually.
        Microsoft.UI.Xaml.Controls.Grid MakeSwatchRow(string label, FrameworkElement swatch)
        {
            var g = new Microsoft.UI.Xaml.Controls.Grid { ColumnSpacing = 12 };
            g.ColumnDefinitions.Add(new Microsoft.UI.Xaml.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new Microsoft.UI.Xaml.Controls.ColumnDefinition { Width = GridLength.Auto });
            var tb = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(tb, 0);
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(swatch, 1);
            g.Children.Add(tb);
            g.Children.Add(swatch);
            return g;
        }

        var savePresetBtn   = new Microsoft.UI.Xaml.Controls.Button { Content = "Сохранить пресет…" };
        var editPresetBtn   = new Microsoft.UI.Xaml.Controls.Button { Content = "Изменить пресет…" };
        var deletePresetBtn = new Microsoft.UI.Xaml.Controls.Button { Content = "Удалить пресет" };
        var presetActionsRow = new Microsoft.UI.Xaml.Controls.StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        presetActionsRow.Children.Add(savePresetBtn);
        presetActionsRow.Children.Add(editPresetBtn);
        presetActionsRow.Children.Add(deletePresetBtn);

        // Preset chosen most recently in the ComboBox that is USER-owned (not built-in).
        // Persists across colour-picker changes so a user can select "Закат", tweak the
        // colours, and still hit "Изменить пресет…" to save the tweaked colours back to
        // Закат — even though changing the swatches deselected the ComboBox to -1.
        ColorPreset? editTarget = workingPresets.Find(p =>
            string.Equals(p.TopHex, ThemeHelper.ToHex(workingTop),    System.StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.BottomHex, ThemeHelper.ToHex(workingBottom), System.StringComparison.OrdinalIgnoreCase));

        // Plashka bg + fg swatches — replaces the light/dark toggle from v0.4.8 with
        // full colour control. Same swatch mechanic as the gradient top/bottom above.
        var plashkaBgSwatch = MakeSwatchBorder(workingPlashkaBg);
        var plashkaFgSwatch = MakeSwatchBorder(workingPlashkaFg);
        FlyoutBase.SetAttachedFlyout(plashkaBgSwatch, MakeColorFlyout(workingPlashkaBg, c =>
        {
            workingPlashkaBg = c;
            plashkaBgSwatch.Background = new SolidColorBrush(c);
            ApplyLiveTheme();
        }));
        plashkaBgSwatch.Tapped += (_, _) => FlyoutBase.ShowAttachedFlyout(plashkaBgSwatch);
        FlyoutBase.SetAttachedFlyout(plashkaFgSwatch, MakeColorFlyout(workingPlashkaFg, c =>
        {
            workingPlashkaFg = c;
            plashkaFgSwatch.Background = new SolidColorBrush(c);
            ApplyLiveTheme();
        }));
        plashkaFgSwatch.Tapped += (_, _) => FlyoutBase.ShowAttachedFlyout(plashkaFgSwatch);

        // Status colour swatches — five state colours (idle / downloading / seeding /
        // hashing / error). Each drives BOTH the 4 px left-edge stripe AND the 17 %
        // opacity progress-fill tint on any row of that state.
        var statusIdleSwatch        = MakeSwatchBorder(workingStatusIdle);
        var statusDownloadingSwatch = MakeSwatchBorder(workingStatusDownloading);
        var statusSeedingSwatch     = MakeSwatchBorder(workingStatusSeeding);
        var statusHashingSwatch     = MakeSwatchBorder(workingStatusHashing);
        var statusErrorSwatch       = MakeSwatchBorder(workingStatusError);
        void WireStatusSwatch(Border swatch, Action<Color> assign)
        {
            var initial = ((SolidColorBrush)swatch.Background).Color;
            FlyoutBase.SetAttachedFlyout(swatch, MakeColorFlyout(initial, c =>
            {
                assign(c);
                swatch.Background = new SolidColorBrush(c);
                ApplyLiveTheme();
            }));
            swatch.Tapped += (_, _) => FlyoutBase.ShowAttachedFlyout(swatch);
        }
        WireStatusSwatch(statusIdleSwatch,        c => workingStatusIdle = c);
        WireStatusSwatch(statusDownloadingSwatch, c => workingStatusDownloading = c);
        WireStatusSwatch(statusSeedingSwatch,     c => workingStatusSeeding = c);
        WireStatusSwatch(statusHashingSwatch,     c => workingStatusHashing = c);
        WireStatusSwatch(statusErrorSwatch,       c => workingStatusError = c);

        // The full "Colored theme" section, shown/hidden as one block based on themeBox.
        // Small section headers keep the growing list of colour controls scannable.
        TextBlock SectionHeader(string text) => new TextBlock
        {
            Text = text,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Opacity = 0.85,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var coloredSection = new Microsoft.UI.Xaml.Controls.StackPanel { Spacing = 10 };
        coloredSection.Children.Add(SectionHeader("Фон окна (градиент)"));
        coloredSection.Children.Add(presetBox);
        coloredSection.Children.Add(MakeSwatchRow("Верхний цвет", topSwatch));
        coloredSection.Children.Add(MakeSwatchRow("Нижний цвет",  bottomSwatch));
        coloredSection.Children.Add(SectionHeader("Плашки строк"));
        coloredSection.Children.Add(MakeSwatchRow("Плашка (фон)",   plashkaBgSwatch));
        coloredSection.Children.Add(MakeSwatchRow("Плашка (текст)", plashkaFgSwatch));
        coloredSection.Children.Add(SectionHeader("Цвета статусов"));
        coloredSection.Children.Add(MakeSwatchRow("Ожидание", statusIdleSwatch));
        coloredSection.Children.Add(MakeSwatchRow("Загрузка", statusDownloadingSwatch));
        coloredSection.Children.Add(MakeSwatchRow("Раздача",  statusSeedingSwatch));
        coloredSection.Children.Add(MakeSwatchRow("Проверка", statusHashingSwatch));
        coloredSection.Children.Add(MakeSwatchRow("Ошибка",   statusErrorSwatch));
        // Preset actions live at the very bottom now — they operate on the FULL palette
        // above (gradient + plashka bg/fg + all 5 status colours), so it reads more
        // naturally after the user has finished picking every colour.
        coloredSection.Children.Add(SectionHeader("Пресеты"));
        coloredSection.Children.Add(new TextBlock
        {
            Text = "Сохраняет весь набор цветов выше (градиент, плашки, статусы).",
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
        });
        coloredSection.Children.Add(presetActionsRow);

        // Initial preset selection: try to match current colors to a preset.
        RebuildPresetItems();
        presetBox.SelectedIndex = FindPresetIndexMatchingColors(workingTop, workingBottom);

        void UpdatePresetActionButtonStates()
        {
            var isUserPreset = presetBox.SelectedIndex >= 0 && !IsBuiltInIndex(presetBox.SelectedIndex);
            deletePresetBtn.IsEnabled = isUserPreset;
            editPresetBtn.IsEnabled   = editTarget is not null;
        }
        UpdatePresetActionButtonStates();

        // Preset selection applies every colour the preset defines. Built-in presets
        // only carry TopHex + BottomHex — the other (nullable) fields stay null so
        // plashka + status colours don't get clobbered when the user picks a built-in.
        // User-saved presets (v0.5.1+) carry the full 9-colour palette.
        void ApplyPresetFieldIfSet(string? hex, System.Action<Color> assignWorking, Border swatch)
        {
            var parsed = ThemeHelper.TryParseHex(hex);
            if (parsed is null) return;
            assignWorking(parsed.Value);
            swatch.Background = new SolidColorBrush(parsed.Value);
        }
        presetBox.SelectionChanged += (_, _) =>
        {
            var p = PresetAt(presetBox.SelectedIndex);
            if (p is not null)
            {
                var top    = ThemeHelper.TryParseHex(p.TopHex);
                var bottom = ThemeHelper.TryParseHex(p.BottomHex);
                if (top is not null && bottom is not null)
                {
                    workingTop = top.Value;
                    workingBottom = bottom.Value;
                    topSwatch.Background = new SolidColorBrush(workingTop);
                    bottomSwatch.Background = new SolidColorBrush(workingBottom);
                }
                // Plashka + status colours — applied only if the preset defines them.
                ApplyPresetFieldIfSet(p.PlashkaBgHex,        c => workingPlashkaBg        = c, plashkaBgSwatch);
                ApplyPresetFieldIfSet(p.PlashkaFgHex,        c => workingPlashkaFg        = c, plashkaFgSwatch);
                ApplyPresetFieldIfSet(p.StatusIdleHex,       c => workingStatusIdle       = c, statusIdleSwatch);
                ApplyPresetFieldIfSet(p.StatusDownloadingHex,c => workingStatusDownloading= c, statusDownloadingSwatch);
                ApplyPresetFieldIfSet(p.StatusSeedingHex,    c => workingStatusSeeding    = c, statusSeedingSwatch);
                ApplyPresetFieldIfSet(p.StatusHashingHex,    c => workingStatusHashing    = c, statusHashingSwatch);
                ApplyPresetFieldIfSet(p.StatusErrorHex,      c => workingStatusError      = c, statusErrorSwatch);
                ApplyLiveTheme();
                // Track the user's edit target: selecting a user preset makes it editable,
                // selecting a built-in clears the target (built-ins can't be edited).
                editTarget = IsBuiltInIndex(presetBox.SelectedIndex) ? null : p;
            }
            else
            {
                // SelectedIndex == -1 (colours no longer match any preset) does NOT clear
                // editTarget — the user might have just tweaked colours of their preset
                // and still wants to save them back via "Изменить пресет…".
            }
            UpdatePresetActionButtonStates();
        };

        // Colored section is only relevant for Colored — hide as one block for Dark / Light
        // themes, no colour pickers apply there.
        void UpdateColoredSectionVisibility()
        {
            var themeIdx = System.Math.Max(0, themeBox.SelectedIndex);
            coloredSection.Visibility = themeItems[themeIdx].Theme == AppTheme.Colored
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        UpdateColoredSectionVisibility();
        themeBox.SelectionChanged += (_, _) =>
        {
            UpdateColoredSectionVisibility();
            ApplyLiveTheme();
        };

        // Single-column vertical layout: path row on top (full width), then all the
        // stacked controls (max downloads, theme, and — when Colored — the whole colour
        // section). The dialog's built-in ContentScrollViewer (see ThemeHelper) provides
        // vertical scrolling if the list overflows a small main window.
        // Auto-update opt-out. Default ON — silent download + silent install + relaunch
        // when a newer GitHub release appears. OFF flips back to the classic prompt
        // ("Обновить / Позже" with release notes).
        var autoUpdateChk = new Microsoft.UI.Xaml.Controls.CheckBox
        {
            Content = "Обновлять автоматически (тихая установка в фоне)",
            IsChecked = current.AutoUpdateEnabled,
        };

        // "Оформление" section — theme-independent shape controls (buttons + rows).
        var buttonRadiusHeader = new TextBlock { Opacity = 0.85, Margin = new Thickness(0, 0, 0, 2) };
        void SyncButtonRadiusHeader() =>
            buttonRadiusHeader.Text = $"Скругление кнопок: {workingButtonRadius} px";
        SyncButtonRadiusHeader();
        var buttonRadiusSlider = new Microsoft.UI.Xaml.Controls.Slider
        {
            Minimum = 0, Maximum = 15, Value = workingButtonRadius,
            StepFrequency = 1, SmallChange = 1, LargeChange = 4,
            TickPlacement = Microsoft.UI.Xaml.Controls.Primitives.TickPlacement.None,
            Width = 320, HorizontalAlignment = HorizontalAlignment.Left,
        };
        buttonRadiusSlider.ValueChanged += (_, e) =>
        {
            workingButtonRadius = (int)Math.Round(e.NewValue);
            SyncButtonRadiusHeader();
            ApplyLiveTheme();
        };

        var plashkaRadiusHeader = new TextBlock { Opacity = 0.85, Margin = new Thickness(0, 0, 0, 2) };
        void SyncPlashkaRadiusHeader() =>
            plashkaRadiusHeader.Text = $"Скругление плашек: {workingPlashkaRadius} px";
        SyncPlashkaRadiusHeader();
        var plashkaRadiusSlider = new Microsoft.UI.Xaml.Controls.Slider
        {
            Minimum = 0, Maximum = 20, Value = workingPlashkaRadius,
            StepFrequency = 1, SmallChange = 1, LargeChange = 4,
            TickPlacement = Microsoft.UI.Xaml.Controls.Primitives.TickPlacement.None,
            Width = 320, HorizontalAlignment = HorizontalAlignment.Left,
        };
        plashkaRadiusSlider.ValueChanged += (_, e) =>
        {
            workingPlashkaRadius = (int)Math.Round(e.NewValue);
            SyncPlashkaRadiusHeader();
            ApplyLiveTheme();
        };

        // Status indicator style toggle. Off = Circle (v0.5.5+ default), On = Stripe
        // (pre-v0.5.5 look, pairs well with plashkaRadius = 0).
        var indicatorToggle = new Microsoft.UI.Xaml.Controls.ToggleSwitch
        {
            Header = "Индикатор статуса",
            OnContent = "Полоска слева",
            OffContent = "Кругляшок",
            IsOn = workingIndicatorStyle == StatusIndicatorStyle.Stripe,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        indicatorToggle.Toggled += (_, _) =>
        {
            workingIndicatorStyle = indicatorToggle.IsOn ? StatusIndicatorStyle.Stripe : StatusIndicatorStyle.Circle;
            ApplyLiveTheme();
        };

        var appearanceSection = new Microsoft.UI.Xaml.Controls.StackPanel { Spacing = 8 };
        appearanceSection.Children.Add(new TextBlock
        {
            Text = "Оформление",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Opacity = 0.85,
            Margin = new Thickness(0, 4, 0, 4),
        });
        appearanceSection.Children.Add(buttonRadiusHeader);
        appearanceSection.Children.Add(buttonRadiusSlider);
        appearanceSection.Children.Add(plashkaRadiusHeader);
        appearanceSection.Children.Add(plashkaRadiusSlider);
        appearanceSection.Children.Add(indicatorToggle);

        var outerPanel = new Microsoft.UI.Xaml.Controls.StackPanel { Spacing = 16, MinWidth = 320 };
        outerPanel.Children.Add(pathRow);
        outerPanel.Children.Add(maxBox);
        outerPanel.Children.Add(themeBox);
        outerPanel.Children.Add(autoUpdateChk);
        outerPanel.Children.Add(appearanceSection);
        outerPanel.Children.Add(coloredSection);

        dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = "Настройки",
            Content = outerPanel,
            PrimaryButtonText = "Сохранить",
            CloseButtonText = "Отмена",
            DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        // Single-column layout fits in the default ContentDialogMaxWidth (~548 dip),
        // so no MaxWidth override is needed. HEIGHT gets more room — the full colour
        // list (gradient + plashka + 5 status swatches + preset actions) is ~830 dip,
        // more than the default ContentDialogMaxHeight of 756. Raising the cap lets
        // the dialog show every option without scrolling on any window taller than
        // ~950 dip; smaller windows still scroll via the ContentScrollViewer fix.
        ThemeHelper.ApplyToDialog(dialog, ThemeHelper.CurrentTheme);
        dialog.Resources["ContentDialogMaxHeight"] = 1200.0;

        // Save-as-preset: WinUI 3 disallows stacking ContentDialogs (a nested ShowAsync
        // throws COMException "Only one ContentDialog can be open at a time" — inside an
        // async void handler that bubbles as an unhandled exception and crashes the
        // process). A Flyout, unlike ContentDialog, IS allowed to render on top of an
        // open ContentDialog — hence the manual TextBox + OK/Cancel row here.
        var nameBox = new Microsoft.UI.Xaml.Controls.TextBox
        {
            Header = "Название пресета",
            PlaceholderText = "например, Закат",
            MinWidth = 240,
        };
        var errorLine = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x60, 0x60)),
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
        };
        var flyoutOkBtn = new Microsoft.UI.Xaml.Controls.Button
        {
            Content = "OK",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            MinWidth = 80,
        };
        var flyoutCancelBtn = new Microsoft.UI.Xaml.Controls.Button
        {
            Content = "Отмена",
            MinWidth = 80,
        };
        var flyoutButtonRow = new Microsoft.UI.Xaml.Controls.StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        flyoutButtonRow.Children.Add(flyoutCancelBtn);
        flyoutButtonRow.Children.Add(flyoutOkBtn);
        var flyoutContent = new Microsoft.UI.Xaml.Controls.StackPanel
        {
            Spacing = 10,
            MinWidth = 260,
        };
        flyoutContent.Children.Add(new TextBlock
        {
            Text = "Сохранить пресет",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 15,
        });
        flyoutContent.Children.Add(nameBox);
        flyoutContent.Children.Add(errorLine);
        flyoutContent.Children.Add(flyoutButtonRow);

        var savePresetFlyout = new Microsoft.UI.Xaml.Controls.Flyout
        {
            Content = flyoutContent,
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top,
        };
        FlyoutBase.SetAttachedFlyout(savePresetBtn, savePresetFlyout);
        savePresetBtn.Click += (_, _) =>
        {
            nameBox.Text = "";
            errorLine.Visibility = Visibility.Collapsed;
            FlyoutBase.ShowAttachedFlyout(savePresetBtn);
            nameBox.Focus(FocusState.Programmatic);
        };
        flyoutCancelBtn.Click += (_, _) => savePresetFlyout.Hide();
        flyoutOkBtn.Click += (_, _) =>
        {
            var name = nameBox.Text?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                errorLine.Text = "Введите название пресета.";
                errorLine.Visibility = Visibility.Visible;
                return;
            }
            // Reject duplicate names (case-insensitive) so the ComboBox doesn't grow
            // two entries with the same label — user must pick a different name or
            // delete the existing one first.
            bool duplicate = BuiltInColorPresets.All.Any(p => string.Equals(p.Name, name, System.StringComparison.OrdinalIgnoreCase))
                          || workingPresets.Any(p => string.Equals(p.Name, name, System.StringComparison.OrdinalIgnoreCase));
            if (duplicate)
            {
                errorLine.Text = $"Пресет с именем «{name}» уже есть. Выберите другое имя.";
                errorLine.Visibility = Visibility.Visible;
                return;
            }
            workingPresets.Add(new ColorPreset
            {
                Name = name,
                TopHex               = ThemeHelper.ToHex(workingTop),
                BottomHex            = ThemeHelper.ToHex(workingBottom),
                PlashkaBgHex         = ThemeHelper.ToHex(workingPlashkaBg),
                PlashkaFgHex         = ThemeHelper.ToHex(workingPlashkaFg),
                StatusIdleHex        = ThemeHelper.ToHex(workingStatusIdle),
                StatusDownloadingHex = ThemeHelper.ToHex(workingStatusDownloading),
                StatusSeedingHex     = ThemeHelper.ToHex(workingStatusSeeding),
                StatusHashingHex     = ThemeHelper.ToHex(workingStatusHashing),
                StatusErrorHex       = ThemeHelper.ToHex(workingStatusError),
            });
            RebuildPresetItems(BuiltInColorPresets.All.Count + workingPresets.Count - 1);
            savePresetFlyout.Hide();
        };

        deletePresetBtn.Click += (_, _) =>
        {
            var i = presetBox.SelectedIndex;
            if (i < 0 || IsBuiltInIndex(i)) return;
            var j = i - BuiltInColorPresets.All.Count;
            var removed = workingPresets[j];
            workingPresets.RemoveAt(j);
            // If we were editing this preset, drop the reference — there is no target
            // to save changes back into anymore.
            if (ReferenceEquals(editTarget, removed))
                editTarget = null;
            RebuildPresetItems();
            presetBox.SelectedIndex = FindPresetIndexMatchingColors(workingTop, workingBottom);
            UpdatePresetActionButtonStates();
        };

        // --- Edit-preset flyout: mirrors the save-preset flyout but pre-fills the
        // current preset's name, and on OK REPLACES the preset (name + top/bottom hex)
        // instead of appending a new entry.
        var editNameBox = new Microsoft.UI.Xaml.Controls.TextBox
        {
            Header = "Название пресета",
            MinWidth = 240,
        };
        var editErrorLine = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x60, 0x60)),
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
        };
        var editOkBtn = new Microsoft.UI.Xaml.Controls.Button
        {
            Content = "OK",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            MinWidth = 80,
        };
        var editCancelBtn = new Microsoft.UI.Xaml.Controls.Button
        {
            Content = "Отмена",
            MinWidth = 80,
        };
        var editButtonRow = new Microsoft.UI.Xaml.Controls.StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        editButtonRow.Children.Add(editCancelBtn);
        editButtonRow.Children.Add(editOkBtn);
        var editFlyoutContent = new Microsoft.UI.Xaml.Controls.StackPanel
        {
            Spacing = 10,
            MinWidth = 260,
        };
        editFlyoutContent.Children.Add(new TextBlock
        {
            Text = "Изменить пресет",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 15,
        });
        editFlyoutContent.Children.Add(new TextBlock
        {
            Text = "Название и текущие цвета верх/низ будут сохранены в этот пресет.",
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        });
        editFlyoutContent.Children.Add(editNameBox);
        editFlyoutContent.Children.Add(editErrorLine);
        editFlyoutContent.Children.Add(editButtonRow);

        var editPresetFlyout = new Microsoft.UI.Xaml.Controls.Flyout
        {
            Content = editFlyoutContent,
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top,
        };
        FlyoutBase.SetAttachedFlyout(editPresetBtn, editPresetFlyout);

        editPresetBtn.Click += (_, _) =>
        {
            if (editTarget is null) return;
            editNameBox.Text = editTarget.Name;
            editErrorLine.Visibility = Visibility.Collapsed;
            FlyoutBase.ShowAttachedFlyout(editPresetBtn);
            editNameBox.Focus(FocusState.Programmatic);
            editNameBox.SelectAll();
        };
        editCancelBtn.Click += (_, _) => editPresetFlyout.Hide();
        editOkBtn.Click += (_, _) =>
        {
            if (editTarget is null) return;
            var newName = editNameBox.Text?.Trim();
            if (string.IsNullOrEmpty(newName))
            {
                editErrorLine.Text = "Введите название пресета.";
                editErrorLine.Visibility = Visibility.Visible;
                return;
            }
            // Only check duplicates when renaming — keeping the same name is always OK.
            if (!string.Equals(newName, editTarget.Name, System.StringComparison.OrdinalIgnoreCase))
            {
                bool duplicate = BuiltInColorPresets.All.Any(p => string.Equals(p.Name, newName, System.StringComparison.OrdinalIgnoreCase))
                              || workingPresets.Any(p => !ReferenceEquals(p, editTarget)
                                                       && string.Equals(p.Name, newName, System.StringComparison.OrdinalIgnoreCase));
                if (duplicate)
                {
                    editErrorLine.Text = $"Пресет с именем «{newName}» уже есть. Выберите другое имя.";
                    editErrorLine.Visibility = Visibility.Visible;
                    return;
                }
            }
            var idx = workingPresets.IndexOf(editTarget);
            if (idx < 0) return; // should never happen: editTarget was validated on set
            var updated = new ColorPreset
            {
                Name = newName,
                TopHex               = ThemeHelper.ToHex(workingTop),
                BottomHex            = ThemeHelper.ToHex(workingBottom),
                PlashkaBgHex         = ThemeHelper.ToHex(workingPlashkaBg),
                PlashkaFgHex         = ThemeHelper.ToHex(workingPlashkaFg),
                StatusIdleHex        = ThemeHelper.ToHex(workingStatusIdle),
                StatusDownloadingHex = ThemeHelper.ToHex(workingStatusDownloading),
                StatusSeedingHex     = ThemeHelper.ToHex(workingStatusSeeding),
                StatusHashingHex     = ThemeHelper.ToHex(workingStatusHashing),
                StatusErrorHex       = ThemeHelper.ToHex(workingStatusError),
            };
            workingPresets[idx] = updated;
            editTarget = updated;
            RebuildPresetItems(BuiltInColorPresets.All.Count + idx);
            editPresetFlyout.Hide();
        };

        var res = await dialog.ShowAsync();
        if (res != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
        {
            // Revert live-preview changes: restore snapshot theme + colours + plashka + statuses.
            ThemeHelper.SetStatusColors(snapshotStatusIdle, snapshotStatusDownloading,
                snapshotStatusSeeding, snapshotStatusHashing, snapshotStatusError);
            ThemeHelper.SetCornerRadii(snapshotBtnRadius, snapshotPlashkaRadius);
            ThemeHelper.SetStatusIndicatorStyle(snapshotIndicatorStyle);
            ThemeHelper.Apply(RootGrid, snapshotTheme, snapshotTop, snapshotBottom, snapshotPlashkaBg, snapshotPlashkaFg);
            ThemeHelper.ApplyToTitleBar(AppWindow.TitleBar, snapshotTheme);
            ApplyButtonCornerRadius(snapshotBtnRadius);
            var revertPlashkaCorner = new Microsoft.UI.Xaml.CornerRadius(snapshotPlashkaRadius);
            var revertAsCircle = snapshotIndicatorStyle == StatusIndicatorStyle.Circle;
            foreach (var vm in ViewModel.Torrents)
            {
                vm.PlashkaCornerRadius = revertPlashkaCorner;
                vm.StatusAsCircle = revertAsCircle;
                vm.RefreshBrushes();
                vm.Refresh();
            }
            return;
        }

        var newDir = pathBox.Text?.Trim();
        var newMax = (int)Math.Round(maxBox.Value);
        if (newMax < 1) newMax = 1;
        var newTheme = themeItems[System.Math.Max(0, themeBox.SelectedIndex)].Theme;

        var updated = current with
        {
            LastDownloadDir = string.IsNullOrEmpty(newDir) ? null : newDir,
            MaxSimultaneousDownloads = newMax,
            Theme = newTheme,
            ColoredTopHex        = ThemeHelper.ToHex(workingTop),
            ColoredBottomHex     = ThemeHelper.ToHex(workingBottom),
            PlashkaBgHex         = ThemeHelper.ToHex(workingPlashkaBg),
            PlashkaFgHex         = ThemeHelper.ToHex(workingPlashkaFg),
            StatusIdleHex        = ThemeHelper.ToHex(workingStatusIdle),
            StatusDownloadingHex = ThemeHelper.ToHex(workingStatusDownloading),
            StatusSeedingHex     = ThemeHelper.ToHex(workingStatusSeeding),
            StatusHashingHex     = ThemeHelper.ToHex(workingStatusHashing),
            StatusErrorHex       = ThemeHelper.ToHex(workingStatusError),
            CustomPresets        = workingPresets,
            AutoUpdateEnabled    = autoUpdateChk.IsChecked ?? true,
            ButtonCornerRadius   = workingButtonRadius,
            PlashkaCornerRadius  = workingPlashkaRadius,
            StatusIndicatorStyle = workingIndicatorStyle,
        };
        SettingsStore.Save(updated);

        // Ensure the window matches saved state — live preview already applied it while
        // the dialog was open, but re-apply here so a "Save without visiting any picker"
        // path still commits correctly (theme changed via combobox, colors unchanged).
        ThemeHelper.SetStatusColors(workingStatusIdle, workingStatusDownloading,
            workingStatusSeeding, workingStatusHashing, workingStatusError);
        ThemeHelper.Apply(RootGrid, updated.Theme, workingTop, workingBottom, workingPlashkaBg, workingPlashkaFg);
        ThemeHelper.ApplyToTitleBar(AppWindow.TitleBar, updated.Theme);
        foreach (var vm in ViewModel.Torrents)
        {
            vm.RefreshBrushes();
            vm.Refresh();
        }

        try
        {
            await _service.ApplySettingsAsync(updated);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Не удалось применить настройки", ex.Message);
        }
    }
}
