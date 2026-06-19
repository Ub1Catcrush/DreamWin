using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LibVLCSharp.Shared;
using DreamWin.Models;
using DreamWin.ViewModels;

namespace DreamWin.Views;

[SupportedOSPlatform("windows")]
public partial class LiveTVView : UserControl
{
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private bool _vlcInitialized;
    private LiveTVViewModel? _vm;

    public LiveTVView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_vlcInitialized) return;
        _vlcInitialized = true;

        Debug.WriteLine("[LiveTVView] Initializing LibVLC");
        Core.Initialize();
        _libVlc = new LibVLC("--no-video-title-show", "--network-caching=1000");
        _mediaPlayer = new MediaPlayer(_libVlc);
        VideoView.MediaPlayer = _mediaPlayer;
        _mediaPlayer.Playing += OnMediaPlayerPlaying;
        // ES/stream events: refresh audio track list when elementary streams are added/changed
        _mediaPlayer.ESAdded += OnMediaPlayerEsAdded;
        _mediaPlayer.MediaChanged += OnMediaPlayerMediaChanged;
        Debug.WriteLine("[LiveTVView] LibVLC initialized");
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
        }
    }

    private void Vm_StreamRequested(object? sender, string url) => PlayStream(url);

    private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_mediaPlayer == null || _vm == null) return;

        if (e.PropertyName == nameof(LiveTVViewModel.Volume))
            _mediaPlayer.Volume = (int)_vm.Volume;
        else if (e.PropertyName == nameof(LiveTVViewModel.IsMuted))
            _mediaPlayer.Mute = _vm.IsMuted;
        else if (e.PropertyName == nameof(LiveTVViewModel.IsPaused))
        {
            // Use SetPause to reliably set paused state without breaking audio on resume
            try
            {
                _mediaPlayer.SetPause(_vm.IsPaused);
            }
            catch
            {
                if (_vm.IsPaused)
                    _mediaPlayer.Pause();
                else if (!_mediaPlayer.IsPlaying)
                    _mediaPlayer.Play();
            }
        }
        else if (e.PropertyName == nameof(LiveTVViewModel.SelectedAudioTrack) && _vm.SelectedAudioTrack != null)
            _mediaPlayer.SetAudioTrack(_vm.SelectedAudioTrack.Id);
    }

    private void OnMediaPlayerPlaying(object? sender, EventArgs e)
    {
        if (_mediaPlayer == null || _vm == null) return;

        Dispatcher.InvokeAsync(RefreshAudioTracks);
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
                        _vm.AudioTracks.Add(new LiveTVViewModel.AudioTrackInfo
                        {
                            Id = audio.Id,
                            Name = string.IsNullOrWhiteSpace(audio.Name) ? $"Track {audio.Id}" : audio.Name
                        });
                    }

                    // select current track if available
                    try
                    {
                        var currentId = _mediaPlayer.AudioTrack;
                        var sel = _vm.AudioTracks.FirstOrDefault(a => a.Id == currentId);
                        if (sel != null)
                            _vm.SelectedAudioTrack = sel;
                        else if (_vm.SelectedAudioTrack == null && _vm.AudioTracks.Any())
                            _vm.SelectedAudioTrack = _vm.AudioTracks.First();
                    }
                    catch
                    {
                        if (_vm.SelectedAudioTrack == null && _vm.AudioTracks.Any())
                            _vm.SelectedAudioTrack = _vm.AudioTracks.First();
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

        try
        {
            var media = new Media(_libVlc, new Uri(url));
            _mediaPlayer.Play(media);
            if (_vm != null)
            {
                _mediaPlayer.Volume = (int)_vm.Volume;
                _mediaPlayer.Mute = _vm.IsMuted;
                if (_vm.SelectedAudioTrack != null)
                    _mediaPlayer.SetAudioTrack(_vm.SelectedAudioTrack.Id);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LiveTV] PlayStream error: {ex}");
        }

        _ = Dispatcher.InvokeAsync(RefreshAudioTracks);
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            _ = Dispatcher.InvokeAsync(RefreshAudioTracks);
            await Task.Delay(1500);
            _ = Dispatcher.InvokeAsync(RefreshAudioTracks);
        });
    }

    public void Stop()
    {
        _mediaPlayer?.Stop();
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
