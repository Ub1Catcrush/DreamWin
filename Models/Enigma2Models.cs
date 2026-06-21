using System.IO;
using Newtonsoft.Json;

namespace DreamWin.Models;

public class BouquetList
{
    [JsonProperty("services")]
    public List<Service> Services { get; set; } = [];
}

public class Service
{
    [JsonProperty("servicereference")]
    public string ServiceReference { get; set; } = "";

    [JsonProperty("servicename")]
    public string ServiceName { get; set; } = "";

    [JsonProperty("icon")]
    public string? ServiceIcon { get; set; }

    public bool IsBouquet => ServiceReference.StartsWith("1:7:") || ServiceReference.StartsWith("1:134:");
}

public class EpgEvent
{
    [JsonProperty("id")]
    public long? Id { get; set; }

    [JsonProperty("begin_timestamp")]
    public long BeginTimestamp { get; set; }

    [JsonProperty("duration_sec")]
    public int DurationSec { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; } = "";

    [JsonProperty("shortdesc")]
    public string ShortDesc { get; set; } = "";

    [JsonProperty("longdesc")]
    public string LongDesc { get; set; } = "";

    [JsonProperty("sref")]
    public string ServiceRef { get; set; } = "";

    [JsonProperty("sname")]
    public string ServiceName { get; set; } = "";

    [JsonProperty("genreid")]
    public int GenreId { get; set; }

    public DateTime BeginTime => DateTimeOffset.FromUnixTimeSeconds(BeginTimestamp).LocalDateTime;
    public DateTime EndTime => BeginTime.AddSeconds(DurationSec);
    public string Duration => $"{DurationSec / 60} min";
    public string TimeRange => $"{BeginTime:HH:mm} - {EndTime:HH:mm}";
    public double ProgressPercent
    {
        get
        {
            var now = DateTime.Now;
            if (now < BeginTime) return 0;
            if (now > EndTime) return 100;
            return (now - BeginTime).TotalSeconds / DurationSec * 100;
        }
    }
    public bool IsCurrentlyAiring => DateTime.Now >= BeginTime && DateTime.Now <= EndTime;

    // Set at runtime when building the EPG grid
    // OffsetPx = vertical offset from grid start (Y axis = time)
    // HeightPx = block height proportional to duration
    public double OffsetPx { get; set; }
    public double HeightPx { get; set; }
}

public class EpgResponse
{
    [JsonProperty("events")]
    public List<EpgEvent> Events { get; set; } = [];
}

public class Timer
{
    [JsonProperty("serviceref")]
    public string ServiceRef { get; set; } = "";

    [JsonProperty("servicename")]
    public string ServiceName { get; set; } = "";

    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("description")]
    public string Description { get; set; } = "";

    [JsonProperty("begin")]
    public long Begin { get; set; }

    [JsonProperty("end")]
    public long End { get; set; }

    [JsonProperty("state")]
    public int State { get; set; }

    [JsonProperty("repeated")]
    public int Repeated { get; set; }

    [JsonProperty("afterevent")]
    public int AfterEvent { get; set; }

    [JsonProperty("eit")]
    public long Eit { get; set; }

    [JsonProperty("disabled")]
    public int Disabled { get; set; }

    public DateTime BeginTime => DateTimeOffset.FromUnixTimeSeconds(Begin).LocalDateTime;
    public DateTime EndTime => DateTimeOffset.FromUnixTimeSeconds(End).LocalDateTime;
    public string StateText => State switch { 0 => "Waiting", 1 => "Preparing", 2 => "Recording", 3 => "Done", _ => "Unknown" };
    public string TimeRange => $"{BeginTime:dd.MM HH:mm} - {EndTime:HH:mm}";
    public bool IsActive => State == 2;
    public bool IsDisabled => Disabled == 1;
}

public class TimerList
{
    [JsonProperty("timers")]
    public List<Timer> Timers { get; set; } = [];
}

public class Movie
{
    [JsonProperty("servicereference")]
    public string ServiceReference { get; set; } = "";

    [JsonProperty("servicename")]
    public string ServiceName { get; set; } = "";

    [JsonProperty("title")]
    public string Title { get; set; } = "";

    [JsonProperty("description")]
    public string Description { get; set; } = "";

    [JsonProperty("descriptionextended")]
    public string DescriptionExtended { get; set; } = "";

    [JsonProperty("filename")]
    public string Filename { get; set; } = "";

    [JsonProperty("filesize")]
    public long Filesize { get; set; }

    [JsonProperty("length")]
    public string Length { get; set; } = "";

    [JsonProperty("tags")]
    public string Tags { get; set; } = "";

    [JsonProperty("recordingtime")]
    public long RecordingTime { get; set; }

    public DateTime RecordingDate => DateTimeOffset.FromUnixTimeSeconds(RecordingTime).LocalDateTime;
    public string FilesizeMB => $"{Filesize / 1024 / 1024:0} MB";
    public string DisplayTitle =>
        !string.IsNullOrWhiteSpace(Title) ? Title : Path.GetFileNameWithoutExtension(Filename);
    public string FolderName =>
        !string.IsNullOrWhiteSpace(Filename)
            ? Path.GetDirectoryName(Filename)?.Replace("\\", "/") ?? string.Empty
            : string.Empty;
}

public class MovieList
{
    [JsonProperty("movies")]
    public List<Movie> Movies { get; set; } = [];

    [JsonProperty("bookmarks")]
    public List<string> Bookmarks { get; set; } = [];

    [JsonProperty("directory")]
    public string Directory { get; set; } = "";
}

public class CurrentEvent
{
    [JsonProperty("info")]
    public CurrentInfo? Info { get; set; }

    [JsonProperty("now")]
    public EpgEvent? Now { get; set; }

    [JsonProperty("next")]
    public EpgEvent? Next { get; set; }
}

public class CurrentInfo
{
    [JsonProperty("servicereference")]
    public string ServiceReference { get; set; } = "";

    [JsonProperty("servicename")]
    public string ServiceName { get; set; } = "";
}

public class GenericResponse
{
    [JsonProperty("result")]
    public bool Result { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; } = "";
}

public class SignalStatus
{
    [JsonProperty("snr")]
    public int? Snr { get; set; }

    [JsonProperty("snr_db")]
    public double? SnrDb { get; set; }

    [JsonProperty("agc")]
    public int? Agc { get; set; }

    [JsonProperty("ber")]
    public int? Ber { get; set; }

    [JsonProperty("tunertype")]
    public string TunerType { get; set; } = "";
}

public class AutoTimer
{
    [JsonProperty("id")]
    public string Id { get; set; } = "";

    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("match")]
    public string Match { get; set; } = "";

    [JsonProperty("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonProperty("searchType")]
    public int SearchType { get; set; }

    [JsonProperty("searchCase")]
    public int SearchCase { get; set; }

    [JsonProperty("justplay")]
    public int JustPlay { get; set; }

    [JsonProperty("avoidDuplicateDescription")]
    public int AvoidDuplicates { get; set; }

    [JsonProperty("from")]
    public string From { get; set; } = "";

    [JsonProperty("to")]
    public string To { get; set; } = "";

    [JsonProperty("serviceref")]
    public string ServiceRef { get; set; } = "";

    [JsonProperty("maxduration")]
    public int MaxDuration { get; set; }

    [JsonProperty("tags")]
    public string Tags { get; set; } = "";

    public string SearchTypeText => SearchType switch { 0 => "Contains", 1 => "Exact", 2 => "Starts with", 3 => "Ends with", _ => "Contains" };
    public string JustPlayText => JustPlay == 1 ? "Zap" : "Record";
    public string StatusText => Enabled ? "Enabled" : "Disabled";
}

public class AutoTimerList
{
    [JsonProperty("autotimers")]
    public List<AutoTimer> AutoTimers { get; set; } = [];
}
