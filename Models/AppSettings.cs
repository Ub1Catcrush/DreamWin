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

    // Color scheme
    public string AccentColor { get; set; } = "#6C63FF";
    public string BgDeepColor { get; set; } = "#0F1117";
    public string BgPanelColor { get; set; } = "#1A1D27";

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
