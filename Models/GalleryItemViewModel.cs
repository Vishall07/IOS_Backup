using System;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace IosBackupApp.Models;

public enum GalleryMediaType
{
    Photo,
    LivePhoto,
    Video
}

public partial class GalleryItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _relativePath = string.Empty;

    [ObservableProperty]
    private string _folder = string.Empty;

    [ObservableProperty]
    private long _fileSize;

    [ObservableProperty]
    private DateTime _dateModified;

    [ObservableProperty]
    private GalleryMediaType _mediaType = GalleryMediaType.Photo;

    [ObservableProperty]
    private bool _isLivePhoto;

    [ObservableProperty]
    private bool _isVideo;

    [ObservableProperty]
    private bool _isHeic;

    [ObservableProperty]
    private string? _companionVideoPath;

    [ObservableProperty]
    private BitmapSource? _thumbnail;

    [ObservableProperty]
    private bool _isThumbnailLoading;

    [ObservableProperty]
    private FileDiffStatus _status = FileDiffStatus.BackedUp;

    [ObservableProperty]
    private bool _isSelected;

    public event EventHandler? SelectionChanged;

    partial void OnIsSelectedChanged(bool value)
    {
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    [ObservableProperty]
    private string _durationFormatted = string.Empty;

    public string FileSizeFormatted => StorageMetrics.FormatBytes(FileSize);
    public string DateFormatted => DateModified.ToString("yyyy-MM-dd HH:mm");

    public string BadgeText => MediaType switch
    {
        GalleryMediaType.LivePhoto => "◉ LIVE",
        GalleryMediaType.Video => "▶ VIDEO",
        _ => IsHeic ? "HEIC" : Path.GetExtension(FileName).TrimStart('.').ToUpperInvariant()
    };

    public string BadgeBg => MediaType switch
    {
        GalleryMediaType.LivePhoto => "#0284C7", // Cyan/Sky blue
        GalleryMediaType.Video => "#8B5CF6",     // Violet
        _ => "#334155"                          // Slate
    };
}
