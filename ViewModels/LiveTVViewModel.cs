using System.Collections.ObjectModel;
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
    [ObservableProperty] private Service? _selectedBouquet;
    [ObservableProperty] private Service? _selectedService;
    [ObservableProperty] private EpgEvent? _currentEvent;
    [ObservableProperty] private EpgEvent? _nextEvent;
    [ObservableProperty] private string _currentStreamUrl = "";
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _volume = 80;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _isFullscreen;
    [ObservableProperty] private string _searchText = "";

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
        }
        catch
        {
            // Non-fatal: now/next is supplementary info, channel list already loaded.
        }
    }

    [RelayCommand]
    private async Task SelectServiceAsync(Service service)
    {
        SelectedService = service;
        await PlayServiceAsync(service);
    }

    private async Task PlayServiceAsync(Service service)
    {
        await RunAsync(async () =>
        {
            // Get current EPG
            var epgEvents = await _api.GetEpgNowAsync(service.ServiceReference);
            var now = epgEvents.FirstOrDefault(e => e.IsCurrentlyAiring);
            var next = epgEvents.FirstOrDefault(e => e.BeginTime > DateTime.Now);
            CurrentEvent = now;
            NextEvent = next;

            // Get stream URL and trigger playback
            var url = _api.GetStreamUrl(service.ServiceReference);
            CurrentStreamUrl = url;
            IsPlaying = true;
            StreamRequested?.Invoke(this, url);
        });
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

    partial void OnSearchTextChanged(string value)
    {
        // Filter handled by converter in view
    }
}
