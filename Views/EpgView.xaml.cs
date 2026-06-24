using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    // A plain click on an event block must still open the EPG detail popup via
    // that Border's MouseBinding. If we captured the mouse and marked the event
    // Handled on the very first MouseDown, the click never reaches the Border —
    // the ScrollViewer swallows it and the UI looks like it always starts a
    // drag. So: on MouseDown we only remember where the press started (no
    // capture, not Handled yet). Only once the mouse actually moves past a
    // small threshold do we promote it to a real drag — at that point we
    // capture the mouse and start consuming move events. A short click that
    // never crosses the threshold is left alone and bubbles normally to the
    // Border's MouseBinding/Command.
    //
    // The EPG detail popup is a separate overlay (Grid with its own
    // MouseBinding/buttons) drawn on top of everything, not a child of the
    // ScrollViewer. Closing it therefore never raises a MouseUp on the
    // ScrollViewer, so EndDrag previously never ran and _dragArmed stayed
    // true — the very next mouse move over the grid (even with the button
    // no longer held) was then misread as an in-progress drag. To guard
    // against that: (1) ContinueDrag re-checks that the left button is
    // actually still pressed before doing anything, dropping the armed
    // state otherwise, and (2) LostMouseCapture always clears both flags,
    // covering any case where capture is released or stolen without our
    // own MouseUp handler running (e.g. focus moving to an overlay).
    private bool _dragArmed;   // mouse is down, watching for movement past the threshold

    private void BeginDrag(MouseButtonEventArgs e, ScrollViewer? sv)
    {
        if (e.ChangedButton != MouseButton.Left || sv == null) return;
        _dragArmed   = true;
        _isDragging  = false;
        _dragStart   = e.GetPosition(sv);
        _dragScrollH = sv.HorizontalOffset;
        _dragScrollV = sv.VerticalOffset;
        // Don't capture or mark Handled yet — a plain click must still be able
        // to reach the event Border underneath and trigger its MouseBinding.
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
                return; // still just a press — not a drag (yet)

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
        _dragArmed = false;
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
        _dragArmed  = false;
        _isDragging = false;
        if (sender is ScrollViewer sv) sv.Cursor = Cursors.Arrow;
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
