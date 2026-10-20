using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IosBackupApp.Models;

namespace IosBackupApp.Services.Interfaces;

public interface IDeviceService
{
    event EventHandler? DevicesChanged;

    Task<IReadOnlyList<DeviceInfoModel>> GetAvailableDevicesAsync();
    Task<(long totalBytes, long freeBytes)> GetStorageMetricsAsync(string deviceId);
    Task<DeviceState> ScanDeviceAsync(string deviceId, IProgress<string>? progress, CancellationToken ct);
    Task CopyFileAsync(string deviceId, string sourcePath, string destPath, IProgress<long>? byteProgress, CancellationToken ct);
    void StartMonitoring();
    void StopMonitoring();
}
