using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DreamWin.Models;
using DreamWin.Services;

namespace DreamWin.ViewModels;

public partial class LiveTVViewModel : BaseViewModel
{
    private readonly Enigma2Service _api;

    [ObservableProperty] private ObservableCollection<Service> _bouquets = [];
    [ObservableProperty] private ObservableCollection<Service> _services = [];
    [ObservableProperty] private ObservableCollection<EpgEvent> _nowNextEvents = [];
    [ObservableProperty] private ObservableCollection<NearbyChannelInfo> _nearbyNowNext = [];
    [ObservableProperty] private Service? _selectedBouquet;
    [ObservableProperty] private Service? _selectedService;
    [ObservableProperty] private EpgEvent? _currentEvent;
    [ObservableProperty] private EpgEvent? _nextEvent;
    [ObservableProperty] private string _currentStreamUrl = "";
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private double _volume = 80;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _isFullscreen;
    [ObservableProperty] private AudioTrackInfo? _selectedAudioTrack;
    [ObservableProperty] private ObservableCollection<AudioTrackInfo> _audioTracks = [];
    [ObservableProperty] private Service? _streamingService;  // channel currently playing (may differ from SelectedService)
    [ObservableProperty] private int _signalSnr;
    [ObservableProperty] private int _signalAgc;
    [ObservableProperty] private bool _hasSignal;
    [ObservableProperty] private string _searchText = "";

    private System.Windows.Threading.DispatcherTimer? _signalTimer;

    public string SignalLabel => HasSignal ? $"SNR {SignalSnr}%  AGC {SignalAgc}%" : "";

    partial void OnSignalSnrChanged(int value) => OnPropertyChanged(nameof(SignalLabel));
    partial void OnSignalAgcChanged(int value) => OnPropertyChanged(nameof(SignalLabel));

    private void StartSignalPolling()
    {
        _signalTimer?.Stop();
        _signalTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _signalTimer.Tick += async (_, _) =>
        {
            var sig = await _api.GetSignalAsync();
            if (sig != null)
            {
                SignalSnr = sig.Snr ?? 0;
                SignalAgc = sig.Agc ?? 0;
                HasSignal = sig.Snr is > 0;
            }
        };
        _signalTimer.Start();
    }

    public void StopSignalPolling() => _signalTimer?.Stop();

    public event EventHandler<string>? StreamRequested;
    public event EventHandler<bool>? FullscreenRequested;

    public LiveTVViewModel(Enigma2Service api)
    {
        _api = api;
    }

    public async Task LoadBouquetsAsync()
    {
        await RunAsync(async () =>
        {
            var bouquets = await _api.GetBouquetsAsync();
            Bouquets.Clear();
            foreach (var b in bouquets.Where(b => b.IsBouquet))
                Bouquets.Add(b);
            if (Bouquets.Any())
                await SelectBouquetAsync(Bouquets.First());
        });
    }

    [RelayCommand]
    private async Task SelectBouquetAsync(Service bouquet)
    {
        SelectedBouquet = bouquet;
        await RunAsync(async () =>
        {
            var services = await _api.GetServicesAsync(bouquet.ServiceReference);
            Services.Clear();
            foreach (var s in services.Where(s => !s.IsBouquet))
                Services.Add(s);
        });

        // Fire-and-forget: now/next is supplementary info for the channel list and must
        // never block the connect chain (MainViewModel.IsBusy) or the channel list display.
        _ = LoadNowNextInBackgroundAsync(bouquet.ServiceReference);
    }

    private async Task LoadNowNextInBackgroundAsync(string bouquetRef)
    {
        try
        {
            var nowNext = await _api.GetNowNextAsync(bouquetRef);
            NowNextEvents.Clear();
            foreach (var e in nowNext)
                NowNextEvents.Add(e);

            // Build nearby channel now/next for the fullscreen overlay
            RefreshNearbyNowNext();
        }
        catch
        {
            // Non-fatal: now/next is supplementary info, channel list already loaded.
        }
    }

    // Cancels any in-flight channel switch so rapid clicks only act on the last one
    private CancellationTokenSource? _switchCts;

    [RelayCommand]
    private async Task SelectServiceAsync(Service service)
    {
        SelectedService = service;
        await PlayServiceAsync(service);
    }

    private async Task PlayServiceAsync(Service service)
    {
        // Cancel previous switch — only the most recent click wins
        _switchCts?.Cancel();
        _switchCts = new CancellationTokenSource();
        var ct = _switchCts.Token;

        // ── Step 1: fire stream immediately (no EPG wait) ───────────────
        var url = _api.GetStreamUrl(service.ServiceReference);
        CurrentStreamUrl = url;
        IsPlaying = true;
        StreamingService = service;
        CurrentEvent = null;
        NextEvent = null;
        StreamRequested?.Invoke(this, url);
        StartSignalPolling();

        if (ct.IsCancellationRequested) return;

        // ── Step 2: load EPG in background, don't block stream start ────
        try
        {
            var epgEvents = await _api.GetEpgNowAsync(service.ServiceReference);
            if (ct.IsCancellationRequested) return;

            CurrentEvent = epgEvents.FirstOrDefault(e => e.IsCurrentlyAiring);
            NextEvent    = epgEvents.FirstOrDefault(e => e.BeginTime > DateTime.Now);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LiveTV] EPG load failed for {service.ServiceName}: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ToggleMute()
    {
        IsMuted = !IsMuted;
    }

    [RelayCommand]
    private void ToggleFullscreen()
    {
        IsFullscreen = !IsFullscreen;
        FullscreenRequested?.Invoke(this, IsFullscreen);
    }

    [RelayCommand]
    private void PauseResume()
    {
        IsPaused = !IsPaused;
    }

    [RelayCommand]
    private async Task ChannelUpAsync()
    {
        if (SelectedService == null || !Services.Any()) return;
        var idx = Services.IndexOf(SelectedService);
        if (idx < Services.Count - 1)
            await SelectServiceAsync(Services[idx + 1]);
    }

    [RelayCommand]
    private async Task ChannelDownAsync()
    {
        if (SelectedService == null || !Services.Any()) return;
        var idx = Services.IndexOf(SelectedService);
        if (idx > 0)
            await SelectServiceAsync(Services[idx - 1]);
    }

    public EpgEvent? GetNowNextForService(string serviceRef)
    {
        return NowNextEvents.FirstOrDefault(e => e.ServiceRef == serviceRef && e.IsCurrentlyAiring);
    }

    private void RefreshNearbyNowNext()
    {
        if (SelectedService == null || !Services.Any()) return;
        var idx = Services.IndexOf(SelectedService);
        if (idx < 0) return;

        var nearby = new List<NearbyChannelInfo>();
        for (int i = Math.Max(0, idx - 3); i < Math.Min(Services.Count, idx + 8); i++)
        {
            var svc = Services[i];
            if (svc == SelectedService) continue; // current channel shown separately

            var now = NowNextEvents.FirstOrDefault(e =>
                e.ServiceRef == svc.ServiceReference && e.IsCurrentlyAiring);
            if (now != null)
                nearby.Add(new NearbyChannelInfo
                {
                    ServiceName = svc.ServiceName,
                    NowTitle = now.Title,
                    NowTime = now.TimeRange
                });
        }
        NearbyNowNext.Clear();
        foreach (var n in nearby) NearbyNowNext.Add(n);
    }

    partial void OnSearchTextChanged(string value)
    {
        // Filter handled by converter in view
    }
}

// Shared between LiveTVViewModel and MoviesViewModel — not tied to either's namespace nesting.
public class AudioTrackInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    public override string ToString() => Name;
}

// A single row in the "nearby channels" now/next panel shown alongside the current channel.
public class NearbyChannelInfo
{
    public string ServiceName { get; set; } = "";
    public string NowTitle { get; set; } = "";
    public string NowTime { get; set; } = "";
}
