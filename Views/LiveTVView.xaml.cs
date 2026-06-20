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

    private void Vm_StreamRequested(object? sender, string url) => PlayStream(url);

    // Per-bouquet scroll positions: key = bouquet ServiceReference
    private readonly Dictionary<string, double> _bouquetScrollPositions = new();

    private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_vm == null) return;

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
            _mediaPlayer.SetAudioTrack(_vm.SelectedAudioTrack.Id);
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
