using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DreamWin.Models;
using DreamWin.Services;

namespace DreamWin.ViewModels;

public enum AppView { LiveTV, EPG, Timers, Movies, Settings }

public partial class MainViewModel : BaseViewModel
{
    private readonly SettingsService _settingsService;
    public readonly Enigma2Service Api;

    [ObservableProperty] private AppView _currentView = AppView.LiveTV;
    [ObservableProperty] private ReceiverConfig? _activeReceiver;
    [ObservableProperty] private string _connectionStatus = "Not connected";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _windowTitle = "DreamWin";

    public ObservableCollection<ReceiverConfig> Receivers { get; } = [];

    // Child ViewModels
    public LiveTVViewModel LiveTV { get; }
    public EpgViewModel Epg { get; }
    public TimersViewModel Timers { get; }
    public MoviesViewModel Movies { get; }

    public MainViewModel(SettingsService settingsService, Enigma2Service api)
    {
        _settingsService = settingsService;
        Api = api;

        LiveTV = new LiveTVViewModel(api);
        Epg = new EpgViewModel(api);
        Timers = new TimersViewModel(api);
        Movies = new MoviesViewModel(api);

        foreach (var r in settingsService.Settings.Receivers)
            Receivers.Add(r);

        settingsService.ActiveReceiverChanged += (_, r) => _ = ConnectAsync(r);

        LiveTV.StreamRequested += (_, url) => { };
    }

    public async Task InitializeAsync()
    {
        var receiver = _settingsService.GetActiveReceiver();
        if (receiver != null)
            await ConnectAsync(receiver);
    }

    [RelayCommand]
    private async Task ConnectAsync(ReceiverConfig? receiver)
    {
        if (receiver == null) return;
        ActiveReceiver = receiver;
        Api.SetReceiver(receiver);
        WindowTitle = $"DreamWin — {receiver.Name}";

        await RunAsync(async () =>
        {
            var ok = await Api.PingAsync();
            IsConnected = ok;
            ConnectionStatus = ok ? $"Connected to {receiver.Name}" : "Connection failed";
            if (ok)
            {
                await LiveTV.LoadBouquetsAsync();
            }
        }, "Connecting...");
    }

    [RelayCommand]
    private void SwitchReceiver(ReceiverConfig receiver)
    {
        _settingsService.SetActiveReceiver(receiver);
    }

    [RelayCommand]
    private void Navigate(AppView view)
    {
        CurrentView = view;
        if (IsConnected)
        {
            switch (view)
            {
                case AppView.EPG: _ = Epg.LoadAsync(); break;
                case AppView.Timers: _ = Timers.LoadAsync(); break;
                case AppView.Movies: _ = Movies.LoadAsync(); break;
            }
        }
    }

    [RelayCommand]
    private async Task StandbyAsync()
    {
        await Api.PowerAsync(5);
    }

    public void AddReceiver(ReceiverConfig config)
    {
        _settingsService.AddReceiver(config);
        Receivers.Add(config);
    }

    public void UpdateReceiver(ReceiverConfig config)
    {
        _settingsService.UpdateReceiver(config);
        var idx = Receivers.IndexOf(Receivers.First(r => r.Id == config.Id));
        if (idx >= 0) { Receivers.RemoveAt(idx); Receivers.Insert(idx, config); }
    }

    public void RemoveReceiver(ReceiverConfig config)
    {
        _settingsService.RemoveReceiver(config);
        Receivers.Remove(config);
    }
}
