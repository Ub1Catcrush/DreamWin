using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using LibVLCSharp.Shared;
using DreamWin.ViewModels;

namespace DreamWin.Views;

public partial class LiveTVView : UserControl
{
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private bool _vlcInitialized;

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
        Debug.WriteLine("[LiveTVView] LibVLC initialized");
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is LiveTVViewModel vm)
        {
            vm.StreamRequested += (_, url) => PlayStream(url);
        }
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

    public void Stop()
    {
        _mediaPlayer?.Stop();
    }
}
