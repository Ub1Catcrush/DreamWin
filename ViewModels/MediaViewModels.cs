using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DreamWin.Models;
using DreamWin.Services;

namespace DreamWin.ViewModels;

public class EpgGridRow
{
    public Service Service { get; set; } = new();
    public List<EpgEvent> Events { get; set; } = [];
    public DateTime GridStart { get; set; }
}

// A folder of recordings, shown as an expandable group in the Movies view.
public partial class MovieFolderGroup : ObservableObject
{
    public string FolderName { get; }
    public string DisplayName { get; }
    public ObservableCollection<Movie> Recordings { get; } = [];

    [ObservableProperty] private bool _isExpanded;

    public MovieFolderGroup(string folderName, string displayName)
    {
        FolderName = folderName;
        DisplayName = displayName;
    }
}

// ─── EPG ViewModel ────────────────────────────────────────────────────────────
public partial class EpgViewModel : BaseViewModel
{
    private readonly Enigma2Service _api;

    [ObservableProperty] private ObservableCollection<Service> _bouquets = [];
    [ObservableProperty] private ObservableCollection<Service> _services = [];
    [ObservableProperty] private Service? _selectedBouquet;
    [ObservableProperty] private Service? _selectedService;
    [ObservableProperty] private ObservableCollection<EpgEvent> _events = [];
    [ObservableProperty] private EpgEvent? _selectedEvent;
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private bool _isSearchMode;
    [ObservableProperty] private string _currentServiceName = "";

    // Grid constants: 54px per 30-minute slot → 1.8px per minute
    public const double PxPerMinute = 1.8;
    public const double SlotHeightPx = 54.0;  // 30 min × 1.8px

    // Grid view
    [ObservableProperty] private bool _isGridView;
    [ObservableProperty] private ObservableCollection<EpgGridRow> _gridRows = [];
    [ObservableProperty] private DateTime _gridStart = SnapToSlot(DateTime.Now);

    // Snap to the nearest 30-minute boundary at or before the given time
    private static DateTime SnapToSlot(DateTime t)
        => t.Date.AddHours(t.Hour).AddMinutes(t.Minute >= 30 ? 30 : 0);
    [ObservableProperty] private int _gridHours = 3;

    // Time slot labels: one per 30 min slot along Y axis
    public List<string> GridTimeSlots => Enumerable.Range(0, GridHours * 2)
        .Select(i => GridStart.AddMinutes(i * 30).ToString("HH:mm"))
        .ToList();

    // Total canvas height for all time slots
    public double GridTotalHeight => GridHours * 2 * SlotHeightPx;

    // Y position of the red "now" line
    public double NowLineY => Math.Max(0,
        Math.Min(GridTotalHeight, (DateTime.Now - GridStart).TotalMinutes * PxPerMinute));

    partial void OnGridStartChanged(DateTime value)
    {
        OnPropertyChanged(nameof(GridTimeSlots));
        OnPropertyChanged(nameof(GridTotalHeight));
        OnPropertyChanged(nameof(NowLineY));
    }
    partial void OnGridHoursChanged(int value)
    {
        OnPropertyChanged(nameof(GridTimeSlots));
        OnPropertyChanged(nameof(GridTotalHeight));
        OnPropertyChanged(nameof(NowLineY));
    }

    public event EventHandler<EpgEvent>? AddTimerRequested;

    public EpgViewModel(Enigma2Service api) => _api = api;

    public async Task LoadAsync()
    {
        var ct = NewLoadToken();
        await RunAsync(async () =>
        {
            var bouquets = await _api.GetBouquetsAsync();
            ct.ThrowIfCancellationRequested();
            await OnUiAsync(() => {
                Bouquets.Clear();
                foreach (var b in bouquets.Where(b => b.IsBouquet))
                    Bouquets.Add(b);
            });

            if (Bouquets.Any())
                await SelectBouquetAsync(Bouquets.First());
        }, "Loading EPG guide...");
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task SelectBouquetAsync(Service bouquet)
    {
        SelectedBouquet = bouquet;
        var ct = NewLoadToken();
        await RunAsync(async () =>
        {
            var services = await _api.GetServicesAsync(bouquet.ServiceReference);
            Services.Clear();
            foreach (var s in services.Where(s => !s.IsBouquet))
                Services.Add(s);
        }, $"Loading channels for {bouquet.ServiceName}...");

        if (Services.Any())
            await SelectServiceAsync(Services.First());

        if (IsGridView)
            await LoadGridAsync();
    }

    partial void OnSelectedServiceChanged(Service? value)
    {
        if (value != null)
            _ = SelectServiceAsync(value);
    }

    // EPG cache: keyed by service reference, expires after 5 minutes
    private readonly Dictionary<string, (List<EpgEvent> Events, DateTime LoadedAt)> _epgCache = new();
    private static readonly TimeSpan EpgCacheTtl = TimeSpan.FromMinutes(5);

    private async Task<List<EpgEvent>> GetEpgCachedAsync(string serviceRef)
    {
        if (_epgCache.TryGetValue(serviceRef, out var cached) &&
            DateTime.Now - cached.LoadedAt < EpgCacheTtl)
        {
            Debug.WriteLine($"[EpgViewModel] Cache hit for {serviceRef}");
            return cached.Events;
        }
        var events = await _api.GetEpgForServiceAsync(serviceRef, 48);
        _epgCache[serviceRef] = (events, DateTime.Now);
        return events;
    }

    public void ClearEpgCache() => _epgCache.Clear();

    [RelayCommand]
    private async Task SelectServiceAsync(Service service)
    {
        SelectedService = service;
        await LoadForServiceAsync(service.ServiceReference, service.ServiceName);
    }

    public async Task LoadForServiceAsync(string serviceRef, string serviceName)
    {
        CurrentServiceName = serviceName;
        IsSearchMode = false;
        var ct = NewLoadToken();
        await RunAsync(async () =>
        {
            var events = await GetEpgCachedAsync(serviceRef);
            ct.ThrowIfCancellationRequested();
            await OnUiAsync(() =>
            {
                Events.Clear();
                foreach (var e in events.OrderBy(e => e.BeginTimestamp))
                    Events.Add(e);
            });
            Debug.WriteLine($"[EpgViewModel] loaded {Events.Count} events for {serviceName}");
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

    [RelayCommand]
    private async Task ToggleGridViewAsync()
    {
        IsGridView = !IsGridView;
        if (IsGridView && SelectedBouquet != null)
            await LoadGridAsync();
    }

    [RelayCommand]
    private async Task GridStepBackAsync()
    {
        var newStart = GridStart.AddHours(-GridHours);
        // Don't go more than 7 days in the past
        if (newStart < DateTime.Now.AddDays(-7)) return;
        GridStart = newStart;
        if (SelectedBouquet != null) await LoadGridAsync();
    }

    [RelayCommand]
    private async Task GridStepForwardAsync()
    {
        GridStart = GridStart.AddHours(GridHours);
        if (SelectedBouquet != null) await LoadGridAsync();
    }

    [RelayCommand]
    private async Task GridNowAsync()
    {
        GridStart = SnapToSlot(DateTime.Now);
        if (SelectedBouquet != null) await LoadGridAsync();
    }

    private async Task LoadGridAsync()
    {
        if (SelectedBouquet == null) return;
        await RunAsync(async () =>
        {
            var events = await _api.GetEpgBouquetTimeWindowAsync(SelectedBouquet.ServiceReference, GridStart, GridHours);
            var grouped = events
                .GroupBy(e => e.ServiceRef)
                .ToDictionary(g => g.Key, g => g.OrderBy(e => e.BeginTimestamp).ToList());

            await OnUiAsync(() => GridRows.Clear());
            var gridRowsTemp = new System.Collections.Generic.List<EpgGridRow>();
            foreach (var svc in Services)
            {
                var evts = grouped.TryGetValue(svc.ServiceReference, out var list) ? list : [];
                foreach (var e in evts)
                {
                    // Y = time: 1.8px/min. Events that started before GridStart get negative offset
                    // → clamp to 0 but reduce height so the block ends at the correct time.
                    var startMinutes = (e.BeginTime - GridStart).TotalMinutes;
                    var endMinutes   = startMinutes + e.DurationSec / 60.0;
                    var clampedStart = Math.Max(0, startMinutes);
                    e.OffsetPx = clampedStart * EpgViewModel.PxPerMinute;
                    e.HeightPx = Math.Max(4, (endMinutes - clampedStart) * EpgViewModel.PxPerMinute - 2);
                }

                var row = new EpgGridRow { Service = svc, GridStart = GridStart, Events = evts };
                gridRowsTemp.Add(row);
            }
            await OnUiAsync(() => { foreach (var r in gridRowsTemp) GridRows.Add(r); });
        }, "Loading EPG grid...");
    }
}

// ─── Timers ViewModel ─────────────────────────────────────────────────────────
public partial class TimersViewModel : BaseViewModel
{
    private readonly Enigma2Service _api;

    [ObservableProperty] private ObservableCollection<Models.Timer> _timers = [];
    [ObservableProperty] private Models.Timer? _selectedTimer;
    [ObservableProperty] private bool _isEditing;

    // Edit form properties
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editServiceRef = "";
    [ObservableProperty] private string _editServiceName = "";
    [ObservableProperty] private DateTime _editBeginDate = DateTime.Today;
    [ObservableProperty] private string _editBeginTime = "20:00";
    [ObservableProperty] private string _editEndTime = "21:00";
    [ObservableProperty] private int _editAfterEvent = 3;
    [ObservableProperty] private bool _editEnabled = true;

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
        var r = System.Windows.MessageBox.Show(
            $"Delete timer '{timer.Name}'?\n\nThis cannot be undone.",
            "Confirm Delete", System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (r != System.Windows.MessageBoxResult.Yes) return;
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

    [RelayCommand]
    private void NewTimer()
    {
        SelectedTimer = null;
        EditName = "";
        EditServiceRef = "";
        EditServiceName = "";
        EditBeginDate = DateTime.Today;
        EditBeginTime = "20:00";
        EditEndTime = "21:00";
        EditAfterEvent = 3;
        EditEnabled = true;
        IsEditing = true;
    }

    [RelayCommand]
    private void EditTimer(Models.Timer timer)
    {
        SelectedTimer = timer;
        EditName = timer.Name;
        EditServiceRef = timer.ServiceRef;
        EditServiceName = timer.ServiceName;
        EditBeginDate = timer.BeginTime.Date;
        EditBeginTime = timer.BeginTime.ToString("HH:mm");
        EditEndTime = timer.EndTime.ToString("HH:mm");
        EditAfterEvent = timer.AfterEvent;
        EditEnabled = timer.Disabled == 0;
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        SelectedTimer = null;
    }

    [RelayCommand]
    private async Task SaveTimerAsync()
    {
        await RunAsync(async () =>
        {
            if (!TimeSpan.TryParse(EditBeginTime, out var beginTs) ||
                !TimeSpan.TryParse(EditEndTime, out var endTs))
            {
                StatusMessage = "Invalid time format. Use HH:MM.";
                HasError = true;
                return;
            }

            var begin = new DateTimeOffset(EditBeginDate.Date + beginTs).ToUnixTimeSeconds();
            var end = new DateTimeOffset(EditBeginDate.Date + endTs).ToUnixTimeSeconds();
            if (end <= begin) end = begin + 3600; // minimum 1h

            if (SelectedTimer != null)
            {
                // Delete old, create new (Enigma2 has no edit API)
                await _api.DeleteTimerAsync(SelectedTimer.ServiceRef, SelectedTimer.Begin, SelectedTimer.End);
            }

            await _api.AddTimerAsync(EditServiceRef, EditServiceName, begin, end, EditName, "", EditAfterEvent);
            IsEditing = false;
            SelectedTimer = null;
            await LoadAsync();
        }, "Saving timer...");
    }
}

// ─── Movies ViewModel ─────────────────────────────────────────────────────────
public partial class MoviesViewModel : BaseViewModel
{
    private readonly Enigma2Service _api;
    private List<Movie> _allMovies = [];
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private DateTime _lastLoaded = DateTime.MinValue;

    [ObservableProperty] private ObservableCollection<Movie> _movies = [];
    [ObservableProperty] private ObservableCollection<MovieFolderGroup> _folderGroups = [];
    [ObservableProperty] private Movie? _selectedMovie;
    [ObservableProperty] private string _currentStreamUrl = "";
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private double _volume = 80;
    [ObservableProperty] private double _positionPercent;
    [ObservableProperty] private string _positionText = "0:00:00";
    [ObservableProperty] private string _durationText = "0:00:00";
    [ObservableProperty] private AudioTrackInfo? _selectedAudioTrack;
    [ObservableProperty] private ObservableCollection<AudioTrackInfo> _audioTracks = [];

    public string TotalSizeText => _allMovies.Count == 0 ? "" :
        $"{_allMovies.Count} recordings · {_allMovies.Sum(m => m.Filesize) / 1024.0 / 1024 / 1024:0.0} GB";

    public event EventHandler<string>? StreamRequested;
    public event EventHandler? StopRequested;

    public MoviesViewModel(Enigma2Service api)
    {
        _api = api;
        Movies.CollectionChanged += (_, _) => OnPropertyChanged(nameof(TotalSizeText));
    }

    [RelayCommand]
    public async Task LoadAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && DateTime.Now - _lastLoaded < CacheTtl && _allMovies.Count > 0)
        {
            return;
        }

        var ct = NewLoadToken();
        await RunAsync(async () =>
        {
            // GetMoviesAsync() already recursively walks the root directory and every
            // bookmark (using their full device paths), deduplicating by filename as it goes.
            var root = await _api.GetMoviesAsync();
            ct.ThrowIfCancellationRequested();

            var all = root
                .OrderByDescending(m => m.RecordingTime)
                .ToList();

            _allMovies = all;
            _lastLoaded = DateTime.Now;

            await OnUiAsync(() =>
            {
                RebuildFolderGroups(all);
                OnPropertyChanged(nameof(TotalSizeText));
            });
        }, "Loading recordings...");
    }

    private void RebuildFolderGroups(List<Movie> all)
    {
        // Remember which folder was expanded (if any) so a refresh doesn't collapse
        // whatever the user had open.
        var previouslyExpanded = FolderGroups.FirstOrDefault(g => g.IsExpanded)?.FolderName;

        var groups = all
            .GroupBy(m => m.FolderName)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var displayName = string.IsNullOrEmpty(g.Key)
                    ? "Recordings"
                    : System.IO.Path.GetFileName(g.Key.TrimEnd('/'));
                var group = new MovieFolderGroup(g.Key, displayName);
                foreach (var movie in g.OrderByDescending(m => m.RecordingTime))
                    group.Recordings.Add(movie);
                return group;
            })
            .ToList();

        FolderGroups.Clear();
        foreach (var g in groups) FolderGroups.Add(g);

        // Expand whichever folder was previously open; otherwise default to the first one.
        var toExpand = (previouslyExpanded != null
            ? FolderGroups.FirstOrDefault(g => g.FolderName == previouslyExpanded)
            : null) ?? FolderGroups.FirstOrDefault();

        if (toExpand != null) toExpand.IsExpanded = true;
    }

    [RelayCommand]
    private void ToggleFolder(MovieFolderGroup group)
    {
        group.IsExpanded = !group.IsExpanded;
    }

    [RelayCommand]
    public void PlayMovie(Movie? movie)
    {
        if (movie == null) return;
        SelectedMovie = movie;
        var url = _api.GetMovieStreamUrl(movie.Filename);
        CurrentStreamUrl = url;
        IsPlaying = true;
        IsPaused = false;
        StreamRequested?.Invoke(this, url);
    }

    [RelayCommand]
    private void PauseResume() => IsPaused = !IsPaused;

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    [RelayCommand]
    private void Stop()
    {
        IsPlaying = false;
        IsPaused = false;
        StopRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void SeekBack() { /* handled in view via media player */ }

    [RelayCommand]
    private void SeekForward() { /* handled in view via media player */ }

    [RelayCommand]
    private async Task DeleteMovieAsync(Movie? movie)
    {
        if (movie == null) return;
        var r = System.Windows.MessageBox.Show(
            $"Permanently delete recording:\n'{movie.DisplayTitle}'?\n\nThis cannot be undone.",
            "Confirm Delete Recording", System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (r != System.Windows.MessageBoxResult.Yes) return;
        await RunAsync(async () =>
        {
            await _api.DeleteMovieAsync(movie.ServiceReference);
            _allMovies.Remove(movie);
            _lastLoaded = DateTime.MinValue; // invalidate cache
            await OnUiAsync(() =>
            {
                RebuildFolderGroups(_allMovies);
                OnPropertyChanged(nameof(TotalSizeText));
            });
        });
    }

    public override Task RetryAsync() => LoadAsync(forceRefresh: true);
}

// ─── AutoTimers ViewModel ──────────────────────────────────────────────────────
public partial class AutoTimersViewModel : BaseViewModel
{
    private readonly Enigma2Service _api;

    [ObservableProperty] private ObservableCollection<AutoTimer> _autoTimers = [];
    [ObservableProperty] private AutoTimer? _selectedAutoTimer;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _pluginAvailable = true;

    // Edit form properties
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editMatch = "";
    [ObservableProperty] private bool _editEnabled = true;
    [ObservableProperty] private int _editSearchType;
    [ObservableProperty] private int _editSearchCase;
    [ObservableProperty] private int _editJustPlay;
    [ObservableProperty] private int _editAvoidDuplicates;
    [ObservableProperty] private string _editFrom = "";
    [ObservableProperty] private string _editTo = "";
    [ObservableProperty] private string _editServiceRef = "";
    [ObservableProperty] private int _editMaxDuration;
    private string _editId = "";

    public AutoTimersViewModel(Enigma2Service api) => _api = api;

    [RelayCommand]
    public async Task LoadAsync()
    {
        await RunAsync(async () =>
        {
            var timers = await _api.GetAutoTimersAsync();
            await OnUiAsync(() => {
                AutoTimers.Clear();
                PluginAvailable = true;
                foreach (var t in timers.OrderBy(t => t.Name))
                    AutoTimers.Add(t);
            });
        }, "Loading AutoTimers...");
        // If no timers and no error, plugin may not be installed
    }

    [RelayCommand]
    private void EditAutoTimer(AutoTimer timer)
    {
        _editId = timer.Id;
        EditName = timer.Name;
        EditMatch = timer.Match;
        EditEnabled = timer.Enabled;
        EditSearchType = timer.SearchType;
        EditSearchCase = timer.SearchCase;
        EditJustPlay = timer.JustPlay;
        EditAvoidDuplicates = timer.AvoidDuplicates;
        EditFrom = timer.From;
        EditTo = timer.To;
        EditServiceRef = timer.ServiceRef;
        EditMaxDuration = timer.MaxDuration;
        SelectedAutoTimer = timer;
        IsEditing = true;
    }

    [RelayCommand]
    private void NewAutoTimer()
    {
        _editId = "";
        EditName = "";
        EditMatch = "";
        EditEnabled = true;
        EditSearchType = 0;
        EditSearchCase = 0;
        EditJustPlay = 0;
        EditAvoidDuplicates = 0;
        EditFrom = "";
        EditTo = "";
        EditServiceRef = "";
        EditMaxDuration = 0;
        SelectedAutoTimer = null;
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
    }

    [RelayCommand]
    private async Task SaveAutoTimerAsync()
    {
        await RunAsync(async () =>
        {
            var timer = new AutoTimer
            {
                Id = _editId,
                Name = EditName,
                Match = EditMatch,
                Enabled = EditEnabled,
                SearchType = EditSearchType,
                SearchCase = EditSearchCase,
                JustPlay = EditJustPlay,
                AvoidDuplicates = EditAvoidDuplicates,
                From = EditFrom,
                To = EditTo,
                ServiceRef = EditServiceRef,
                MaxDuration = EditMaxDuration,
            };
            await _api.SaveAutoTimerAsync(timer);
            IsEditing = false;
            await LoadAsync();
        }, "Saving AutoTimer...");
    }

    [RelayCommand]
    private async Task DeleteAutoTimerAsync(AutoTimer timer)
    {
        var r = System.Windows.MessageBox.Show(
            $"Delete AutoTimer rule '{timer.Name}'?",
            "Confirm Delete", System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (r != System.Windows.MessageBoxResult.Yes) return;
        await RunAsync(async () =>
        {
            await _api.DeleteAutoTimerAsync(timer.Id);
            AutoTimers.Remove(timer);
        });
    }

    [RelayCommand]
    private async Task ParseEpgAsync()
    {
        await RunAsync(async () => await _api.ParseEpgForAutoTimersAsync(), "Parsing EPG...");
    }
}
