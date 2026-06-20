using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DreamWin.Views;

public partial class EpgView : UserControl
{
    public EpgView() => InitializeComponent();

    // Sync horizontal scroll of channel header with body
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
