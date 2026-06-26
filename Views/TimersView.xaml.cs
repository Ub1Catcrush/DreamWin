using System.Windows.Controls;
using System.Windows.Input;
namespace DreamWin.Views;
public partial class TimersView : UserControl
{
    public TimersView() => InitializeComponent();

    // Stops a click inside the picker dialog from bubbling up to the backdrop's
    // close-on-click MouseBinding. Using MouseDown (bubbling, fires after children
    // have processed their click) rather than PreviewMouseDown (tunneling, fires
    // before children) so the ✕ close button inside the dialog still works.
    private void ChannelPickerDialog_MouseDown(object sender, MouseButtonEventArgs e)
        => e.Handled = true;
}
