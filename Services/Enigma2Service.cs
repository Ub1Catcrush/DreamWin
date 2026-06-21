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

public class Enigma2Service : IDisposable
{
    private HttpClient? _http;
    private HttpClientHandler? _handler;
    private ReceiverConfig? _config;
    private Guid _lastConfigId = Guid.Empty;
    private bool _disposed;

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
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http?.Dispose();
        _handler?.Dispose();
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
            return await _http.GetStringAsync(url, cts.Token);
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
    public async Task<List<Service>> GetBouquetsAsync()
    {
        var bouquets = await GetListAsync<Service>("getservices", null, "services", "service");
        Debug.WriteLine($"[Enigma2Service] GetBouquetsAsync returned {bouquets.Count} bouquets");
        return bouquets;
    }

    public async Task<List<Service>> GetServicesAsync(string bouquetRef)
    {
        var services = await GetListAsync<Service>("getservices", new() { ["sRef"] = bouquetRef }, "services", "service");
        Debug.WriteLine($"[Enigma2Service] GetServicesAsync({bouquetRef}) returned {services.Count} services");
        return services;
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

    public async Task<List<EpgEvent>> GetEpgForServiceRangeAsync(string serviceRef, DateTime start, int hours = 3)
    {
        // Fetch from start-1h to start+hours to catch programs that began before our window
        var begin = new DateTimeOffset(start.AddHours(-1)).ToUnixTimeSeconds();
        var end = new DateTimeOffset(start.AddHours(hours)).ToUnixTimeSeconds();
        var events = await GetEpgEventsAsync("epgservice", new()
        {
            ["sRef"] = serviceRef,
            ["begin"] = begin.ToString(),
            ["end"] = end.ToString()
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

    /// <summary>Fetches a time window of EPG for all services in a bouquet (for grid view).</summary>
    public async Task<List<EpgEvent>> GetEpgBouquetTimeWindowAsync(string bouquetRef, DateTime start, int hours = 3)
    {
        // Fetch 1h before window start to include programs already in progress
        var begin = new DateTimeOffset(start.AddHours(-1)).ToUnixTimeSeconds();
        var end = new DateTimeOffset(start.AddHours(hours)).ToUnixTimeSeconds();
        var events = await GetEpgEventsAsync("epgbouquet", new()
        {
            ["bRef"] = bouquetRef,
            ["begin"] = begin.ToString(),
            ["end"] = end.ToString()
        });
        Debug.WriteLine($"[Enigma2Service] epgbouquet returned {events.Count} events for {bouquetRef}");

        // Fallback: receiver doesn't support bRef time-window — fetch per service in parallel
        if (events.Count == 0)
        {
            var services = await GetServicesAsync(bouquetRef);
            Debug.WriteLine($"[Enigma2Service] epgbouquet fallback: fetching {services.Count} services in parallel");
            var semaphore = new SemaphoreSlim(4);
            var tasks = services.Take(50).Select(async svc =>
            {
                await semaphore.WaitAsync();
                try { return await GetEpgForServiceRangeAsync(svc.ServiceReference, start, hours); }
                finally { semaphore.Release(); }
            });
            var results = await Task.WhenAll(tasks);
            events = results.SelectMany(r => r).ToList();
            Debug.WriteLine($"[Enigma2Service] epgbouquet fallback got {events.Count} total events");
        }

        return events;
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
            var raw = await GetRawResponseAsync("autotimer");
            Debug.WriteLine($"[Enigma2Service] AutoTimer raw response (first 500): {(raw.Length > 500 ? raw[..500] : raw)}");

            var token = Newtonsoft.Json.Linq.JToken.Parse(raw);
            var list = new List<AutoTimer>();
            var js = JsonSerializer.Create(_jsonSettings);

            // Navigate to the timer array regardless of nesting depth
            // Shape A: { "autotimers": { "autotimer": [...] } }
            // Shape B: { "autotimers": { "autotimer": {...} } }  (single item as object)
            // Shape C: { "autotimers": [...] }
            // Shape D: { "autotimer": [...] }  (root level)
            Newtonsoft.Json.Linq.JToken? arr =
                token["autotimers"]?["autotimer"]   // Shape A/B
                ?? token["autotimers"]              // Shape C
                ?? token["autotimer"];              // Shape D

            Debug.WriteLine($"[Enigma2Service] AutoTimer arr type: {arr?.Type}");

            if (arr == null)
            {
                Debug.WriteLine("[Enigma2Service] GetAutoTimersAsync: no recognisable autotimer key in response");
                return [];
            }

            if (arr.Type == Newtonsoft.Json.Linq.JTokenType.Array)
            {
                foreach (var item in arr)
                {
                    var at = item.ToObject<AutoTimer>(js);
                    if (at != null) list.Add(at);
                }
            }
            else if (arr.Type == Newtonsoft.Json.Linq.JTokenType.Object)
            {
                var at = arr.ToObject<AutoTimer>(js);
                if (at != null) list.Add(at);
            }

            Debug.WriteLine($"[Enigma2Service] GetAutoTimersAsync: parsed {list.Count} AutoTimers");
            return list;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Enigma2Service] GetAutoTimersAsync error: {ex}");
            return [];
        }
    }

    // (JSON deserialization uses the shared _jsonSettings field)

    public async Task<bool> SaveAutoTimerAsync(AutoTimer timer)
    {
        try
        {
            // Enigma2 AutoTimer plugin: use /add for new (no id), /edit for existing
            var endpoint = string.IsNullOrEmpty(timer.Id) ? "autotimer/add" : "autotimer/edit";
            var query = new Dictionary<string, string>
            {
                ["name"]    = timer.Name,
                ["match"]   = timer.Match,
                ["enabled"] = timer.Enabled ? "yes" : "no",
                ["searchType"]  = timer.SearchType.ToString(),
                ["searchCase"]  = timer.SearchCase.ToString(),
                ["justplay"]    = timer.JustPlay.ToString(),
                ["avoidDuplicateDescription"] = timer.AvoidDuplicates.ToString(),
            };
            if (!string.IsNullOrEmpty(timer.Id)) query["id"] = timer.Id;
            if (!string.IsNullOrEmpty(timer.From)) query["from"] = timer.From;
            if (!string.IsNullOrEmpty(timer.To))   query["to"]   = timer.To;
            if (!string.IsNullOrEmpty(timer.ServiceRef)) query["serviceref"] = timer.ServiceRef;
            if (timer.MaxDuration > 0) query["maxduration"] = timer.MaxDuration.ToString();
            if (!string.IsNullOrEmpty(timer.Tags)) query["tags"] = timer.Tags;

            var result = await GetAsync<GenericResponse>(endpoint, query);
            return result?.Result ?? false;
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
            var result = await GetAsync<GenericResponse>("autotimer/remove", new Dictionary<string, string> { ["id"] = id });
            return result?.Result ?? false;
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
            await GetRawResponseAsync("autotimer/parse");
            return true;
        }
        catch { return false; }
    }
}
