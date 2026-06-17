using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using DreamWin.Services;
using DreamWin.ViewModels;
using DreamWin.Views;

namespace DreamWin;

public partial class App : Application
{
    public static SettingsService SettingsService { get; private set; } = null!;
    public static Enigma2Service Enigma2 { get; private set; } = null!;
    public static UpdateService UpdateService { get; private set; } = null!;
    public static MainViewModel MainVM { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        Debug.WriteLine($"[App] OnStartup begin, thread={Environment.CurrentManagedThreadId}");
        base.OnStartup(e);

        // Surface any exception that would otherwise silently kill startup (e.g. inside
        // a Loaded handler) so it shows up in the Debug/Output window instead of leaving
        // a window that looks alive but is actually unresponsive.
        DispatcherUnhandledException += (_, args) =>
        {
            Debug.WriteLine($"[App] DispatcherUnhandledException: {args.Exception}");
        };

        SettingsService = new SettingsService();
        Enigma2 = new Enigma2Service();
        UpdateService = new UpdateService("1.0.0");
        MainVM = new MainViewModel(SettingsService, Enigma2);

        // Subscribe to update service events
        UpdateService.UpdateProgressChanged += (_, msg) => Debug.WriteLine($"[UpdateService] {msg}");
        UpdateService.UpdateError += (_, msg) => Debug.WriteLine($"[UpdateService] ERROR: {msg}");
        UpdateService.UpdateAvailable += (_, msg) => Debug.WriteLine($"[UpdateService] {msg}");

        Debug.WriteLine("[App] OnStartup end");
    }
}
