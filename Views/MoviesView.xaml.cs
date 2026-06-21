using System.Diagnostics;
using System.Runtime.Versioning;
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
    private bool _seekingFromCode;

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
        _libVlc = new LibVLC("--no-video-title-show", "--network-caching=2000");
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

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.StreamRequested -= OnStreamRequested;
            _vm.StopRequested -= OnStopRequested;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }
        if (e.NewValue is MoviesViewModel vm)
        {
            _vm = vm;
            _vm.StreamRequested += OnStreamRequested;
            _vm.StopRequested += OnStopRequested;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

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
            var media = new Media(_libVlc, new Uri(url));
            _mediaPlayer.Play(media);
        });
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
            if (_vm == null || _mediaPlayer == null || _seekingFromCode) return;
            var pos = _mediaPlayer.Position * 100.0;
            _seekingFromCode = true;
            _vm.PositionPercent = pos;
            _seekingFromCode = false;
            var ts = TimeSpan.FromMilliseconds(e.Time);
            _vm.PositionText = ts.ToString(@"h\:mm\:ss");
        });
    }

    private void OnLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
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

    private void PositionSlider_DragStarted(object sender, DragStartedEventArgs e)
    {
        _isDraggingSlider = true;
    }

    private void PositionSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _isDraggingSlider = false;
        if (_mediaPlayer == null || PositionSlider == null) return;
        _mediaPlayer.Position = (float)(PositionSlider.Value / 100.0);
    }

    private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Only seek when user is actively dragging (not during programmatic updates)
        if (!_isDraggingSlider || _mediaPlayer == null) return;
        _mediaPlayer.Position = (float)(e.NewValue / 100.0);
    }
}
