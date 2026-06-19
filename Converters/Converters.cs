using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DreamWin.Models;
using DreamWin.ViewModels;

namespace DreamWin.Converters;

public class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c)
        => (value is bool b && b != Invert) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => value is Visibility v && v == Visibility.Visible;
}

public class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c)
        => (value != null) != Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotImplementedException();
}

public class AppViewToBoolConverter : IValueConverter
{
    public AppView TargetView { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is AppView v && v == TargetView;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotImplementedException();
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is bool b && !b;
    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => value is bool b && !b;
}

// Compares a bound enum value against the ConverterParameter and returns Visibility.
// Used for bindings like: Visibility="{Binding CurrentView, Converter={StaticResource EnumToVisibility}, ConverterParameter={x:Static vm:AppView.LiveTV}}"
public class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c)
        => (value != null && parameter != null && value.Equals(parameter)) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object parameter, CultureInfo c) => throw new NotImplementedException();
}

public class ProgressToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type t, object p, CultureInfo c)
    {
        if (values.Length == 2 && values[0] is double progress && values[1] is double totalWidth)
            return totalWidth * Math.Max(0, Math.Min(100, progress)) / 100.0;
        return 0.0;
    }
    public object[] ConvertBack(object value, Type[] types, object p, CultureInfo c) => throw new NotImplementedException();
}

public class TimerStateToColorConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        if (value is int state)
        {
            return state switch
            {
                2 => new SolidColorBrush(Color.FromRgb(239, 68, 68)),   // Recording - Red
                3 => new SolidColorBrush(Color.FromRgb(107, 114, 128)), // Done - Gray
                _ => new SolidColorBrush(Color.FromRgb(59, 130, 246))   // Waiting - Blue
            };
        }
        return Brushes.Gray;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotImplementedException();
}

public class EpgProgressConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is EpgEvent e ? e.ProgressPercent : 0.0;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotImplementedException();
}

public class FilesizeMBConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        if (value is long size) return $"{size / 1024 / 1024:0} MB";
        return "";
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotImplementedException();
}

public class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        if (value is int count)
            return count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (value is long longCount)
            return longCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotImplementedException();
}

// Compares two bound values for equality and returns a bool. DataTrigger.Value only
// accepts a literal, not a Binding, so "highlight this item if it equals the currently
// selected item" can't be expressed as a single DataTrigger with a bound Value — instead
// bind both sides through this converter and trigger on the resulting bool.
// Usage: <MultiBinding Converter="{StaticResource ObjectsEqual}">
//            <Binding Path="."/>                          (the item itself)
//            <Binding Path="DataContext.SelectedX" .../>   (the ambient "selected" value)
//        </MultiBinding>
public class ObjectsEqualConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type t, object p, CultureInfo c)
        => values.Length == 2 && values[0] != null && values[0].Equals(values[1]);
    public object[] ConvertBack(object value, Type[] types, object p, CultureInfo c) => throw new NotImplementedException();
}

// ObjectsEqualToBrush: same as ObjectsEqual but returns a Brush so it can be used
// directly as a BorderBrush without a DataTrigger.
public class ObjectsEqualToBrushConverter : IMultiValueConverter
{
    public Brush? MatchBrush { get; set; }
    public Brush? NoMatchBrush { get; set; }

    public object? Convert(object[] values, Type t, object p, CultureInfo c)
    {
        bool match = values.Length == 2 && values[0] != null && values[0].Equals(values[1]);
        return match ? MatchBrush : NoMatchBrush;
    }
    public object[] ConvertBack(object value, Type[] types, object p, CultureInfo c) => throw new NotImplementedException();
}

// Converts a service reference string into a BitmapImage picon URL using the receiver base URL.
// Values[0] = ServiceReference string, Values[1] = ReceiverConfig (or null).
public class PiconUrlConverter : IMultiValueConverter
{
    private static readonly System.Windows.Media.Imaging.BitmapImage _placeholder = MakePlaceholder();

    private static System.Windows.Media.Imaging.BitmapImage MakePlaceholder()
    {
        // 1×1 transparent PNG
        var bmp = new System.Windows.Media.Imaging.BitmapImage();
        return bmp;
    }

    public object? Convert(object[] values, Type t, object p, CultureInfo c)
    {
        if (values.Length < 2) return null;
        var sref = values[0] as string;
        var config = values[1] as DreamWin.Models.ReceiverConfig;
        if (string.IsNullOrEmpty(sref) || config == null) return null;
        // Enigma2 picon path: /picon/<serviceref sanitized>.png
        // Sanitize: trim, remove trailing colons, replace : with _
        var piconName = sref.Trim().TrimEnd(':').Replace(":", "_");
        var url = $"{config.BaseUrl}/picon/{piconName}.png";
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(url, UriKind.Absolute);
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
            bmp.DecodePixelWidth = 52;
            bmp.EndInit();
            return bmp;
        }
        catch { return null; }
    }
    public object[] ConvertBack(object value, Type[] types, object p, CultureInfo c) => throw new NotImplementedException();
}

// EpgEventWidth: pixel width from event duration (3px per minute, min 4px)
public class EpgEventWidthConverter : IValueConverter
{
    public const double PxPerMinute = 3.0;
    public const double MinWidth = 4.0;

    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        if (value is DreamWin.Models.EpgEvent evt)
            return Math.Max(MinWidth, evt.DurationSec / 60.0 * PxPerMinute - 2);
        return MinWidth;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotImplementedException();
}
