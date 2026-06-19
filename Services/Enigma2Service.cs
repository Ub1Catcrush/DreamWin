using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Web;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using DreamWin.Models;

namespace DreamWin.Services;

public class Enigma2Service
{
    private HttpClient _http = new();
    private ReceiverConfig? _config;

    public ReceiverConfig? CurrentConfig => _config;

    public void SetReceiver(ReceiverConfig config)
    {
        _config = config;
        _http = new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(8);
        if (!string.IsNullOrEmpty(config.Username))
        {
            var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{config.Username}:{config.Password}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }
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

    private async Task<string> GetRawResponseAsync(string endpoint, Dictionary<string, string>? query = null)
    {
        if (_config == null) throw new InvalidOperationException("No receiver configured");
        var url = $"{_config.BaseUrl}/api/{endpoint}";
        if (query?.Count > 0)
        {
            var qs = string.Join("&", query.Select(kv => $"{kv.Key}={HttpUtility.UrlEncode(kv.Value)}"));
            url += "?" + qs;
        }
        Debug.WriteLine($"[Enigma2Service] GET {url}");
        using var cts = new CancellationTokenSource(_http.Timeout);
        try
        {
            return await _http.GetStringAsync(url, cts.Token);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Enigma2Service] HTTP error for {url}: {ex.Message}");
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
        var events = await GetEpgEventsAsync("epgservice", new()
        {
            ["sRef"] = serviceRef,
            ["begin"] = now.ToUnixTimeSeconds().ToString(),
            ["end"] = now.AddHours(hours).ToUnixTimeSeconds().ToString()
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
        var begin = new DateTimeOffset(start).ToUnixTimeSeconds();
        var end = new DateTimeOffset(start.AddHours(hours)).ToUnixTimeSeconds();
        var events = await GetEpgEventsAsync("epgbouquet", new()
        {
            ["bRef"] = bouquetRef,
            ["begin"] = begin.ToString(),
            ["end"] = end.ToString()
        });
        // Fallback: if receiver doesn't support bRef timewindow, do sequential per service
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
    public async Task<List<Movie>> GetMoviesAsync(string? location = null)
    {
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
                var directoryMovies = await GetMoviesAsync(directoryPath);
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
        // Enigma2/OpenWebif streaming URLs pass the service reference through verbatim,
        // colons and all, e.g. http://192.168.1.4:8001/1:0:1:1047:1047:233A:EEEE0000:0:0:0:
        // (no colon-to-underscore substitution — that was a previous bug here).
        return $"http://{_config.Host}:{_config.StreamingPort}/{serviceRef}";
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
            var token = Newtonsoft.Json.Linq.JToken.Parse(raw);
            var list = new List<AutoTimer>();
            // Enigma2 AutoTimer plugin returns { "autotimers": { "autotimer": [...] } }
            // or { "autotimers": [...] }
            Newtonsoft.Json.Linq.JToken? arr = null;
            if (token["autotimers"]?["autotimer"] is { } nested)
                arr = nested;
            else if (token["autotimers"] is { } top && top.Type == Newtonsoft.Json.Linq.JTokenType.Array)
                arr = top;
            else if (token["autotimers"] is { } obj2 && obj2.Type == Newtonsoft.Json.Linq.JTokenType.Object)
                arr = obj2["autotimer"];

            if (arr != null)
            {
                if (arr.Type == Newtonsoft.Json.Linq.JTokenType.Array)
                {
                    foreach (var item in arr)
                        list.Add(item.ToObject<AutoTimer>(_jsonSerializer) ?? new AutoTimer());
                }
                else if (arr.Type == Newtonsoft.Json.Linq.JTokenType.Object)
                {
                    list.Add(arr.ToObject<AutoTimer>(_jsonSerializer) ?? new AutoTimer());
                }
            }
            return list;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Enigma2Service] GetAutoTimersAsync error: {ex.Message}");
            return [];
        }
    }

    private readonly JsonSerializer _jsonSerializer = JsonSerializer.Create(new JsonSerializerSettings
    {
        MissingMemberHandling = MissingMemberHandling.Ignore,
        NullValueHandling = NullValueHandling.Include,
        Error = (_, args) => { args.ErrorContext.Handled = true; }
    });

    public async Task<bool> SaveAutoTimerAsync(AutoTimer timer)
    {
        try
        {
            var query = new Dictionary<string, string>
            {
                ["id"] = timer.Id,
                ["name"] = timer.Name,
                ["match"] = timer.Match,
                ["enabled"] = timer.Enabled ? "yes" : "no",
                ["searchType"] = timer.SearchType.ToString(),
                ["searchCase"] = timer.SearchCase.ToString(),
                ["justplay"] = timer.JustPlay.ToString(),
                ["avoidDuplicateDescription"] = timer.AvoidDuplicates.ToString(),
            };
            if (!string.IsNullOrEmpty(timer.From)) query["from"] = timer.From;
            if (!string.IsNullOrEmpty(timer.To)) query["to"] = timer.To;
            if (!string.IsNullOrEmpty(timer.ServiceRef)) query["serviceref"] = timer.ServiceRef;
            if (timer.MaxDuration > 0) query["maxduration"] = timer.MaxDuration.ToString();
            var result = await GetAsync<GenericResponse>("autotimer/edit", query);
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
