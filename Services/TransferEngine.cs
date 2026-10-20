using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using IosBackupApp.Models;
using IosBackupApp.Services.Interfaces;

namespace IosBackupApp.Services;

public class TransferEngine : ITransferEngine
{
    private readonly IDeviceService _deviceService;
    private readonly ILocalStorageService _localStorageService;

    private readonly ManualResetEventSlim _pauseEvent = new(true);
    private CancellationTokenSource? _cts;

    private bool _isRunning;
    private bool _isPaused;

    public bool IsRunning => _isRunning;
    public bool IsPaused => _isPaused;

    public TransferEngine(IDeviceService deviceService, ILocalStorageService localStorageService)
    {
        _deviceService = deviceService;
        _localStorageService = localStorageService;
    }

    public void Pause()
    {
        if (_isRunning && !_isPaused)
        {
            _isPaused = true;
            _pauseEvent.Reset();
        }
    }

    public void Resume()
    {
        if (_isRunning && _isPaused)
        {
            _isPaused = false;
            _pauseEvent.Set();
        }
    }

    public void Cancel()
    {
        if (_isRunning)
        {
            _pauseEvent.Set(); // Unblock paused workers so they can observe cancellation
            _cts?.Cancel();
        }
    }

    public async Task StartTransferAsync(
        IReadOnlyList<FileDiffNodeViewModel> filesToTransfer,
        string deviceId,
        string destinationDirectory,
        string laptopStateFilePath,
        string deviceName,
        int concurrency,
        IProgress<TransferProgressInfo> progress,
        Action<FileDiffNodeViewModel, bool, string?> onFileCompleted,
        CancellationToken cancellationToken)
    {
        if (_isRunning)
        {
            throw new InvalidOperationException("Transfer is already running.");
        }

        _isRunning = true;
        _isPaused = false;
        _pauseEvent.Set();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _cts.Token;

        int totalFiles = filesToTransfer.Count;
        long totalBytes = filesToTransfer.Sum(f => f.Size);
        long transferredBytes = 0;
        int completedFiles = 0;
        int failedFiles = 0;

        var speedTracker = new MovingAverageSpeedTracker(TimeSpan.FromSeconds(5));

        var progressInfo = new TransferProgressInfo
        {
            TotalFiles = totalFiles,
            TotalBytes = totalBytes,
            State = TransferState.Running,
            StatusMessage = "Starting backup..."
        };
        progress.Report(progressInfo);

        try
        {
            var options = new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, concurrency),
                CancellationToken = ct
            };

            var actionBlock = new ActionBlock<FileDiffNodeViewModel>(async fileNode =>
            {
                // Pause gate
                _pauseEvent.Wait(ct);
                ct.ThrowIfCancellationRequested();

                long lastReportedChunk = 0;

                var fileProgress = new Progress<long>(currentBytes =>
                {
                    long delta = currentBytes - lastReportedChunk;
                    if (delta > 0)
                    {
                        lastReportedChunk = currentBytes;
                        long newTotal = Interlocked.Add(ref transferredBytes, delta);
                        speedTracker.RecordBytes(newTotal);

                        var (speed, eta) = speedTracker.GetMetrics(totalBytes, newTotal);

                        progress.Report(new TransferProgressInfo
                        {
                            TotalFiles = totalFiles,
                            CompletedFiles = completedFiles,
                            FailedFiles = failedFiles,
                            TotalBytes = totalBytes,
                            TransferredBytes = newTotal,
                            CurrentFileName = fileNode.Name,
                            CurrentFileTransferredBytes = currentBytes,
                            CurrentFileSize = fileNode.Size,
                            BytesPerSecond = speed,
                            EstimatedTimeRemaining = eta,
                            State = _isPaused ? TransferState.Paused : TransferState.Running,
                            StatusMessage = $"Copying {fileNode.Name}..."
                        });
                    }
                });

                bool success = false;
                string? errorMsg = null;

                try
                {
                    // Copy file from device (or simulator) to destination
                    await _deviceService.CopyFileAsync(deviceId, fileNode.FullPathSource, fileNode.FullPathDest, fileProgress, ct);

                    // Ensure any remaining difference in file size is accounted for
                    long remainingDelta = fileNode.Size - lastReportedChunk;
                    if (remainingDelta > 0)
                    {
                        long newTotal = Interlocked.Add(ref transferredBytes, remainingDelta);
                        speedTracker.RecordBytes(newTotal);
                    }

                    // Update laptop state JSON file atomically for consistency
                    var mediaNode = new MediaItemNode
                    {
                        Name = fileNode.Name,
                        IsDirectory = false,
                        Size = fileNode.Size,
                        RelativePath = fileNode.RelativePath
                    };
                    await _localStorageService.AppendOrUpdateLaptopStateAsync(laptopStateFilePath, deviceName, mediaNode);

                    Interlocked.Increment(ref completedFiles);
                    fileNode.Status = FileDiffStatus.Success;
                    fileNode.NotifyStatusChanged();
                    success = true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failedFiles);
                    fileNode.Status = FileDiffStatus.Failed;
                    fileNode.NotifyStatusChanged();
                    errorMsg = ex.Message;
                }
                finally
                {
                    onFileCompleted(fileNode, success, errorMsg);

                    var (speed, eta) = speedTracker.GetMetrics(totalBytes, transferredBytes);
                    progress.Report(new TransferProgressInfo
                    {
                        TotalFiles = totalFiles,
                        CompletedFiles = completedFiles,
                        FailedFiles = failedFiles,
                        TotalBytes = totalBytes,
                        TransferredBytes = transferredBytes,
                        CurrentFileName = fileNode.Name,
                        BytesPerSecond = speed,
                        EstimatedTimeRemaining = eta,
                        State = _isPaused ? TransferState.Paused : TransferState.Running,
                        StatusMessage = success ? $"Backed up {fileNode.Name}" : $"Failed: {fileNode.Name} ({errorMsg})"
                    });
                }
            }, options);

            // Post all files into dataflow pipeline
            foreach (var file in filesToTransfer)
            {
                ct.ThrowIfCancellationRequested();
                actionBlock.Post(file);
            }

            actionBlock.Complete();
            await actionBlock.Completion;

            progress.Report(new TransferProgressInfo
            {
                TotalFiles = totalFiles,
                CompletedFiles = completedFiles,
                FailedFiles = failedFiles,
                TotalBytes = totalBytes,
                TransferredBytes = transferredBytes,
                State = TransferState.Completed,
                StatusMessage = $"Backup finished: {completedFiles} completed, {failedFiles} failed."
            });
        }
        catch (OperationCanceledException)
        {
            progress.Report(new TransferProgressInfo
            {
                TotalFiles = totalFiles,
                CompletedFiles = completedFiles,
                FailedFiles = failedFiles,
                TotalBytes = totalBytes,
                TransferredBytes = transferredBytes,
                State = TransferState.Cancelled,
                StatusMessage = "Backup cancelled by user."
            });
        }
        catch (Exception ex)
        {
            progress.Report(new TransferProgressInfo
            {
                TotalFiles = totalFiles,
                CompletedFiles = completedFiles,
                FailedFiles = failedFiles,
                TotalBytes = totalBytes,
                TransferredBytes = transferredBytes,
                State = TransferState.Error,
                StatusMessage = $"Transfer error: {ex.Message}"
            });
            throw;
        }
        finally
        {
            _isRunning = false;
            _isPaused = false;
            _cts?.Dispose();
            _cts = null;
        }
    }
}

/// <summary>
/// Calculates moving average byte transfer rate over a rolling window (5 seconds).
/// </summary>
public class MovingAverageSpeedTracker
{
    private readonly TimeSpan _window;
    private readonly Queue<(DateTime Timestamp, long TotalBytes)> _samples = new();
    private readonly object _lock = new();

    public MovingAverageSpeedTracker(TimeSpan window)
    {
        _window = window;
    }

    public void RecordBytes(long totalBytes)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            _samples.Enqueue((now, totalBytes));

            // Remove samples older than the rolling window
            var cutoff = now - _window;
            while (_samples.Count > 1 && _samples.Peek().Timestamp < cutoff)
            {
                _samples.Dequeue();
            }
        }
    }

    public (double speedBytesPerSec, TimeSpan? estimatedTimeRemaining) GetMetrics(long targetTotalBytes, long currentTransferredBytes)
    {
        lock (_lock)
        {
            if (_samples.Count < 2)
            {
                return (0, null);
            }

            var oldest = _samples.Peek();
            var newest = _samples.Last();

            double elapsedSec = (newest.Timestamp - oldest.Timestamp).TotalSeconds;
            if (elapsedSec <= 0.05)
            {
                return (0, null);
            }

            long byteDiff = newest.TotalBytes - oldest.TotalBytes;
            double speed = Math.Max(0, byteDiff / elapsedSec);

            if (speed > 1000) // At least 1 KB/s to give meaningful ETA
            {
                long remainingBytes = Math.Max(0, targetTotalBytes - currentTransferredBytes);
                double remainingSec = remainingBytes / speed;
                if (remainingSec >= 0 && remainingSec < 86400 * 30) // Within 30 days
                {
                    return (speed, TimeSpan.FromSeconds(remainingSec));
                }
            }

            return (speed, null);
        }
    }
}
