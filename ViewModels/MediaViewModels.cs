using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DreamWin.Models;
using DreamWin.Services;

namespace DreamWin.ViewModels;

// ─── EPG ViewModel ────────────────────────────────────────────────────────────
public partial class EpgViewModel : BaseViewModel
{
    private readonly Enigma2Service _api;

    [ObservableProperty] private ObservableCollection<EpgEvent> _events = [];
    [ObservableProperty] private EpgEvent? _selectedEvent;
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private bool _isSearchMode;
    [ObservableProperty] private string _currentServiceName = "";

    public event EventHandler<EpgEvent>? AddTimerRequested;

    public EpgViewModel(Enigma2Service api) => _api = api;

    public Task LoadAsync() => Task.CompletedTask;

    public async Task LoadForServiceAsync(string serviceRef, string serviceName)
    {
        CurrentServiceName = serviceName;
        IsSearchMode = false;
        await RunAsync(async () =>
        {
            var events = await _api.GetEpgForServiceAsync(serviceRef, 48);
            Events.Clear();
            foreach (var e in events.OrderBy(e => e.BeginTimestamp))
                Events.Add(e);
        }, $"Loading EPG for {serviceName}...");
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery)) return;
        IsSearchMode = true;
        await RunAsync(async () =>
        {
            var results = await _api.SearchEpgAsync(SearchQuery);
            Events.Clear();
            foreach (var e in results.OrderBy(e => e.BeginTimestamp))
                Events.Add(e);
        }, "Searching EPG...");
    }

    [RelayCommand]
    private void RequestAddTimer(EpgEvent evt)
    {
        AddTimerRequested?.Invoke(this, evt);
    }
}

// ─── Timers ViewModel ─────────────────────────────────────────────────────────
public partial class TimersViewModel : BaseViewModel
{
    private readonly Enigma2Service _api;

    [ObservableProperty] private ObservableCollection<Models.Timer> _timers = [];
    [ObservableProperty] private Models.Timer? _selectedTimer;

    public TimersViewModel(Enigma2Service api) => _api = api;

    [RelayCommand]
    public async Task LoadAsync()
    {
        await RunAsync(async () =>
        {
            var timers = await _api.GetTimersAsync();
            Timers.Clear();
            foreach (var t in timers.OrderBy(t => t.Begin))
                Timers.Add(t);
        }, "Loading timers...");
    }

    [RelayCommand]
    private async Task DeleteTimerAsync(Models.Timer timer)
    {
        await RunAsync(async () =>
        {
            await _api.DeleteTimerAsync(timer.ServiceRef, timer.Begin, timer.End);
            Timers.Remove(timer);
        });
    }

    [RelayCommand]
    private async Task ToggleTimerAsync(Models.Timer timer)
    {
        await RunAsync(async () =>
        {
            await _api.ToggleTimerAsync(timer);
            await LoadAsync();
        });
    }

    public async Task AddFromEpgAsync(EpgEvent evt)
    {
        await RunAsync(async () =>
        {
            await _api.AddTimerFromEpgAsync(evt);
            await LoadAsync();
        }, "Adding timer...");
    }
}

// ─── Movies ViewModel ─────────────────────────────────────────────────────────
public partial class MoviesViewModel : BaseViewModel
{
    private readonly Enigma2Service _api;

    [ObservableProperty] private ObservableCollection<Movie> _movies = [];
    [ObservableProperty] private Movie? _selectedMovie;
    [ObservableProperty] private string _currentStreamUrl = "";
    [ObservableProperty] private bool _isPlaying;

    public event EventHandler<string>? StreamRequested;

    public MoviesViewModel(Enigma2Service api) => _api = api;

    public async Task LoadAsync()
    {
        await RunAsync(async () =>
        {
            var movies = await _api.GetMoviesAsync();
            Movies.Clear();
            foreach (var m in movies.OrderByDescending(m => m.RecordingTime))
                Movies.Add(m);
        }, "Loading recordings...");
    }

    [RelayCommand]
    private void PlayMovie(Movie movie)
    {
        SelectedMovie = movie;
        var url = _api.GetMovieStreamUrl(movie.Filename);
        CurrentStreamUrl = url;
        IsPlaying = true;
        StreamRequested?.Invoke(this, url);
    }

    [RelayCommand]
    private async Task DeleteMovieAsync(Movie movie)
    {
        await RunAsync(async () =>
        {
            await _api.DeleteMovieAsync(movie.ServiceReference);
            Movies.Remove(movie);
        });
    }
}
