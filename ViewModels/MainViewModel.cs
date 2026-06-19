using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DreamWin.Models;
using DreamWin.Services;

namespace DreamWin.ViewModels;

public enum AppView { LiveTV, EPG, Timers, AutoTimers, Movies, Settings }

public partial class MainViewModel : BaseViewModel
{
    private readonly SettingsService _settingsService;
    public readonly Enigma2Service Api;

    [ObservableProperty] private AppView _currentView = AppView.LiveTV;
    [ObservableProperty] private ReceiverConfig? _activeReceiver;
    [ObservableProperty] private string _connectionStatus = "Not connected";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _windowTitle = "DreamWin";
    [ObservableProperty] private string _updateStatus = "Checking for updates...";
    [ObservableProperty] private bool _updateAvailable = false;
    [ObservableProperty] private GitHubRelease? _latestRelease;
    [ObservableProperty] private string _accentColor = "#6C63FF";

    public ObservableCollection<ReceiverConfig> Receivers { get; } = [];

    // Child ViewModels
    public LiveTVViewModel LiveTV { get; }
    public EpgViewModel Epg { get; }
    public TimersViewModel Timers { get; }
    public AutoTimersViewModel AutoTimers { get; }
    public MoviesViewModel Movies { get; }

    public MainViewModel(SettingsService settingsService, Enigma2Service api)
    {
        _settingsService = settingsService;
        Api = api;

        LiveTV = new LiveTVViewModel(api);
        Epg = new EpgViewModel(api);
        Timers = new TimersViewModel(api);
        AutoTimers = new AutoTimersViewModel(api);
        Movies = new MoviesViewModel(api);

        foreach (var r in settingsService.Settings.Receivers)
            Receivers.Add(r);

        settingsService.ActiveReceiverChanged += (_, r) => _ = ConnectAsync(r);

        LiveTV.StreamRequested += (_, url) => { };

        // Apply saved color scheme
        AccentColor = _settingsService.Settings.AccentColor;
        ThemeService.Apply(_settingsService.Settings);

        // Setup update service event subscriptions
        App.UpdateService.UpdateProgressChanged += (_, msg) =>
        {
            UpdateStatus = msg;
        };

        App.UpdateService.UpdateError += (_, msg) =>
        {
            UpdateStatus = $"Error: {msg}";
        };
    }

    public async Task InitializeAsync()
    {
        var receiver = _settingsService.GetActiveReceiver();
        if (receiver != null)
            await ConnectAsync(receiver);

        // Check for updates on startup if enabled and enough time has passed
        if (_settingsService.Settings.CheckForUpdates)
        {
            var lastCheck = _settingsService.Settings.LastUpdateCheckTime;
            var timeSinceLastCheck = DateTime.Now - lastCheck;

            // Only check if it's been more than 1 day since last check
            if (timeSinceLastCheck.TotalHours > 24)
            {
                // Run check in background, don't block initialization
                _ = CheckForUpdatesAsync();
            }
        }
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
                case AppView.AutoTimers: _ = AutoTimers.LoadAsync(); break;
                case AppView.Movies: _ = Movies.LoadAsync(); break;
            }
        }
    }

    [RelayCommand]
    private async Task StandbyAsync()
    {
        await Api.PowerAsync(5);
    }

    [RelayCommand]
    public async Task CheckForUpdatesAsync()
    {
        await RunAsync(async () =>
        {
            var release = await App.UpdateService.CheckForUpdatesAsync(
                App.SettingsService.Settings.IncludePrereleases);

            if (release != null)
            {
                LatestRelease = release;
                UpdateAvailable = true;
                UpdateStatus = $"Update available: {release.Name}";
                App.SettingsService.Settings.LastUpdateCheckTime = DateTime.Now;
                App.SettingsService.Settings.Save();
            }
            else
            {
                UpdateAvailable = false;
                UpdateStatus = "Already up to date";
                App.SettingsService.Settings.LastUpdateCheckTime = DateTime.Now;
                App.SettingsService.Settings.Save();
            }
        }, "Checking for updates");
    }

    [RelayCommand]
    public async Task InstallUpdateAsync()
    {
        if (LatestRelease == null)
            return;

        await RunAsync(async () =>
        {
            var downloadPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DreamWin", "updates");

            var installerPath = await App.UpdateService.DownloadUpdateAsync(LatestRelease, downloadPath);
            if (installerPath == null)
            {
                UpdateStatus = "Download failed";
                return;
            }

            var result = await App.UpdateService.InstallUpdateAsync(installerPath);
            if (result)
            {
                UpdateStatus = "Update installer started";
                // Optionally close the application after starting the installer
                // Application.Current.Shutdown();
            }
            else
            {
                UpdateStatus = "Installation failed";
            }
        }, "Installing update");
    }

    [RelayCommand]
    public void ApplyThemePreset(string presetName)
    {
        if (ThemeService.Presets.TryGetValue(presetName, out var p))
        {
            AccentColor = p.Accent;
            _settingsService.Settings.AccentColor = p.Accent;
            _settingsService.Settings.BgDeepColor = p.BgDeep;
            _settingsService.Settings.BgPanelColor = p.BgPanel;
            _settingsService.Settings.Save();
            ThemeService.Apply(p.Accent, p.BgDeep, p.BgPanel);
        }
    }

    [RelayCommand]
    public void ApplyCustomAccent(string hexColor)
    {
        if (string.IsNullOrWhiteSpace(hexColor)) return;
        AccentColor = hexColor;
        _settingsService.Settings.AccentColor = hexColor;
        _settingsService.Settings.Save();
        ThemeService.Apply(_settingsService.Settings);
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
