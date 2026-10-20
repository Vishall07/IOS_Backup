using System;
using System.Windows.Media;

namespace IosBackupApp.Models;

public enum LogLevel
{
    Info,
    Scan,
    Transfer,
    Success,
    Warning,
    Error
}

public class LogEntry
{
    public DateTime Timestamp { get; } = DateTime.Now;
    public LogLevel Level { get; set; } = LogLevel.Info;
    public string Category { get; set; } = "SYSTEM";
    public string Message { get; set; } = string.Empty;

    public string TimestampFormatted => Timestamp.ToString("HH:mm:ss.fff");

    public string LevelBadge => Level switch
    {
        LogLevel.Info => "INFO",
        LogLevel.Scan => "SCAN",
        LogLevel.Transfer => "XFER",
        LogLevel.Success => "OK",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERR",
        _ => "LOG"
    };

    public SolidColorBrush LevelBrush => Level switch
    {
        LogLevel.Info => new SolidColorBrush(Color.FromRgb(56, 189, 248)),     // Sky Blue
        LogLevel.Scan => new SolidColorBrush(Color.FromRgb(168, 85, 247)),    // Purple
        LogLevel.Transfer => new SolidColorBrush(Color.FromRgb(245, 158, 11)),// Amber
        LogLevel.Success => new SolidColorBrush(Color.FromRgb(34, 197, 94)),  // Green
        LogLevel.Warning => new SolidColorBrush(Color.FromRgb(251, 191, 36)), // Yellow
        LogLevel.Error => new SolidColorBrush(Color.FromRgb(239, 68, 68)),    // Red
        _ => new SolidColorBrush(Color.FromRgb(148, 163, 184))                // Gray
    };

    public string FullText => $"[{TimestampFormatted}] [{Category}] {Message}";

    public override string ToString() => FullText;
}
