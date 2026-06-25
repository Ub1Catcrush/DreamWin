using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DreamWin.Models;
using DreamWin.ViewModels;

namespace DreamWin.Views;

public partial class EpgView : UserControl
{
    // Drag state (shared by both orientations)
    private bool   _isDragging;
    private Point  _dragStart;
    private double _dragScrollH;
    private double _dragScrollV;

    public EpgView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is EpgViewModel vm)
        {
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(EpgViewModel.GridRows))
                    Dispatcher.InvokeAsync(ScrollToNow,
                        System.Windows.Threading.DispatcherPriority.Loaded);
            };
        }
    }

    // ── Day picker ────────────────────────────────────────────────────────────
    private void DayPickerButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int offset } && DataContext is EpgViewModel vm)
            vm.JumpToDayCommand.Execute(offset.ToString());
    }

    // ── HORIZONTAL layout — master scroll sync ────────────────────────────────
    private void EventScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        HeaderScrollViewer?.ScrollToHorizontalOffset(e.HorizontalOffset);
        TimeScrollViewer?.ScrollToVerticalOffset(e.VerticalOffset);
    }

    private void EventScrollViewer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        => BeginDrag(e, EventScrollViewer);

    private void EventScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
        => ContinueDrag(e, EventScrollViewer);

    private void EventScrollViewer_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        => EndDrag(EventScrollViewer);

    // ── VERTICAL layout — master scroll sync ──────────────────────────────────
    private void VEventScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        VHeaderScrollViewer?.ScrollToHorizontalOffset(e.HorizontalOffset);
        VChannelScrollViewer?.ScrollToVerticalOffset(e.VerticalOffset);
    }

    private void VEventScrollViewer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        => BeginDrag(e, VEventScrollViewer);

    private void VEventScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
        => ContinueDrag(e, VEventScrollViewer);

    private void VEventScrollViewer_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        => EndDrag(VEventScrollViewer);

    // ── Shared drag helpers ─────────────────────────────────────────────────
    // Clicking an event block should open the EPG detail popup — but only on
    // mouse-up, and only if the mouse wasn't actually dragged to get there.
    // Relying on XAML MouseBinding for this competes directly with our own
    // drag-to-scroll handling on the ScrollViewer (which sits above the event
    // Borders in the tunneling/bubbling chain), so click vs. drag is decided
    // here in one place instead:
    //
    //  - PreviewMouseDown on the ScrollViewer (tunnels in first) just notes
    //    the press position. No capture yet, nothing marked Handled — the
    //    event is still free to reach the Border underneath.
    //  - PreviewMouseMove watches for movement past the OS drag threshold.
    //    Below it: still just a held-down click, nothing happens. Past it:
    //    this is a real drag — capture the mouse and start scrolling.
    //  - PreviewMouseUp on the ScrollViewer ends the drag (if one was
    //    running) and remembers whether a drag just happened in
    //    _wasDragging, then the matching MouseLeftButtonUp bubbles up from
    //    the Border afterwards (tunneling completes before bubbling starts).
    //    The Border's handler only opens the popup when _wasDragging is
    //    false — i.e. the button went down and came back up without ever
    //    crossing the drag threshold.
    private bool _dragArmed;     // mouse is down, watching for movement past the threshold
    private bool _wasDragging;   // a drag just finished on this mouse-up — suppress the click

    private void BeginDrag(MouseButtonEventArgs e, ScrollViewer? sv)
    {
        if (e.ChangedButton != MouseButton.Left || sv == null) return;
        _dragArmed   = true;
        _isDragging  = false;
        _dragStart   = e.GetPosition(sv);
        _dragScrollH = sv.HorizontalOffset;
        _dragScrollV = sv.VerticalOffset;
        // Don't capture or mark Handled yet — a plain click must still be able
        // to reach the event Border underneath.
    }

    private void ContinueDrag(MouseEventArgs e, ScrollViewer? sv)
    {
        if (!_dragArmed || sv == null) return;

        // The left button may have been released elsewhere (e.g. while the
        // detail popup was open) without our PreviewMouseUp ever firing on
        // this ScrollViewer. Don't start or continue a drag in that case —
        // just drop the stale armed state.
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragArmed  = false;
            _isDragging = false;
            return;
        }

        var cur = e.GetPosition(sv);

        if (!_isDragging)
        {
            var dx = Math.Abs(cur.X - _dragStart.X);
            var dy = Math.Abs(cur.Y - _dragStart.Y);
            if (dx < SystemParameters.MinimumHorizontalDragDistance &&
                dy < SystemParameters.MinimumVerticalDragDistance)
                return; // still just a held-down press — not a drag (yet)

            // Movement crossed the threshold: this is a real drag now.
            _isDragging = true;
            sv.CaptureMouse();
            sv.Cursor = Cursors.SizeAll;
        }

        sv.ScrollToHorizontalOffset(_dragScrollH + (_dragStart.X - cur.X));
        sv.ScrollToVerticalOffset  (_dragScrollV + (_dragStart.Y - cur.Y));
        e.Handled = true;
    }

    private void EndDrag(ScrollViewer? sv)
    {
        _dragArmed     = false;
        _wasDragging   = _isDragging;
        if (!_isDragging) return;
        _isDragging = false;
        sv?.ReleaseMouseCapture();
        if (sv != null) sv.Cursor = Cursors.Arrow;
    }

    // Safety net: whatever the reason capture was lost (ReleaseMouseCapture
    // above, an overlay popup taking focus, the window losing activation,
    // etc.), make sure we never stay "stuck" thinking a drag is in progress.
    private void ScrollViewer_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _dragArmed   = false;
        _isDragging  = false;
        if (sender is ScrollViewer sv) sv.Cursor = Cursors.Arrow;
    }

    // ── Event block click → EPG detail popup ───────────────────────────────
    // Fires after PreviewMouseUp/EndDrag above (tunneling completes before
    // bubbling), so _wasDragging already reflects whether this mouse-up
    // followed a real drag. Only open the popup when it didn't.
    private void EventBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var justDragged = _wasDragging;
        _wasDragging = false;
        if (justDragged) return;

        if (sender is FrameworkElement { DataContext: EpgEvent evt } && DataContext is EpgViewModel vm)
            vm.ShowEpgDetailCommand.Execute(evt);
    }

    // ── Scroll to current time on load / orientation change ───────────────────
    public void ScrollToNow()
    {
        if (DataContext is not EpgViewModel vm) return;
        // 108px = 2 slots above "now" — gives context of what was on before
        var target = Math.Max(0, vm.NowLineY - 108);

        if (vm.IsGridHorizontal)
            EventScrollViewer?.ScrollToVerticalOffset(target);
        else
            VEventScrollViewer?.ScrollToHorizontalOffset(target);
    }
}
