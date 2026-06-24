using System.Diagnostics;
using System.Threading.Tasks;
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

        // Show the splash immediately — before any service/native-library work — so the
        // user sees something the instant the process starts, instead of a blank taskbar
        // entry while LibVLC's native libraries load on first navigation to Live TV.
        var splash = new SplashWindow();
        splash.Show();

        // Surface any exception that would otherwise silently kill startup (e.g. inside
        // a Loaded handler) so it shows up in the Debug/Output window instead of leaving
        // a window that looks alive but is actually unresponsive.
        DispatcherUnhandledException += (_, args) =>
        {
            Debug.WriteLine($"[App] DispatcherUnhandledException: {args.Exception}");
            args.Handled = true;
            MessageBox.Show(
                $"An unexpected error occurred:\n\n{args.Exception.Message}\n\nThe application will continue running.",
                "DreamWin — Unexpected Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Debug.WriteLine($"[App] Fatal unhandled: {args.ExceptionObject}");

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Debug.WriteLine($"[App] Unobserved task exception: {args.Exception}");
            args.SetObserved();
        };

        splash.SetStatus("Loading settings…");
        SettingsService = new SettingsService();
        Enigma2 = new Enigma2Service();
        // Read version from FileVersionInfo — this reflects <Version> in DreamWin.csproj
        // and is always accurate regardless of GenerateAssemblyInfo setting.
        // Assembly.GetName().Version returns 0.0.0.0 when GenerateAssemblyInfo=false
        // unless [assembly: AssemblyVersion(...)] is explicitly declared.
        string appVersion;
        try
        {
            var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            var v = fvi.ProductVersion ?? fvi.FileVersion ?? "1.0.0";
            // Strip any commit suffix (e.g. "1.2.3+abc1234" → "1.2.3")
            appVersion = v.Contains('+') ? v[..v.IndexOf('+')] : v;
        }
        catch
        {
            appVersion = "1.0.0";
        }
        UpdateService = new UpdateService(appVersion);
        MainVM = new MainViewModel(SettingsService, Enigma2);

        // Warn user if settings file was corrupted/unreadable
        if (SettingsService.Settings.LoadError != null)
        {
            MessageBox.Show(SettingsService.Settings.LoadError,
                "Settings Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // Subscribe to update service events
        UpdateService.UpdateProgressChanged += (_, msg) => Debug.WriteLine($"[UpdateService] {msg}");
        UpdateService.UpdateError += (_, msg) => Debug.WriteLine($"[UpdateService] ERROR: {msg}");
        UpdateService.UpdateAvailable += (_, msg) => Debug.WriteLine($"[UpdateService] {msg}");

        splash.SetStatus("Starting player engine…");

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;

        // Close the splash as soon as we know the first view is actually ready to show.
        // Live TV is the default landing view, and its LibVLC init is the real "library
        // loading" this splash exists for — VlcReady fires the instant that finishes.
        // Loaded is kept as a fallback in case the default view is ever changed to
        // something that doesn't touch LibVLC, and a timeout guards against the splash
        // getting stuck open if something throws before either fires.
        var splashClosed = false;
        void CloseSplashOnce()
        {
            if (splashClosed) return;
            splashClosed = true;
            LiveTVView.VlcReady -= CloseSplashOnce;
            // Wait one more render frame so the main window is fully painted before splash disappears
            mainWindow.Dispatcher.InvokeAsync(() =>
            {
                splash.Close();
                mainWindow.Activate();
            }, System.Windows.Threading.DispatcherPriority.Render);
        }

        // VlcReady fires after LibVLC Core.Initialize() in LiveTVView — reliable signal
        // that the heaviest startup work is done and the window is ready for use.
        LiveTVView.VlcReady += CloseSplashOnce;

        // ContentRendered fires after the first frame is fully painted on screen (later than Loaded).
        // Use as fallback in case the user navigates away from LiveTV before VlcReady fires.
        mainWindow.ContentRendered += (_, _) =>
        {
            // Delay slightly so the window is fully visible before splash disappears
            _ = mainWindow.Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(500);
                CloseSplashOnce();
            });
        };

        // Hard timeout: 12 seconds max for splash regardless of what happens
        _ = Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(12));
            CloseSplashOnce();
        });

        mainWindow.Show();

        Debug.WriteLine("[App] OnStartup end");
    }
}
