using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IosBackupApp.Models;

namespace IosBackupApp.Services.Interfaces;

public interface ITransferEngine
{
    bool IsRunning { get; }
    bool IsPaused { get; }

    void Pause();
    void Resume();
    void Cancel();

    Task StartTransferAsync(
        IReadOnlyList<FileDiffNodeViewModel> filesToTransfer,
        string deviceId,
        string destinationDirectory,
        string laptopStateFilePath,
        string deviceName,
        int concurrency,
        IProgress<TransferProgressInfo> progress,
        Action<FileDiffNodeViewModel, bool, string?> onFileCompleted,
        CancellationToken cancellationToken);
}
