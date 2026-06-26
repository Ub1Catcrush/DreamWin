using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace DreamWin.Controls;

/// <summary>
/// A reusable hour/minute time picker that exposes a single bindable
/// <see cref="Value"/> string in "HH:mm" format — a drop-in replacement for the
/// free-text "HH:MM" TextBoxes previously used for timer/AutoTimer start and end
/// times, with no view-model changes required since the bound type is unchanged.
/// </summary>
public partial class TimePicker : UserControl
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value), typeof(string), typeof(TimePicker),
            new FrameworkPropertyMetadata("00:00", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public ObservableCollection<string> HourOptions { get; } = new(Enumerable.Range(0, 24).Select(h => h.ToString("00")));
    public ObservableCollection<string> MinuteOptions { get; } = new(Enumerable.Range(0, 60).Select(m => m.ToString("00")));

    public static readonly DependencyProperty SelectedHourProperty =
        DependencyProperty.Register(nameof(SelectedHour), typeof(string), typeof(TimePicker),
            new PropertyMetadata("00", OnPartChanged));

    public static readonly DependencyProperty SelectedMinuteProperty =
        DependencyProperty.Register(nameof(SelectedMinute), typeof(string), typeof(TimePicker),
            new PropertyMetadata("00", OnPartChanged));

    public string SelectedHour
    {
        get => (string)GetValue(SelectedHourProperty);
        set => SetValue(SelectedHourProperty, value);
    }

    public string SelectedMinute
    {
        get => (string)GetValue(SelectedMinuteProperty);
        set => SetValue(SelectedMinuteProperty, value);
    }

    // Guards against the Hour/Minute -> Value write-back re-triggering the
    // Value -> Hour/Minute split below, which would otherwise be a feedback loop
    // (each side updating the other indefinitely on every keystroke/selection).
    private bool _isSyncingFromValue;

    public TimePicker()
    {
        InitializeComponent();
        SyncPartsFromValue();
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimePicker picker) picker.SyncPartsFromValue();
    }

    private static void OnPartChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimePicker { _isSyncingFromValue: false } picker) picker.SyncValueFromParts();
    }

    private void SyncPartsFromValue()
    {
        _isSyncingFromValue = true;
        try
        {
            var (hour, minute) = ParseTime(Value);
            SelectedHour = hour;
            SelectedMinute = minute;
        }
        finally
        {
            _isSyncingFromValue = false;
        }
    }

    private void SyncValueFromParts() => Value = $"{SelectedHour}:{SelectedMinute}";

    // Accepts "H:mm", "HH:mm", or anything System.TimeSpan can parse; anything
    // unparseable (including empty/null, which the optional AutoTimer from/to
    // fields legitimately are) falls back to midnight rather than throwing, since
    // this runs on every keystroke while the bound property is still mid-edit.
    private static (string hour, string minute) ParseTime(string? raw)
    {
        if (!string.IsNullOrWhiteSpace(raw) && TimeSpan.TryParse(raw, out var ts) &&
            ts.Hours is >= 0 and < 24)
        {
            return (ts.Hours.ToString("00"), ts.Minutes.ToString("00"));
        }
        return ("00", "00");
    }
}
