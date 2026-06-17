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
