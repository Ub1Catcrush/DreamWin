using System.Diagnostics;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DreamWin.ViewModels;

namespace DreamWin.Views;

[SupportedOSPlatform("windows")]
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    // Cache of already-created views, keyed by AppView. We keep at most the views the
    // user has actually visited (not all five eagerly) so navigating back preserves state
    // such as scroll position and the currently loaded channel list, while still only ever
    // having ONE view (and therefore at most one VLC VideoView/HwndHost) in the visual tree
    // at any given time. See MainWindow.xaml for why that matters.
    private readonly Dictionary<AppView, FrameworkElement> _viewCache = new();

    private LiveTVView? _liveTvView;
    private MoviesView? _moviesView;

    public MainWindow()
    {
        Debug.WriteLine("[MainWindow] ctor begin");
        InitializeComponent();
        _vm = App.MainVM;
        DataContext = _vm;
        // Do NOT set MaxHeight/MaxWidth here: it clips content behind the taskbar
        // when the window is maximized. WindowChrome + WindowState.Maximized already
        // respects the WorkArea boundary correctly without these overrides.

        _vm.Epg.AddTimerRequested += async (_, evt) => await _vm.Timers.AddFromEpgAsync(evt);

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentView))
                ShowView(_vm.CurrentView);
        };

        SourceInitialized += (_, _) => Debug.WriteLine("[MainWindow] SourceInitialized");
        Activated += (_, _) => Debug.WriteLine("[MainWindow] Activated");
        Deactivated += (_, _) => Debug.WriteLine("[MainWindow] Deactivated");
        IsVisibleChanged += (_, _) => Debug.WriteLine($"[MainWindow] IsVisibleChanged -> {IsVisible}");
        StateChanged += (_, _) => Debug.WriteLine($"[MainWindow] StateChanged -> {WindowState}");

        Loaded += async (_, _) =>
        {
            Debug.WriteLine("[MainWindow] Loaded");
            ShowView(_vm.CurrentView);
            await _vm.InitializeAsync();
            Debug.WriteLine("[MainWindow] InitializeAsync complete");
        };

        Debug.WriteLine("[MainWindow] ctor end");
    }

    private void ShowView(AppView view)
    {
        Debug.WriteLine($"[MainWindow] ShowView({view})");
        if (!_viewCache.TryGetValue(view, out var element))
        {
            element = CreateView(view);
            _viewCache[view] = element;
        }

        ViewHost.Content = element;
    }

    private FrameworkElement CreateView(AppView view)
    {
        switch (view)
        {
            case AppView.LiveTV:
                _liveTvView = new LiveTVView { DataContext = _vm.LiveTV };
                _vm.LiveTV.StreamRequested += (_, url) => _liveTvView!.PlayStream(url);
                _vm.LiveTV.FullscreenRequested += (_, fs) => ToggleFullscreen(fs);
                return _liveTvView;

            case AppView.EPG:
                return new EpgView { DataContext = _vm.Epg };

            case AppView.Timers:
                return new TimersView { DataContext = _vm.Timers };

            case AppView.AutoTimers:
                return new AutoTimersView { DataContext = _vm.AutoTimers };

            case AppView.Movies:
                _moviesView = new MoviesView { DataContext = _vm.Movies };
                _vm.Movies.StreamRequested += (_, url) => _moviesView!.PlayStream(url);
                return _moviesView;

            case AppView.Settings:
                return new SettingsView { DataContext = _vm };

            default:
                throw new ArgumentOutOfRangeException(nameof(view));
        }
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            Topmost = false;
        }
        else
        {
            WindowState = WindowState.Maximized;
            Topmost = false;
        }
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private System.Windows.Threading.DispatcherTimer? _cursorTimer;

    private void ToggleFullscreen(bool fullscreen)
    {
        if (fullscreen)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            Topmost = true;
            if (TitleBarRow != null)
                TitleBarRow.Height = new System.Windows.GridLength(0);
            StartCursorHideTimer();
        }
        else
        {
            Topmost = false;
            ResizeMode = ResizeMode.CanResize;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            if (TitleBarRow != null)
                TitleBarRow.Height = new System.Windows.GridLength(44);
            _cursorTimer?.Stop();
            Mouse.OverrideCursor = null;
        }
    }

    private void StartCursorHideTimer()
    {
        _cursorTimer?.Stop();
        Mouse.OverrideCursor = null;
        _cursorTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _cursorTimer.Tick += (_, _) =>
        {
            _cursorTimer.Stop();
            if (_vm.LiveTV.IsFullscreen)
                Mouse.OverrideCursor = Cursors.None;
        };
        _cursorTimer.Start();
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_vm?.LiveTV.IsFullscreen == true)
        {
            Mouse.OverrideCursor = null;
            StartCursorHideTimer();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Global shortcuts active on any view
        switch (e.Key)
        {
            // Fullscreen
            case Key.F11:
            case Key.F when _vm.CurrentView == AppView.LiveTV:
                _vm.LiveTV.ToggleFullscreenCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Escape when _vm.LiveTV.IsFullscreen:
                _vm.LiveTV.ToggleFullscreenCommand.Execute(null);
                e.Handled = true;
                break;

            // Playback (only when in Live TV)
            case Key.Space when _vm.CurrentView == AppView.LiveTV:
                _vm.LiveTV.PauseResumeCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.M when _vm.CurrentView == AppView.LiveTV:
                _vm.LiveTV.ToggleMuteCommand.Execute(null);
                e.Handled = true;
                break;

            // Volume (no modifier — only when not typing in a TextBox)
            case Key.Add or Key.OemPlus when _vm.CurrentView == AppView.LiveTV
                && !(Keyboard.FocusedElement is TextBox):
                _vm.LiveTV.Volume = Math.Min(100, _vm.LiveTV.Volume + 5);
                e.Handled = true;
                break;

            case Key.Subtract or Key.OemMinus when _vm.CurrentView == AppView.LiveTV
                && !(Keyboard.FocusedElement is TextBox):
                _vm.LiveTV.Volume = Math.Max(0, _vm.LiveTV.Volume - 5);
                e.Handled = true;
                break;

            // Channel navigation
            case Key.PageUp when _vm.CurrentView == AppView.LiveTV:
                _vm.LiveTV.ChannelUpCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.PageDown when _vm.CurrentView == AppView.LiveTV:
                _vm.LiveTV.ChannelDownCommand.Execute(null);
                e.Handled = true;
                break;

            // Refresh
            case Key.F5:
                RefreshCurrentView();
                e.Handled = true;
                break;

            // Quick navigation
            case Key.D1 when Keyboard.Modifiers == ModifierKeys.Control:
                _vm.NavigateCommand.Execute(AppView.LiveTV);
                e.Handled = true;
                break;
            case Key.D2 when Keyboard.Modifiers == ModifierKeys.Control:
                _vm.NavigateCommand.Execute(AppView.EPG);
                e.Handled = true;
                break;
            case Key.D3 when Keyboard.Modifiers == ModifierKeys.Control:
                _vm.NavigateCommand.Execute(AppView.Timers);
                e.Handled = true;
                break;
            case Key.D4 when Keyboard.Modifiers == ModifierKeys.Control:
                _vm.NavigateCommand.Execute(AppView.Movies);
                e.Handled = true;
                break;
        }
    }

    private void RefreshCurrentView()
    {
        switch (_vm.CurrentView)
        {
            case AppView.LiveTV: _ = _vm.LiveTV.LoadBouquetsAsync(); break;
            case AppView.EPG:    _ = _vm.Epg.LoadAsync(); break;
            case AppView.Timers: _ = _vm.Timers.LoadAsync(); break;
            case AppView.AutoTimers: _ = _vm.AutoTimers.LoadAsync(); break;
            case AppView.Movies: _ = _vm.Movies.LoadAsync(); break;
        }
    }
}
