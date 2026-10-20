using CommunityToolkit.Mvvm.ComponentModel;

namespace IosBackupApp.Models;

public partial class StorageMetrics : ObservableObject
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private long _totalBytes;

    [ObservableProperty]
    private long _freeBytes;

    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);

    public double UsedPercentage => TotalBytes > 0 ? (double)UsedBytes / TotalBytes * 100.0 : 0;

    public string TotalFormatted => FormatBytes(TotalBytes);
    public string FreeFormatted => FormatBytes(FreeBytes);
    public string UsedFormatted => FormatBytes(UsedBytes);

    public void NotifyMetricsChanged()
    {
        OnPropertyChanged(nameof(UsedBytes));
        OnPropertyChanged(nameof(UsedPercentage));
        OnPropertyChanged(nameof(TotalFormatted));
        OnPropertyChanged(nameof(FreeFormatted));
        OnPropertyChanged(nameof(UsedFormatted));
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1)
        {
            number /= 1024;
            counter++;
            if (counter == suffixes.Length - 1) break;
        }
        return $"{number:n1} {suffixes[counter]}";
    }
}
