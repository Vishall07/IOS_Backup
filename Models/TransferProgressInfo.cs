using System;

namespace IosBackupApp.Models;

public enum TransferState
{
    Idle,
    Running,
    Paused,
    Completed,
    Cancelled,
    Error
}

public class TransferProgressInfo
{
    public int TotalFiles { get; set; }
    public int CompletedFiles { get; set; }
    public int FailedFiles { get; set; }
    public int SkippedFiles { get; set; }

    public long TotalBytes { get; set; }
    public long TransferredBytes { get; set; }

    public string CurrentFileName { get; set; } = string.Empty;
    public long CurrentFileTransferredBytes { get; set; }
    public long CurrentFileSize { get; set; }

    public double BytesPerSecond { get; set; }
    public TimeSpan? EstimatedTimeRemaining { get; set; }

    public TransferState State { get; set; } = TransferState.Idle;
    public string StatusMessage { get; set; } = string.Empty;

    public double OverallPercentage => TotalBytes > 0 
        ? Math.Min(100.0, (double)TransferredBytes / TotalBytes * 100.0) 
        : (TotalFiles > 0 ? (double)CompletedFiles / TotalFiles * 100.0 : 0);

    public double CurrentFilePercentage => CurrentFileSize > 0 
        ? Math.Min(100.0, (double)CurrentFileTransferredBytes / CurrentFileSize * 100.0) 
        : 0;
}
