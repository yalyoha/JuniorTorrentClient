using System.Net;
using System.Reflection;
using JTC.Helpers;
using MonoTorrent;
using MonoTorrent.Client;

namespace JTC.Services;

public sealed class TorrentService : IAsyncDisposable
{
    // Higher than MonoTorrent's default; more peer slots = better download parallelism.
    // 500 is safe on modern hardware; going higher yields diminishing returns.
    private const int MaxPeerConnections = 500;

    // Faster ramp-up when adding a torrent — more parallel connection attempts.
    // Raised from 30 → 50 because the typical usage pattern here is ONE hot torrent
    // at a time; all half-open budget goes to it, so more parallel attempts = faster
    // pool build-up = faster time-to-peak-speed.
    private const int MaxHalfOpenConnections = 50;

    // Fixed TCP+UDP port so UPnP/NAT-PMP mappings survive restarts and known peers
    // can reconnect to the same address. 51413 is the qBittorrent default — well-known,
    // not conflicting with other common apps.
    private const int PeerListenPort = 51413;

    // Per-torrent connection cap. MonoTorrent's default per-torrent limit is much lower
    // than the engine-wide MaximumConnections=500 above, so a single hot torrent leaves
    // most of the global peer budget idle. 200 lets one torrent claim a large share
    // without saturating Windows' half-open TCP limit (~50) or starving a second torrent
    // if the user adds one — two active torrents can still fit under the global 500.
    private const int PerTorrentMaxConnections = 200;

    // Per-torrent upload slots. Raising slots above the MonoTorrent default helps our
    // typical single-torrent workload get better tit-for-tat reciprocity from peers,
    // which improves download throughput. 16 is conservative — high enough to matter
    // on a 200-peer swarm, low enough to leave upload bandwidth for other traffic.
    private const int PerTorrentUploadSlots = 16;

    private readonly ClientEngine _engine;
    private readonly StateStore _store;
    private readonly Dictionary<TorrentManager, PersistedTorrent> _persistedByManager = new();
    // Serializes add/remove so an Add can never race a still-running Remove.
    // MonoTorrent otherwise throws "A manager for this torrent has already been registered"
    // because its info-hash cleanup lags the RemoveAsync return.
    private readonly SemaphoreSlim _mutation = new(1, 1);

    // Periodic diagnostic timer — writes one metrics line per active torrent to debug.log
    // every ~10 s. Without this the "download stalls / drops" reports have nothing to
    // work with: the app-side numbers change second-to-second in the UI and are lost.
    // 10 s is a compromise: dense enough to see ramp-up curves after Add, sparse enough
    // that debug.log's 1 MB rotation lasts many hours on a single active torrent.
    private readonly System.Threading.Timer _diagTimer;

    // Periodic fast-resume flush. Without this, MonoTorrent's AutoSaveLoadFastResume only
    // writes fast-resume data on a clean StopAsync — so a hard kill / crash forces a full
    // re-hash on the next launch (slow start, zero download speed until the hash finishes).
    // 45 s is between the plan's 30–60 s window: dense enough to keep the potential re-hash
    // window short, sparse enough not to hammer HDDs / SMR drives with metadata writes.
    private readonly System.Threading.Timer _fastResumeTimer;

    // The Save method is internal in MonoTorrent 3.0.2 (there's a compiler-generated
    // <SaveFastResumeAsync>d__229 state machine in MonoTorrent.Client.dll, but no public
    // TorrentManager.SaveFastResumeAsync member). We reach it via reflection, resolved once
    // on the first tick and cached. Null after first-tick means the method wasn't found —
    // we log once and stop trying. Reflection makes us fragile to future MonoTorrent
    // renames, but the alternative (full re-hash on every crash) is worse.
    private MethodInfo? _saveFastResumeMethod;
    private bool _fastResumeReflectionAttempted;
    private bool _fastResumeUnavailableLogged;

    // Watchdog state per manager: current retry budget + backoff schedule.
    // Lives keyed by TorrentManager because the policy is torrent-scoped (removing a
    // torrent should forget its history; a fresh add starts with a clean budget).
    // Access is only from the state-changed event handler → no lock needed as long as
    // we never hop threads inside a single OnManagerStateChanged invocation.
    private readonly Dictionary<TorrentManager, TorrentRestartPolicy> _restartPolicies = new();

    // Consecutive "phantom Seeding" tick count per manager. StallCheckTick increments each
    // time it sees a manager stuck in Seeding with progress well below 100 % — after three
    // consecutive hits (~90 s) we assume MonoTorrent's bitfield is stale and force a hash
    // recheck. Reset to zero when the manager leaves Seeding (handled in OnManagerStateChanged)
    // or after a recheck completes. Access only from the timer thread and state handler; both
    // never run concurrently against the same manager's slot in practice.
    private readonly Dictionary<TorrentManager, int> _stallHits = new();
    // Managers currently being rechecked by the phantom-Seeding path. Prevents overlapping
    // recheck attempts if the tick fires while a previous recheck is still running (Hash-
    // CheckAsync on a large torrent can take minutes).
    private readonly HashSet<TorrentManager> _stallRecheckInProgress = new();
    // Periodic stall watchdog. Fires every 30 s after a 60 s startup grace period, so freshly-
    // restored torrents get time to legitimately settle before we start second-guessing state.
    private readonly System.Threading.Timer _stallTimer;

    // Cached AppSettings. Prior code called SettingsStore.Load() (synchronous disk read)
    // from CanStartMore() inside a while-loop on the hot queue path — every state
    // transition would re-read the same JSON. Now we snapshot at construction and refresh
    // on ApplySettingsAsync; hot path is a plain memory read. Volatile-not-needed here
    // because the update happens on the UI thread and the readers tolerate the tiny window
    // where the queue uses the previous value.
    private AppSettings _currentSettings = SettingsStore.Load();

    private bool _disposed;

    public event EventHandler<TorrentManager>? TorrentAdded;
    public event EventHandler<TorrentManager>? TorrentRemoved;

    public IReadOnlyList<TorrentManager> Torrents => (IReadOnlyList<TorrentManager>)_engine.Torrents;

    public TorrentService()
    {
        AppPaths.EnsureExists();
        _store = new StateStore(AppPaths.Root);
        _engine = new ClientEngine(new EngineSettingsBuilder
        {
            CacheDirectory = AppPaths.CacheDir,
            MaximumConnections = MaxPeerConnections,
            MaximumHalfOpenConnections = MaxHalfOpenConnections,
            // 128 MB in-memory disk cache (up from 50 MB). Under our typical single-hot-
            // torrent workload one torrent gets all 500 peer slots — the cache smooths
            // its bursty piece writes on slower disks (HDDs, SMR drives) and reduces
            // syscall churn. On SSDs it's still helpful for the read-ahead side of
            // ReadsAndWrites when we upload to peers.
            DiskCacheBytes = 128 * 1024 * 1024,
            DiskCachePolicy = MonoTorrent.PieceWriter.CachePolicy.ReadsAndWrites,
            // Encryption preference: encrypted first, plain-text as fallback.
            // Some ISPs / trackers reject purely-plain-text connections.
            AllowedEncryption = new System.Collections.Generic.List<MonoTorrent.Connections.EncryptionType>
            {
                MonoTorrent.Connections.EncryptionType.RC4Full,
                MonoTorrent.Connections.EncryptionType.RC4Header,
                MonoTorrent.Connections.EncryptionType.PlainText,
            },
            // Fixed listen endpoints so UPnP/NAT-PMP maps a stable port across restarts
            // (peers can reconnect to the same address; DHT UDP socket doesn't shift).
            ListenEndPoints = new System.Collections.Generic.Dictionary<string, IPEndPoint>
            {
                { "ipv4", new IPEndPoint(IPAddress.Any, PeerListenPort) },
            },
            DhtEndPoint = new IPEndPoint(IPAddress.Any, PeerListenPort),
            // Note: MonoTorrent 3.0.2 doesn't expose DhtBootstrapRouters — it uses its
            // own default bootstrap list internally. If a newer version exposes it we
            // should re-add explicit routers here as a resilience measure.
            AllowLocalPeerDiscovery = true,      // BEP 14 — LAN peer discovery
            AllowPortForwarding = true,          // UPnP / NAT-PMP — inbound reachability
            AutoSaveLoadDhtCache = true,         // remember DHT nodes across restarts
            AutoSaveLoadFastResume = true,       // skip full re-hash on restart
            AutoSaveLoadMagnetLinkMetadata = true, // cache magnet metadata
        }.ToSettings());

        // First tick after 15 s so we skip the noisy startup burst; subsequent ticks
        // every 10 s. Ticks are read-only property fetches on background thread —
        // never touches _mutation, never awaits, so it can't deadlock or delay Add/Remove.
        _diagTimer = new System.Threading.Timer(
            LogDiagnosticsTick, null,
            dueTime: TimeSpan.FromSeconds(15),
            period: TimeSpan.FromSeconds(10));

        // First tick after 45 s — no point saving fast-resume before we've downloaded
        // anything worth resuming from. Then every 45 s.
        _fastResumeTimer = new System.Threading.Timer(
            FastResumeTick, null,
            dueTime: TimeSpan.FromSeconds(45),
            period: TimeSpan.FromSeconds(45));

        // Stall watchdog for "phantom Seeding" (MonoTorrent flips a mid-download torrent
        // to Seeding state without progress reaching 100 %). 60 s startup grace so restored
        // Seeding torrents that are legitimately complete don't get flagged, then 30 s tick.
        _stallTimer = new System.Threading.Timer(
            StallCheckTick, null,
            dueTime: TimeSpan.FromSeconds(60),
            period: TimeSpan.FromSeconds(30));

        // Fire-and-forget DHT warm-up so bootstrapping runs in parallel with LoadStateAsync
        // and the first UI paint instead of lazily on the first Downloading torrent — the
        // lazy path leaves DHT NotReady for 15–60 s of dead time after app launch (verified
        // in debug.log: dht=NotReady persists across whole sessions when only Seeding
        // torrents are loaded). Magnet metadata fetches lose the most from cold DHT;
        // .torrent adds recover a smaller window because trackers work independently.
        _ = Task.Run(WarmupDhtAsync);
    }

    /// <summary>
    /// Applies user-facing settings that affect torrent-management policy.
    /// Currently: if the download-limit was raised, resume waiting torrents; if lowered,
    /// no-op (we don't preempt running torrents on the fly).
    /// </summary>
    public Task ApplySettingsAsync(AppSettings settings)
    {
        // Cache in memory so hot-path callers (CanStartMore) don't re-read from disk.
        _currentSettings = settings;
        return StartNextIfSlotFreeAsync();
    }

    public async Task<TorrentManager> AddTorrentFileAsync(
        string torrentPath, string downloadDir, bool startImmediately,
        IReadOnlySet<int>? skipFileIndices = null)
    {
        DebugLog.Info($"AddTorrentFileAsync ENTER path='{torrentPath}' start={startImmediately} skip={skipFileIndices?.Count ?? 0}");
        if (!File.Exists(torrentPath))
            throw new FileNotFoundException("Torrent file not found", torrentPath);
        Directory.CreateDirectory(downloadDir);

        await _mutation.WaitAsync();
        DebugLog.Info("  Add: semaphore acquired");
        try
        {
            if (IsAlreadyTracked(torrentPath))
            {
                DebugLog.Info("  Add: rejected as duplicate");
                throw new InvalidOperationException("Этот торрент уже добавлен.");
            }
            DebugLog.Info($"  Add: engine.Torrents.Count before = {_engine.Torrents.Count}");
            var manager = await AddWithRetryAsync(() => _engine.AddAsync(torrentPath, downloadDir, BuildTorrentSettings()));
            DebugLog.Info($"  Add: engine.AddAsync ok, engine.Torrents.Count after = {_engine.Torrents.Count}");
            WireStateChange(manager);

            // Apply DoNotDownload to unselected files BEFORE StartAsync so the piece picker
            // never touches their pieces. Setting priority after Start is fine too (MonoTorrent
            // re-evaluates on next pick) but pre-Start avoids a brief window where the engine
            // could allocate slots to a file the user doesn't want.
            if (skipFileIndices is { Count: > 0 })
                await ApplySkipFilePrioritiesAsync(manager, skipFileIndices);

            _persistedByManager[manager] = new PersistedTorrent
            {
                Source = torrentPath,
                SourceKind = PersistedSourceKind.TorrentFile,
                DownloadDir = downloadDir,
                Paused = !startImmediately,
                // Persist skip selection so a restart of the app doesn't quietly re-enable
                // download of files the user explicitly excluded. Normalise to a sorted
                // int[] so the JSON diff is stable and predictable.
                SkipFileIndices = skipFileIndices is { Count: > 0 }
                    ? skipFileIndices.OrderBy(i => i).ToArray()
                    : null,
            };
            TorrentAdded?.Invoke(this, manager);
            if (startImmediately && CanStartMore())
                await manager.StartAsync();
            await SaveStateAsync();
            DebugLog.Info("  Add: done");
            return manager;
        }
        catch (Exception ex) { DebugLog.Error("AddTorrentFileAsync", ex); throw; }
        finally { _mutation.Release(); DebugLog.Info("  Add: semaphore released"); }
    }

    private static async Task ApplySkipFilePrioritiesAsync(TorrentManager manager, IReadOnlySet<int> skipIndices)
    {
        var files = manager.Files;
        if (files is null) return;
        var count = 0;
        for (int i = 0; i < files.Count; i++)
        {
            if (!skipIndices.Contains(i)) continue;
            try
            {
                await manager.SetFilePriorityAsync(files[i], Priority.DoNotDownload);
                count++;
            }
            catch (Exception ex) { DebugLog.Error($"skip file [{i}] {files[i].Path}", ex); }
        }
        DebugLog.Info($"  Add: marked {count}/{files.Count} files as DoNotDownload");
    }

    public async Task<TorrentManager> AddMagnetAsync(string magnetUri, string downloadDir, bool startImmediately)
    {
        DebugLog.Info($"AddMagnetAsync ENTER uri='{magnetUri}' start={startImmediately}");
        if (!MagnetLink.TryParse(magnetUri, out var link) || link is null)
            throw new ArgumentException("Invalid magnet link", nameof(magnetUri));
        Directory.CreateDirectory(downloadDir);

        await _mutation.WaitAsync();
        DebugLog.Info("  AddMagnet: semaphore acquired");
        TorrentManager manager;
        try
        {
            if (IsAlreadyTracked(magnetUri))
            {
                DebugLog.Info("  AddMagnet: rejected as duplicate");
                throw new InvalidOperationException("Этот magnet уже добавлен.");
            }

            manager = await AddWithRetryAsync(() => _engine.AddAsync(link, downloadDir, BuildTorrentSettings()));
            DebugLog.Info($"  AddMagnet: engine.AddAsync ok, engine.Torrents.Count={_engine.Torrents.Count}");
            WireStateChange(manager);
            _persistedByManager[manager] = new PersistedTorrent
            {
                Source = magnetUri,
                SourceKind = PersistedSourceKind.Magnet,
                DownloadDir = downloadDir,
                Paused = !startImmediately,
            };
            TorrentAdded?.Invoke(this, manager);
            await SaveStateAsync();
            DebugLog.Info("  AddMagnet: done (start deferred outside lock)");
        }
        catch (Exception ex) { DebugLog.Error("AddMagnetAsync", ex); throw; }
        finally { _mutation.Release(); DebugLog.Info("  AddMagnet: semaphore released"); }

        // StartAsync is fire-and-forget and lives OUTSIDE the mutation lock. For
        // magnets, TorrentManager.StartAsync can block for a long time (or forever
        // on a bad network / unreachable DHT) waiting for the first metadata
        // exchange with a peer — if we awaited it while holding the mutation
        // semaphore, every subsequent Add / Remove would hang behind the stuck
        // magnet. StartAsync errors are logged; state changes reach the UI via
        // TorrentStateChanged the same way as the .torrent path.
        if (startImmediately && CanStartMore())
        {
            _ = Task.Run(async () =>
            {
                try { await manager.StartAsync(); }
                catch (Exception ex) { DebugLog.Error($"magnet StartAsync '{manager.Torrent?.Name ?? magnetUri}'", ex); }
            });
        }
        return manager;
    }

    public async Task PauseAsync(TorrentManager manager)
    {
        await manager.PauseAsync();
        if (_persistedByManager.TryGetValue(manager, out var entry))
            _persistedByManager[manager] = entry with { Paused = true };
        await SaveStateAsync();
    }

    public async Task ResumeAsync(TorrentManager manager)
    {
        // Resume respects the user's explicit intent — bypasses the auto-queue limit.
        await manager.StartAsync();
        if (_persistedByManager.TryGetValue(manager, out var entry))
            _persistedByManager[manager] = entry with { Paused = false };
        await SaveStateAsync();
    }

    /// <summary>
    /// Force-rehash every piece of the torrent from disk. Used by the row context-menu
    /// "Обновить" action — helpful when files have been changed externally or when
    /// fast-resume data is suspected to be stale. Auto-resumes downloading if the
    /// torrent was running before the check.
    /// </summary>
    public async Task RecheckAsync(TorrentManager manager)
    {
        var name = manager.Torrent?.Name ?? "(no metadata)";
        var startState = manager.State;
        DebugLog.Info($"RecheckAsync ENTER name='{name}' state={startState}");

        var wasRunning = startState is not TorrentState.Stopped
                                    and not TorrentState.Paused
                                    and not TorrentState.Error;

        // HashCheckAsync requires the manager to be in Stopped state. StopAsync with
        // a 2s timeout matches what RemoveAsync uses — enough for one tracker round,
        // but doesn't wall for flaky trackers.
        try
        {
            DebugLog.Info($"  Recheck: StopAsync (from state={manager.State})");
            await manager.StopAsync(TimeSpan.FromSeconds(2));
            DebugLog.Info($"  Recheck: stopped, state={manager.State}");
        }
        catch (Exception ex) { DebugLog.Error("Recheck.Stop", ex); }

        DebugLog.Info($"  Recheck: HashCheckAsync(autoStart={wasRunning}) begin");
        try
        {
            await manager.HashCheckAsync(autoStart: wasRunning);
            DebugLog.Info($"  Recheck: HashCheckAsync done, state={manager.State}, progress={manager.Progress:F1}%");
        }
        catch (Exception ex)
        {
            DebugLog.Error("Recheck.HashCheckAsync", ex);
            throw;
        }
    }

    public async Task RemoveAsync(TorrentManager manager, bool deleteFilesOnDisk)
    {
        DebugLog.Info($"RemoveAsync ENTER name='{manager.Torrent?.Name}' state={manager.State} deleteFiles={deleteFilesOnDisk}");
        var downloadDir = manager.SavePath;
        var torrentName = manager.Torrent?.Name;

        manager.TorrentStateChanged -= OnManagerStateChanged;

        await _mutation.WaitAsync();
        DebugLog.Info("  Remove: semaphore acquired");
        try
        {
            try
            {
                if (manager.State != TorrentState.Stopped)
                {
                    DebugLog.Info($"  Remove: StopAsync(2s timeout) (from state={manager.State})");
                    // MonoTorrent's default StopAsync waits for tracker "stopped" announces to reply,
                    // which can be 30-60 seconds on flaky trackers. 2s is enough to try one round.
                    try { await manager.StopAsync(TimeSpan.FromSeconds(2)); }
                    catch (Exception ex) { DebugLog.Error("StopAsync", ex); }
                    DebugLog.Info($"  Remove: after StopAsync, state={manager.State}");
                }
                DebugLog.Info($"  Remove: engine.RemoveAsync start, engine.Torrents.Count={_engine.Torrents.Count}");
                var removed = await _engine.RemoveAsync(manager);
                DebugLog.Info($"  Remove: engine.RemoveAsync returned {removed}, engine.Torrents.Count={_engine.Torrents.Count}");

                // MonoTorrent's RemoveAsync doesn't guarantee full internal eviction on return —
                // Add of the same torrent immediately after can still throw "already registered".
                // Poll until the manager reference is truly gone from engine's tracking, up to 15s.
                var deadline = DateTime.UtcNow.AddSeconds(15);
                var polls = 0;
                while (_engine.Torrents.Any(t => ReferenceEquals(t, manager)) && DateTime.UtcNow < deadline)
                {
                    polls++;
                    await Task.Delay(100);
                }
                DebugLog.Info($"  Remove: manager-reference eviction confirmed after {polls} polls");
                // Extra safety buffer for MonoTorrent's async info-hash tracking cleanup.
                await Task.Delay(500);
                DebugLog.Info("  Remove: buffer wait done");
            }
            finally
            {
                _persistedByManager.Remove(manager);
                _restartPolicies.Remove(manager);
                TorrentRemoved?.Invoke(this, manager);
                try { await SaveStateAsync(); }
                catch (Exception ex) { DebugLog.Error("SaveStateAsync in remove finally", ex); }
            }
        }
        catch (Exception ex) { DebugLog.Error("RemoveAsync", ex); throw; }
        finally { _mutation.Release(); DebugLog.Info("  Remove: semaphore released"); }

        if (deleteFilesOnDisk)
        {
            await DeleteTorrentFilesAsync(manager, downloadDir, torrentName);
        }

        // A slot may have just freed up.
        _ = StartNextIfSlotFreeAsync();
    }

    // True while LoadStateAsync is replaying persisted torrents at startup.
    // Consumers (MainViewModel) use it to decide row-order: restored torrents
    // append in persisted order, user-initiated adds prepend to the top.
    public bool IsRestoringState { get; private set; }

    public async Task LoadStateAsync()
    {
        IsRestoringState = true;
        var items = await _store.LoadAsync();
        foreach (var item in items)
        {
            try
            {
                switch (item.SourceKind)
                {
                    case PersistedSourceKind.TorrentFile:
                        if (File.Exists(item.Source))
                        {
                            // Rehydrate the file-selection so the piece-picker keeps skipping
                            // files the user excluded at first add. Magnet path stays without
                            // skip support: metadata isn't guaranteed at add time, and the
                            // add API used for magnets doesn't accept the skip set.
                            IReadOnlySet<int>? skip = null;
                            if (item.SkipFileIndices is { Length: > 0 })
                                skip = new HashSet<int>(item.SkipFileIndices);
                            await AddTorrentFileAsync(item.Source, item.DownloadDir,
                                startImmediately: !item.Paused, skipFileIndices: skip);
                        }
                        break;
                    case PersistedSourceKind.Magnet:
                        await AddMagnetAsync(item.Source, item.DownloadDir, startImmediately: !item.Paused);
                        break;
                }
            }
            catch
            {
                // Ignore individual failures; keep loading the rest.
            }
        }
        IsRestoringState = false;
    }

    /// <summary>
    /// Deletes the torrent's data files from disk. Uses MonoTorrent's file list to get
    /// each file's actual path (works for both single- and multi-file torrents), then
    /// removes any now-empty containing directories. Retries a few times because
    /// file handles from the piece writer may not be fully released the moment
    /// engine.RemoveAsync returns.
    /// </summary>
    private static async Task DeleteTorrentFilesAsync(TorrentManager manager, string downloadDir, string? torrentName)
    {
        // Collect all files' full paths BEFORE we release the manager reference.
        var filePaths = new List<string>();
        try
        {
            foreach (var f in manager.Files)
            {
                if (!string.IsNullOrEmpty(f.FullPath))
                    filePaths.Add(f.FullPath);
            }
        }
        catch (Exception ex) { DebugLog.Error("collect file paths", ex); }

        // Fallback for magnet torrents whose metadata never resolved (Files is empty):
        // best guess is downloadDir\torrentName.
        if (filePaths.Count == 0 && !string.IsNullOrEmpty(torrentName))
            filePaths.Add(Path.Combine(downloadDir, torrentName));

        DebugLog.Info($"DeleteTorrentFiles: {filePaths.Count} paths to delete");

        // Try each file with a few retries — piece writer may still be flushing.
        foreach (var path in filePaths)
            await DeletePathWithRetriesAsync(path);

        // Multi-file torrents put everything in a subfolder <downloadDir>\<torrentName>.
        // Remove it (and any empty ancestor folders inside downloadDir).
        if (!string.IsNullOrEmpty(torrentName))
        {
            var container = Path.Combine(downloadDir, torrentName);
            if (Directory.Exists(container))
            {
                // Wipe any leftover empty subfolders + the container itself.
                try { Directory.Delete(container, recursive: true); }
                catch (Exception ex) { DebugLog.Error($"remove container {container}", ex); }
            }
        }
    }

    private static async Task DeletePathWithRetriesAsync(string path)
    {
        // Retry with escalating delays: 0, 200, 400, 800, 1600ms — total ~3s.
        var delays = new[] { 0, 200, 400, 800, 1600 };
        foreach (var delay in delays)
        {
            if (delay > 0) await Task.Delay(delay);
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                    DebugLog.Info($"  deleted dir: {path}");
                    return;
                }
                if (File.Exists(path))
                {
                    File.Delete(path);
                    DebugLog.Info($"  deleted file: {path}");
                    return;
                }
                // Path doesn't exist — nothing to do.
                return;
            }
            catch (IOException) { /* file may be locked, retry */ }
            catch (UnauthorizedAccessException) { /* AV or read-only, retry */ }
            catch (Exception ex)
            {
                DebugLog.Error($"delete {path}", ex);
                return; // unrecoverable, don't retry
            }
        }
        DebugLog.Info($"  gave up deleting: {path}");
    }

    private Task SaveStateAsync() => _store.SaveAsync(_persistedByManager.Values.ToArray());

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await _diagTimer.DisposeAsync();
        await _fastResumeTimer.DisposeAsync();
        await _stallTimer.DisposeAsync();
        foreach (var mgr in _engine.Torrents)
            mgr.TorrentStateChanged -= OnManagerStateChanged;
        await _engine.StopAllAsync();
        _engine.Dispose();
    }

    // ---- Simultaneous-download queue -------------------------------------------------

    private void WireStateChange(TorrentManager manager)
    {
        manager.TorrentStateChanged -= OnManagerStateChanged;
        manager.TorrentStateChanged += OnManagerStateChanged;
    }

    private async void OnManagerStateChanged(object? sender, TorrentStateChangedEventArgs e)
    {
        var manager = sender as TorrentManager;

        // Unconditional transition log. Historically we only logged Error transitions —
        // which meant a "download stalls to Seeding at 40 %" bug (MonoTorrent's piece
        // picker gets confused → premature Seeding flip without progress ≈ 100 %) left
        // nothing in the log to reason about. Every transition now goes to debug.log
        // with the manager's current progress, so post-mortem can spot bad flips.
        if (manager is not null)
        {
            var progNow = manager.Progress;
            DebugLog.Info($"state '{ShortName(manager.Torrent?.Name)}': {e.OldState} → {e.NewState} (prog={progNow:F1}%)");
        }

        // A successful re-entry into a downloading state means the previous retry cycle
        // (if any) worked. Reset the policy so the next stall gets a fresh 5-attempt budget.
        if (manager is not null && !WasDownloading(e.OldState) && WasDownloading(e.NewState))
        {
            if (_restartPolicies.TryGetValue(manager, out var priorPolicy))
                priorPolicy.RecordSuccess();
        }

        // Reset the stall-watchdog's consecutive-hit counter for this manager when it leaves
        // a "phantom Seeding" state — either the recheck fixed the bitfield or MonoTorrent
        // corrected itself. Counter lives in _stallHits (Dictionary keyed by manager).
        if (manager is not null && e.OldState == TorrentState.Seeding && e.NewState != TorrentState.Seeding)
            _stallHits.Remove(manager);

        // Diagnostic: capture Error-state transitions with the underlying cause so post-mortem
        // has a concrete exception to look at instead of just "torrent went red". Done outside
        // the try/catch below so a logging bug can't silently be swallowed.
        if (e.NewState == TorrentState.Error && manager is not null)
        {
            var name = manager.Torrent?.Name ?? "(no metadata)";
            var ex = manager.Error?.Exception;
            if (ex is not null)
                DebugLog.Error($"torrent -> Error '{name}'", ex);
            else
                DebugLog.Info($"torrent -> Error '{name}' (no manager.Error captured)");

            // Fire-and-forget the recovery attempt — we're already inside an async void handler
            // and the delay in the watchdog is measured in seconds. Errors inside the watchdog
            // are logged there, not surfaced back up here.
            _ = TryRestartAfterErrorAsync(manager);
        }

        try
        {
            // A slot frees when a manager stops downloading (finished → Seeding, or externally paused/stopped/errored).
            if (WasDownloading(e.OldState) && !WasDownloading(e.NewState))
                await StartNextIfSlotFreeAsync();
        }
        catch (Exception ex)
        {
            // Was silently swallowed before task 4. Log so we can see why StartNextIfSlotFreeAsync failed.
            DebugLog.Error("OnManagerStateChanged.StartNextIfSlotFreeAsync", ex);
        }
    }

    // Watchdog: if a manager fell into Error, wait the policy's backoff and try to restart.
    // Attempts 1–3 do a plain StartAsync (fastest path — often the underlying condition has
    // resolved by itself). Attempts 4+ throw a HashCheckAsync in first, in case data on disk
    // no longer matches fast-resume state (e.g. an external tool touched the files during the
    // outage). Fatal exceptions from manager.Error skip the whole retry cycle.
    private async Task TryRestartAfterErrorAsync(TorrentManager manager)
    {
        try
        {
            if (!_restartPolicies.TryGetValue(manager, out var policy))
            {
                policy = new TorrentRestartPolicy();
                _restartPolicies[manager] = policy;
            }

            var originalException = manager.Error?.Exception;
            if (TorrentRestartPolicy.IsFatalException(originalException))
            {
                policy.MarkFatal();
                DebugLog.Info($"watchdog '{manager.Torrent?.Name}': fatal error, no retries " +
                              $"({originalException?.GetType().Name}: {originalException?.Message})");
                return;
            }

            if (!policy.TryReserveNextAttempt(out var delay))
            {
                DebugLog.Info($"watchdog '{manager.Torrent?.Name}': retry budget exhausted after " +
                              $"{policy.AttemptsUsed} attempts");
                return;
            }

            var attemptNum = policy.AttemptsUsed; // reflects the attempt we just reserved
            DebugLog.Info($"watchdog '{manager.Torrent?.Name}': scheduling attempt {attemptNum}/{TorrentRestartPolicy.MaxAttempts} " +
                          $"in {delay.TotalSeconds:F0}s");
            await Task.Delay(delay);

            // If someone removed the torrent while we slept, bail out silently.
            if (_disposed || !_engine.Torrents.Any(t => ReferenceEquals(t, manager)))
                return;

            // Only act if the torrent is still stuck in Error. Any other state means either the
            // user intervened (Pause/Recheck) or it already recovered on its own.
            if (manager.State != TorrentState.Error)
            {
                DebugLog.Info($"watchdog '{manager.Torrent?.Name}': no longer in Error (now {manager.State}), skipping");
                return;
            }

            if (attemptNum >= 4)
            {
                DebugLog.Info($"watchdog '{manager.Torrent?.Name}': attempt {attemptNum}, doing HashCheck before start");
                try { await manager.HashCheckAsync(autoStart: true); }
                catch (Exception ex) { DebugLog.Error("watchdog HashCheckAsync", ex); }
            }
            else
            {
                DebugLog.Info($"watchdog '{manager.Torrent?.Name}': attempt {attemptNum}, StartAsync");
                try { await manager.StartAsync(); }
                catch (Exception ex) { DebugLog.Error("watchdog StartAsync", ex); }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Error("watchdog outer", ex);
        }
    }

    // Timer tick: emit one debug.log line per manager currently doing anything interesting.
    // Runs on a ThreadPool thread, off the mutation semaphore path. Each property access is
    // best-effort — MonoTorrent may throw briefly during teardown, so a per-torrent try/catch
    // logs the failure and moves on rather than killing the whole tick.
    private void LogDiagnosticsTick(object? _)
    {
        if (_disposed) return;
        try
        {
            // Snapshot to avoid enumerating engine.Torrents while add/remove mutates it.
            var snapshot = _engine.Torrents.ToArray();
            foreach (var m in snapshot)
            {
                try { LogOneTorrentDiagnostics(m); }
                catch (Exception ex) { DebugLog.Error("diag one-torrent", ex); }
            }
        }
        catch (Exception ex) { DebugLog.Error("diag tick", ex); }
    }

    private void LogOneTorrentDiagnostics(TorrentManager m)
    {
        var state = m.State;
        // Skip fully-idle torrents (Stopped/Paused) — no useful metrics, only log noise.
        // Error state is logged separately by OnManagerStateChanged when it flips there,
        // but we still emit a periodic line so we can see how long it's been stuck.
        if (state is TorrentState.Stopped or TorrentState.Paused)
            return;

        var name = ShortName(m.Torrent?.Name);
        var down = m.Monitor.DownloadRate;
        var up = m.Monitor.UploadRate;
        var progress = m.Progress;

        // Peers.Available = known-but-not-currently-connected peers from tracker/DHT/PeX.
        // OpenConnections = TCP sockets currently established. Both matter: connections
        // without a peer pool = we can't grow; peer pool without connections = we're not
        // making outbound attempts (half-open cap? blocked? firewall?).
        int available = -1, seeds = -1, leechs = -1, open = -1;
        try { available = m.Peers.Available; } catch { }
        try { seeds     = m.Peers.Seeds;     } catch { }
        try { leechs    = m.Peers.Leechs;    } catch { }
        try { open      = m.OpenConnections; } catch { }

        var dhtState = "?";
        try { dhtState = _engine.Dht.State.ToString(); } catch { }

        // Trackers are optional (magnet-only pre-metadata has none). Summarise as
        // "OK/total" counting tiers with at least one working tracker — anything more
        // detailed would blow up the log for public torrents with 30+ trackers.
        int trackerTiers = -1, trackerOk = -1;
        try
        {
            var tiers = m.TrackerManager?.Tiers;
            if (tiers is not null)
            {
                trackerTiers = tiers.Count;
                trackerOk = 0;
                foreach (var tier in tiers)
                {
                    try
                    {
                        if (tier.ActiveTracker?.Status == MonoTorrent.Trackers.TrackerState.Ok)
                            trackerOk++;
                    }
                    catch { }
                }
            }
        }
        catch { }

        DebugLog.Info(
            $"DIAG '{name}' state={state} prog={progress:F1}% " +
            $"D={Formatting.RateToHuman(down)} U={Formatting.RateToHuman(up)} " +
            $"conn={open} peers(avail/seeds/leech)={available}/{seeds}/{leechs} " +
            $"trackers={trackerOk}/{trackerTiers} dht={dhtState}");
    }

    private static string ShortName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "(no metadata)";
        return name.Length <= 60 ? name : name.Substring(0, 57) + "…";
    }

    // Below this cutoff, a Seeding state at that progress is treated as a MonoTorrent
    // bookkeeping bug — real 100 % has margin over 99 % (bytes-vs-piece rounding at most
    // pulls progress a fraction below 100). 99.0 % over 3 consecutive ticks (~90 s) means
    // several MB missing that MonoTorrent won't try to fetch. Anything closer to 100 % is
    // more likely to be legitimate.
    private const double PhantomSeedingProgressCutoff = 99.0;
    private const int PhantomSeedingHitsRequired = 3;

    // Stall watchdog tick: iterate current torrents, count consecutive phantom-Seeding hits
    // (state=Seeding with progress well under 100 %), and force a hash recheck once we've
    // seen the same condition three times in a row. Rechecks are fire-and-forget on a
    // background thread — HashCheckAsync on a 5+ GB torrent can take minutes and we don't
    // want the timer thread to block.
    private void StallCheckTick(object? _)
    {
        if (_disposed) return;
        try
        {
            var snapshot = _engine.Torrents.ToArray();
            foreach (var m in snapshot)
            {
                try { EvaluateOneForStall(m); }
                catch (Exception ex) { DebugLog.Error($"stall check '{ShortName(m.Torrent?.Name)}'", ex); }
            }
        }
        catch (Exception ex) { DebugLog.Error("stall check tick", ex); }
    }

    private void EvaluateOneForStall(TorrentManager m)
    {
        if (m.State != TorrentState.Seeding)
        {
            // Not Seeding this tick — reset the hit counter so a future phantom episode
            // starts from zero, not from stale accounting.
            _stallHits.Remove(m);
            return;
        }
        var progress = m.Progress;
        if (progress >= PhantomSeedingProgressCutoff)
        {
            _stallHits.Remove(m);
            return;
        }
        if (_stallRecheckInProgress.Contains(m))
            return; // already fixing this one, skip until it finishes

        _stallHits.TryGetValue(m, out var hits);
        hits++;
        _stallHits[m] = hits;
        var name = ShortName(m.Torrent?.Name);
        DebugLog.Info($"stall watchdog: '{name}' phantom Seeding (prog={progress:F1}%, hit {hits}/{PhantomSeedingHitsRequired})");

        if (hits < PhantomSeedingHitsRequired) return;
        _stallHits.Remove(m);
        _ = Task.Run(() => TryPhantomSeedingRecheckAsync(m));
    }

    // Force a hash recheck to reset MonoTorrent's stale bitfield: Stop, HashCheck, autoStart.
    // Runs off the timer thread on a background task. Failure is logged; on success the state
    // machine will emit its normal transitions (Hashing → Downloading or Seeding) which
    // OnManagerStateChanged already logs.
    private async Task TryPhantomSeedingRecheckAsync(TorrentManager m)
    {
        _stallRecheckInProgress.Add(m);
        var name = ShortName(m.Torrent?.Name);
        try
        {
            DebugLog.Info($"stall watchdog: '{name}' forcing StopAsync + HashCheckAsync");
            try { await m.StopAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception ex) { DebugLog.Error($"stall watchdog Stop '{name}'", ex); }
            await m.HashCheckAsync(autoStart: true);
            DebugLog.Info($"stall watchdog: '{name}' recheck done, state={m.State} prog={m.Progress:F1}%");
        }
        catch (Exception ex)
        {
            DebugLog.Error($"stall watchdog recheck '{name}'", ex);
        }
        finally
        {
            _stallRecheckInProgress.Remove(m);
        }
    }

    // Timer tick: for each Downloading/Seeding torrent, ask MonoTorrent to flush its
    // fast-resume snapshot to EngineSettings.FastResumeCacheDirectory. The method is
    // internal so we invoke by reflection — see field comments on _saveFastResumeMethod.
    private void FastResumeTick(object? _)
    {
        if (_disposed) return;
        try
        {
            var method = ResolveSaveFastResumeMethod();
            if (method is null) return;

            var snapshot = _engine.Torrents.ToArray();
            var saved = 0;
            foreach (var m in snapshot)
            {
                // Only bother with torrents that actually have progress worth resuming.
                // Downloading = mid-flight; Seeding = 100% but the fast-resume still saves
                // us the recheck on the next start.
                if (m.State is not TorrentState.Downloading and not TorrentState.Seeding)
                    continue;

                try
                {
                    var result = method.Invoke(m, null);
                    if (result is Task task)
                    {
                        // Don't block the timer thread; if the save is slow (SMR drive
                        // flush), wait up to 10 s so back-to-back ticks can't overlap.
                        if (!task.Wait(TimeSpan.FromSeconds(10)))
                            DebugLog.Info($"fast-resume '{ShortName(m.Torrent?.Name)}': save timed out (>10s)");
                        else
                            saved++;
                    }
                    else
                    {
                        saved++;
                    }
                }
                catch (TargetInvocationException tie)
                {
                    DebugLog.Error($"fast-resume '{ShortName(m.Torrent?.Name)}'", tie.InnerException ?? tie);
                }
                catch (Exception ex)
                {
                    DebugLog.Error($"fast-resume '{ShortName(m.Torrent?.Name)}'", ex);
                }
            }
            if (saved > 0)
                DebugLog.Info($"fast-resume: saved {saved}/{snapshot.Length} torrents");
        }
        catch (Exception ex) { DebugLog.Error("fast-resume tick", ex); }
    }

    private MethodInfo? ResolveSaveFastResumeMethod()
    {
        if (_fastResumeReflectionAttempted) return _saveFastResumeMethod;
        _fastResumeReflectionAttempted = true;

        // MonoTorrent 3.0.2 keeps SaveFastResumeAsync as internal — GetMethod with
        // NonPublic flag reaches it. Signature is expected to be () -> Task or
        // () -> Task<FastResume>; the return-type isn't checked, only param-less.
        var mi = typeof(TorrentManager).GetMethod(
            "SaveFastResumeAsync",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);

        if (mi is null && !_fastResumeUnavailableLogged)
        {
            _fastResumeUnavailableLogged = true;
            DebugLog.Info("fast-resume: TorrentManager.SaveFastResumeAsync() not found via reflection — " +
                          "MonoTorrent may have renamed it. Falling back to AutoSaveLoadFastResume on Stop only.");
        }
        _saveFastResumeMethod = mi;
        return mi;
    }

    // ClientEngine.DhtEngine is a non-public property returning an internal IDhtEngine;
    // both are unreachable from user code without reflection. We call StartAsync() on it
    // early so the UDP listener binds, cached nodes load (AutoSaveLoadDhtCache=true), and
    // bootstrap begins immediately at app launch — the alternative is MonoTorrent's
    // internal lazy path that only triggers on the first Downloading torrent. If reflection
    // ever breaks (MonoTorrent renames the member), we log once and silently fall back to
    // the original behaviour — no harm done.
    private async Task WarmupDhtAsync()
    {
        try
        {
            var prop = typeof(ClientEngine).GetProperty(
                "DhtEngine",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var dht = prop?.GetValue(_engine);
            if (dht is null)
            {
                DebugLog.Info("DHT warmup: ClientEngine.DhtEngine not accessible via reflection — MonoTorrent may have renamed it");
                return;
            }
            var t = dht.GetType();
            var start = t.GetMethod(
                "StartAsync",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null, types: Type.EmptyTypes, modifiers: null);
            if (start is null)
            {
                DebugLog.Info($"DHT warmup: no parameterless StartAsync() on {t.Name}");
                return;
            }
            var stateProp = t.GetProperty("State");
            var before = stateProp?.GetValue(dht);
            DebugLog.Info($"DHT warmup: engine={t.Name} state={before} — calling StartAsync()");
            var result = start.Invoke(dht, null);
            if (result is Task task) await task;
            var after = stateProp?.GetValue(dht);
            DebugLog.Info($"DHT warmup: StartAsync completed, state now={after}");
        }
        catch (TargetInvocationException tie)
        {
            DebugLog.Error("DHT warmup", tie.InnerException ?? tie);
        }
        catch (Exception ex)
        {
            DebugLog.Error("DHT warmup", ex);
        }
    }

    private async Task StartNextIfSlotFreeAsync()
    {
        // Start "wanted" torrents (Paused=false in our record) that aren't currently downloading,
        // until we hit the configured limit.
        while (CanStartMore())
        {
            TorrentManager? next = null;
            foreach (var kvp in _persistedByManager)
            {
                if (kvp.Value.Paused) continue;
                if (WasDownloading(kvp.Key.State)) continue;
                if (kvp.Key.State == TorrentState.Seeding) continue; // already done
                next = kvp.Key;
                break;
            }
            if (next is null) return;
            try { await next.StartAsync(); }
            catch { return; /* stop trying if start fails */ }
        }
    }

    private bool CanStartMore()
    {
        var active = 0;
        foreach (var mgr in _engine.Torrents)
        {
            if (WasDownloading(mgr.State)) active++;
        }
        return active < _currentSettings.MaxSimultaneousDownloads;
    }

    private static bool WasDownloading(TorrentState state) => state is
        TorrentState.Starting
        or TorrentState.Downloading
        or TorrentState.Hashing
        or TorrentState.Metadata
        or TorrentState.FetchingHashes;

    // MonoTorrent's engine.AddAsync can throw briefly after a previous RemoveAsync because
    // internal cleanup (fastresume flush, hash-table eviction, etc.) is asynchronous. The
    // exception message is "A manager for this torrent has already been registered".
    // Retry with escalating backoff before surfacing the error to the user. We match by
    // message rather than exception type so a future MonoTorrent refactor won't break us.
    // Real errors (corrupt file, disk permissions, invalid magnet) don't match this
    // message and surface on the first attempt.
    private bool IsAlreadyTracked(string source) =>
        _persistedByManager.Values.Any(v =>
            string.Equals(v.Source, source, StringComparison.OrdinalIgnoreCase));

    // Retry loop for the race window between engine.RemoveAsync completing and MonoTorrent's
    // async InfoHash cleanup finishing — the manager reference is gone (RemoveAsync polls
    // that), but the internal hash-lookup table lags. On fast Add-Remove-Add cycles the old
    // 4-attempt / ~1.5 s budget was too tight; the user hit the "already been registered"
    // error and had to click Add several times before it stuck. Bumped to 8 attempts with
    // an explicit backoff table topping out at ~10 s — long enough to outlast any realistic
    // internal cleanup, still short enough that a real permission or corrupt-file failure
    // surfaces before the user gives up. Duplicates by user-visible source (path / URI)
    // are caught upstream by IsAlreadyTracked, so anything reaching here is a genuine race.
    private static readonly int[] AddRetryBackoffsMs = new[] { 200, 400, 800, 1200, 1800, 2500, 3200 };
    private static async Task<TorrentManager> AddWithRetryAsync(Func<Task<TorrentManager>> add)
    {
        for (int i = 0; i < AddRetryBackoffsMs.Length; i++)
        {
            try { return await add(); }
            catch (Exception ex) when (IsAlreadyRegistered(ex))
            {
                DebugLog.Info($"AddWithRetry: 'already registered' on attempt {i + 1}, waiting {AddRetryBackoffsMs[i]} ms");
                await Task.Delay(AddRetryBackoffsMs[i]);
            }
        }
        return await add(); // final attempt: surface any remaining exception to the caller
    }

    private static bool IsAlreadyRegistered(Exception ex) =>
        ex.Message.Contains("already been registered", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("already registered", StringComparison.OrdinalIgnoreCase);

    // Per-torrent settings passed to _engine.AddAsync. Without an explicit settings
    // argument the engine assigns MonoTorrent's default per-torrent connection cap,
    // which is far below our engine-wide MaximumConnections=500 — so a single hot
    // torrent used to stall long before it approached the global budget. See the
    // PerTorrentMaxConnections / PerTorrentUploadSlots constants at the top for why
    // these numbers were chosen. DHT and PeX are pinned true explicitly so future
    // MonoTorrent default flips can't silently disable peer discovery on us.
    private static TorrentSettings BuildTorrentSettings() =>
        new TorrentSettingsBuilder
        {
            MaximumConnections = PerTorrentMaxConnections,
            UploadSlots = PerTorrentUploadSlots,
            AllowDht = true,
            AllowPeerExchange = true,
        }.ToSettings();
}
