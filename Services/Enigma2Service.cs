using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Text;
using System.Web;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using DreamWin.Models;

namespace DreamWin.Services;

/// <summary>
/// Runs a sequence of async "fetch" operations against a remote device with the next
/// fetch starting the moment the previous one returns its raw result — but BEFORE that
/// raw result has been processed/parsed. Processing of item N then happens concurrently
/// with the network request for item N+1.
///
/// This exists because some receivers (cheap embedded HTTP servers on set-top boxes)
/// fall over or hang when hit with more than one concurrent request — even 2-4 at once,
/// gated by a SemaphoreSlim, was enough to crash the box. A true one-at-a-time request
/// queue avoids that while still overlapping CPU-bound processing (JSON/XML parsing,
/// LINQ filtering, etc.) with the next request's network latency, so we don't pay for
/// fetch and process fully sequentially either.
///
/// Order of results is preserved and matches the input order.
/// </summary>
public static class SequentialPipeline
{
    /// <summary>
    /// <paramref name="fetch"/> should do ONLY the network call (e.g. the GetStringAsync /
    /// GetAsync&lt;T&gt; call) and return the raw fetched value. <paramref name="process"/>
    /// takes that raw value and the original input item and turns it into the final
    /// result — this is where parsing/filtering/mapping should live, so it can run while
    /// the next item's <paramref name="fetch"/> is already in flight.
    /// </summary>
    public static async Task<List<TResult>> RunAsync<TItem, TRaw, TResult>(
        IEnumerable<TItem> items,
        Func<TItem, Task<TRaw>> fetch,
        Func<TItem, TRaw, Task<TResult>> process,
        Func<TItem, Exception, TResult?>? onError = null)
    {
        var itemList = items.ToList();
        var results = new List<TResult>(itemList.Count);
        if (itemList.Count == 0) return results;

        // Kick off the first request before we have anything to process. SafeFetch
        // guards against a fetch implementation that throws synchronously (rather than
        // via a faulted Task), so it doesn't escape uncaught here before the loop's own
        // try/catch applies.
        Task<TRaw> inFlight = SafeFetch(fetch, itemList[0]);

        for (int i = 0; i < itemList.Count; i++)
        {
            var currentItem = itemList[i];
            TRaw raw;
            try
            {
                raw = await inFlight; // wait for THIS request only — never more than one outstanding
            }
            catch (Exception ex)
            {
                if (onError == null) throw;
                var fallback = onError(currentItem, ex);
                if (fallback != null) results.Add(fallback);
                // Still need to advance to the next fetch even though this one failed.
                if (i + 1 < itemList.Count) inFlight = SafeFetch(fetch, itemList[i + 1]);
                continue;
            }

            // Start the NEXT request immediately — before processing the current
            // response — so the network round-trip for i+1 overlaps with processing
            // of i. The receiver only ever sees one request at a time either way.
            if (i + 1 < itemList.Count)
                inFlight = SafeFetch(fetch, itemList[i + 1]);

            results.Add(await process(currentItem, raw));
        }

        return results;
    }

    private static Task<TRaw> SafeFetch<TItem, TRaw>(Func<TItem, Task<TRaw>> fetch, TItem item)
    {
        try { return fetch(item); }
        catch (Exception ex) { return Task.FromException<TRaw>(ex); }
    }
}

public class Enigma2Service : IDisposable
{
    private HttpClient? _http;
    private HttpClientHandler? _handler;
    private ReceiverConfig? _config;
    private Guid _lastConfigId = Guid.Empty;
    private bool _disposed;

    // Minimum spacing between any two requests actually sent to the receiver. This is
    // separate from (and on top of) the one-request-at-a-time SequentialPipeline
    // constraint above — even single, fully sequential requests fired back-to-back the
    // instant a response lands were still enough to upset some receivers, so this adds
    // a short forced pause between the END of one request and the START of the next.
    private static readonly TimeSpan RequestCooldown = TimeSpan.FromMilliseconds(100);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private DateTime _lastRequestCompletedAt = DateTime.MinValue;

    public ReceiverConfig? CurrentConfig => _config;

    public void SetReceiver(ReceiverConfig config)
    {
        // Only rebuild if the config actually changed (avoids socket exhaustion on rapid calls)
        if (_lastConfigId == config.Id && _http != null) return;

        _http?.Dispose();
        _handler?.Dispose();

        _handler = new HttpClientHandler();

        // SEC-04: Opt-in per-receiver self-signed cert acceptance
        if (config.AcceptSelfSignedCert)
        {
            _handler.ServerCertificateCustomValidationCallback =
                (_, cert, _, errors) =>
                    errors == SslPolicyErrors.None ||
                    errors == SslPolicyErrors.RemoteCertificateNameMismatch ||
                    errors == SslPolicyErrors.RemoteCertificateChainErrors;
        }

        _http = new HttpClient(_handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        if (!string.IsNullOrEmpty(config.Username) && !string.IsNullOrEmpty(config.Password))
        {
            var cred = Convert.ToBase64String(
                Encoding.ASCII.GetBytes($"{config.Username}:{config.Password}"));
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", cred);
        }

        _config = config;
        _lastConfigId = config.Id;
        InvalidateServiceListCache();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http?.Dispose();
        _handler?.Dispose();
        _requestGate.Dispose();
    }

    private readonly JsonSerializerSettings _jsonSettings = new JsonSerializerSettings
    {
        MissingMemberHandling = MissingMemberHandling.Ignore,
        NullValueHandling = NullValueHandling.Include,
        Error = (sender, args) =>
        {
            Debug.WriteLine($"[Enigma2Service] JSON error at {args.ErrorContext.Path}: {args.ErrorContext.Error.Message}");
            args.ErrorContext.Handled = true;
        }
    };

    private async Task<T?> GetAsync<T>(string endpoint, Dictionary<string, string>? query = null) where T : class
    {
        var response = await GetRawResponseAsync(endpoint, query);
        try
        {
            return DeserializeWrapperOrDirect<T>(response);
        }
        catch (JsonException ex)
        {
            var preview = response.Length > 500 ? response[..500] + "..." : response;
            Debug.WriteLine($"[Enigma2Service] JSON parse error for {endpoint}: {ex.Message}\nRaw response: {preview}");
            throw;
        }
    }

    private T? DeserializeWrapperOrDirect<T>(string response) where T : class
    {
        var token = JToken.Parse(response);
        if (token.Type == JTokenType.Object)
        {
            var obj = (JObject)token;
            if (obj.Count == 1)
            {
                var inner = obj.Properties().First().Value;
                try
                {
                    var wrapped = inner.ToObject<T>(JsonSerializer.Create(_jsonSettings));
                    if (wrapped != null)
                        return wrapped;
                }
                catch
                {
                    // fallback to direct deserialize below
                }
            }
        }

        return JsonConvert.DeserializeObject<T>(response, _jsonSettings);
    }

    private static string SanitizeUrl(string url)
    {
        // Strip any embedded user:pass@ from URL before logging
        try
        {
            var u = new Uri(url);
            return u.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
                UriFormat.UriEscaped);
        }
        catch { return "[invalid url]"; }
    }

    /// <summary>
    /// Wraps a single HTTP call to the receiver with a global cooldown: if another
    /// request to this receiver completed less than <see cref="RequestCooldown"/> ago,
    /// waits out the remainder before sending this one. Combined with the one-request-
    /// at-a-time discipline in SequentialPipeline, this guarantees the receiver never
    /// sees back-to-back requests with no breathing room between them.
    /// </summary>
    private async Task<string> SendThrottledAsync(string url, CancellationToken token)
    {
        await _requestGate.WaitAsync(token);
        try
        {
            var elapsedSinceLast = DateTime.UtcNow - _lastRequestCompletedAt;
            if (elapsedSinceLast < RequestCooldown)
                await Task.Delay(RequestCooldown - elapsedSinceLast, token);

            return await _http!.GetStringAsync(url, token);
        }
        finally
        {
            _lastRequestCompletedAt = DateTime.UtcNow;
            _requestGate.Release();
        }
    }

    private async Task<string> GetRawResponseAsync(string endpoint, Dictionary<string, string>? query = null)
    {
        if (_config == null) throw new InvalidOperationException("No receiver configured");
        if (_http == null) throw new InvalidOperationException("HttpClient not initialised — call SetReceiver first");

        var url = $"{_config.BaseUrl}/api/{endpoint}";
        if (query?.Count > 0)
        {
            var qs = string.Join("&", query.Select(kv => $"{kv.Key}={HttpUtility.UrlEncode(kv.Value)}"));
            url += "?" + qs;
        }
#if DEBUG
        Debug.WriteLine($"[Enigma2Service] GET {SanitizeUrl(url)}");
#endif
        using var cts = new CancellationTokenSource(_http.Timeout);
        try
        {
            return await SendThrottledAsync(url, cts.Token);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Enigma2Service] HTTP error for {SanitizeUrl(url)}: {ex.Message}");
            throw;
        }
    }

    // The AutoTimer plugin exposes its OWN standalone web interface directly at the
    // receiver root (e.g. http://<receiver>/autotimer), completely separate from
    // OpenWebif's /web/ and /api/ namespaces — it is not part of OpenWebif at all, so
    // neither of those prefixes apply here. It returns XML.
    private async Task<string> GetAutoTimerPluginResponseAsync(string endpoint, Dictionary<string, string>? query = null)
    {
        if (_config == null) throw new InvalidOperationException("No receiver configured");
        if (_http == null) throw new InvalidOperationException("HttpClient not initialised — call SetReceiver first");

        var url = $"{_config.BaseUrl}/{endpoint}";
        if (query?.Count > 0)
        {
            var qs = string.Join("&", query.Select(kv => $"{kv.Key}={HttpUtility.UrlEncode(kv.Value)}"));
            url += "?" + qs;
        }
#if DEBUG
        Debug.WriteLine($"[Enigma2Service] GET (autotimer plugin/xml) {SanitizeUrl(url)}");
#endif
        using var cts = new CancellationTokenSource(_http.Timeout);
        try
        {
            return await SendThrottledAsync(url, cts.Token);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Enigma2Service] HTTP error for {SanitizeUrl(url)}: {ex.Message}");
            throw;
        }
    }

    private async Task<List<T>> GetListAsync<T>(string endpoint, Dictionary<string, string>? query, string arrayKey, string itemKey) where T : class
    {
        var response = await GetRawResponseAsync(endpoint, query);
        try
        {
            var token = JToken.Parse(response);
            var list = ParseListToken<T>(token, arrayKey, itemKey);
            Debug.WriteLine($"[Enigma2Service] {endpoint} parsed {list.Count} {typeof(T).Name} items (wrapper={arrayKey}/{itemKey})");
            return list;
        }
        catch (JsonException ex)
        {
            var preview = response.Length > 500 ? response[..500] + "..." : response;
            Debug.WriteLine($"[Enigma2Service] JSON parse error for {endpoint}: {ex.Message}\nRaw response: {preview}");
            return [];
        }
    }

    private async Task<List<EpgEvent>> GetEpgEventsAsync(string endpoint, Dictionary<string, string>? query = null)
    {
        var response = await GetRawResponseAsync(endpoint, query);
        try
        {
            var token = JToken.Parse(response);
            var events = ParseListToken<EpgEvent>(token, "events", "event");
            Debug.WriteLine($"[Enigma2Service] {endpoint} parsed {events.Count} EPG events");
            return events;
        }
        catch (JsonException ex)
        {
            var preview = response.Length > 500 ? response[..500] + "..." : response;
            Debug.WriteLine($"[Enigma2Service] JSON parse error for {endpoint}: {ex.Message}\nRaw response: {preview}");
            return [];
        }
    }

    private List<T> ParseListToken<T>(JToken token, string arrayKey, string itemKey) where T : class
    {
        if (token.Type == JTokenType.Array)
            return token.ToObject<List<T>>(JsonSerializer.Create(_jsonSettings)) ?? [];

        if (token.Type != JTokenType.Object)
            return [];

        var obj = (JObject)token;

        // First try the expected array wrapper.
        var listToken = obj[arrayKey];
        if (listToken != null)
        {
            if (listToken.Type == JTokenType.Array)
                return listToken.ToObject<List<T>>(JsonSerializer.Create(_jsonSettings)) ?? [];

            if (listToken.Type == JTokenType.Object)
            {
                var inner = listToken[itemKey];
                if (inner != null)
                {
                    if (inner.Type == JTokenType.Array)
                        return inner.ToObject<List<T>>(JsonSerializer.Create(_jsonSettings)) ?? [];

                    var single = inner.ToObject<T>(JsonSerializer.Create(_jsonSettings));
                    return single != null ? new List<T> { single } : [];
                }
            }
        }

        // Then try a direct item wrapper.
        var directToken = obj[itemKey];
        if (directToken != null)
        {
            if (directToken.Type == JTokenType.Array)
                return directToken.ToObject<List<T>>(JsonSerializer.Create(_jsonSettings)) ?? [];

            var single = directToken.ToObject<T>(JsonSerializer.Create(_jsonSettings));
            return single != null ? new List<T> { single } : [];
        }

        // Finally, search nested object wrappers recursively. This handles payloads like
        // { "movielist": { "movies": { "movie": [...] } } } or nested EPG wrappers.
        foreach (var property in obj.Properties())
        {
            if (property.Value.Type == JTokenType.Object || property.Value.Type == JTokenType.Array)
            {
                var nested = ParseListToken<T>(property.Value, arrayKey, itemKey);
                if (nested.Count > 0)
                    return nested;
            }
        }

        return [];
    }

    // ─── Services / Bouquets ───────────────────────────────────────────
    // Bouquets and services lists are static for the lifetime of a receiver connection —
    // the user isn't changing their channel configuration while watching TV. Caching them
    // session-wide eliminates the repeated getservices calls visible in the logs (once per
    // EPG open, once per prewarm, once per SelectBouquetAsync, etc.).
    // Both caches are invalidated together on SetReceiver() so a reconnect to a different
    // (or reconfigured) receiver always gets a fresh load.
    private List<Service>? _cachedBouquets;
    private readonly Dictionary<string, List<Service>> _cachedServices = new();

    public async Task<List<Service>> GetBouquetsAsync()
    {
        if (_cachedBouquets != null)
        {
            Debug.WriteLine($"[Enigma2Service] GetBouquetsAsync cache hit ({_cachedBouquets.Count} bouquets)");
            return _cachedBouquets;
        }
        var bouquets = await GetListAsync<Service>("getservices", null, "services", "service");
        Debug.WriteLine($"[Enigma2Service] GetBouquetsAsync returned {bouquets.Count} bouquets");
        _cachedBouquets = bouquets;
        return bouquets;
    }

    public async Task<List<Service>> GetServicesAsync(string bouquetRef)
    {
        if (_cachedServices.TryGetValue(bouquetRef, out var cached))
        {
            Debug.WriteLine($"[Enigma2Service] GetServicesAsync cache hit for {bouquetRef} ({cached.Count} services)");
            return cached;
        }
        var services = await GetListAsync<Service>("getservices", new() { ["sRef"] = bouquetRef }, "services", "service");
        Debug.WriteLine($"[Enigma2Service] GetServicesAsync({bouquetRef}) returned {services.Count} services");
        _cachedServices[bouquetRef] = services;
        return services;
    }

    public void InvalidateServiceListCache()
    {
        _cachedBouquets = null;
        _cachedServices.Clear();
        Debug.WriteLine("[Enigma2Service] Service list cache invalidated");
    }

    // ─── EPG ─────────────────────────────────────────────────────────
    public async Task<List<EpgEvent>> GetNowNextAsync(string bouquetRef)
    {
        return await GetEpgEventsAsync("epgnow", new() { ["bRef"] = bouquetRef });
    }

    public async Task<List<EpgEvent>> GetEpgForServiceAsync(string serviceRef, int hours = 24)
    {
        var now = DateTimeOffset.Now;
        return await GetEpgForServiceRangeAsync(serviceRef, now.DateTime, hours);
    }

    public async Task<List<EpgEvent>> GetEpgForServiceRangeAsync(string serviceRef, DateTime start, int hours = 24)
    {
        // No implicit lookback here — callers control the exact window via `start`
        // (e.g. GetEpgForGridDayCachedAsync already subtracts 30 minutes for "today" to
        // catch an in-progress show at the window boundary; other callers that don't
        // need that can pass an exact boundary).
        var begin = new DateTimeOffset(start).ToUnixTimeSeconds();
        var end = new DateTimeOffset(start.AddHours(hours)).ToUnixTimeSeconds();
        // NOTE: OpenWebif's epgservice handler (P_epgservice) reads its time-window
        // query parameters as "time" and "endTime" — NOT "begin"/"end" (those are the
        // parameter names for the unrelated timer endpoints like timeradd/timerdelete).
        //
        // CONFIRMED (via the actual model source, controllers/models/services.py):
        // P_epgservice calls getChannelEpg(sRef, begintime, endtime), which calls
        // Enigma2's native eEPGCache.lookupEvent(['IBDTSENCW', (ref, 0, begintime,
        // endtime)]) — query type 0 takes two ABSOLUTE Unix timestamps as a literal
        // [begin, end) range. So "endTime" here genuinely IS an absolute end-of-window
        // timestamp, unlike epgmulti/epgbouquet's getBouquetEpg (see
        // TryGetEpgMultiAsync), which is a DIFFERENT model function under the hood with
        // a different parameter convention for the same-named query argument. Do not
        // "fix" this to send a duration — that was tried and is wrong; this absolute-
        // timestamp form is correct for epgservice specifically.
        //
        // The zero-events-despite-data-existing symptom seen in practice is a separate,
        // real quirk of this endpoint/receiver combination (independently reported by
        // other OpenWebif users with this exact begin/end timestamp shape) and is NOT
        // fixed by changing the parameter semantics here. It's mitigated structurally
        // instead: GetEpgBouquetTimeWindowAsync and the grid/list view caches now both
        // prefer the bouquet-wide epgmulti result and only fall back to this endpoint
        // when epgmulti itself is unavailable, so this flaky path is exercised rarely.
        var events = await GetEpgEventsAsync("epgservice", new()
        {
            ["sRef"] = serviceRef,
            ["time"] = begin.ToString(),
            ["endTime"] = end.ToString()
        });

        if (events.Count == 0)
        {
            Debug.WriteLine($"[Enigma2Service] epgservice returned zero events for {serviceRef}, falling back to epgservicenow");
            events = await GetEpgNowAsync(serviceRef);
        }

        return events;
    }

    public async Task<List<EpgEvent>> GetEpgNowAsync(string serviceRef)
    {
        return await GetEpgEventsAsync("epgservicenow", new() { ["sRef"] = serviceRef });
    }

    public async Task<List<EpgEvent>> SearchEpgAsync(string query)
    {
        return await GetEpgEventsAsync("epgsearch", new() { ["search"] = query });
    }

    /// <summary>Fetches now+next for all services in a bouquet in one call using epgbouquet.</summary>
    public async Task<List<EpgEvent>> GetEpgBouquetNowNextAsync(string bouquetRef)
    {
        var events = await GetEpgEventsAsync("epgnow", new() { ["bRef"] = bouquetRef });
        if (events.Count == 0)
            events = await GetEpgEventsAsync("epgbouquet", new() { ["bRef"] = bouquetRef });
        return events;
    }

    /// <summary>Fetches EPG for all services in a bouquet for a given day (start = midnight).</summary>
    public Task<List<EpgEvent>> GetEpgBouquetTimeWindowAsync(string bouquetRef, DateTime start, int hours = 24)
        => GetEpgBouquetTimeWindowAsync(bouquetRef, null, start, hours);

    /// <summary>
    /// Same as above, but accepts an already-loaded service list for the bouquet to
    /// avoid a redundant GetServicesAsync round-trip when the caller (e.g. the EPG grid
    /// ViewModel) already has it loaded and bound. Pass null to have this method fetch
    /// the service list itself.
    /// </summary>
    public async Task<List<EpgEvent>> GetEpgBouquetTimeWindowAsync(string bouquetRef, List<Service>? knownServices, DateTime start, int hours = 24)
    {
        // NOTE: deliberately NOT using "epgbouquet" here, even though it accepts
        // begin/end query parameters. On this (and apparently many) receivers it
        // ignores them entirely and always returns just the single now-playing event
        // per channel — which is why the grid view only ever showed one event per
        // column regardless of the requested time window. Because it never returns
        // zero events (there's always "something now playing"), a `Count == 0`
        // fallback check can never trigger, so the broken behavior was silent.
        var windowStart = start.Date == DateTime.Today.Date
            ? start.AddMinutes(-30)   // today: slightly before to catch in-progress shows
            : start;                   // other days: exact midnight

        // "epgmulti" returns EPG for every service in a bouquet in a SINGLE request
        // (unlike "epgbouquet" above, which only ever returns the single now-playing
        // event regardless of params) — this replaces what used to be one HTTP
        // round-trip per channel with exactly one round-trip for the whole bouquet,
        // which is both faster and far gentler on receivers that can't tolerate many
        // requests in a session. "time" sets the window start and "endTime" sets the
        // window's DURATION IN SECONDS from there (not an end timestamp — see
        // TryGetEpgMultiAsync), so the requested window is genuinely server-side
        // bounded here, unlike epgbouquet. Not every OpenWebif build exposes epgmulti,
        // so we fall back to the per-channel path on failure or on a suspiciously-
        // empty result.
        var multiEvents = await TryGetEpgMultiAsync(bouquetRef, windowStart, hours);
        if (multiEvents != null)
        {
            Debug.WriteLine($"[Enigma2Service] GetEpgBouquetTimeWindowAsync: epgmulti got {multiEvents.Count} events for {bouquetRef} in one request");
            return multiEvents;
        }

        Debug.WriteLine($"[Enigma2Service] GetEpgBouquetTimeWindowAsync: epgmulti unavailable/empty for {bouquetRef}, falling back to per-channel fetch");

        var services = knownServices ?? await GetServicesAsync(bouquetRef);
        Debug.WriteLine($"[Enigma2Service] GetEpgBouquetTimeWindowAsync: fetching {services.Count} services sequentially (one request in flight at a time) for {bouquetRef}");

        // One HTTP request to the receiver at a time (it can't handle concurrent
        // requests) — but the next request is issued the moment the previous response
        // arrives, before that response is parsed, so parsing overlaps with the next
        // network round-trip instead of the whole thing running fully serially.
        var perServiceResults = await SequentialPipeline.RunAsync(
            items: services.Take(50),
            fetch: svc => GetEpgForServiceRangeAsync(svc.ServiceReference, windowStart, hours),
            process: (_, raw) => Task.FromResult(raw),
            onError: (svc, ex) =>
            {
                Debug.WriteLine($"[Enigma2Service] EPG fetch failed for {svc.ServiceReference}: {ex.Message}");
                return new List<EpgEvent>();
            });

        var events = perServiceResults.SelectMany(r => r).ToList();
        Debug.WriteLine($"[Enigma2Service] GetEpgBouquetTimeWindowAsync got {events.Count} total events for {bouquetRef}");

        return events;
    }

    /// <summary>
    /// Single-request fetch of EPG for every channel in a bouquet via "epgmulti".
    /// Returns null (rather than an empty list) on any failure or on a zero-event
    /// result, so the caller can distinguish "this receiver doesn't support/like
    /// epgmulti, fall back" from "epgmulti worked and the bouquet genuinely has no
    /// events right now" — and, per the no-error-caching rule, so a transient failure
    /// here is never mistaken by a caller for a real (cacheable) empty result.
    ///
    /// "bRef" is the only mandatory parameter. Both "time" and "endTime" are optional
    /// but, IMPORTANT, do not have the shape you'd guess from "epgservice"'s
    /// parameters of the same name:
    ///   - time: starting Unix timestamp (seconds since 1970) — same meaning as
    ///     elsewhere.
    ///   - endTime: NOT an end-of-window timestamp. It's a DURATION IN SECONDS (e.g.
    ///     7200 = 2 hours) measured from "time". Passing an absolute Unix timestamp
    ///     here (as if it meant "end at this moment") would ask for a window lasting
    ///     decades, not hours — silently fetching/returning vastly more data than
    ///     intended.
    /// </summary>
    private async Task<List<EpgEvent>?> TryGetEpgMultiAsync(string bouquetRef, DateTime start, int hours)
    {
        try
        {
            var events = await GetEpgEventsAsync("epgmulti", new()
            {
                ["bRef"] = bouquetRef,
                ["time"] = new DateTimeOffset(start).ToUnixTimeSeconds().ToString(),
                ["endTime"] = (hours * 3600).ToString()
            });
            return events.Count > 0 ? events : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Enigma2Service] epgmulti failed for {bouquetRef}: {ex.Message}");
            return null;
        }
    }

    // ─── Current Service ──────────────────────────────────────────────
    public async Task<CurrentEvent?> GetCurrentEventAsync()
    {
        return await GetAsync<CurrentEvent>("statusinfo");
    }

    // ─── Zapping ─────────────────────────────────────────────────────
    public async Task<bool> ZapAsync(string serviceRef)
    {
        var result = await GetAsync<GenericResponse>("zap", new() { ["sRef"] = serviceRef });
        return result?.Result ?? false;
    }

    // ─── Timers ──────────────────────────────────────────────────────
    public async Task<List<Models.Timer>> GetTimersAsync()
    {
        return await GetListAsync<Models.Timer>("timerlist", null, "timers", "timer");
    }

    public async Task<bool> AddTimerAsync(string serviceRef, string serviceName, long begin, long end, string name, string description = "", int afterEvent = 3)
    {
        var result = await GetAsync<GenericResponse>("timeradd", new()
        {
            ["sRef"] = serviceRef,
            ["begin"] = begin.ToString(),
            ["end"] = end.ToString(),
            ["name"] = name,
            ["description"] = description,
            ["eit"] = "0",
            ["disabled"] = "0",
            ["justplay"] = "0",
            ["afterevent"] = afterEvent.ToString()
        });
        return result?.Result ?? false;
    }

    public async Task<bool> AddTimerFromEpgAsync(EpgEvent evt, int marginBefore = 5, int marginAfter = 5)
    {
        return await AddTimerAsync(
            evt.ServiceRef,
            evt.ServiceName,
            evt.BeginTimestamp - marginBefore * 60,
            ((DateTimeOffset)evt.EndTime).ToUnixTimeSeconds() + marginAfter * 60,
            evt.Title,
            evt.ShortDesc
        );
    }

    public async Task<bool> DeleteTimerAsync(string serviceRef, long begin, long end)
    {
        var result = await GetAsync<GenericResponse>("timerdelete", new()
        {
            ["sRef"] = serviceRef,
            ["begin"] = begin.ToString(),
            ["end"] = end.ToString()
        });
        return result?.Result ?? false;
    }

    public async Task<bool> ToggleTimerAsync(Models.Timer timer)
    {
        var result = await GetAsync<GenericResponse>("timertogglestatus", new()
        {
            ["sRef"] = timer.ServiceRef,
            ["begin"] = timer.Begin.ToString(),
            ["end"] = timer.End.ToString()
        });
        return result?.Result ?? false;
    }

    // ─── Movies ──────────────────────────────────────────────────────
    public async Task<MovieList> GetMovieListWithBookmarksAsync(string? location = null)
    {
        var query = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(location)) query["dirname"] = location;
        return await GetAsync<MovieList>("movielist", query) ?? new MovieList();
    }

    public async Task<List<Movie>> GetMoviesAsync(string? location = null, int depth = 0, HashSet<string>? visited = null)
    {
        const int MaxDepth = 2;
        visited ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (depth > MaxDepth) return [];

        if (!string.IsNullOrEmpty(location))
        {
            if (!visited.Add(location)) return []; // already scanned — loop guard
        }

        if (string.IsNullOrEmpty(location))
        {
            var response = await GetAsync<MovieList>("movielist");
            if (response == null)
            {
                Debug.WriteLine("[Enigma2Service] GetMoviesAsync failed to deserialize movielist response");
                return [];
            }

            var allMovies = new List<Movie>();
            if (response.Movies.Any())
            {
                allMovies.AddRange(response.Movies);
            }

            var directoriesToScan = new List<string>();
            if (!string.IsNullOrEmpty(response.Directory))
            {
                var rootDirectory = response.Directory.TrimEnd('/');
                if (!string.IsNullOrEmpty(rootDirectory))
                    directoriesToScan.Add(rootDirectory);
            }

            foreach (var bookmark in response.Bookmarks)
            {
                var trimmedBookmark = bookmark.Trim();
                if (string.IsNullOrEmpty(trimmedBookmark))
                    continue;

                if (trimmedBookmark.StartsWith('.') || trimmedBookmark.Split('/').LastOrDefault()?.StartsWith('.') == true)
                {
                    Debug.WriteLine($"[Enigma2Service] GetMoviesAsync skipping hidden bookmark directory: {trimmedBookmark}");
                    continue;
                }

                var directoryPath = !string.IsNullOrEmpty(response.Directory)
                    ? response.Directory.TrimEnd('/') + "/" + trimmedBookmark.TrimStart('/')
                    : trimmedBookmark;

                directoriesToScan.Add(directoryPath);
            }

            foreach (var directoryPath in directoriesToScan.Distinct())
            {
                Debug.WriteLine($"[Enigma2Service] GetMoviesAsync scanning directory: {directoryPath}");
                var directoryMovies = await GetMoviesAsync(directoryPath, depth + 1, visited);
                foreach (var movie in directoryMovies)
                {
                    if (!string.IsNullOrEmpty(movie.Filename) && allMovies.All(m => !string.Equals(m.Filename, movie.Filename, StringComparison.OrdinalIgnoreCase)))
                        allMovies.Add(movie);
                }
            }

            if (allMovies.Any())
            {
                Debug.WriteLine($"[Enigma2Service] GetMoviesAsync returned {allMovies.Count} recordings from {directoriesToScan.Distinct().Count()} directories");
                return allMovies;
            }

            Debug.WriteLine("[Enigma2Service] GetMoviesAsync returned 0 recordings and no valid directories produced movies");
            return [];
        }

        var query = new Dictionary<string, string> { ["dirname"] = location };
        var movies = await GetListAsync<Movie>("movielist", query, "movies", "movie");
        Debug.WriteLine($"[Enigma2Service] GetMoviesAsync({location}) returned {movies.Count} recordings");
        return movies;
    }

    public async Task<bool> DeleteMovieAsync(string serviceRef)
    {
        var result = await GetAsync<GenericResponse>("moviedelete", new() { ["sRef"] = serviceRef });
        return result?.Result ?? false;
    }

    // ─── Streaming URLs ───────────────────────────────────────────────
    public string GetStreamUrl(string serviceRef)
    {
        if (_config == null) return "";
        // Enigma2 streaming port is always HTTP (port 8001 is an unencrypted MPEG-TS port).
        // StreamBaseUrl uses plain http:// intentionally — this is correct Enigma2 behaviour.
        return $"{_config.StreamBaseUrl}/{serviceRef}";
    }

    public string GetMovieStreamUrl(string filename)
    {
        if (_config == null) return "";
        var encoded = HttpUtility.UrlEncode(filename).Replace("+", "%20");
        return $"http://{_config.Host}:{_config.StreamingPort}/file?file={encoded}";
    }

    // ─── Power / Remote ──────────────────────────────────────────────
    public async Task<bool> PowerAsync(int action)
    {
        // 0=Toggle Standby, 1=Deep Standby, 2=Reboot, 3=Restart GUI, 4=Wakeup, 5=Standby
        var result = await GetAsync<GenericResponse>("powerstate", new() { ["newstate"] = action.ToString() });
        return result?.Result ?? false;
    }

    public async Task<bool> SendRemoteKeyAsync(int key)
    {
        var result = await GetAsync<GenericResponse>("remotecontrol", new() { ["command"] = key.ToString() });
        return result?.Result ?? false;
    }

    public async Task<bool> SetVolumeAsync(int vol)
    {
        var result = await GetAsync<GenericResponse>("vol", new() { ["set"] = $"set{vol}" });
        return result?.Result ?? false;
    }

    public async Task<SignalStatus?> GetSignalAsync()
    {
        return await GetAsync<SignalStatus>("tunersignal");
    }

    // ─── Screenshots ─────────────────────────────────────────────────
    public string GetScreenshotUrl() => $"{_config?.BaseUrl}/grab?format=jpg&r=720&o=false&n=false";

    // ─── Connectivity Check ───────────────────────────────────────────
    public async Task<bool> PingAsync()
    {
        try
        {
            var result = await GetAsync<GenericResponse>("powerstate");
            return result != null;
        }
        catch { return false; }
    }

    // ─── AutoTimers ───────────────────────────────────────────────────
    public async Task<List<AutoTimer>> GetAutoTimersAsync()
    {
        try
        {
            // NOTE: this is NOT an OpenWebif endpoint (so neither /api/ nor /web/
            // apply) — the AutoTimer plugin serves its own standalone interface
            // directly at the receiver root (http://<receiver>/autotimer), returning
            // XML, which is why this is parsed differently from the rest of this
            // service's JSON-based calls.
            var raw = await GetAutoTimerPluginResponseAsync("autotimer");
            Debug.WriteLine($"[Enigma2Service] AutoTimer raw XML response (first 500): {(raw.Length > 500 ? raw[..500] : raw)}");

            var list = new List<AutoTimer>();
            var doc = System.Xml.Linq.XDocument.Parse(raw);

            // Shape (per the autotimer plugin's XML; confirmed against both a real
            // receiver response and the plugin source). A rule can restrict to
            // MULTIPLE services, not just one — they show up as repeated child
            // elements. Different AutoTimer/OpenWebif versions have used slightly
            // different tag shapes for this over the years, so all known variants
            // are read defensively rather than assuming one fixed schema:
            //   <timer name="..." match="..." enabled="yes" id="1" from="21:00" to="00:30" ...>
            //     <e2service><e2servicereference>...</e2servicereference><e2servicename>...</e2servicename></e2service>
            //     <e2service><e2servicereference>...</e2servicereference><e2servicename>...</e2servicename></e2service>
            //     <!-- OR, on some versions: -->
            //     <serviceref>1:0:1:...:</serviceref>
            //     <serviceref>1:0:1:...:</serviceref>
            //     ...
            //   </timer>
            foreach (var timerEl in doc.Descendants("timer"))
            {
                var serviceRefs = new List<string>();
                var serviceNames = new List<string>();

                foreach (var svcEl in timerEl.Elements("e2service"))
                {
                    var sref = svcEl.Element("e2servicereference")?.Value;
                    if (string.IsNullOrWhiteSpace(sref)) continue;
                    serviceRefs.Add(sref);
                    serviceNames.Add(svcEl.Element("e2servicename")?.Value ?? "");
                }

                // Fallback schema: bare <serviceref>...</serviceref> elements with no
                // name attached (older/alternate plugin versions).
                if (serviceRefs.Count == 0)
                {
                    foreach (var srefEl in timerEl.Elements("serviceref"))
                    {
                        var sref = srefEl.Value;
                        if (string.IsNullOrWhiteSpace(sref)) continue;
                        serviceRefs.Add(sref);
                        serviceNames.Add("");
                    }
                }

                var at = new AutoTimer
                {
                    Id = timerEl.Attribute("id")?.Value ?? "",
                    Name = timerEl.Attribute("name")?.Value ?? "",
                    Match = timerEl.Attribute("match")?.Value ?? "",
                    Enabled = string.Equals(timerEl.Attribute("enabled")?.Value, "yes", StringComparison.OrdinalIgnoreCase),
                    From = timerEl.Attribute("from")?.Value ?? "",
                    To = timerEl.Attribute("to")?.Value ?? "",
                    SearchType = ParseAutoTimerSearchType(timerEl.Attribute("searchType")?.Value),
                    SearchCase = string.Equals(timerEl.Attribute("searchCase")?.Value, "sensitive", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
                    JustPlay = timerEl.Attribute("justplay")?.Value == "1" ? 1 : 0,
                    AvoidDuplicates = int.TryParse(timerEl.Attribute("avoidDuplicateDescription")?.Value, out var avd) ? avd : 0,
                    ServiceRefs = serviceRefs,
                    ServiceNames = serviceNames,
                    MaxDuration = int.TryParse(timerEl.Attribute("maxduration")?.Value, out var md) ? md : 0,
                    Tags = timerEl.Attribute("tags")?.Value ?? "",
                };
                list.Add(at);
            }

            Debug.WriteLine($"[Enigma2Service] GetAutoTimersAsync: parsed {list.Count} AutoTimers from XML");
            return list;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Enigma2Service] GetAutoTimersAsync error: {ex}");
            return [];
        }
    }

    // "searchType" in the autotimer XML is a word (e.g. "start", "end", "exact",
    // "partial"), not the numeric code the rest of this app's AutoTimer model uses
    // internally (0=Contains, 1=Exact, 2=Starts with, 3=Ends with — see
    // AutoTimer.SearchTypeText) — map the known values; default to 0 (partial/
    // contains) for anything unrecognised rather than throwing.
    private static int ParseAutoTimerSearchType(string? value) => value?.ToLowerInvariant() switch
    {
        "exact" => 1,
        "start" => 2,
        "end" => 3,
        "partial" or null or "" => 0,
        _ => 0
    };

    public async Task<bool> SaveAutoTimerAsync(AutoTimer timer)
    {
        try
        {
            // Same standalone-plugin situation as GetAutoTimersAsync — these are not
            // OpenWebif endpoints.
            var endpoint = string.IsNullOrEmpty(timer.Id) ? "autotimer/add" : "autotimer/edit";

            // searchType/searchCase/justplay are word-valued in the real AutoTimer
            // HTTP API (AutoTimerAddOrEditAutoTimerResource), not the numeric codes
            // this app uses internally for its own ComboBox SelectedIndex bindings —
            // map them here rather than sending raw ints, which the plugin would
            // silently fail to recognise (and fall back to its own defaults for).
            var searchTypeWord = timer.SearchType switch
            {
                1 => "exact",
                2 => "start",
                3 => "end",
                _ => "partial",
            };
            var searchCaseWord = timer.SearchCase == 1 ? "sensitive" : "insensitive";
            var justplayWord = timer.JustPlay == 1 ? "zap" : "record";

            var query = new Dictionary<string, string>
            {
                ["name"]    = timer.Name,
                ["match"]   = timer.Match,
                ["enabled"] = timer.Enabled ? "yes" : "no",
                ["searchType"]  = searchTypeWord,
                ["searchCase"]  = searchCaseWord,
                ["justplay"]    = justplayWord,
                ["avoidDuplicateDescription"] = timer.AvoidDuplicates.ToString(),
            };
            if (!string.IsNullOrEmpty(timer.Id)) query["id"] = timer.Id;
            // The plugin's real parameter names for the time window are
            // "timespanFrom"/"timespanTo" (HH:MM), NOT "from"/"to" — those only
            // exist as XML attribute names in the saved autotimer.xml, not as HTTP
            // parameters the add/edit endpoint understands. Sending "from"/"to" was
            // silently ignored, which is why time-window filters never actually took
            // effect despite being entered in the edit form.
            if (!string.IsNullOrEmpty(timer.From) && !string.IsNullOrEmpty(timer.To))
            {
                query["timespanFrom"] = timer.From;
                query["timespanTo"] = timer.To;
            }
            // "services" takes a COMMA-SEPARATED LIST of service references — the
            // plugin genuinely supports restricting one rule to multiple channels
            // (confirmed against AutoTimerAddOrEditAutoTimerResource's source, which
            // does get("services").split(',')). An empty/omitted value means "search
            // all channels".
            if (timer.ServiceRefs.Count > 0)
                query["services"] = string.Join(",", timer.ServiceRefs);
            if (timer.MaxDuration > 0) query["maxduration"] = timer.MaxDuration.ToString();
            if (!string.IsNullOrEmpty(timer.Tags)) query["tags"] = timer.Tags;

            // autotimer/add|edit returns XML whose exact success-indicator shape we
            // haven't confirmed against a live receiver — rather than guess at
            // parsing it (and risk silently reporting failure on success or vice
            // versa), treat a successful HTTP response (no exception thrown) as
            // success. GetAutoTimersAsync's subsequent reload is what the UI actually
            // relies on to reflect the true state either way.
            await GetAutoTimerPluginResponseAsync(endpoint, query);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Enigma2Service] SaveAutoTimerAsync error: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> DeleteAutoTimerAsync(string id)
    {
        try
        {
            await GetAutoTimerPluginResponseAsync("autotimer/remove", new Dictionary<string, string> { ["id"] = id });
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Enigma2Service] DeleteAutoTimerAsync error: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> ParseEpgForAutoTimersAsync()
    {
        try
        {
            await GetAutoTimerPluginResponseAsync("autotimer/parse");
            return true;
        }
        catch { return false; }
    }
}
