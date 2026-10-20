using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using IosBackupApp.Models;

namespace IosBackupApp.Converters;

public class FileSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is long l) return StorageMetrics.FormatBytes(l);
        if (value is ulong ul) return StorageMetrics.FormatBytes((long)ul);
        if (value is int i) return StorageMetrics.FormatBytes(i);
        return "0 B";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class SpeedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double speed)
        {
            if (speed <= 0) return "0 KB/s";
            if (speed >= 1024 * 1024)
            {
                return $"{(speed / (1024 * 1024)):F1} MB/s";
            }
            return $"{(speed / 1024):F0} KB/s";
        }
        return "0 KB/s";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class TimeRemainingConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is TimeSpan ts)
        {
            if (ts.TotalHours >= 1)
            {
                return $"{Math.Floor(ts.TotalHours):00}h {ts.Minutes:00}m {ts.Seconds:00}s";
            }
            return $"{ts.Minutes:00}m {ts.Seconds:00}s";
        }
        return "--:--";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class StatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush PendingBrush = new(Color.FromRgb(245, 158, 11)); // Amber
    private static readonly SolidColorBrush BackedUpBrush = new(Color.FromRgb(16, 185, 129)); // Emerald Green
    private static readonly SolidColorBrush SuccessBrush = new(Color.FromRgb(34, 197, 94)); // Green
    private static readonly SolidColorBrush FailedBrush = new(Color.FromRgb(239, 68, 68)); // Red
    private static readonly SolidColorBrush DefaultBrush = new(Color.FromRgb(156, 163, 175)); // Gray

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is FileDiffStatus status)
        {
            return status switch
            {
                FileDiffStatus.PendingBackup => PendingBrush,
                FileDiffStatus.BackedUp => BackedUpBrush,
                FileDiffStatus.Success => SuccessBrush,
                FileDiffStatus.Failed => FailedBrush,
                _ => DefaultBrush
            };
        }
        return DefaultBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b) return !b;
        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b) return !b;
        return false;
    }
}

public class BooleanToVisibilityInverseConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b) return b ? Visibility.Collapsed : Visibility.Visible;
        return Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}
