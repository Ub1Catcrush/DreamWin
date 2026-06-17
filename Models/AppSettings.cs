using System.IO;
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

    private static readonly string SettingsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DreamWin", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
        catch { }
    }
}
