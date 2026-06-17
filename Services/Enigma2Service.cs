using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Web;
using Newtonsoft.Json;
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

    private async Task<T?> GetAsync<T>(string endpoint, Dictionary<string, string>? query = null) where T : class
    {
        if (_config == null) throw new InvalidOperationException("No receiver configured");
        var url = $"{_config.BaseUrl}/api/{endpoint}";
        if (query?.Count > 0)
        {
            var qs = string.Join("&", query.Select(kv => $"{kv.Key}={HttpUtility.UrlEncode(kv.Value)}"));
            url += "?" + qs;
        }
        using var cts = new CancellationTokenSource(_http.Timeout);
        string response;
        try
        {
            response = await _http.GetStringAsync(url, cts.Token);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Enigma2Service] HTTP error for {url}: {ex.Message}");
            throw;
        }

        try
        {
            return JsonConvert.DeserializeObject<T>(response);
        }
        catch (JsonException ex)
        {
            // Log the raw payload so a real schema mismatch (wrong field name/type, or an
            // HTML/error page returned instead of JSON) is visible instead of just a bare
            // "JsonSerializationException" with no context.
            var preview = response.Length > 500 ? response[..500] + "..." : response;
            Debug.WriteLine($"[Enigma2Service] JSON parse error for {url}: {ex.Message}\nRaw response: {preview}");
            throw;
        }
    }

    // ─── Services / Bouquets ───────────────────────────────────────────
    public async Task<List<Service>> GetBouquetsAsync()
    {
        var result = await GetAsync<BouquetList>("getservices");
        return result?.Services ?? [];
    }

    public async Task<List<Service>> GetServicesAsync(string bouquetRef)
    {
        var result = await GetAsync<BouquetList>("getservices", new() { ["sRef"] = bouquetRef });
        return result?.Services ?? [];
    }

    // ─── EPG ─────────────────────────────────────────────────────────
    public async Task<List<EpgEvent>> GetNowNextAsync(string bouquetRef)
    {
        var result = await GetAsync<EpgResponse>("epgnow", new() { ["bRef"] = bouquetRef });
        return result?.Events ?? [];
    }

    public async Task<List<EpgEvent>> GetEpgForServiceAsync(string serviceRef, int hours = 24)
    {
        var result = await GetAsync<EpgResponse>("epgservice", new()
        {
            ["sRef"] = serviceRef,
            ["time"] = ((DateTimeOffset)DateTime.Now).ToUnixTimeSeconds().ToString(),
            ["endTime"] = ((DateTimeOffset)DateTime.Now.AddHours(hours)).ToUnixTimeSeconds().ToString()
        });
        return result?.Events ?? [];
    }

    public async Task<List<EpgEvent>> GetEpgNowAsync(string serviceRef)
    {
        var result = await GetAsync<EpgResponse>("epgservicenow", new() { ["sRef"] = serviceRef });
        return result?.Events ?? [];
    }

    public async Task<List<EpgEvent>> SearchEpgAsync(string query)
    {
        var result = await GetAsync<EpgResponse>("epgsearch", new() { ["search"] = query });
        return result?.Events ?? [];
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
        var result = await GetAsync<TimerList>("timerlist");
        return result?.Timers ?? [];
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
        var query = string.IsNullOrEmpty(location) ? null : new Dictionary<string, string> { ["dirname"] = location };
        var result = await GetAsync<MovieList>("movielist", query);
        return result?.Movies ?? [];
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
}
