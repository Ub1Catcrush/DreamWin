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
        _vm.Epg.AddAutoTimerRequested += (_, evt) =>
        {
            // Switch to the AutoTimers tab first so the prefilled edit form (opened
            // synchronously below) is actually visible to the user right away.
            _vm.NavigateCommand.Execute(AppView.AutoTimers);
            _vm.AutoTimers.AddFromEpg(evt);
        };

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentView))
                ShowView(_vm.CurrentView);
        };

        SourceInitialized += (_, _) =>
        {
            Debug.WriteLine("[MainWindow] SourceInitialized");
            InstallLowLevelMouseHook();
        };
        Closed += (_, _) => RemoveLowLevelMouseHook();
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
                var epgView = new EpgView { DataContext = _vm.Epg };
                // After layout, scroll the grid to the current time
                epgView.Dispatcher.InvokeAsync(epgView.ScrollToNow,
                    System.Windows.Threading.DispatcherPriority.Background);
                return epgView;

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

        // While in fullscreen video mode, bounds are set explicitly by
        // ApplyFullMonitorBounds() instead of relying on WindowState.Maximized — skip
        // the work-area cap entirely so it doesn't fight with that.
        if (_isInFullscreenVideo)
        {
            MaxWidth  = double.PositiveInfinity;
            MaxHeight = double.PositiveInfinity;
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            // WPF bug: WindowChrome + WindowStyle.None ignores WorkArea when maximized,
            // causing the window to extend behind the taskbar.
            // Fix: cap MaxHeight/MaxWidth to the current monitor WorkArea via Win32 interop.
            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            var src = PresentationSource.FromVisual(this);
            var dpiX = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            var dpiY = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

            // Use MonitorFromWindow + GetMonitorInfo for per-monitor DPI awareness
            var hMonitor = NativeMethods.MonitorFromWindow(helper.Handle, 2 /*MONITOR_DEFAULTTONEAREST*/);
            var info = new NativeMethods.MONITORINFO();
            info.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(info);
            if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
            {
                MaxWidth  = (info.rcWork.right  - info.rcWork.left)  / dpiX;
                MaxHeight = (info.rcWork.bottom - info.rcWork.top)   / dpiY;
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

    // Sets Left/Top/Width/Height to the CURRENT monitor's full bounds (rcMonitor, which
    // includes the taskbar area) rather than relying on WindowState.Maximized. This is
    // what actually makes fullscreen video cover every pixel with no hairline gap.
    private void ApplyFullMonitorBounds()
    {
        var helper = new System.Windows.Interop.WindowInteropHelper(this);
        var src = PresentationSource.FromVisual(this);
        var dpiX = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var dpiY = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        var hMonitor = NativeMethods.MonitorFromWindow(helper.Handle, 2 /*MONITOR_DEFAULTTONEAREST*/);
        var info = new NativeMethods.MONITORINFO();
        info.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(info);
        if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
        {
            Left   = info.rcMonitor.left / dpiX;
            Top    = info.rcMonitor.top / dpiY;
            Width  = (info.rcMonitor.right  - info.rcMonitor.left) / dpiX;
            Height = (info.rcMonitor.bottom - info.rcMonitor.top)  / dpiY;
        }
        else
        {
            // Fallback: SystemParameters only knows the primary monitor and its work
            // area, but it's better than nothing if the Win32 call somehow fails.
            Left   = 0;
            Top    = 0;
            Width  = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
        }
    }

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

            // Collapse the 1px outer chrome border too — otherwise it's drawn right at
            // the monitor edge and a sliver of desktop/taskbar peeks through behind it.
            if (OuterChromeBorder != null)
                OuterChromeBorder.BorderThickness = new Thickness(0);

            // _isInFullscreenVideo is now true, so OnWindowStateChanged (below) will skip
            // its usual work-area cap.
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            MaxWidth = double.PositiveInfinity;
            MaxHeight = double.PositiveInfinity;

            // Deliberately NOT using WindowState.Maximized here. With WindowChrome +
            // WindowStyle.None, WPF's maximize layout leaves a 1px gap on the left/top/
            // right edges versus the true monitor bounds (related to the invisible resize
            // border WindowChrome still reserves even with ResizeMode.NoResize) — that gap
            // is exactly the "hairline of desktop/taskbar visible at the edges" bug.
            // Instead we stay in WindowState.Normal and set Left/Top/Width/Height to the
            // monitor's FULL bounds (rcMonitor, not rcWork) ourselves via Win32, which
            // covers the screen pixel-for-pixel including the taskbar area.
            WindowState = WindowState.Normal;
            ApplyFullMonitorBounds();

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

            // Restore the 1px outer chrome border now that we're back to windowed mode.
            if (OuterChromeBorder != null)
                OuterChromeBorder.BorderThickness = new Thickness(1);

            Topmost = false;
            ResizeMode = ResizeMode.CanResize;
            WindowStyle = WindowStyle.None;

            // Restore exactly what was there before fullscreen was entered.
            if (_preFullscreenState == WindowState.Maximized)
            {
                // _isInFullscreenVideo is already false, so this re-triggers
                // OnWindowStateChanged's normal work-area cap.
                WindowState = WindowState.Maximized;
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

    // ── Low-level mouse hook ─────────────────────────────────────────────────
    //
    // Why this is needed: the LibVLC VideoView hosts video rendering in a real,
    // separate native CHILD HWND (via HwndHost) — not just a WPF-drawn element.
    // Mouse messages over that child HWND are delivered to ITS OWN window
    // procedure, not the top-level MainWindow's. That means:
    //   - WPF's routed MouseMove/MouseDown events never fire for it (they only
    //     traverse the WPF visual tree, which stops at the HwndHost boundary).
    //   - Even Win32-level HwndSource.AddHook on the top-level window's HWND
    //     does NOT see these messages, because they simply never reach that
    //     HWND's WndProc — they go straight to the child's.
    // Since fullscreen video is almost entirely video surface, essentially all
    // mouse activity was being lost, so the auto-hiding overlay showed once on
    // entry and then never reappeared.
    //
    // The reliable fix is a system-wide low-level mouse hook (WH_MOUSE_LL),
    // which intercepts mouse input in the OS input pipeline before it's even
    // dispatched to any specific HWND, so it sees activity over the video
    // surface, the rest of the window, and anywhere else alike.
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;

    private NativeMethods.LowLevelMouseProc? _mouseHookProc;
    private IntPtr _mouseHookHandle = IntPtr.Zero;

    private void InstallLowLevelMouseHook()
    {
        if (_mouseHookHandle != IntPtr.Zero) return;

        // Keep a reference to the delegate for the hook's lifetime — otherwise the GC
        // can collect it while it's still registered with the OS, crashing the process.
        _mouseHookProc = LowLevelMouseHookCallback;

        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _mouseHookHandle = NativeMethods.SetWindowsHookEx(
            WH_MOUSE_LL,
            _mouseHookProc,
            NativeMethods.GetModuleHandle(curModule.ModuleName!),
            0);

        if (_mouseHookHandle == IntPtr.Zero)
            Debug.WriteLine("[MainWindow] Failed to install low-level mouse hook");
    }

    private void RemoveLowLevelMouseHook()
    {
        if (_mouseHookHandle == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_mouseHookHandle);
        _mouseHookHandle = IntPtr.Zero;
        _mouseHookProc = null;
    }

    private IntPtr LowLevelMouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = wParam.ToInt32();
            if (msg is WM_MOUSEMOVE or WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
            {
                // Only react while THIS window is the active/foreground one, and
                // marshal back onto the UI thread since hook callbacks run inline
                // with the global input pipeline, not on our Dispatcher thread.
                if (IsActive)
                {
                    Dispatcher.BeginInvoke(new Action(OnFullscreenUserActivity));
                }
            }
        }

        return NativeMethods.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
    }

    // Shared "user is active" handling for fullscreen video — shows the cursor, resets
    // its auto-hide timer, and tells LiveTVView to re-show its overlays. Called both
    // from the normal WPF OnMouseMove override (covers input when not in fullscreen,
    // or over plain WPF-drawn areas) and from the low-level mouse hook (covers input
    // over the VLC video's native child HWND, which WPF routed events never see).
    private void OnFullscreenUserActivity()
    {
        if (_vm?.LiveTV.IsFullscreen != true) return;

        Mouse.OverrideCursor = null;
        StartCursorHideTimer();
        _liveTvView?.OnFullscreenMouseMove();
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        OnFullscreenUserActivity();
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

        public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

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