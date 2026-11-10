using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using IosBackupApp.Models;
using IosBackupApp.Services;
using IosBackupApp.ViewModels;

namespace IosBackupApp;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    private readonly DispatcherTimer _videoTimer;
    private bool _isDraggingSlider;
    private bool _isLooping = true;

    public MainWindow()
    {
        InitializeComponent();

        var deviceService = new DeviceService();
        var localStorageService = new LocalStorageService();
        var diffEngine = new DiffEngine();
        var transferEngine = new TransferEngine(deviceService, localStorageService);
        var thumbnailService = new ThumbnailService();

        ViewModel = new MainViewModel(deviceService, localStorageService, diffEngine, transferEngine, thumbnailService);
        DataContext = ViewModel;

        _videoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _videoTimer.Tick += VideoTimer_Tick;

        PreviewKeyDown += MainWindow_PreviewKeyDown;
        StateChanged += MainWindow_StateChanged;
        LightboxMediaPlayer.MediaOpened += LightboxMediaPlayer_MediaOpened;
        LightboxMediaPlayer.MediaEnded += LightboxMediaPlayer_MediaEnded;
        LightboxMediaPlayer.MediaFailed += LightboxMediaPlayer_MediaFailed;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        ViewModel.ProcessLogs.CollectionChanged += (s, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add && ViewModel.ProcessLogs.Count > 0)
            {
                Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        if (ModalLogListBox?.Items.Count > 0)
                        {
                            ModalLogListBox.ScrollIntoView(ModalLogListBox.Items[^1]);
                        }
                        if (SidebarLogListBox?.Items.Count > 0)
                        {
                            SidebarLogListBox.ScrollIntoView(SidebarLogListBox.Items[^1]);
                        }
                    }
                    catch { }
                });
            }
        };

        Loaded += async (s, e) =>
        {
            await ViewModel.InitializeAsync();
        };
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ActiveVideoUri))
        {
            Dispatcher.InvokeAsync(() =>
            {
                VideoCodecFallbackOverlay.Visibility = Visibility.Collapsed;
                if (!string.IsNullOrEmpty(ViewModel.ActiveVideoUri))
                {
                    LightboxMediaPlayer.Source = new Uri(ViewModel.ActiveVideoUri, UriKind.Absolute);
                    LightboxMediaPlayer.Position = TimeSpan.Zero;
                    LightboxMediaPlayer.Play();
                    VideoPlayPauseBtn.Content = "⏸";
                    _videoTimer.Start();
                }
                else
                {
                    _videoTimer.Stop();
                    LightboxMediaPlayer.Stop();
                    LightboxMediaPlayer.Source = null;
                }
            });
        }
        else if (e.PropertyName == nameof(MainViewModel.IsLightboxOpen))
        {
            if (!ViewModel.IsLightboxOpen)
            {
                _videoTimer.Stop();
                LightboxMediaPlayer.Stop();
                LightboxMediaPlayer.Source = null;
                VideoCodecFallbackOverlay.Visibility = Visibility.Collapsed;
            }
        }
        else if (e.PropertyName == nameof(MainViewModel.IsGalleryView))
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (ViewModel.IsGalleryView)
                {
                    // Full size preview mode window
                    WindowState = WindowState.Maximized;
                }
                UpdateMaximizeButtonText();
            });
        }
    }

    private void VideoTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isDraggingSlider && LightboxMediaPlayer.NaturalDuration.HasTimeSpan)
        {
            double totalSecs = LightboxMediaPlayer.NaturalDuration.TimeSpan.TotalSeconds;
            double curSecs = LightboxMediaPlayer.Position.TotalSeconds;
            if (totalSecs > 0)
            {
                VideoScrubberSlider.Value = curSecs / totalSecs;
            }
            VideoTimeLabel.Text = $"{LightboxMediaPlayer.Position:mm\\:ss} / {LightboxMediaPlayer.NaturalDuration.TimeSpan:mm\\:ss}";
        }
    }

    private void VideoPlayPauseBtn_Click(object sender, RoutedEventArgs e)
    {
        if (VideoPlayPauseBtn.Content.ToString() == "⏸")
        {
            LightboxMediaPlayer.Pause();
            VideoPlayPauseBtn.Content = "▶";
        }
        else
        {
            LightboxMediaPlayer.Play();
            VideoPlayPauseBtn.Content = "⏸";
        }
    }

    private void VideoLoopBtn_Click(object sender, RoutedEventArgs e)
    {
        _isLooping = !_isLooping;
        VideoLoopBtn.Content = _isLooping ? "🔁 Loop: On" : "🔁 Loop: Off";
    }

    private void VideoScrubberSlider_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingSlider = true;
    }

    private void VideoScrubberSlider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingSlider = false;
        if (LightboxMediaPlayer.NaturalDuration.HasTimeSpan)
        {
            double totalSecs = LightboxMediaPlayer.NaturalDuration.TimeSpan.TotalSeconds;
            LightboxMediaPlayer.Position = TimeSpan.FromSeconds(VideoScrubberSlider.Value * totalSecs);
        }
    }

    private void VideoScrubberSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isDraggingSlider && LightboxMediaPlayer.NaturalDuration.HasTimeSpan)
        {
            double totalSecs = LightboxMediaPlayer.NaturalDuration.TimeSpan.TotalSeconds;
            var pos = TimeSpan.FromSeconds(e.NewValue * totalSecs);
            VideoTimeLabel.Text = $"{pos:mm\\:ss} / {LightboxMediaPlayer.NaturalDuration.TimeSpan:mm\\:ss}";
        }
    }

    private void LightboxMediaPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        VideoPlayPauseBtn.Content = "⏸";
        if (LightboxMediaPlayer.NaturalDuration.HasTimeSpan)
        {
            VideoTimeLabel.Text = $"00:00 / {LightboxMediaPlayer.NaturalDuration.TimeSpan:mm\\:ss}";
        }
        _videoTimer.Start();
    }

    private void LightboxMediaPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsLightboxLivePhotoPlaying)
        {
            ViewModel.StopLivePhotoVideo();
        }
        else if (_isLooping)
        {
            LightboxMediaPlayer.Position = TimeSpan.Zero;
            LightboxMediaPlayer.Play();
        }
        else
        {
            LightboxMediaPlayer.Pause();
            VideoPlayPauseBtn.Content = "▶";
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }

        if (ViewModel.IsLightboxOpen)
        {
            if (e.Key == Key.Escape)
            {
                ViewModel.CloseLightbox();
                e.Handled = true;
            }
            else if (e.Key == Key.Left)
            {
                ViewModel.PreviousGalleryItemCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Right)
            {
                ViewModel.NextGalleryItemCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Space)
            {
                if (ViewModel.SelectedGalleryItem?.IsLivePhoto == true)
                {
                    ViewModel.PlayLivePhoto();
                    e.Handled = true;
                }
                else if (ViewModel.IsLightboxVideo)
                {
                    VideoPlayPauseBtn_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                }
            }
        }
    }

    private void LightboxMediaPlayer_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        _videoTimer.Stop();
        VideoCodecFallbackOverlay.Visibility = Visibility.Visible;
        ViewModel.Log($"[VIDEO] Native playback failed: {e.ErrorException?.Message ?? "Codec not supported by Windows Media Foundation"}. File: {ViewModel.ActiveVideoUri}", LogLevel.Warning, "VIDEO");
    }

    private void OpenInDefaultPlayerBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(ViewModel.ActiveVideoUri) && File.Exists(ViewModel.ActiveVideoUri))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = ViewModel.ActiveVideoUri,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ViewModel.Log($"Failed to open video in external player: {ex.Message}", LogLevel.Error, "VIDEO");
            }
        }
    }

    private void GalleryCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // If the click was directly on or inside a CheckBox, don't open the lightbox
        if (e.OriginalSource is DependencyObject dep && FindVisualParent<CheckBox>(dep) != null)
        {
            return;
        }

        if (sender is FrameworkElement fe && fe.DataContext is GalleryItemViewModel item)
        {
            ViewModel.OpenGalleryItemCommand.Execute(item);
            e.Handled = true;
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        DependencyObject? parentObject = child;
        while (parentObject != null)
        {
            if (parentObject is T parent) return parent;
            parentObject = VisualTreeHelper.GetParent(parentObject);
        }
        return null;
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        UpdateMaximizeButtonText();
    }

    private void UpdateMaximizeButtonText()
    {
        string text = WindowState == WindowState.Maximized ? "🗗 Normal Window" : "⛶ Fullscreen";
        if (GalleryMaximizeBtnText != null) GalleryMaximizeBtnText.Text = text;
        if (LightboxMaximizeBtn != null) LightboxMaximizeBtn.Content = text;
    }

    private void ToggleMaximizeBtn_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    public void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateMaximizeButtonText();
    }
}