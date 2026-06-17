using System.Windows;
using System.Windows.Controls;
using DreamWin.Models;
using DreamWin.ViewModels;

namespace DreamWin.Views;

public partial class SettingsView : UserControl
{
    private ReceiverConfig? _editingReceiver;

    public SettingsView() => InitializeComponent();

    private MainViewModel? VM => DataContext as MainViewModel;

    private void AddReceiverClick(object sender, RoutedEventArgs e)
    {
        _editingReceiver = null;
        FormTitle.Text = "Add Receiver";
        ClearForm();
        ReceiverForm.Visibility = Visibility.Visible;
    }

    private void EditReceiverClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ReceiverConfig config })
        {
            _editingReceiver = config;
            FormTitle.Text = "Edit Receiver";
            FieldName.Text = config.Name;
            FieldHost.Text = config.Host;
            FieldPort.Text = config.Port.ToString();
            FieldStreamPort.Text = config.StreamingPort.ToString();
            FieldUsername.Text = config.Username ?? "";
            FieldPassword.Password = config.Password ?? "";
            ReceiverForm.Visibility = Visibility.Visible;
        }
    }

    private void ConnectReceiverClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ReceiverConfig config })
            VM?.SwitchReceiverCommand.Execute(config);
    }

    private void RemoveReceiverClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ReceiverConfig config })
        {
            var result = MessageBox.Show($"Remove receiver '{config.Name}'?",
                "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
                VM?.RemoveReceiver(config);
        }
    }

    private void SaveReceiverClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(FieldHost.Text) || string.IsNullOrWhiteSpace(FieldName.Text))
        {
            MessageBox.Show("Name and Host are required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_editingReceiver != null)
        {
            _editingReceiver.Name = FieldName.Text.Trim();
            _editingReceiver.Host = FieldHost.Text.Trim();
            _editingReceiver.Port = int.TryParse(FieldPort.Text, out var p) ? p : 80;
            _editingReceiver.StreamingPort = int.TryParse(FieldStreamPort.Text, out var sp) ? sp : 8001;
            _editingReceiver.Username = string.IsNullOrWhiteSpace(FieldUsername.Text) ? null : FieldUsername.Text;
            _editingReceiver.Password = string.IsNullOrWhiteSpace(FieldPassword.Password) ? null : FieldPassword.Password;
            VM?.UpdateReceiver(_editingReceiver);
        }
        else
        {
            var config = new ReceiverConfig
            {
                Name = FieldName.Text.Trim(),
                Host = FieldHost.Text.Trim(),
                Port = int.TryParse(FieldPort.Text, out var p) ? p : 80,
                StreamingPort = int.TryParse(FieldStreamPort.Text, out var sp) ? sp : 8001,
                Username = string.IsNullOrWhiteSpace(FieldUsername.Text) ? null : FieldUsername.Text,
                Password = string.IsNullOrWhiteSpace(FieldPassword.Password) ? null : FieldPassword.Password,
            };
            VM?.AddReceiver(config);
        }

        ReceiverForm.Visibility = Visibility.Collapsed;
        ClearForm();
    }

    private void CancelFormClick(object sender, RoutedEventArgs e)
    {
        ReceiverForm.Visibility = Visibility.Collapsed;
        ClearForm();
    }

    private void ClearForm()
    {
        FieldName.Text = "My Receiver";
        FieldHost.Text = "";
        FieldPort.Text = "80";
        FieldStreamPort.Text = "8001";
        FieldUsername.Text = "";
        FieldPassword.Password = "";
    }
}
