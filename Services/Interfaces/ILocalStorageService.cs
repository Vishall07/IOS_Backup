using System;
using System.Threading;
using System.Threading.Tasks;
using IosBackupApp.Models;

namespace IosBackupApp.Services.Interfaces;

public interface ILocalStorageService
{
    (long totalBytes, long freeBytes) GetDriveStorageMetrics(string directoryPath);
    Task<DeviceState> ScanLocalDirectoryAsync(string destinationPath, IProgress<string>? progress, CancellationToken ct);
    Task SaveStateJsonAsync(string filePath, DeviceState state);
    Task<DeviceState?> LoadStateJsonAsync(string filePath);
    Task AppendOrUpdateLaptopStateAsync(string stateFilePath, string deviceName, MediaItemNode copiedFile);
}
