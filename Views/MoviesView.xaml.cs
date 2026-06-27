using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LibVLCSharp.Shared;
using DreamWin.ViewModels;

namespace DreamWin.Views;

[SupportedOSPlatform("windows")]
public partial class MoviesView : UserControl
{
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private MoviesViewModel? _vm;
    private bool _vlcInitialized;

    public static MoviesView? Instance { get; private set; }

    public MoviesView()
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
        Core.Initialize();
        _libVlc = new LibVLC(App.SettingsService.Settings.BuildVlcArgs());
        _mediaPlayer = new MediaPlayer(_libVlc);
        MovieVideoView.MediaPlayer = _mediaPlayer;

        _mediaPlayer.Playing += OnPlaying;
        _mediaPlayer.Paused += (_, _) => Dispatcher.InvokeAsync(() => { if (_vm != null) _vm.IsPaused = true; });
        _mediaPlayer.Stopped += (_, _) => Dispatcher.InvokeAsync(() => { if (_vm != null) { _vm.IsPlaying = false; _vm.PositionPercent = 0; } });
        _mediaPlayer.TimeChanged += OnTimeChanged;
        _mediaPlayer.LengthChanged += OnLengthChanged;
        _mediaPlayer.ESAdded += OnEsAdded;
        Debug.WriteLine("[MoviesView] LibVLC initialized");
    }

    /// <summary>Seek relative to current position (seconds, negative = back).</summary>
    public void SeekRelative(double offsetSec)
    {
        if (_mediaPlayer == null) return;
        var currentMs  = _mediaPlayer.Time;
        if (currentMs < 0) return;

        // Length is used only to clamp against overshooting the end, and only when
        // actually known — it must NOT gate the seek itself. See SeekByPosition's
        // doc comment: Length can stay 0 well after playback has visibly started on
        // some TS streams (this was previously dropping every seek attempt silently
        // whenever that happened, which looked indistinguishable from "seeking does
        // nothing").
        var durationMs = _mediaPlayer.Length;
        var targetMs = durationMs > 0
            ? (long)Math.Max(0, Math.Min(durationMs - 1000, currentMs + offsetSec * 1000))
            : (long)Math.Max(0, currentMs + offsetSec * 1000);

        if (durationMs > 0)
        {
            SeekByPosition((float)(targetMs / (double)durationMs), fallbackTargetMs: targetMs);
        }
        else
        {
            // No known duration to convert to a fraction yet — nudge Time directly
            // instead. This still benefits from IsSeekable (set Time, no restart) and
            // simply can't clamp against the end since the end isn't known.
            Debug.WriteLine($"[MoviesView] SeekRelative: Length unknown, using Time directly (target={targetMs}ms)");
            if (_mediaPlayer.IsSeekable)
                _mediaPlayer.Time = targetMs;
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.StreamRequested -= OnStreamRequested;
            _vm.StopRequested   -= OnStopRequested;
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.SeekRequested   -= OnSeekRequested;
        }
        if (e.NewValue is MoviesViewModel vm)
        {
            _vm = vm;
            _vm.StreamRequested += OnStreamRequested;
            _vm.StopRequested   += OnStopRequested;
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.SeekRequested   += OnSeekRequested;
        }
    }

    private void OnSeekRequested(object? sender, double offsetSec)
        => SeekRelative(offsetSec);

    private void OnStreamRequested(object? sender, string url)
    {
        // Enforce single stream: stop live TV if running
        LiveTVView.Instance?.StopPlayback();
        PlayStream(url);
    }

    private void OnStopRequested(object? sender, EventArgs e)
    {
        _mediaPlayer?.Stop();
    }

    public void StopPlayback()
    {
        _mediaPlayer?.Stop();
        if (_vm != null) _vm.IsPlaying = false;
    }

    public void PlayStream(string url)
    {
        if (_libVlc == null || _mediaPlayer == null || string.IsNullOrEmpty(url)) return;
        Dispatcher.Invoke(() =>
        {
            Debug.WriteLine($"[MoviesView] PlayStream raw url string: {url}");
            var uri = new Uri(url);
            Debug.WriteLine($"[MoviesView] PlayStream Uri.AbsoluteUri (what libvlc actually sees): {uri.AbsoluteUri}");
            var media = new Media(_libVlc, uri);
            ApplyTsSeekOptions(media);
            _mediaPlayer.Play(media);
        });
    }

    /// <summary>
    /// MPEG-TS (what Enigma2 recordings are) has no byte-accurate time index, so a
    /// seek to an arbitrary timestamp is inherently a guess: libvlc estimates a byte
    /// offset from the average bitrate, lands near but not exactly on the requested
    /// time, then resyncs against the stream's PCR (clock reference) — and on DVB
    /// recordings, where PCR is frequently noisy/discontinuous (channel-change
    /// markers, ad-insertion splices, recording start latency), that resync can
    /// overshoot and correct itself more than once before settling. That self-
    /// correction is what looks like "stutters, then jumps forward in chunks" even
    /// once the seek itself is a direct libvlc Time/Position call with no connection
    /// restart involved.
    /// ts-seek-percent switches the byte-offset estimate to be based on the seek
    /// fraction of total file size directly (skipping a PCR-based estimate step),
    /// and no-ts-trust-pcr stops the demuxer from trusting in-stream PCR values for
    /// timing at all — between the two, this is the standard recommended mitigation
    /// for this exact class of "seek lands roughly right after some correcting" TS
    /// symptom. It's applied per-recording-Media (not globally via BuildVlcArgs)
    /// since PCR trust is desirable for live TV, where a continuous, well-behaved
    /// source benefits from it for A/V sync — this is recordings-only.
    /// </summary>
    private static void ApplyTsSeekOptions(Media media)
    {
        if (!App.SettingsService.Settings.VlcTsSeekOptions) return;
        media.AddOption(":no-ts-trust-pcr");
        media.AddOption(":ts-seek-percent");
    }

    private void OnPlaying(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_vm == null || _mediaPlayer == null) return;
            _vm.IsPaused = false;

            // Populate audio tracks
            _vm.AudioTracks.Clear();
            var tracks = _mediaPlayer.AudioTrackDescription;
            foreach (var t in tracks)
                _vm.AudioTracks.Add(new AudioTrackInfo { Id = t.Id, Name = t.Name ?? $"Track {t.Id}" });
            if (_vm.AudioTracks.Any())
                _vm.SelectedAudioTrack = _vm.AudioTracks.First();
        });
    }

    private void OnEsAdded(object? sender, MediaPlayerESAddedEventArgs e)
    {
        if (e.Type != TrackType.Audio) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (_vm == null || _mediaPlayer == null) return;
            _vm.AudioTracks.Clear();
            foreach (var t in _mediaPlayer.AudioTrackDescription)
                _vm.AudioTracks.Add(new AudioTrackInfo { Id = t.Id, Name = t.Name ?? $"Track {t.Id}" });
        });
    }

    private void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_vm == null || _mediaPlayer == null) return;

            if (!_isDraggingSlider)
                _vm.PositionPercent = _mediaPlayer.Position * 100.0;

            var ts = TimeSpan.FromMilliseconds(e.Time);
            _vm.PositionText = ts.ToString(@"h\:mm\:ss");
        });
    }

    private void OnLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        Debug.WriteLine($"[MoviesView] OnLengthChanged fired: e.Length={e.Length}");
        // Guard against e.Length <= 0: libvlc can fire LengthChanged with a not-yet-
        // known length (0, or briefly negative) before/instead of ever resolving a
        // real one on some HTTP MPEG-TS streams. Without this guard, that 0 was
        // unconditionally overwriting DurationText — including the value PlayMovie
        // seeds from the receiver's own recording metadata — which is exactly why
        // the duration kept showing "0:00:00" even after that fix: this handler
        // stomped it right back to zero on the very next (still-zero) firing.
        if (e.Length <= 0) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (_vm == null) return;
            _vm.DurationText = TimeSpan.FromMilliseconds(e.Length).ToString(@"h\:mm\:ss");
        });
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_mediaPlayer == null || _vm == null) return;
        switch (e.PropertyName)
        {
            case nameof(MoviesViewModel.IsPaused):
                try { _mediaPlayer.SetPause(_vm.IsPaused); }
                catch { if (_vm.IsPaused) _mediaPlayer.Pause(); }
                break;
            case nameof(MoviesViewModel.IsMuted):
                _mediaPlayer.Mute = _vm.IsMuted;
                break;
            case nameof(MoviesViewModel.Volume):
                _mediaPlayer.Volume = (int)_vm.Volume;
                break;
            // PositionPercent changes from code are NOT applied back to player here
            // — seeking only happens via PositionSlider_DragCompleted to avoid feedback loops
            case nameof(MoviesViewModel.SelectedAudioTrack) when _vm.SelectedAudioTrack != null:
                _mediaPlayer.SetAudioTrack(_vm.SelectedAudioTrack.Id);
                break;
        }
    }
    private void MovieVideoView_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_vm?.CurrentStreamUrl is { Length: > 0 } url)
        {
            Debug.WriteLine("[MoviesView] Double-click restart stream");
            PlayStream(url);
        }
    }

    private bool _isDraggingSlider;
    private CancellationTokenSource? _seekCts;

    private void PositionSlider_DragStarted(object sender, DragStartedEventArgs e)
        => _isDraggingSlider = true;

    private void PositionSlider_DragCompleted(object sender, DragCompletedEventArgs e)
        => _isDraggingSlider = false;

    // IsMoveToPointEnabled="True" on the Slider (see XAML) lets a plain click
    // anywhere on the track jump Value straight to that point — but that jump
    // happens through the Slider's own Track click-handling, NOT through a Thumb
    // drag, so Thumb.DragStarted/DragCompleted never fire for a plain click.
    // That's why dragging the thumb seeked correctly but a single click on the bar
    // did nothing at all.
    // PreviewMouseLeftButtonUp is the single trigger point for BOTH gestures (a
    // plain click, and the mouse-up that ends a thumb drag) rather than having
    // DragCompleted seek too — relying on DragCompleted firing strictly before or
    // after this tunneling event isn't something to depend on, since they come
    // from two different elements (Thumb vs. the Slider itself) with no documented
    // guaranteed ordering between them; a single trigger point sidesteps that
    // entirely instead of needing a "was this just handled by a drag" flag.
    // By the time this fires, IsMoveToPointEnabled has already updated Value
    // synchronously for the click case, so reading PositionSlider.Value here is
    // accurate for both gestures.
    private void PositionSlider_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => TriggerSeekFromSliderValue();

    private void TriggerSeekFromSliderValue()
    {
        if (_mediaPlayer == null || _libVlc == null || PositionSlider == null)
        {
            Debug.WriteLine("[MoviesView] Seek dropped: player/PositionSlider not ready");
            return;
        }
        if (string.IsNullOrEmpty(_vm?.CurrentStreamUrl))
        {
            Debug.WriteLine("[MoviesView] Seek dropped: no CurrentStreamUrl");
            return;
        }

        // The slider's value (0-100%) IS the seek fraction — used directly rather
        // than first converting to an absolute target-ms via Length. Length can
        // legitimately still be 0/unknown at this point on some TS streams (its
        // detection is async and isn't guaranteed to have completed by the time the
        // user drags the slider), and gating the seek on it being known was why
        // seeking silently did nothing: the fraction the user actually dragged to is
        // available immediately regardless of whether Length has arrived yet.
        var fraction = (float)(PositionSlider.Value / 100.0);
        var durationMs = _mediaPlayer.Length; // only used for the restart fallback's :start-time, and only if known
        Debug.WriteLine($"[MoviesView] Seek requested: slider={PositionSlider.Value:F1}% (fraction={fraction:F4}), duration={durationMs}ms");
        SeekByPosition(fraction, fallbackTargetMs: durationMs > 0 ? (long)(fraction * durationMs) : 0);
    }

    /// <summary>
    /// Seeks to a fraction (0.0-1.0) of the stream's total length. Recordings are
    /// served from Enigma2's /file endpoint (a plain static-file HTTP handler with
    /// Range support), so libvlc can normally seek the existing playback session
    /// directly — no need to tear down the connection and reopen it elsewhere in
    /// the file. That teardown/reopen is what produced the original "jumps ~2
    /// minutes instead of landing on the exact spot" symptom: the receiver only
    /// resumes at its nearest I-frame after a fresh connection.
    /// Takes a fraction rather than an absolute time deliberately, for two
    /// independent reasons:
    ///  - Per VideoLAN's own developers, MPEG-TS only really supports relative/
    ///    percentage seeking under the hood — "TS seek is implemented by linear
    ///    extrapolation from the bandwidth" (forum.videolan.org/viewtopic.php?t=72968)
    ///    — so asking for an absolute Time still has to go through a PCR-time ->
    ///    byte-offset conversion internally before doing that same extrapolation,
    ///    and that extra step (compounded by often-noisy PCR on DVB recordings)
    ///    is what produced the "overshoot, resync, overshoot again" stutter.
    ///  - It works even when Length hasn't been determined yet. Length detection
    ///    on an HTTP MPEG-TS source is asynchronous and was, in practice, observed
    ///    to sometimes never fire (or fire with 0) well after playback had visibly
    ///    started — when that happened, gating the seek on Length being known and
    ///    positive silently dropped every seek attempt, which looked indistinguishable
    ///    from "seeking does nothing at all". Position is relative by definition,
    ///    so it has no such dependency.
    /// IsSeekable confirms libvlc actually opened this input as seekable (true for
    /// recordings; false for things like a still-live, growing file or a genuine
    /// live stream) — restart-by-:start-time is kept as the fallback for those
    /// cases, since a non-seekable input has no other way to jump at all. The
    /// fallback does need an absolute time (:start-time is seconds, not a fraction),
    /// so fallbackTargetMs carries that — it's only used if the fast path can't run,
    /// and is allowed to be 0 (start of file) if Length was genuinely never known.
    /// Even with the native fast path, MPEG-TS seeking is inherently approximate —
    /// see ApplyTsSeekOptions for why — so don't expect frame-accurate landings; the
    /// goal here is "fast and roughly right" instead of "slow, then roughly right
    /// after several visible corrections".
    /// </summary>
    private void SeekByPosition(float fraction, long fallbackTargetMs)
    {
        if (_libVlc == null || _mediaPlayer == null || _vm == null)
        {
            Debug.WriteLine("[MoviesView] SeekByPosition dropped: libVlc/mediaPlayer/vm is null");
            return;
        }
        var url = _vm.CurrentStreamUrl;
        if (string.IsNullOrEmpty(url))
        {
            Debug.WriteLine("[MoviesView] SeekByPosition dropped: no CurrentStreamUrl");
            return;
        }
        fraction = Math.Clamp(fraction, 0f, 0.999f);

        // Cancel any previous in-flight seek so rapid seeks don't queue up
        _seekCts?.Cancel();
        _seekCts = new CancellationTokenSource();
        var ct = _seekCts.Token;

        if (_mediaPlayer.IsSeekable)
        {
            // Fast path: ask the already-open input to jump directly. No Stop(),
            // no new HTTP connection, no reconnect-to-nearest-keyframe behaviour.
            // Deliberately NOT also requiring Length > 0 here (see doc comment) —
            // IsSeekable is libvlc's own signal that Position-style seeking will
            // work on this input, independent of whether duration has been
            // determined yet.
            Debug.WriteLine($"[MoviesView] Native seek to fraction={fraction:F4} (IsSeekable=true, Length={_mediaPlayer.Length})");
            _mediaPlayer.Position = fraction;
            return;
        }

        Debug.WriteLine($"[MoviesView] Falling back to restart-based seek (IsSeekable={_mediaPlayer.IsSeekable}, Length={_mediaPlayer.Length}, fallbackTargetMs={fallbackTargetMs})");
        var wasPaused  = _vm.IsPaused;
        var targetSec  = fallbackTargetMs / 1000.0;
        var fileCache  = App.SettingsService.Settings.VlcFileCacheMs;
        var netCache   = App.SettingsService.Settings.VlcNetworkCacheMs;

        Task.Run(() =>
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                _mediaPlayer.Stop();
                if (ct.IsCancellationRequested) return;

                var media = new Media(_libVlc, new Uri(url));
                media.AddOption($":start-time={targetSec:F1}");
                media.AddOption($":file-caching={fileCache}");
                media.AddOption($":network-caching={netCache}");
                ApplyTsSeekOptions(media);
                _mediaPlayer.Play(media);

                if (wasPaused && !ct.IsCancellationRequested)
                {
                    Thread.Sleep(500);
                    if (!ct.IsCancellationRequested)
                        _mediaPlayer.SetPause(true);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MoviesView] SeekByPosition error: {ex.Message}");
            }
        }, ct);
    }

    private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Intentionally empty — seeking only on DragCompleted, never during drag.
    }
}
