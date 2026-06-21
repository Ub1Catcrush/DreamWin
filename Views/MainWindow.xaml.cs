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

    // Captured immediately before entering fullscreen video mode, so exiting restores
    // exactly what was there before — whether that was a normal resized window or an
    // already-maximized one — instead of always snapping back to a hardcoded state.
    private bool _isInFullscreenVideo;
    private WindowState _preFullscreenState;
    private double _preFullscreenLeft, _preFullscreenTop, _preFullscreenWidth, _preFullscreenHeight;

    public MainWindow()
    {
        Debug.WriteLine("[MainWindow] ctor begin");
        InitializeComponent();
        _vm = App.MainVM;
        DataContext = _vm;

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
        // WindowChrome + WindowStyle.None does NOT respect the taskbar's WorkArea on its
        // own when maximized — OnWindowStateChanged manually caps MaxHeight/MaxWidth via
        // Win32 interop to compensate. See OnWindowStateChanged for details.
        StateChanged += OnWindowStateChanged;
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
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    // Fix: WPF + WindowChrome + WindowStyle.None does NOT automatically respect the
    // WorkArea (taskbar) when maximizing. We must manually cap MaxHeight/MaxWidth to
    // the current monitor's WorkArea whenever the window is maximized, and clear them
    // when restored so the user can freely resize.
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        Debug.WriteLine($"[MainWindow] StateChanged -> {WindowState}");
        if (WindowState == WindowState.Maximized)
        {
            var workArea = GetCurrentMonitorBounds(useWorkArea: true);
            if (workArea.HasValue)
            {
                MaxWidth = workArea.Value.Width;
                MaxHeight = workArea.Value.Height;
            }
            else
            {
                // Fallback to SystemParameters
                MaxWidth  = SystemParameters.WorkArea.Width;
                MaxHeight = SystemParameters.WorkArea.Height;
            }
        }
        else
        {
            MaxWidth  = double.PositiveInfinity;
            MaxHeight = double.PositiveInfinity;
        }
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private void ToggleFullscreen(bool fullscreen)
    {
        if (fullscreen)
        {
            if (_isInFullscreenVideo) return;
            _isInFullscreenVideo = true;

            // Remember exactly how the window was before, so exiting restores it precisely —
            // whether that was a normal resized window or one that was already maximized.
            _preFullscreenState = WindowState;
            _preFullscreenLeft = Left;
            _preFullscreenTop = Top;
            _preFullscreenWidth = Width;
            _preFullscreenHeight = Height;

            // Hide title bar row
            if (TitleBarRow != null)
                TitleBarRow.Height = new System.Windows.GridLength(0);

            // Hide sidebar
            if (SidebarColumn != null)
                SidebarColumn.Width = new System.Windows.GridLength(0);
            if (SidebarBorder != null)
                SidebarBorder.Visibility = Visibility.Collapsed;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;

            // Clear any work-area cap left over from OnWindowStateChanged (e.g. if the
            // window was maximized before going fullscreen) — otherwise the full-monitor
            // bounds we're about to set below would get clipped right back to the work area.
            MaxWidth = double.PositiveInfinity;
            MaxHeight = double.PositiveInfinity;

            // Cover the ENTIRE monitor, including the area normally reserved for the
            // taskbar — WindowState.Maximized can't do this on its own here, since
            // OnWindowStateChanged deliberately caps maximized windows to the work area
            // (so the *normal* maximized UI doesn't sit behind the taskbar). Fullscreen
            // video wants the opposite: go behind/over the taskbar entirely. Force Normal
            // first so that capping logic doesn't fight these manually-set bounds.
            WindowState = WindowState.Normal;
            var bounds = GetCurrentMonitorBounds(useWorkArea: false);
            if (bounds.HasValue)
            {
                Left = bounds.Value.Left;
                Top = bounds.Value.Top;
                Width = bounds.Value.Width;
                Height = bounds.Value.Height;
            }
            else
            {
                // Fallback if Win32 interop fails for some reason
                WindowState = WindowState.Maximized;
            }

            StartCursorHideTimer();
        }
        else
        {
            if (!_isInFullscreenVideo) return;
            _isInFullscreenVideo = false;

            // Restore title bar
            if (TitleBarRow != null)
                TitleBarRow.Height = new System.Windows.GridLength(44);

            // Restore sidebar
            if (SidebarColumn != null)
                SidebarColumn.Width = new System.Windows.GridLength(200);
            if (SidebarBorder != null)
                SidebarBorder.Visibility = Visibility.Visible;

            Topmost = false;
            ResizeMode = ResizeMode.CanResize;
            WindowStyle = WindowStyle.None;

            // Restore exactly what was there before fullscreen was entered.
            if (_preFullscreenState == WindowState.Maximized)
            {
                WindowState = WindowState.Maximized; // OnWindowStateChanged re-caps to the work area
            }
            else
            {
                WindowState = WindowState.Normal;
                Left = _preFullscreenLeft;
                Top = _preFullscreenTop;
                Width = _preFullscreenWidth;
                Height = _preFullscreenHeight;
            }

            _cursorTimer?.Stop();
            Mouse.OverrideCursor = null;
        }
    }

    /// <summary>
    /// Returns the current monitor's full bounds (useWorkArea: false) or work area
    /// (useWorkArea: true) in WPF device-independent units, or null if the Win32 lookup
    /// failed (caller should fall back to WindowState.Maximized in that case).
    /// </summary>
    private Rect? GetCurrentMonitorBounds(bool useWorkArea)
    {
        var helper = new System.Windows.Interop.WindowInteropHelper(this);
        var src = PresentationSource.FromVisual(this);
        var dpiX = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var dpiY = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        var hMonitor = NativeMethods.MonitorFromWindow(helper.Handle, 2 /*MONITOR_DEFAULTTONEAREST*/);
        var info = new NativeMethods.MONITORINFO();
        info.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(info);
        if (!NativeMethods.GetMonitorInfo(hMonitor, ref info))
            return null;

        var rect = useWorkArea ? info.rcWork : info.rcMonitor;
        return new Rect(
            rect.left / dpiX,
            rect.top / dpiY,
            (rect.right - rect.left) / dpiX,
            (rect.bottom - rect.top) / dpiY);
    }

    private System.Windows.Threading.DispatcherTimer? _cursorTimer;

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
            // Notify LiveTVView to show overlays
            _liveTvView?.OnFullscreenMouseMove();
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

    // Win32 interop used only by OnWindowStateChanged to find the current monitor's
    // WorkArea (excludes the taskbar) for capping MaxWidth/MaxHeight when maximized,
    // since WindowChrome + WindowStyle.None doesn't respect WorkArea on its own.
    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }
    }
}