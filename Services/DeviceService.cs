using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IosBackupApp.Models;
using IosBackupApp.Services.Interfaces;
using MediaDevices;

namespace IosBackupApp.Services;

public class DeviceService : IDeviceService
{
    public const string SimulatedDeviceId = "SIMULATED_IPHONE_15_PRO";
    public event EventHandler? DevicesChanged;

    private readonly Timer? _pollTimer;
    private readonly HashSet<string> _knownDeviceIds = new();

    public DeviceService()
    {
        // Poll every 3 seconds for USB device changes
        _pollTimer = new Timer(async _ => await CheckDeviceChangesAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void StartMonitoring()
    {
        _pollTimer?.Change(TimeSpan.Zero, TimeSpan.FromSeconds(3));
    }

    public void StopMonitoring()
    {
        _pollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private async Task CheckDeviceChangesAsync()
    {
        try
        {
            var devices = await GetAvailableDevicesAsync();
            var currentIds = new HashSet<string>(devices.Select(d => d.Id));
            
            bool changed = !currentIds.SetEquals(_knownDeviceIds);
            if (changed)
            {
                _knownDeviceIds.Clear();
                foreach (var id in currentIds) _knownDeviceIds.Add(id);
                DevicesChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch
        {
            // Ignore polling errors
        }
    }

    public Task<IReadOnlyList<DeviceInfoModel>> GetAvailableDevicesAsync()
    {
        return Task.Run(() =>
        {
            var result = new List<DeviceInfoModel>();

            try
            {
                var devices = (MediaDeviceManager.Instance.GetDevices() ?? Enumerable.Empty<MediaDevice>()).ToList();
                foreach (var dev in devices)
                {
                    try
                    {
                        int? battery = null;
                        try { battery = dev.PowerLevel.HasValue ? (int)dev.PowerLevel.Value : null; } catch { }

                        string name = string.IsNullOrWhiteSpace(dev.FriendlyName) ? (dev.Description ?? "Apple iPhone") : dev.FriendlyName;
                        string model = string.IsNullOrWhiteSpace(dev.Model) ? (dev.Manufacturer ?? "Apple Inc.") : dev.Model;

                        result.Add(new DeviceInfoModel
                        {
                            Id = dev.DeviceId,
                            Name = name,
                            Model = model,
                            Manufacturer = dev.Manufacturer ?? "Apple Inc.",
                            BatteryLevel = battery,
                            IsSimulated = false,
                            IsConnected = true
                        });
                    }
                    catch
                    {
                        // Device might have disconnected or is locked
                    }
                }
            }
            catch
            {
                // WPD enumeration failure
            }

            // Always add the realistic Simulated device so the user can test all features immediately
            result.Add(new DeviceInfoModel
            {
                Id = SimulatedDeviceId,
                Name = "iPhone 15 Pro",
                Model = "A3102 (256 GB)",
                Manufacturer = "Apple Inc.",
                BatteryLevel = 87,
                IsSimulated = true,
                IsConnected = true
            });

            return (IReadOnlyList<DeviceInfoModel>)result;
        });
    }

    public Task<(long totalBytes, long freeBytes)> GetStorageMetricsAsync(string deviceId)
    {
        return Task.Run(() =>
        {
            if (deviceId == SimulatedDeviceId)
            {
                // Simulated: 256 GB total, 72 GB free (184 GB used)
                long total = 256L * 1024 * 1024 * 1024;
                long free = 72L * 1024 * 1024 * 1024;
                return (total, free);
            }

            var device = (MediaDeviceManager.Instance.GetDevices() ?? Enumerable.Empty<MediaDevice>()).FirstOrDefault(d => d.DeviceId == deviceId);
            if (device == null)
            {
                throw new InvalidOperationException("Selected iPhone device was not found or is disconnected.");
            }

            bool wasConnected = device.IsConnected;
            try
            {
                if (!wasConnected)
                {
                    device.Connect();
                }

                var drives = device.GetDrives()?.ToList();
                if (drives != null && drives.Count > 0)
                {
                    long totalSize = drives.Sum(d => d.TotalSize);
                    long freeSpace = drives.Sum(d => d.AvailableFreeSpace);
                    return (totalSize, freeSpace);
                }

                return (0L, 0L);
            }
            finally
            {
                if (!wasConnected && device.IsConnected)
                {
                    device.Disconnect();
                }
            }
        });
    }

    public Task<DeviceState> ScanDeviceAsync(string deviceId, IProgress<string>? progress, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            if (deviceId == SimulatedDeviceId)
            {
                return GenerateSimulatedIPhoneState(progress, ct);
            }

            var device = (MediaDeviceManager.Instance.GetDevices() ?? Enumerable.Empty<MediaDevice>()).FirstOrDefault(d => d.DeviceId == deviceId);
            if (device == null)
            {
                throw new InvalidOperationException("iPhone is disconnected or not recognized.");
            }

            device.Connect();
            try
            {
                var state = new DeviceState
                {
                    DeviceName = device.FriendlyName ?? "Apple iPhone",
                    ScanDate = DateTime.UtcNow,
                    Root = new MediaItemNode
                    {
                        Name = "DCIM",
                        IsDirectory = true,
                        RelativePath = "DCIM",
                        Children = new List<MediaItemNode>()
                    }
                };

                progress?.Report("Discovering photo folders on iPhone...");

                // Find candidate photo folder roots (either DCIM subfolders or direct month folders)
                var folderRoots = FindMediaFolderRoots(device);

                if (folderRoots.Count == 0)
                {
                    throw new DirectoryNotFoundException(
                        "No photos or media folders could be accessed on the iPhone. Please ensure your iPhone is UNLOCKED with your passcode and you have tapped 'Trust This Computer' on the iPhone screen.");
                }

                progress?.Report($"Scanning {folderRoots.Count} media folder(s) on iPhone...");
                foreach (var (mtpPath, folderName) in folderRoots)
                {
                    ct.ThrowIfCancellationRequested();

                    var folderNode = new MediaItemNode
                    {
                        Name = folderName,
                        IsDirectory = true,
                        RelativePath = $"DCIM/{folderName}",
                        Children = new List<MediaItemNode>()
                    };

                    state.Root.Children ??= new List<MediaItemNode>();
                    state.Root.Children.Add(folderNode);

                    progress?.Report($"Scanning folder: {folderName}...");
                    ScanMtpDirectoryRecursive(device, mtpPath, folderNode, $"DCIM/{folderName}", progress, ct);
                }

                state.Root.Size = state.Root.Children?.Sum(c => c.Size) ?? 0;
                return state;
            }
            finally
            {
                if (device.IsConnected)
                {
                    device.Disconnect();
                }
            }
        }, ct);
    }

    private List<(string MtpPath, string FolderName)> FindMediaFolderRoots(MediaDevice device)
    {
        var result = new List<(string MtpPath, string FolderName)>();
        try
        {
            var drives = device.GetDrives() ?? Enumerable.Empty<MediaDriveInfo>();
            foreach (var drive in drives)
            {
                if (drive.RootDirectory == null) continue;
                string rootPath = drive.RootDirectory.FullName;

                // 1. Get subdirectories of drive root
                string[] rootSubDirs;
                try
                {
                    rootSubDirs = device.GetDirectories(rootPath);
                }
                catch
                {
                    rootSubDirs = Array.Empty<string>();
                }

                // Check for DCIM folder directly under drive root
                string? dcimPath = rootSubDirs.FirstOrDefault(d => 
                    string.Equals(Path.GetFileName(d.TrimEnd('\\', '/')), "DCIM", StringComparison.OrdinalIgnoreCase));

                // Check for DCIM folder one level deeper (e.g. \Internal Storage\DCIM)
                if (dcimPath == null)
                {
                    foreach (var dir in rootSubDirs)
                    {
                        try
                        {
                            var inner = device.GetDirectories(dir);
                            var innerDcim = inner.FirstOrDefault(d => 
                                string.Equals(Path.GetFileName(d.TrimEnd('\\', '/')), "DCIM", StringComparison.OrdinalIgnoreCase));
                            if (innerDcim != null)
                            {
                                dcimPath = innerDcim;
                                break;
                            }
                        }
                        catch { }
                    }
                }

                // If DCIM folder exists: return its subfolders (e.g. 100APPLE, 101APPLE)
                if (dcimPath != null)
                {
                    try
                    {
                        var dcimSub = device.GetDirectories(dcimPath);
                        foreach (var sub in dcimSub)
                        {
                            string folderName = Path.GetFileName(sub.TrimEnd('\\', '/'));
                            result.Add((sub, folderName));
                        }
                    }
                    catch { }

                    if (result.Count > 0) return result;
                }

                // If NO DCIM folder exists:
                // Check if \Internal Storage contains media/month folders (e.g. 202409__, 202610__, etc.)
                foreach (var dir in rootSubDirs)
                {
                    try
                    {
                        var inner = device.GetDirectories(dir);
                        if (inner.Length > 0)
                        {
                            foreach (var innerDir in inner)
                            {
                                string innerName = Path.GetFileName(innerDir.TrimEnd('\\', '/'));
                                result.Add((innerDir, innerName));
                            }
                            if (result.Count > 0) return result;
                        }
                    }
                    catch { }
                }

                // Fallback: use rootSubDirs directly
                if (rootSubDirs.Length > 0)
                {
                    foreach (var dir in rootSubDirs)
                    {
                        string folderName = Path.GetFileName(dir.TrimEnd('\\', '/'));
                        result.Add((dir, folderName));
                    }
                    if (result.Count > 0) return result;
                }
            }
        }
        catch { }

        return result;
    }

    private void ScanMtpDirectoryRecursive(
        MediaDevice device,
        string currentMtpPath,
        MediaItemNode parentNode,
        string currentRelativePath,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // 1. Get subdirectories
        string[] subDirs;
        try
        {
            subDirs = device.GetDirectories(currentMtpPath);
        }
        catch
        {
            subDirs = Array.Empty<string>();
        }

        foreach (var subDir in subDirs)
        {
            ct.ThrowIfCancellationRequested();
            string folderName = Path.GetFileName(subDir.TrimEnd('\\', '/'));
            string subRelativePath = $"{currentRelativePath}/{folderName}";

            var folderNode = new MediaItemNode
            {
                Name = folderName,
                IsDirectory = true,
                RelativePath = subRelativePath,
                Children = new List<MediaItemNode>()
            };

            parentNode.Children ??= new List<MediaItemNode>();
            parentNode.Children.Add(folderNode);

            progress?.Report($"Scanning {folderName}...");
            ScanMtpDirectoryRecursive(device, subDir, folderNode, subRelativePath, progress, ct);
        }

        // 2. Get files
        string[] files;
        try
        {
            files = device.GetFiles(currentMtpPath);
        }
        catch
        {
            files = Array.Empty<string>();
        }

        foreach (var filePath in files)
        {
            ct.ThrowIfCancellationRequested();
            string fileName = Path.GetFileName(filePath);
            long fileSize = 0;

            try
            {
                var fileInfo = device.GetFileInfo(filePath);
                fileSize = (long)fileInfo.Length;
            }
            catch
            {
                // If file info cannot be queried directly
            }

            var fileNode = new MediaItemNode
            {
                Name = fileName,
                IsDirectory = false,
                Size = fileSize,
                RelativePath = $"{currentRelativePath}/{fileName}",
                SourcePath = filePath
            };

            parentNode.Children ??= new List<MediaItemNode>();
            parentNode.Children.Add(fileNode);
            parentNode.Size += fileSize;
        }
    }

    public async Task CopyFileAsync(
        string deviceId,
        string sourcePath,
        string destPath,
        IProgress<long>? byteProgress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string? destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        if (deviceId == SimulatedDeviceId)
        {
            await CopySimulatedFileAsync(sourcePath, destPath, byteProgress, ct);
            return;
        }

        await Task.Run(() =>
        {
            var device = (MediaDeviceManager.Instance.GetDevices() ?? Enumerable.Empty<MediaDevice>()).FirstOrDefault(d => d.DeviceId == deviceId);
            if (device == null)
            {
                throw new InvalidOperationException("Device not found.");
            }

            bool wasConnected = device.IsConnected;
            try
            {
                if (!wasConnected) device.Connect();

                // Download using MediaDevices DownloadFile with stream for progress reporting
                using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
                using var progressStream = new ProgressReportingStream(fileStream, byteProgress, ct);

                device.DownloadFile(sourcePath, progressStream);

                // Preserve original timestamp if available
                try
                {
                    var fileInfo = device.GetFileInfo(sourcePath);
                    if (fileInfo.LastWriteTime.HasValue)
                    {
                        File.SetLastWriteTime(destPath, fileInfo.LastWriteTime.Value);
                    }
                    if (fileInfo.CreationTime.HasValue)
                    {
                        File.SetCreationTime(destPath, fileInfo.CreationTime.Value);
                    }
                }
                catch { }
            }
            finally
            {
                if (!wasConnected && device.IsConnected)
                {
                    device.Disconnect();
                }
            }
        }, ct);
    }

    private static async Task CopySimulatedFileAsync(
        string sourcePath,
        string destPath,
        IProgress<long>? byteProgress,
        CancellationToken ct)
    {
        // Extract size hint or generate realistic bytes (e.g. 2 MB - 8 MB)
        long fileSize = 3_500_000;
        if (sourcePath.EndsWith(".MOV", StringComparison.OrdinalIgnoreCase) || sourcePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            fileSize = 15_000_000; // 15 MB video
        }

        byte[] buffer = new byte[65536];
        new Random().NextBytes(buffer);

        using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        long written = 0;

        while (written < fileSize)
        {
            ct.ThrowIfCancellationRequested();
            int toWrite = (int)Math.Min(buffer.Length, fileSize - written);
            await fs.WriteAsync(buffer.AsMemory(0, toWrite), ct);
            written += toWrite;
            byteProgress?.Report(written);
            // Simulate realistic USB transfer speed (~40 MB/s Lightning)
            await Task.Delay(2, ct);
        }

        File.SetLastWriteTime(destPath, DateTime.Now.AddDays(-10));
    }

    private DeviceState GenerateSimulatedIPhoneState(IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Scanning simulated iPhone DCIM directory...");
        Thread.Sleep(300);

        var state = new DeviceState
        {
            DeviceName = "iPhone 15 Pro",
            ScanDate = DateTime.UtcNow,
            Root = new MediaItemNode
            {
                Name = "DCIM",
                IsDirectory = true,
                RelativePath = "DCIM",
                Children = new List<MediaItemNode>()
            }
        };

        string[] folders = { "100APPLE", "101APPLE", "102APPLE" };
        int photoIndex = 1;

        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Scanning {folder} on iPhone 15 Pro...");
            Thread.Sleep(200);

            var folderNode = new MediaItemNode
            {
                Name = folder,
                IsDirectory = true,
                RelativePath = $"DCIM/{folder}",
                Children = new List<MediaItemNode>()
            };

            // Add realistic HEIC photos, MOV videos, and AAE metadata files
            int countInFolder = folder == "100APPLE" ? 18 : (folder == "101APPLE" ? 14 : 12);
            for (int i = 0; i < countInFolder; i++)
            {
                string baseName = $"IMG_{photoIndex:D4}";
                photoIndex++;

                // 80% HEIC photos, 20% MOV videos
                if (i % 5 == 0)
                {
                    // Video
                    folderNode.Children.Add(new MediaItemNode
                    {
                        Name = $"{baseName}.MOV",
                        IsDirectory = false,
                        Size = 14_850_000 + (i * 1_200_000),
                        RelativePath = $"DCIM/{folder}/{baseName}.MOV"
                    });
                }
                else
                {
                    // Photo
                    folderNode.Children.Add(new MediaItemNode
                    {
                        Name = $"{baseName}.HEIC",
                        IsDirectory = false,
                        Size = 2_850_000 + (i * 250_000),
                        RelativePath = $"DCIM/{folder}/{baseName}.HEIC"
                    });

                    // Occasionally add portrait edit / adjustment file .AAE
                    if (i % 3 == 0)
                    {
                        folderNode.Children.Add(new MediaItemNode
                        {
                            Name = $"{baseName}.AAE",
                            IsDirectory = false,
                            Size = 4_096,
                            RelativePath = $"DCIM/{folder}/{baseName}.AAE"
                        });
                    }
                }
            }

            folderNode.Size = folderNode.Children.Sum(c => c.Size);
            state.Root.Children.Add(folderNode);
        }

        state.Root.Size = state.Root.Children.Sum(c => c.Size);
        progress?.Report($"Scan complete: found {photoIndex - 1} media files.");
        return state;
    }
}

/// <summary>
/// Stream wrapper that intercepts writes to report byte-level progress.
/// </summary>
public class ProgressReportingStream : Stream
{
    private readonly Stream _baseStream;
    private readonly IProgress<long>? _progress;
    private readonly CancellationToken _ct;
    private long _totalWritten;

    public ProgressReportingStream(Stream baseStream, IProgress<long>? progress, CancellationToken ct)
    {
        _baseStream = baseStream;
        _progress = progress;
        _ct = ct;
    }

    public override bool CanRead => _baseStream.CanRead;
    public override bool CanSeek => _baseStream.CanSeek;
    public override bool CanWrite => _baseStream.CanWrite;
    public override long Length => _baseStream.Length;
    public override long Position
    {
        get => _baseStream.Position;
        set => _baseStream.Position = value;
    }

    public override void Flush() => _baseStream.Flush();
    public override int Read(byte[] buffer, int offset, int count) => _baseStream.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => _baseStream.Seek(offset, origin);
    public override void SetLength(long value) => _baseStream.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count)
    {
        _ct.ThrowIfCancellationRequested();
        _baseStream.Write(buffer, offset, count);
        _totalWritten += count;
        _progress?.Report(_totalWritten);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        _ct.ThrowIfCancellationRequested();
        cancellationToken.ThrowIfCancellationRequested();
        await _baseStream.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
        _totalWritten += count;
        _progress?.Report(_totalWritten);
    }
}
