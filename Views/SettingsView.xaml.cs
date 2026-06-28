using System.Windows;
using System.Windows.Controls;
using DreamWin.Models;
using DreamWin.Services;
using DreamWin.ViewModels;

namespace DreamWin.Views;

public partial class SettingsView : UserControl
{
    private ReceiverConfig? _editingReceiver;

    public SettingsView() => InitializeComponent();

    private MainViewModel? VM => DataContext as MainViewModel;

    private void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        // Subscribe to checkbox changes to save settings
        // This will be handled through two-way binding and code-behind if needed
    }

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
            FieldUseHttps.IsChecked = config.UseHttps;
            FieldAcceptSelfSigned.IsChecked = config.AcceptSelfSignedCert;
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
            _editingReceiver.UseHttps = FieldUseHttps.IsChecked == true;
            _editingReceiver.AcceptSelfSignedCert = FieldAcceptSelfSigned.IsChecked == true;
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
                UseHttps = FieldUseHttps.IsChecked == true,
                AcceptSelfSignedCert = FieldAcceptSelfSigned.IsChecked == true,
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
        FieldUseHttps.IsChecked = false;
        FieldAcceptSelfSigned.IsChecked = false;
    }

    private void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        var lang = App.SettingsService.Settings.Language;
        LocalizationService.Instance.Language = lang;
        App.SettingsService.Save();
    }

    private void UpdateSettingChanged(object sender, RoutedEventArgs e)
    {
        App.SettingsService.Settings.Save();
    }

    // Saves VLC settings whenever any VLC control changes, then notifies all active
    // media components so they can apply the new settings to any running stream immediately
    // without needing to restart LibVLC or switch channels.
    private void VlcSettingChanged(object sender, RoutedEventArgs e)
    {
        App.SettingsService.Save();
        App.SettingsService.NotifyVlcSettingsChanged();
    }

    private void ThemePresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string presetName })
            VM?.ApplyThemePresetCommand.Execute(presetName);
    }

    private void ApplyCustomAccentClick(object sender, RoutedEventArgs e)
    {
        if (AccentColorInput != null)
            VM?.ApplyCustomAccentCommand.Execute(AccentColorInput.Text.Trim());
    }
}
