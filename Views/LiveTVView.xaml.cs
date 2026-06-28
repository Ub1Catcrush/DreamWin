using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LibVLCSharp.Shared;
using DreamWin.Models;
using DreamWin.ViewModels;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace DreamWin.Views;

[SupportedOSPlatform("windows")]
public partial class LiveTVView : UserControl
{
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private bool _vlcInitialized;
    private LiveTVViewModel? _vm;

    // Remembers the NAME of the last audio track the user explicitly picked (e.g. via
    // the audio track combo box), so that switching to a different channel that offers
    // a same-named track (e.g. "Deutsch"/"English") restores that preference instead of
    // always falling back to "first available track". Names are matched rather than IDs
    // because VLC's per-stream track Ids aren't stable/comparable across channels.
    private string? _preferredAudioTrackName;
    private bool _suppressAudioTrackSelectionTracking;

    public static LiveTVView? Instance { get; private set; }

    /// <summary>
    /// Raised once after LibVLC has been initialized (or immediately, if it already was,
    /// for any subscriber that attaches late). Lets App.xaml.cs know precisely when the
    /// splash screen can be dismissed, instead of guessing based on WPF Loaded-event timing.
    /// </summary>
    public static event Action? VlcReady;

    public LiveTVView()
    {
        InitializeComponent();
        Instance = this;
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_vlcInitialized) return;
        _vlcInitialized = true;

        Debug.WriteLine("[LiveTVView] Initializing LibVLC");
        Core.Initialize();
        _libVlc = new LibVLC(App.SettingsService.Settings.BuildVlcArgs());
        _mediaPlayer = new MediaPlayer(_libVlc);
        VideoView.MediaPlayer = _mediaPlayer;
        _mediaPlayer.Playing += OnMediaPlayerPlaying;
        // ES/stream events: refresh audio track list when elementary streams are added/changed
        _mediaPlayer.ESAdded += OnMediaPlayerEsAdded;
        _mediaPlayer.MediaChanged += OnMediaPlayerMediaChanged;
        Debug.WriteLine("[LiveTVView] LibVLC initialized");
        VlcReady?.Invoke();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.StreamRequested -= Vm_StreamRequested;
            _vm.PropertyChanged -= Vm_PropertyChanged;
        }

        if (e.NewValue is LiveTVViewModel vm)
        {
            _vm = vm;
            _vm.StreamRequested += Vm_StreamRequested;
            _vm.PropertyChanged += Vm_PropertyChanged;

            // Wire channel list filtering via CollectionViewSource
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_vm.Services);
            view.Filter = o => o is Service s &&
                (string.IsNullOrEmpty(_vm.SearchText) ||
                 s.ServiceName.Contains(_vm.SearchText, StringComparison.OrdinalIgnoreCase));

            _vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(LiveTVViewModel.SearchText))
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(view.Refresh);
            };
        }
    }

    private void Vm_StreamRequested(object? sender, string url)
    {
        // Enforce single stream: stop recordings player if running
        MoviesView.Instance?.StopPlayback();
        // Prevent double-starting the same stream
        if (_mediaPlayer?.IsPlaying == true && _mediaPlayer.Media?.Mrl == url) return;
        PlayStream(url);
    }

    public void StopPlayback()
    {
        _mediaPlayer?.Stop();
        if (_vm != null) _vm.IsPlaying = false;
    }

    // Per-bouquet scroll positions: key = bouquet ServiceReference
    private readonly Dictionary<string, double> _bouquetScrollPositions = new();

    // Fullscreen overlay auto-hide timer
    private System.Windows.Threading.DispatcherTimer? _overlayTimer;

    private void ShowFullscreenOverlays()
    {
        if (PlayerControlsOverlay != null)
            PlayerControlsOverlay.Visibility = Visibility.Visible;
        if (FullscreenEpgOverlay != null)
            FullscreenEpgOverlay.Visibility = Visibility.Visible;
        if (FullscreenChannelOverlay != null)
            FullscreenChannelOverlay.Visibility = Visibility.Visible;

        _overlayTimer?.Stop();
        _overlayTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _overlayTimer.Tick += (_, _) =>
        {
            _overlayTimer?.Stop();
            if (_vm?.IsFullscreen == true)
                HideFullscreenOverlays();
        };
        _overlayTimer.Start();
    }

    private void HideFullscreenOverlays()
    {
        // In fullscreen, hide EPG and channel overlays (controls stay visible briefly)
        if (FullscreenEpgOverlay != null)
            FullscreenEpgOverlay.Visibility = Visibility.Collapsed;
        if (FullscreenChannelOverlay != null)
            FullscreenChannelOverlay.Visibility = Visibility.Collapsed;
        // Controls overlay fades too, but only when NOT hovering over it
        if (PlayerControlsOverlay != null && _vm?.IsFullscreen == true)
            PlayerControlsOverlay.Visibility = Visibility.Collapsed;
    }

    private void EnterFullscreenOverlayMode()
    {
        // Fullscreen video should show nothing but the video itself plus the transient
        // auto-hiding overlays — the permanent channel-list column and the now/next info
        // bar below the video are not part of that.
        // MinWidth must be cleared too: a ColumnDefinition's Width is clamped by its
        // MinWidth regardless of what Width is set to, so Width=0 alone does nothing
        // while MinWidth="260" (set in XAML) is still in effect.
        ChannelListColumn.MinWidth = 0;
        ChannelListColumn.Width = new GridLength(0);
        ChannelListGutterColumn.Width = new GridLength(0);
        if (ChannelListPanel != null) ChannelListPanel.Visibility = Visibility.Collapsed;

        NowNextSplitterRow.Height = new GridLength(0);
        NowNextRow.Height = new GridLength(0);
        if (NowNextSplitter != null) NowNextSplitter.Visibility = Visibility.Collapsed;
        if (NowNextBar != null) NowNextBar.Visibility = Visibility.Collapsed;

        // Show overlays initially, then auto-hide
        ShowFullscreenOverlays();
    }

    private void ExitFullscreenOverlayMode()
    {
        ChannelListColumn.Width = new GridLength(320);
        ChannelListColumn.MinWidth = 260;
        ChannelListGutterColumn.Width = new GridLength(6);
        if (ChannelListPanel != null) ChannelListPanel.Visibility = Visibility.Visible;

        NowNextSplitterRow.Height = new GridLength(4);
        NowNextRow.Height = GridLength.Auto;
        if (NowNextSplitter != null) NowNextSplitter.Visibility = Visibility.Visible;
        if (NowNextBar != null) NowNextBar.Visibility = Visibility.Visible;

        _overlayTimer?.Stop();
        // Restore controls visibility (it's bound to IsPlaying normally)
        if (PlayerControlsOverlay != null)
            PlayerControlsOverlay.Visibility = Visibility.Visible;
        if (FullscreenEpgOverlay != null)
            FullscreenEpgOverlay.Visibility = Visibility.Collapsed;
        if (FullscreenChannelOverlay != null)
            FullscreenChannelOverlay.Visibility = Visibility.Collapsed;
    }

    private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_vm == null) return;

        if (e.PropertyName == nameof(LiveTVViewModel.IsFullscreen))
        {
            if (_vm.IsFullscreen)
                EnterFullscreenOverlayMode();
            else
                ExitFullscreenOverlayMode();
            return;
        }

        // Save scroll position when bouquet changes, restore for new bouquet
        if (e.PropertyName == nameof(LiveTVViewModel.SelectedBouquet))
        {
            SaveChannelScrollPosition();
        }
        else if (e.PropertyName == nameof(LiveTVViewModel.Services))
        {
            Dispatcher.InvokeAsync(() =>
            {
                RestoreChannelScrollPosition();
                ScrollToCurrentChannel();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else if (e.PropertyName == nameof(LiveTVViewModel.SelectedService))
        {
            // Scroll channel into view when changed via keyboard shortcut
            Dispatcher.InvokeAsync(ScrollToCurrentChannel, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        if (_mediaPlayer == null) return;

        if (e.PropertyName == nameof(LiveTVViewModel.Volume))
            _mediaPlayer.Volume = (int)_vm.Volume;
        else if (e.PropertyName == nameof(LiveTVViewModel.IsMuted))
            _mediaPlayer.Mute = _vm.IsMuted;
        else if (e.PropertyName == nameof(LiveTVViewModel.IsPaused))
        {
            try { _mediaPlayer.SetPause(_vm.IsPaused); }
            catch
            {
                if (_vm.IsPaused) _mediaPlayer.Pause();
                else if (!_mediaPlayer.IsPlaying) _mediaPlayer.Play();
            }
        }
        else if (e.PropertyName == nameof(LiveTVViewModel.SelectedAudioTrack) && _vm.SelectedAudioTrack != null)
        {
            _mediaPlayer.SetAudioTrack(_vm.SelectedAudioTrack.Id);

            // Only remember this as the user's preference if it wasn't us assigning it
            // programmatically (e.g. RefreshAudioTracks auto-picking a default track).
            if (!_suppressAudioTrackSelectionTracking)
                _preferredAudioTrackName = _vm.SelectedAudioTrack.Name;
        }
    }

    private void SaveChannelScrollPosition()
    {
        if (_vm?.SelectedBouquet == null) return;
        var sv = GetChannelScrollViewer();
        if (sv != null)
            _bouquetScrollPositions[_vm.SelectedBouquet.ServiceReference] = sv.VerticalOffset;
    }

    private void RestoreChannelScrollPosition()
    {
        if (_vm?.SelectedBouquet == null) return;
        var sv = GetChannelScrollViewer();
        if (sv == null) return;
        if (_bouquetScrollPositions.TryGetValue(_vm.SelectedBouquet.ServiceReference, out var pos))
            sv.ScrollToVerticalOffset(pos);
        else
            sv.ScrollToTop();
    }

    private void ScrollToCurrentChannel()
    {
        if (_vm?.SelectedService == null || ChannelListBox == null) return;
        ChannelListBox.ScrollIntoView(_vm.SelectedService);
    }

    private ScrollViewer? GetChannelScrollViewer()
    {
        if (ChannelListBox == null) return null;
        var border = VisualTreeHelper.GetChild(ChannelListBox, 0);
        if (border == null) return null;
        return VisualTreeHelper.GetChild(border, 0) as ScrollViewer;
    }

    // Fail-safe: hides the channel-switch mask even if the stream never reaches the
    // Playing state (bad URL, dead channel, timeout) so the screen doesn't stay black.
    private System.Windows.Threading.DispatcherTimer? _channelSwitchMaskTimer;

    private void ShowChannelSwitchMask()
    {
        if (ChannelSwitchMask != null)
            ChannelSwitchMask.Visibility = Visibility.Visible;

        _channelSwitchMaskTimer?.Stop();
        _channelSwitchMaskTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(6)
        };
        _channelSwitchMaskTimer.Tick += (_, _) =>
        {
            _channelSwitchMaskTimer?.Stop();
            HideChannelSwitchMask();
        };
        _channelSwitchMaskTimer.Start();
    }

    private void HideChannelSwitchMask()
    {
        _channelSwitchMaskTimer?.Stop();
        if (ChannelSwitchMask != null)
            ChannelSwitchMask.Visibility = Visibility.Collapsed;
    }

    private void OnMediaPlayerPlaying(object? sender, EventArgs e)
    {
        if (_mediaPlayer == null || _vm == null) return;

        Dispatcher.InvokeAsync(RefreshAudioTracks);

        // Playing means VLC's pipeline has started, NOT that a frame has actually been
        // presented to the native video window yet — there's a further short gap during
        // which that window's blank/white background can still show through. Unmasking
        // immediately on Playing just moved the flash slightly later instead of removing
        // it (black mask -> white gap -> real picture). A short delay here gives VLC time
        // to actually present a frame before we reveal the native surface underneath.
        _ = Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(250);
            HideChannelSwitchMask();
        });
    }

            private void OnMediaPlayerEsAdded(object? sender, EventArgs e)
            {
                if (_mediaPlayer == null || _vm == null) return;
                Dispatcher.InvokeAsync(RefreshAudioTracks);
            }

            private void OnMediaPlayerMediaChanged(object? sender, EventArgs e)
            {
                if (_mediaPlayer == null || _vm == null) return;
                Dispatcher.InvokeAsync(RefreshAudioTracks);
            }

            private void RefreshAudioTracks()
            {
                try
                {
                    if (_mediaPlayer == null || _vm == null) return;
                    _vm.AudioTracks.Clear();
                    var desc = _mediaPlayer.AudioTrackDescription ?? Array.Empty<LibVLCSharp.Shared.Structures.TrackDescription>();
                    Debug.WriteLine($"[LiveTV] RefreshAudioTracks: found {desc.Length} descriptions (AudioTrackCount={_mediaPlayer.AudioTrackCount})");
                    foreach (var audio in desc)
                    {
                        Debug.WriteLine($"[LiveTV] AudioTrack id={audio.Id} name='{audio.Name}'");
                        _vm.AudioTracks.Add(new AudioTrackInfo
                        {
                            Id = audio.Id,
                            Name = string.IsNullOrWhiteSpace(audio.Name) ? $"Track {audio.Id}" : audio.Name
                        });
                    }

                    // VLC always includes a pseudo-track with Id == -1 representing "no
                    // audio track" ("Disable"/"Désactivé"). It must never be picked as a
                    // default/fallback selection — only real, playable tracks should be.
                    var realTracks = _vm.AudioTracks.Where(a => a.Id != -1).ToList();

                    AudioTrackInfo? target = null;

                    // 1) Prefer whatever the user explicitly picked before (matched by
                    //    name, since VLC's per-stream track Ids aren't stable/comparable
                    //    across different channels/streams).
                    if (_preferredAudioTrackName != null)
                        target = realTracks.FirstOrDefault(a =>
                            string.Equals(a.Name, _preferredAudioTrackName, StringComparison.OrdinalIgnoreCase));

                    // 2) Otherwise, the first real (non-disabled) audio track.
                    target ??= realTracks.FirstOrDefault();

                    // 3) Truly no real tracks at all (audio-less stream) — fall back to
                    //    whatever VLC reports, even if that's the disabled entry, just so
                    //    SelectedAudioTrack/the combo box isn't left dangling on nothing.
                    target ??= _vm.AudioTracks.FirstOrDefault();

                    if (target != null)
                    {
                        // Mark this as a programmatic assignment so Vm_PropertyChanged
                        // doesn't mistake it for a user choice and overwrite the
                        // remembered preference with it.
                        _suppressAudioTrackSelectionTracking = true;
                        try { _vm.SelectedAudioTrack = target; }
                        finally { _suppressAudioTrackSelectionTracking = false; }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[LiveTV] RefreshAudioTracks failed: {ex}");
                }
            }

    public void PlayStream(string url)
    {
        if (_libVlc == null || _mediaPlayer == null || string.IsNullOrEmpty(url)) return;

        // Close teletext when switching channels
        if (_teletextActive) Dispatcher.InvokeAsync(CloseTeletext);

        // Mask the video surface immediately. The actual VLC render surface is a
        // native child window (see the ChannelSwitchMask comment in XAML) that briefly
        // shows its own blank/white background between the old stream's last frame and
        // the new stream's first one — this WPF-level overlay covers that gap.
        ShowChannelSwitchMask();

        try
        {
            // NOTE: deliberately NOT calling _mediaPlayer.Stop() here first. Stop()
            // immediately clears the native video surface back to its blank state,
            // which is what produced the white flash in the first place — Play() with
            // a new Media handles tearing down the previous stream internally without
            // that extra blank step, and is the standard way to switch streams in VLC.
            var media = new Media(_libVlc, new Uri(url));
            // Add VLC options to reduce startup flash: disable video title and deinterlace
            media.AddOption(":no-video-title-show");
            media.AddOption(":network-caching=800");
            _mediaPlayer.Play(media);
            media.Dispose(); // VLC retains its own reference

            if (_vm != null)
            {
                _mediaPlayer.Volume = (int)_vm.Volume;
                _mediaPlayer.Mute = _vm.IsMuted;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LiveTV] PlayStream error: {ex}");
            HideChannelSwitchMask();
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(800);
            _ = Dispatcher.InvokeAsync(RefreshAudioTracks);
            await Task.Delay(1500);
            _ = Dispatcher.InvokeAsync(RefreshAudioTracks);
        });
    }

    public void Stop()
    {
        _mediaPlayer?.Stop();
    }

    // Double-click on the video is handled in MainWindow's low-level mouse hook
    // (LowLevelMouseHookCallback) because the LibVLC native child HWND consumes
    // mouse messages before WPF ever sees them — so MouseDoubleClick on the VideoView
    // element never fires reliably. See MainWindow.xaml.cs for the implementation.

    // Called from MainWindow's low-level mouse hook to check whether a screen-space
    // point lands inside the video rendering surface, for double-click detection.
    // ── Teletext ─────────────────────────────────────────────────────────

    private bool _teletextActive;
    private int  _teletextPage = 100;

    private void TeletextToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_teletextActive) CloseTeletext();
        else                 OpenTeletext();
    }

    private void OpenTeletext()
    {
        if (_mediaPlayer == null) return;
        _teletextActive = true;
        // Setting Teletext to a page number activates teletext rendering in VLC
        _mediaPlayer.Teletext = _teletextPage;
        TeletextBar.Visibility = Visibility.Visible;
        TeletextPageBox.Text = _teletextPage.ToString();
        TeletextToggleBtn.Foreground = FindResource("Accent") as System.Windows.Media.Brush;
    }

    private void CloseTeletext()
    {
        if (_mediaPlayer == null) return;
        _teletextActive = false;
        // Setting Teletext to 0 deactivates teletext
        _mediaPlayer.Teletext = 0;
        TeletextBar.Visibility = Visibility.Collapsed;
        TeletextToggleBtn.ClearValue(ForegroundProperty);
    }

    private void SeekTeletextPage(int page)
    {
        _teletextPage = Math.Clamp(page, 100, 899);
        TeletextPageBox.Text = _teletextPage.ToString();
        if (_mediaPlayer != null) _mediaPlayer.Teletext = _teletextPage;
    }

    private void TeletextGoPage_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(TeletextPageBox.Text, out var p)) SeekTeletextPage(p);
    }

    private void TeletextPageBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            if (int.TryParse(TeletextPageBox.Text, out var p)) SeekTeletextPage(p);
            e.Handled = true;
        }
    }

    private void TeletextHome_Click  (object sender, RoutedEventArgs e) => SeekTeletextPage(100);
    private void TeletextPrev_Click  (object sender, RoutedEventArgs e) => SeekTeletextPage(_teletextPage - 1 < 100 ? 899 : _teletextPage - 1);
    private void TeletextNext_Click  (object sender, RoutedEventArgs e) => SeekTeletextPage(_teletextPage + 1 > 899 ? 100 : _teletextPage + 1);
    private void TeletextClose_Click (object sender, RoutedEventArgs e) => CloseTeletext();

    // Fastext colour buttons — use VLC's TeletextKey enum values
    private void TeletextRed_Click   (object sender, RoutedEventArgs e) { if (_mediaPlayer != null) _mediaPlayer.Teletext = (int)LibVLCSharp.Shared.TeletextKey.Red; }
    private void TeletextGreen_Click (object sender, RoutedEventArgs e) { if (_mediaPlayer != null) _mediaPlayer.Teletext = (int)LibVLCSharp.Shared.TeletextKey.Green; }
    private void TeletextYellow_Click(object sender, RoutedEventArgs e) { if (_mediaPlayer != null) _mediaPlayer.Teletext = (int)LibVLCSharp.Shared.TeletextKey.Yellow; }
    private void TeletextBlue_Click  (object sender, RoutedEventArgs e) { if (_mediaPlayer != null) _mediaPlayer.Teletext = (int)LibVLCSharp.Shared.TeletextKey.Blue; }

    public bool IsScreenPointInVideo(System.Windows.Point screenPt)
    {
        try
        {
            var localPt = VideoView.PointFromScreen(screenPt);
            return localPt.X >= 0 && localPt.Y >= 0
                && localPt.X <= VideoView.ActualWidth
                && localPt.Y <= VideoView.ActualHeight;
        }
        catch { return false; }
    }

    // Called from MainWindow.OnMouseMove when in fullscreen — shows overlays temporarily
    public void OnFullscreenMouseMove()
    {
        if (_vm?.IsFullscreen == true)
            ShowFullscreenOverlays();
    }

    private void ChannelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm == null) return;
        if (sender is ListBox listBox && listBox.SelectedItem is Service service)
            _vm.SelectServiceCommand.Execute(service);
    }

    private void ScrollBouquetsLeftClick(object sender, RoutedEventArgs e)
    {
        if (BouquetScrollViewer == null) return;
        BouquetScrollViewer.ScrollToHorizontalOffset(Math.Max(0, BouquetScrollViewer.HorizontalOffset - 180));
    }

    private void ScrollBouquetsRightClick(object sender, RoutedEventArgs e)
    {
        if (BouquetScrollViewer == null) return;
        BouquetScrollViewer.ScrollToHorizontalOffset(Math.Min(
            BouquetScrollViewer.ScrollableWidth,
            BouquetScrollViewer.HorizontalOffset + 180));
    }
}
