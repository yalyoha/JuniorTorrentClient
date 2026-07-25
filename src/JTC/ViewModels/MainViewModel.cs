using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using MonoTorrent.Client;
using JTC.Helpers;
using JTC.Services;

namespace JTC.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly TorrentService _service;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;

    public ObservableCollection<TorrentViewModel> Torrents { get; } = [];

    [ObservableProperty] private TorrentViewModel? _selectedTorrent;
    [ObservableProperty] private string _totalDownloadRateText = "—";
    [ObservableProperty] private string _totalUploadRateText = "—";
    [ObservableProperty] private int _totalPeers;

    partial void OnSelectedTorrentChanged(TorrentViewModel? oldValue, TorrentViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    public MainViewModel(TorrentService service, DispatcherQueue dispatcher)
    {
        _service = service;
        _dispatcher = dispatcher;
        _service.TorrentAdded += OnTorrentAdded;
        _service.TorrentRemoved += OnTorrentRemoved;

        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void OnTorrentAdded(object? sender, TorrentManager m)
    {
        // Snapshot the flag OUTSIDE the dispatcher hop — by the time the UI
        // thread runs, LoadStateAsync may have finished and IsRestoringState
        // flipped back to false, which would wrongly promote every restored
        // torrent to the top.
        var restoring = _service.IsRestoringState;
        _dispatcher.TryEnqueue(() =>
        {
            var vm = new TorrentViewModel(m, _dispatcher);
            if (restoring)
                Torrents.Add(vm);           // preserve persisted order at startup
            else
                Torrents.Insert(0, vm);     // user-added → top of the list
        });
    }

    private void OnTorrentRemoved(object? sender, TorrentManager m)
    {
        _dispatcher.TryEnqueue(() =>
        {
            var vm = Torrents.FirstOrDefault(t => t.Manager == m);
            if (vm is null) return;
            vm.Detach();
            Torrents.Remove(vm);
            if (SelectedTorrent == vm)
                SelectedTorrent = null;
        });
    }

    private void Tick()
    {
        long totalDown = 0, totalUp = 0;
        int totalPeers = 0;
        foreach (var vm in Torrents)
        {
            vm.Refresh();
            totalDown += vm.Manager.Monitor.DownloadRate;
            totalUp += vm.Manager.Monitor.UploadRate;
            totalPeers += vm.Manager.Peers.Available;
        }
        TotalDownloadRateText = Formatting.RateToHuman(totalDown);
        TotalUploadRateText = Formatting.RateToHuman(totalUp);
        TotalPeers = totalPeers;
    }

    public void Stop()
    {
        _timer.Stop();
    }
}
