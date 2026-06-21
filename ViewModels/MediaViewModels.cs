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
    [ObservableProperty] private bool _isGridView = true;  // default: grid view
    [ObservableProperty] private ObservableCollection<EpgGridRow> _gridRows = [];
    [ObservableProperty] private DateTime _gridStart = DateTime.Today;  // full day: midnight to midnight

    // Snap to the nearest 30-minute boundary at or before the given time
    // Snap time to the nearest 30-minute boundary (used for NowLineY accuracy)
    private static DateTime SnapToSlot(DateTime t)
        => t.Date.AddHours(t.Hour).AddMinutes(t.Minute >= 30 ? 30 : 0);
    [ObservableProperty] private int _gridHours = 24;  // show full day

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
            {
                // Select first bouquet — this will call LoadGridAsync when IsGridView=true
                await SelectBouquetAsync(Bouquets.First());
            }
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

    // EPG cache: keyed by service reference, expires after 180 seconds.
    private readonly Dictionary<string, (List<EpgEvent> Events, DateTime LoadedAt)> _epgCache = new();
    private static readonly TimeSpan EpgCacheTtl = TimeSpan.FromSeconds(180);

    private async Task<List<EpgEvent>> GetEpgCachedAsync(string serviceRef)
    {
        if (_epgCache.TryGetValue(serviceRef, out var cached) &&
            DateTime.Now - cached.LoadedAt < EpgCacheTtl)
        {
            Debug.WriteLine($"[EpgViewModel] Cache hit for {serviceRef}");
            return cached.Events;
        }

        List<EpgEvent> events;
        try
        {
            events = await _api.GetEpgForServiceAsync(serviceRef, 48);
        }
        catch (Exception ex)
        {
            // Don't cache the failure — surface it to the caller as-is so the UI can
            // show/handle the error, and let the NEXT call retry against the receiver
            // instead of silently returning (and re-returning, for a full TTL) nothing.
            Debug.WriteLine($"[EpgViewModel] GetEpgCachedAsync error for {serviceRef}: {ex.Message}");
            throw;
        }

        // A successful-but-empty result is a legitimate cacheable answer (the channel
        // really has no EPG data right now); only thrown exceptions are treated as
        // "error, don't cache" above.
        _epgCache[serviceRef] = (events, DateTime.Now);
        return events;
    }

    // Separate cache for the GRID view, keyed by (serviceRef, day) rather than just
    // serviceRef. The grid can navigate to arbitrary days — up to 7 days back, and
    // forward indefinitely — which the list view's single "48h from now" cache above
    // doesn't correctly cover (a fetch for one day would otherwise either miss data for
    // other days or silently overwrite the cache entry every other day's view relies on,
    // since that cache has no day component in its key at all). "Today" still only
    // needs ONE entry's worth of network traffic per channel even though the visible
    // grid window can later shift via -3h/+3h or similar without re-fetching, since each
    // entry covers a full calendar day.
    private readonly Dictionary<(string ServiceRef, DateTime Day), (List<EpgEvent> Events, DateTime LoadedAt)> _gridEpgCache = new();

    // Bouquet-wide cache populated by a single epgmulti request covering every channel
    // in a bouquet for one day at once — this is what lets LoadGridAsync avoid ever
    // touching the per-channel path in the common case. Keyed by (bouquetRef, day);
    // each entry holds the already-grouped-by-service-ref events for that whole
    // bouquet/day so individual channels can be sliced out of it with no extra request.
    private readonly Dictionary<(string BouquetRef, DateTime Day), (Dictionary<string, List<EpgEvent>> ByService, DateTime LoadedAt)> _bouquetGridEpgCache = new();

    /// <summary>
    /// Tries to satisfy a (bouquetRef, day) grid load with a single epgmulti request
    /// covering every channel in the bouquet at once, caching the grouped result.
    /// Returns false (without throwing or caching anything) if epgmulti isn't usable —
    /// the caller should fall back to the slower per-channel path in that case.
    /// </summary>
    private async Task<bool> TryWarmBouquetGridCacheAsync(string bouquetRef, DateTime day)
    {
        var dayKey = day.Date;
        var ttl = dayKey == DateTime.Today ? EpgCacheTtl : TimeSpan.FromHours(12);

        if (_bouquetGridEpgCache.TryGetValue((bouquetRef, dayKey), out var cached) &&
            DateTime.Now - cached.LoadedAt < ttl)
        {
            return true; // already warm
        }

        var windowStart = dayKey == DateTime.Today ? DateTime.Now.Date.AddMinutes(-30) : dayKey;
        List<EpgEvent> events;
        try
        {
            events = await _api.GetEpgBouquetTimeWindowAsync(bouquetRef, windowStart, 24);
        }
        catch (Exception ex)
        {
            // Don't cache failures — see GetEpgForGridDayCachedAsync's note below.
            Debug.WriteLine($"[EpgViewModel] epgmulti bouquet warm failed for {bouquetRef} on {dayKey:yyyy-MM-dd}: {ex.Message}");
            return false;
        }

        if (events.Count == 0)
        {
            // Ambiguous: could be a genuinely-empty bouquet, or epgmulti silently
            // unsupported/blocked on this receiver and GetEpgBouquetTimeWindowAsync's
            // own per-channel fallback also came back empty. Either way, don't cache a
            // zero-result bouquet-wide entry — that risks masking a real per-channel
            // result behind an empty grouped dictionary for the rest of the TTL. Let
            // the per-channel path (with its own caching) be the source of truth here.
            return false;
        }

        var byService = events
            .GroupBy(e => e.ServiceRef)
            .ToDictionary(g => g.Key, g => g.ToList());

        _bouquetGridEpgCache[(bouquetRef, dayKey)] = (byService, DateTime.Now);
        Debug.WriteLine($"[EpgViewModel] epgmulti warmed grid cache for {bouquetRef} on {dayKey:yyyy-MM-dd}: {events.Count} events across {byService.Count} channels");
        return true;
    }

    private async Task<List<EpgEvent>> GetEpgForGridDayCachedAsync(string serviceRef, DateTime day)
    {
        var dayKey = day.Date;
        // "Today" can still be actively changing (a show ending, the next one starting)
        // so it gets the normal short TTL. Past/future days are immutable once
        // published, so cache them indefinitely for the lifetime of the app — no point
        // re-fetching "yesterday" every 180 seconds.
        var ttl = dayKey == DateTime.Today ? EpgCacheTtl : TimeSpan.FromHours(12);

        if (_gridEpgCache.TryGetValue((serviceRef, dayKey), out var cached) &&
            DateTime.Now - cached.LoadedAt < ttl)
        {
            Debug.WriteLine($"[EpgViewModel] Grid cache hit for {serviceRef} on {dayKey:yyyy-MM-dd}");
            return cached.Events;
        }

        // If a bouquet-wide epgmulti fetch already warmed this day (see
        // TryWarmBouquetGridCacheAsync, called up-front by LoadGridAsync/
        // PrewarmTodayAndTomorrowAsync), slice this channel's events straight out of
        // it instead of making a per-channel request at all. A bouquet can be warmed
        // under more than one bouquet ref over the app's lifetime (e.g. a channel that
        // appears in two bouquets the user has visited), so check every warmed bouquet
        // for this day rather than just the currently-selected one.
        foreach (var ((_, warmedDay), warmed) in _bouquetGridEpgCache)
        {
            if (warmedDay != dayKey) continue;
            if (DateTime.Now - warmed.LoadedAt >= ttl) continue;
            if (warmed.ByService.TryGetValue(serviceRef, out var sliced))
            {
                _gridEpgCache[(serviceRef, dayKey)] = (sliced, warmed.LoadedAt);
                return sliced;
            }
        }

        // Fetch slightly before midnight to catch any program already in progress at
        // the start of the day, through the following midnight.
        var windowStart = dayKey == DateTime.Today ? DateTime.Now.Date.AddMinutes(-30) : dayKey;

        // Deliberately NOT catching here: a failure must propagate to the caller
        // uncached, so the next request for this (service, day) retries against the
        // receiver instead of being served the same failure-as-empty-list for the rest
        // of the TTL. LoadGridAsync's SequentialPipeline.onError and
        // PrewarmTodayAndTomorrowAsync's own try/catch are what turn a thrown
        // exception into a safely-handled empty result for THEIR callers — this method
        // itself must stay honest about what actually happened.
        var events = await _api.GetEpgForServiceRangeAsync(serviceRef, windowStart, 24);
        _gridEpgCache[(serviceRef, dayKey)] = (events, DateTime.Now);
        return events;
    }

    public void ClearEpgCache()
    {
        _epgCache.Clear();
        _gridEpgCache.Clear();
        _bouquetGridEpgCache.Clear();
    }

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
        var newStart = GridStart.AddDays(-1);
        if (newStart < DateTime.Now.AddDays(-7)) return;
        GridStart = newStart.Date;
        if (SelectedBouquet != null) await LoadGridAsync();
    }

    [RelayCommand]
    private async Task GridStepForwardAsync()
    {
        GridStart = GridStart.AddDays(1).Date;
        if (SelectedBouquet != null) await LoadGridAsync();
    }

    [RelayCommand]
    private async Task GridNowAsync()
    {
        GridStart = DateTime.Today;
        if (SelectedBouquet != null) await LoadGridAsync();
    }

    private async Task LoadGridAsync()
    {
        if (SelectedBouquet == null) return;
        await RunAsync(async () =>
        {
            var windowStart = GridStart.Date == DateTime.Today.Date
                ? GridStart.AddMinutes(-30)   // today: slightly before to catch in-progress shows
                : GridStart;                   // other days: exact midnight
            var windowEnd = GridStart.AddHours(GridHours);

            // Try to satisfy the WHOLE grid (every channel, this one day) with a
            // single epgmulti request first. When it succeeds, every per-channel fetch
            // below becomes a cache hit — zero further requests to the receiver for
            // this load. Falls back silently to the existing per-channel path when
            // epgmulti isn't supported/usable on this receiver.
            await TryWarmBouquetGridCacheAsync(SelectedBouquet.ServiceReference, GridStart);

            // Fetch each channel's EPG for this specific day through the day-scoped
            // grid cache. One request to the receiver at a time — it crashes under
            // concurrent requests — but the next channel's request is started the
            // instant the previous one's response arrives, before that response is
            // filtered/sorted, so processing overlaps with the next network round-trip
            // rather than the whole sequence running fully serially. Re-navigating to a
            // previously-viewed day (e.g. Today -> Tomorrow -> Today) is then served
            // from cache.
            var serviceList = Services.ToList();
            var perServiceResults = await SequentialPipeline.RunAsync(
                items: serviceList,
                fetch: svc => GetEpgForGridDayCachedAsync(svc.ServiceReference, GridStart),
                process: (svc, all) => Task.FromResult((Service: svc, Events: all
                    .Where(e => e.EndTime > windowStart && e.BeginTime < windowEnd)
                    .OrderBy(e => e.BeginTimestamp)
                    .ToList())),
                onError: (svc, ex) =>
                {
                    Debug.WriteLine($"[EpgViewModel] Grid fetch failed for {svc.ServiceReference}: {ex.Message}");
                    return (Service: svc, Events: new List<EpgEvent>());
                });

            await OnUiAsync(() => GridRows.Clear());
            var gridRowsTemp = new System.Collections.Generic.List<EpgGridRow>();
            foreach (var (svc, evts) in perServiceResults)
            {
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

    // Background prefetch: warms the grid cache for TODAY and TOMORROW for the default
    // bouquet, so that when the user actually opens the EPG tab, those two days (by far
    // the most commonly viewed) render instantly from cache instead of waiting on a
    // round trip per channel. Deliberately does NOT use RunAsync/IsBusy — this runs
    // silently in the background and must not touch any UI-bound state (Bouquets,
    // Services, SelectedBouquet, GridRows, busy spinners, error banners) since the user
    // hasn't navigated to the EPG view yet and may never need to see any of this.
    // Other days remain purely on-demand, fetched only when actually navigated to.
    public async Task PrewarmTodayAndTomorrowAsync()
    {
        try
        {
            var bouquets = await _api.GetBouquetsAsync();
            var firstBouquet = bouquets.FirstOrDefault(b => b.IsBouquet);
            if (firstBouquet == null) return;

            var services = await _api.GetServicesAsync(firstBouquet.ServiceReference);
            var realServices = services.Where(s => !s.IsBouquet).ToList();
            if (realServices.Count == 0) return;

            var today = DateTime.Today;
            var tomorrow = today.AddDays(1);

            // Try epgmulti first: one request per day (2 total) covering every channel
            // in the bouquet, instead of one request per (channel, day) pair. Only
            // whichever day(s) epgmulti couldn't warm fall back to the slower
            // per-channel path below.
            var warmedToday = await TryWarmBouquetGridCacheAsync(firstBouquet.ServiceReference, today);
            var warmedTomorrow = await TryWarmBouquetGridCacheAsync(firstBouquet.ServiceReference, tomorrow);

            var daysNeedingFallback = new List<DateTime>();
            if (!warmedToday) daysNeedingFallback.Add(today);
            if (!warmedTomorrow) daysNeedingFallback.Add(tomorrow);

            if (daysNeedingFallback.Count == 0)
            {
                Debug.WriteLine($"[EpgViewModel] Prewarm complete via epgmulti: {realServices.Count} channels x 2 days for {firstBouquet.ServiceName} in 2 requests");
                return;
            }

            // Same one-at-a-time constraint as LoadGridAsync — the receiver can't
            // handle concurrent requests — but each next (channel, day) request starts
            // as soon as the previous response arrives, overlapping with caching/
            // processing of that previous response.
            var prewarmItems = realServices.SelectMany(svc => daysNeedingFallback
                .Select(day => (Service: svc, Day: day)));

            await SequentialPipeline.RunAsync<(Service Service, DateTime Day), List<EpgEvent>, bool>(
                items: prewarmItems,
                fetch: item => GetEpgForGridDayCachedAsync(item.Service.ServiceReference, item.Day),
                process: (_, _) => Task.FromResult(true),
                onError: (item, ex) =>
                {
                    // Best-effort prefetch — a failure here just means that channel/day
                    // will fetch normally (and visibly) when the user gets to it.
                    Debug.WriteLine($"[EpgViewModel] Prewarm failed for {item.Service.ServiceReference} on {item.Day:yyyy-MM-dd}: {ex.Message}");
                    return true;
                });

            Debug.WriteLine($"[EpgViewModel] Prewarm complete: {realServices.Count} channels x {daysNeedingFallback.Count} day(s) via per-channel fallback for {firstBouquet.ServiceName}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EpgViewModel] PrewarmTodayAndTomorrowAsync error: {ex}");
        }
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
