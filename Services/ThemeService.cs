using System.Windows;
using System.Windows.Media;
using DreamWin.Models;

namespace DreamWin.Services;

public static class ThemeService
{
    // Preset themes: name → (accent, bgDeep, bgPanel)
    public static readonly Dictionary<string, (string Accent, string BgDeep, string BgPanel)> Presets = new()
    {
        ["Purple (Default)"] = ("#6C63FF", "#0F1117", "#1A1D27"),
        ["Teal"]             = ("#14B8A6", "#0A1210", "#131D1B"),
        ["Orange"]           = ("#F97316", "#120E09", "#1C1510"),
        ["Rose"]             = ("#F43F5E", "#120910", "#1D1219"),
        ["Sky Blue"]         = ("#0EA5E9", "#090F12", "#12191E"),
        ["Emerald"]          = ("#10B981", "#091210", "#12201B"),
        ["Amber"]            = ("#F59E0B", "#121009", "#1E1A10"),
        ["Light"]            = ("#6C63FF", "#F0F0F5", "#FFFFFF"),
    };

    public static void Apply(AppSettings settings)
    {
        Apply(settings.AccentColor, settings.BgDeepColor, settings.BgPanelColor);
    }

    public static void Apply(string accent, string bgDeep, string bgPanel)
    {
        var res = Application.Current.Resources;

        TrySet(res, "AccentColor", accent);
        TrySet(res, "BgDeepColor", bgDeep);
        TrySet(res, "BgPanelColor", bgPanel);

        // Derived colors
        TrySet(res, "AccentHoverColor", Lighten(accent, 0.12f));
        TrySet(res, "BgCardColor", Blend(bgPanel, accent, 0.06f));
        TrySet(res, "BgHoverColor", Blend(bgPanel, accent, 0.15f));

        // Update brushes
        TrySetBrush(res, "Accent", accent);
        TrySetBrush(res, "AccentHover", Lighten(accent, 0.12f));
        TrySetBrush(res, "BgDeep", bgDeep);
        TrySetBrush(res, "BgPanel", bgPanel);
        TrySetBrush(res, "BgCard", Blend(bgPanel, accent, 0.06f));
        TrySetBrush(res, "BgHover", Blend(bgPanel, accent, 0.15f));
        TrySetBrush(res, "BorderBrush", Blend(bgPanel, accent, 0.25f));

        // System color overrides
        TrySetBrush(res, System.Windows.SystemColors.HighlightBrushKey, accent);
        TrySetBrush(res, System.Windows.SystemColors.WindowBrushKey, Blend(bgPanel, accent, 0.06f));
        TrySetBrush(res, System.Windows.SystemColors.ControlBrushKey, Blend(bgPanel, accent, 0.06f));
    }

    private static void TrySetBrush(ResourceDictionary res, object key, string hexColor)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hexColor);
            var brush = new SolidColorBrush(color);
            // Do NOT freeze — frozen brushes can't be replaced in ResourceDictionary
            // WPF will still reuse the brush instance via the dictionary key
            if (res.Contains(key))
                res[key] = brush;
            else
                res.Add(key, brush);
        }
        catch { }
    }

    private static void TrySet(ResourceDictionary res, object key, string hexColor)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hexColor);
            if (res.Contains(key))
                res[key] = color;
            else
                res.Add(key, color);
        }
        catch { }
    }

    private static string Lighten(string hex, float amount)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            return $"#{Clamp(c.R + (int)(255 * amount)):X2}{Clamp(c.G + (int)(255 * amount)):X2}{Clamp(c.B + (int)(255 * amount)):X2}";
        }
        catch { return hex; }
    }

    private static string Blend(string base_, string accent, float t)
    {
        try
        {
            var b = (Color)ColorConverter.ConvertFromString(base_);
            var a = (Color)ColorConverter.ConvertFromString(accent);
            return $"#{Clamp((int)(b.R + (a.R - b.R) * t)):X2}{Clamp((int)(b.G + (a.G - b.G) * t)):X2}{Clamp((int)(b.B + (a.B - b.B) * t)):X2}";
        }
        catch { return base_; }
    }

    private static int Clamp(int v) => Math.Max(0, Math.Min(255, v));
}
