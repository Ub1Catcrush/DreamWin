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

    /// <summary>
    /// Normalizes an Enigma2 service reference down to its identity-bearing fields:
    /// type, namespace, transport stream ID, original network ID, and service ID
    /// (colon-delimited fields 0, 2, 3, 4, 5). The "flags" field (position 1) and any
    /// trailing fields (CAID/scrambling flags, custom name, etc. from position 6
    /// onward) are dropped.
    ///
    /// This exists because the SAME physical channel can come back with cosmetically
    /// different sRef strings depending on which OpenWebif endpoint produced it — e.g.
    /// "getservices" listing a bouquet's literal entry vs. "epgmulti"/"epgservice"
    /// reporting from the live EPG cache. Observed in practice: identical channel,
    /// differing only in the flags field's CAID/scrambling segment (e.g. "FFFF0000"
    /// vs "C00000"). A plain string-equality dictionary lookup between the two sources
    /// would silently miss in that case — looking like a successful warm with zero
    /// matching channels on the read side — so every bouquet-wide cache key/lookup in
    /// this class goes through this normalization instead of the raw string.
    /// </summary>
    private static string NormalizeServiceRef(string sRef)
    {
        var parts = sRef.Split(':');
        if (parts.Length < 6) return sRef; // not a recognizable sRef shape — pass through unchanged
        return string.Join(":", parts[0], parts[2], parts[3], parts[4], parts[5]);
    }

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

    // List view day (mirrors GridStart concept for the list). Both use the same day
    // navigation buttons in the header (Today / Tomorrow ▶) by sharing this property,
    // so the user's selected day is kept in sync when switching between the two views.
    [ObservableProperty] private DateTime _listDay = DateTime.Today;

    // True when the list view has events to show — used by the XAML empty-state
    // placeholder to avoid briefly flashing "Select a channel" while events are loading.
    public bool HasListEvents => !IsBusy && Events.Count > 0;

    // Snap to the nearest 30-minute boundary (used for NowLineY accuracy)
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
    // Keep HasListEvents consistent whenever IsBusy or Events.Count changes.
    // We can't use partial void OnIsBusyChanged here because IsBusy is declared
    // on BaseViewModel (not EpgViewModel), so its CommunityToolkit partial hook
    // belongs to that class. Overriding OnPropertyChanged is the correct way to
    // react to inherited observable property changes.
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(IsBusy) or nameof(Events))
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(HasListEvents)));
    }

    public event EventHandler<EpgEvent>? AddTimerRequested;

    public EpgViewModel(Enigma2Service api) => _api = api;

    public async Task LoadAsync()
    {
        var ct = NewLoadToken();
        await RunAsync(async () =>
        {
            // Both GetBouquetsAsync and GetServicesAsync are now cached session-wide in
            // Enigma2Service (invalidated on SetReceiver), so these calls are cheap after
            // the first load. We still guard against re-running SelectBouquetAsync when
            // Bouquets is already populated: that method triggers a GetServicesAsync +
            // LoadForServiceAsync which shows loading spinners and re-renders the list
            // even though nothing has changed — skipping it avoids the flash.
            var bouquets = await _api.GetBouquetsAsync();
            ct.ThrowIfCancellationRequested();

            if (!Bouquets.Any())
            {
                await OnUiAsync(() => {
                    Bouquets.Clear();
                    foreach (var b in bouquets.Where(b => b.IsBouquet))
                        Bouquets.Add(b);
                });

                if (Bouquets.Any())
                    await SelectBouquetAsync(Bouquets.First());
            }
            else if (SelectedBouquet != null && IsGridView && !GridRows.Any())
            {
                // Bouquets already loaded but grid is empty (first time in grid mode, or
                // after a cache clear) — just load the grid without re-running the full
                // bouquet/service fetch/select cycle.
                await LoadGridAsync();
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

    private async Task<List<EpgEvent>> GetEpgCachedAsync(string serviceRef, string? serviceName = null)
    {
        if (_epgCache.TryGetValue(serviceRef, out var cached) &&
            DateTime.Now - cached.LoadedAt < EpgCacheTtl)
        {
            Debug.WriteLine($"[EpgViewModel] Cache hit for {serviceRef}");
            return cached.Events;
        }

        // This view wants a 48h-from-now window, which normally spans exactly the
        // "today" + "tomorrow" entries the prewarmer already populates via a single
        // epgmulti request each. If both of those are warm and contain this channel
        // (by sref OR by name for DVB-S/C/T duplicates), use them directly instead of
        // making a redundant per-channel epgservice request for data we already have.
        var todaySlice = TryGetSlicedFromBouquetCache(serviceRef, DateTime.Today, serviceName);
        var tomorrowSlice = TryGetSlicedFromBouquetCache(serviceRef, DateTime.Today.AddDays(1), serviceName);
        if (todaySlice != null || tomorrowSlice != null)
        {
            var sliced = (todaySlice?.Events ?? []).Concat(tomorrowSlice?.Events ?? []).ToList();
            if (sliced.Count > 0)
            {
                Debug.WriteLine($"[EpgViewModel] Served {serviceRef} from bouquet-wide epgmulti cache ({sliced.Count} events), no per-channel request needed");
                _epgCache[serviceRef] = (sliced, DateTime.Now);
                return sliced;
            }
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

    /// <summary>
    /// Slices one channel's events out of an already-warmed bouquet-wide epgmulti
    /// cache entry for the given day, across every bouquet that's been warmed (a
    /// channel can appear in more than one bouquet the user has visited). Returns
    /// null — never throws — if nothing warm is found for that day.
    ///
    /// Falls back to name-matching when an exact normalized-sref lookup misses:
    /// the same channel can appear in a bouquet under multiple service references
    /// (DVB-S, DVB-C, DVB-T all carry the same programme schedule) — epgmulti may
    /// only return events under one of those srefs, but the EPG content is identical
    /// for all of them, so we serve it from whichever entry we have rather than falling
    /// through to a per-channel request that the receiver also can't satisfy.
    /// </summary>
    private (List<EpgEvent> Events, DateTime WarmedAt)? TryGetSlicedFromBouquetCache(
        string serviceRef, DateTime day, string? serviceName = null)
    {
        var dayKey = day.Date;
        var ttl = dayKey == DateTime.Today ? EpgCacheTtl : TimeSpan.FromHours(12);
        var normalizedRef = NormalizeServiceRef(serviceRef);

        foreach (var ((_, warmedDay), warmed) in _bouquetGridEpgCache)
        {
            if (warmedDay != dayKey) continue;
            if (DateTime.Now - warmed.LoadedAt >= ttl) continue;

            // Primary: exact normalized sref match
            if (warmed.ByService.TryGetValue(normalizedRef, out var sliced))
                return (sliced, warmed.LoadedAt);

            // Fallback: same channel name — handles DVB-S/C/T duplicates where the
            // bouquet lists the same channel under different srefs but epgmulti only
            // returned events under one of them (typically the satellite version).
            // We can safely reuse those events for the other transport variants since
            // the programme schedule is identical regardless of which transponder carries it.
            if (!string.IsNullOrEmpty(serviceName))
            {
                var nameMatch = warmed.ByService.Values
                    .FirstOrDefault(evts => evts.Count > 0 &&
                        string.Equals(evts[0].ServiceName, serviceName, StringComparison.OrdinalIgnoreCase));
                if (nameMatch != null)
                {
                    Debug.WriteLine($"[EpgViewModel] Bouquet cache: sref miss for \"{normalizedRef}\" but name matched \"{serviceName}\" — reusing {nameMatch.Count} events from same-named service");
                    return (nameMatch, warmed.LoadedAt);
                }
            }
        }

        return null;
    }

    // Separate cache for the GRID view, keyed by (serviceRef, day) rather than just
    // serviceRef. The grid can navigate to today and forward indefinitely (no
    // backward navigation — EPG is now/future only) — which the list view's single
    // "48h from now" cache above doesn't correctly cover (a fetch for one day would
    // otherwise either miss data for other days or silently overwrite the cache entry
    // every other day's view relies on, since that cache has no day component in its
    // key at all). "Today" still only needs ONE entry's worth of network traffic per
    // channel even though the visible grid window can later shift via -3h/+3h or
    // similar without re-fetching, since each entry covers a full calendar day.
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

        // EPG is now/future only — never fetch or cache a day that's already in the
        // past. The grid's own navigation (GridStepBackAsync) already can't reach
        // here, but this guards the same invariant at the cache layer directly so it
        // holds even if the app stays open across a midnight rollover while showing
        // what was "today".
        if (dayKey < DateTime.Today) return false;

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

        // epgmulti accepts both a starting "time" and an "endTime" duration, and
        // GetEpgBouquetTimeWindowAsync now sends both correctly bounding the request
        // to the desired window. This guard stays anyway as a defensive check: we
        // haven't been able to verify live that every receiver actually *honors* those
        // parameters for days other than "today" (vs. e.g. always returning "now
        // onward" regardless of what window was asked for). Require that at least some
        // returned events actually overlap the requested calendar day before trusting
        // this as a valid same-day result; if not, fall back to the per-channel path
        // instead for this specific day, which doesn't carry this risk.
        var dayEnd = dayKey.AddDays(1);
        var overlapsRequestedDay = events.Any(e => e.BeginTime < dayEnd && e.EndTime > dayKey);
        if (!overlapsRequestedDay)
        {
            Debug.WriteLine($"[EpgViewModel] epgmulti result for {bouquetRef} doesn't overlap requested day {dayKey:yyyy-MM-dd} — falling back to per-channel for this day");
            return false;
        }

        var byService = events
            .GroupBy(e => NormalizeServiceRef(e.ServiceRef))
            .ToDictionary(g => g.Key, g => g.ToList());

        _bouquetGridEpgCache[(bouquetRef, dayKey)] = (byService, DateTime.Now);
        Debug.WriteLine($"[EpgViewModel] epgmulti warmed grid cache for {bouquetRef} on {dayKey:yyyy-MM-dd}: {events.Count} events across {byService.Count} channels");
        // DIAGNOSTIC: dump a few of the exact sref strings epgmulti returned (raw,
        // pre-normalization) alongside their normalized form, so they can be compared
        // character-for-character against getservices's servicereference values in
        // the log if lookups still miss after normalization.
        foreach (var ev in events.GroupBy(e => e.ServiceRef).Take(5))
            Debug.WriteLine($"[EpgViewModel]   epgmulti sref sample: raw=\"{ev.Key}\" normalized=\"{NormalizeServiceRef(ev.Key)}\" ({ev.First().ServiceName})");
        return true;
    }

    private async Task<List<EpgEvent>> GetEpgForGridDayCachedAsync(string serviceRef, DateTime day, string? serviceName = null)
    {
        var dayKey = day.Date;

        // EPG is now/future only. This method is only ever reached with today or a
        // future day in practice (LoadGridAsync's navigation can no longer go
        // backward; PrewarmTodayAndTomorrowAsync only ever asks for today/tomorrow),
        // but guard it here too rather than relying solely on callers.
        if (dayKey < DateTime.Today) return [];

        // "Today" can still be actively changing (a show ending, the next one starting)
        // so it gets the normal short TTL. Future days are immutable once published,
        // so cache them indefinitely for the lifetime of the app — no point re-
        // fetching them every 180 seconds.
        var ttl = dayKey == DateTime.Today ? EpgCacheTtl : TimeSpan.FromHours(12);

        if (_gridEpgCache.TryGetValue((serviceRef, dayKey), out var cached) &&
            DateTime.Now - cached.LoadedAt < ttl)
        {
            Debug.WriteLine($"[EpgViewModel] Grid cache hit for {serviceRef} on {dayKey:yyyy-MM-dd}");
            return cached.Events;
        }

        // If a bouquet-wide epgmulti fetch already warmed this day, slice this channel's
        // events out of it — no per-channel request needed. Pass serviceName so the lookup
        // can fall back to name-matching when the sref differs across transport variants
        // (DVB-S/C/T all carry the same EPG schedule under different srefs).
        var fromBouquetCache = TryGetSlicedFromBouquetCache(serviceRef, dayKey, serviceName);
        if (fromBouquetCache != null)
        {
            _gridEpgCache[(serviceRef, dayKey)] = (fromBouquetCache.Value.Events, fromBouquetCache.Value.WarmedAt);
            return fromBouquetCache.Value.Events;
        }
        Debug.WriteLine($"[EpgViewModel] Grid: no bouquet-cache slice for sref \"{serviceRef}\" (normalized=\"{NormalizeServiceRef(serviceRef)}\") on {dayKey:yyyy-MM-dd} — falling back to epgservice");

        // Fetch slightly before midnight to catch any program already in progress at
        // the start of the day, through the following midnight.
        var windowStart = dayKey == DateTime.Today ? DateTime.Now.Date.AddMinutes(-30) : dayKey;

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

    [RelayCommand]
    private async Task ListDayTodayAsync()
    {
        ListDay = DateTime.Today;
        if (SelectedService != null)
            await LoadForServiceAsync(SelectedService.ServiceReference, SelectedService.ServiceName);
    }

    [RelayCommand]
    private async Task ListDayForwardAsync()
    {
        ListDay = ListDay.AddDays(1).Date;
        if (SelectedService != null)
            await LoadForServiceAsync(SelectedService.ServiceReference, SelectedService.ServiceName);
    }

    public async Task LoadForServiceAsync(string serviceRef, string serviceName)
    {
        CurrentServiceName = serviceName;
        IsSearchMode = false;
        var ct = NewLoadToken();
        var targetDay = ListDay.Date;
        await RunAsync(async () =>
        {
            // Fetch via the day-scoped grid cache — this returns today's or tomorrow's
            // full-day EPG using the same epgmulti-backed cache the grid view warmed,
            // so switching to list mode after the grid has loaded costs zero extra
            // requests. Falls back to a per-channel fetch if the cache is cold.
            var allEvents = await GetEpgForGridDayCachedAsync(serviceRef, targetDay, serviceName);
            ct.ThrowIfCancellationRequested();

            // Filter to just the selected day and deduplicate on (BeginTimestamp, Title)
            // — the same channel can appear under multiple srefs (DVB-S/C/T) and the
            // name-based cache fallback may have merged their identical events together.
            var dayStart = targetDay;
            var dayEnd = targetDay.AddDays(1);
            var seen = new HashSet<(long, string)>();
            var dayEvents = allEvents
                .Where(e => e.BeginTime < dayEnd && e.EndTime > dayStart)
                .Where(e => seen.Add((e.BeginTimestamp, e.Title ?? "")))
                .OrderBy(e => e.BeginTimestamp)
                .ToList();

            await OnUiAsync(() =>
            {
                Events.Clear();
                foreach (var e in dayEvents) Events.Add(e);
            });
            Debug.WriteLine($"[EpgViewModel] loaded {Events.Count} events for {serviceName} on {targetDay:yyyy-MM-dd}");
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
        // EPG is now/future only — never navigate to a day before today. (Previously
        // this allowed stepping up to 7 days into the past; past EPG data isn't
        // something we want to fetch, cache, or show.)
        var newStart = GridStart.AddDays(-1);
        if (newStart.Date < DateTime.Today) return;
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
                fetch: svc => GetEpgForGridDayCachedAsync(svc.ServiceReference, GridStart, svc.ServiceName),
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
                fetch: item => GetEpgForGridDayCachedAsync(item.Service.ServiceReference, item.Day, item.Service.ServiceName),
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
