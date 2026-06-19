using System.Diagnostics;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using LibVLCSharp.Shared;
using DreamWin.ViewModels;

namespace DreamWin.Views;

[SupportedOSPlatform("windows")]
public partial class MoviesView : UserControl
{
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private bool _vlcInitialized;

    public MoviesView()
    {
        InitializeComponent();

        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is MoviesViewModel vm)
                vm.StreamRequested += (_, url) => PlayStream(url);
        };

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_vlcInitialized) return;
        _vlcInitialized = true;

        Debug.WriteLine("[MoviesView] Initializing LibVLC");
        Core.Initialize();
        _libVlc = new LibVLC("--no-video-title-show");
        _mediaPlayer = new MediaPlayer(_libVlc);
        MovieVideoView.MediaPlayer = _mediaPlayer;
        Debug.WriteLine("[MoviesView] LibVLC initialized");
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
}
