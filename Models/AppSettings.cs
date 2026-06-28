using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Newtonsoft.Json;

namespace DreamWin.Models;

public class AppSettings
{
    public List<ReceiverConfig> Receivers { get; set; } = [];
    public Guid? ActiveReceiverId { get; set; }
    public double Volume { get; set; } = 80;
    public bool RememberVolume { get; set; } = true;
    public string Theme { get; set; } = "Dark";
    public int EpgDays { get; set; } = 3;
    public string LastBouquetRef { get; set; } = "";

    // Update settings
    public bool CheckForUpdates { get; set; } = true;
    public bool IncludePrereleases { get; set; } = false;
    public DateTime LastUpdateCheckTime { get; set; } = DateTime.MinValue;
    public string? SkippedUpdateVersion { get; set; }

    // UI Language (ISO 639-1 code: "en", "de", ...)
    public string Language { get; set; } = "en";

    // VLC playback settings
    public string VlcVideoOutput { get; set; } = "auto";       // auto | direct3d11 | opengl | directdraw
    public bool VlcHardwareAcceleration { get; set; } = true;
    public int VlcNetworkCacheMs { get; set; } = 1000;
    public int VlcFileCacheMs { get; set; } = 300;
    public string VlcDeinterlace { get; set; } = "auto";       // off | on | auto
    public string VlcDeinterlaceMode { get; set; } = "blend";  // blend | bob | linear | mean | discard | yadif | yadif2x | phosphor | ivtc

    // Applies :no-ts-trust-pcr and :ts-seek-percent to recording playback only
    // (see MoviesView.ApplyTsSeekOptions). These are a documented VLC mitigation
    // for inaccurate MPEG-TS seeking, but they change how the TS demuxer
    // resyncs after a seek, which can help OR make resync stutter worse
    // depending on the specific recording/encoder — exposed as a toggle so this
    // can be A/B tested per-setup rather than guessed at and hardcoded.
    public bool VlcTsSeekOptions { get; set; } = true;

    // Color scheme
    public string AccentColor { get; set; } = "#6C63FF";
    public string BgDeepColor { get; set; } = "#0F1117";
    public string BgPanelColor { get; set; } = "#1A1D27";

    /// <summary>
    /// Builds the LibVLC constructor argument list from the current VLC settings.
    /// Call this once when initializing LibVLC.
    /// </summary>
    public string[] BuildVlcArgs()
    {
        var args = new System.Collections.Generic.List<string>
        {
            "--no-video-title-show",
            $"--network-caching={VlcNetworkCacheMs}",
            $"--file-caching={VlcFileCacheMs}",
        };

        // Video output module
        if (VlcVideoOutput != "auto")
            args.Add($"--vout={VlcVideoOutput}");

        // Hardware acceleration (avcodec-hw)
        args.Add(VlcHardwareAcceleration ? "--avcodec-hw=any" : "--avcodec-hw=none");

        // Deinterlace
        switch (VlcDeinterlace)
        {
            case "on":
                args.Add("--deinterlace=1");
                args.Add($"--deinterlace-mode={VlcDeinterlaceMode}");
                break;
            case "off":
                args.Add("--deinterlace=-1");
                break;
            default: // "auto"
                args.Add("--deinterlace=0");
                args.Add($"--deinterlace-mode={VlcDeinterlaceMode}");
                break;
        }

        return args.ToArray();
    }

    public static readonly string SettingsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DreamWin", "settings.json");

    public static AppSettings Load()
    {
        AppSettings? settings = null;
        bool hadError = false;
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                settings = JsonConvert.DeserializeObject<AppSettings>(json);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppSettings] Load error: {ex.Message}");
            hadError = true;
        }

        settings ??= new AppSettings();

        // Decrypt passwords after loading
        foreach (var r in settings.Receivers)
            r.DecryptPassword();

        if (hadError)
            settings._loadError = "Settings file could not be read and has been reset to defaults.";

        return settings;
    }

    [JsonIgnore]
    private string? _loadError;

    [JsonIgnore]
    public string? LoadError => _loadError;

    public void Save()
    {
        try
        {
            // Encrypt passwords before saving
            foreach (var r in Receivers)
                r.EncryptPassword();

            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var json = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText(SettingsPath, json);

            // Restore decrypted passwords so in-memory use continues working
            foreach (var r in Receivers)
                r.DecryptPassword();

            HardenFilePermissions();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppSettings] Save error: {ex.Message}");
        }
    }

    private static void HardenFilePermissions()
    {
        try
        {
            // Restrict read to current user only (Windows ACL)
            var fi = new FileInfo(SettingsPath);
            var security = fi.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                WindowsIdentity.GetCurrent().Name,
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.AccessControlType.Allow));
            fi.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppSettings] ACL hardening failed: {ex.Message}");
        }
    }
}
