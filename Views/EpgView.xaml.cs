using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DreamWin.ViewModels;

namespace DreamWin.Views;

public partial class EpgView : UserControl
{
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
                // When grid rows finish loading, scroll "now" into view (vertically)
                if (args.PropertyName == nameof(EpgViewModel.GridRows))
                    Dispatcher.InvokeAsync(ScrollToNow, System.Windows.Threading.DispatcherPriority.Loaded);
            };
        }
    }

    // Scroll the body so the current time is visible near the top (with a small buffer above)
    public void ScrollToNow()
    {
        if (BodyScrollViewer == null) return;
        var vm = DataContext as EpgViewModel;
        if (vm == null) return;
        var nowY = vm.NowLineY;
        // Show 2 slots (108px) above "now" so there is context
        var targetY = Math.Max(0, nowY - 108);
        BodyScrollViewer.ScrollToVerticalOffset(targetY);
    }

    // Sync horizontal scroll of frozen channel headers with body
    private void BodyScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (HeaderScrollViewer != null)
            HeaderScrollViewer.ScrollToHorizontalOffset(e.HorizontalOffset);
    }

    // Forward mouse-wheel on frozen header to body scrollviewer
    private void HeaderScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (BodyScrollViewer == null) return;
        BodyScrollViewer.ScrollToHorizontalOffset(
            BodyScrollViewer.HorizontalOffset - e.Delta * 0.5);
        e.Handled = true;
    }
}
