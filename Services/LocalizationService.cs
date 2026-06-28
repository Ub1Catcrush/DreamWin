using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace DreamWin.Services;

/// <summary>
/// Short XAML alias: {Binding [Key], Source={x:Static svc:Loc.Instance}}
/// </summary>
public static class Loc
{
    public static LocalizationService Instance => LocalizationService.Instance;
}

/// <summary>
/// Runtime localization service. Bind UI strings via {Binding [KeyName], Source={x:Static svc:LocalizationService.Instance}}.
/// Changing <see cref="Language"/> hot-swaps all strings without restarting the app.
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    // ── Singleton ───────────────────────────────────────────────────────
    public static readonly LocalizationService Instance = new();
    private LocalizationService() { }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ── Supported languages ─────────────────────────────────────────────
    public static readonly IReadOnlyList<LanguageOption> SupportedLanguages =
    [
        new("en", "English"),
        new("de", "Deutsch"),
    ];

    // ── Resource manager (set once; points to DreamWin.Resources.Strings.Strings) ──
    private static readonly ResourceManager _rm =
        new("DreamWin.Resources.Strings.Strings", typeof(LocalizationService).Assembly);

    private CultureInfo _culture = CultureInfo.GetCultureInfo("en");

    public string Language
    {
        get => _culture.TwoLetterISOLanguageName;
        set
        {
            if (_culture.TwoLetterISOLanguageName == value) return;
            _culture = string.IsNullOrEmpty(value)
                ? CultureInfo.GetCultureInfo("en")
                : CultureInfo.GetCultureInfo(value);
            // Notify WPF that ALL indexed properties changed
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        }
    }

    /// <summary>Indexer — use as {Binding [Nav_LiveTV], Source={x:Static svc:Loc.Instance}}</summary>
    public string this[string key]
    {
        get
        {
            try   { return _rm.GetString(key, _culture) ?? $"[{key}]"; }
            catch { return $"[{key}]"; }
        }
    }

    // ── Convenience: format with string.Format ───────────────────────────
    public string Format(string key, params object[] args)
        => string.Format(this[key], args);
}

public sealed record LanguageOption(string Code, string DisplayName);
