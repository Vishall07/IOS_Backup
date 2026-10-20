using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IosBackupApp.Models;
using IosBackupApp.Services.Interfaces;

namespace IosBackupApp.Services;

public class LocalStorageService : ILocalStorageService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _stateFileLock = new(1, 1);

    public (long totalBytes, long freeBytes) GetDriveStorageMetrics(string directoryPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                directoryPath = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            }

            var fullPath = Path.GetFullPath(directoryPath);
            var driveLetter = Path.GetPathRoot(fullPath);
            if (!string.IsNullOrEmpty(driveLetter))
            {
                var driveInfo = new DriveInfo(driveLetter);
                if (driveInfo.IsReady)
                {
                    return (driveInfo.TotalSize, driveInfo.AvailableFreeSpace);
                }
            }
        }
        catch
        {
            // Fallback
        }

        return (0L, 0L);
    }

    public Task<DeviceState> ScanLocalDirectoryAsync(string destinationPath, IProgress<string>? progress, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            if (!Directory.Exists(destinationPath))
            {
                Directory.CreateDirectory(destinationPath);
            }

            // Check if there is a DCIM subdirectory or if destinationPath is itself DCIM
            string targetScanPath = destinationPath;
            string dcimSub = Path.Combine(destinationPath, "DCIM");
            if (Directory.Exists(dcimSub))
            {
                targetScanPath = dcimSub;
            }

            var state = new DeviceState
            {
                DeviceName = Environment.MachineName,
                ScanDate = DateTime.UtcNow,
                Root = new MediaItemNode
                {
                    Name = "DCIM",
                    IsDirectory = true,
                    RelativePath = "DCIM",
                    Children = new List<MediaItemNode>()
                }
            };

            progress?.Report($"Scanning local backup directory: {destinationPath}...");
            ScanDirectoryRecursive(new DirectoryInfo(targetScanPath), state.Root, "DCIM", progress, ct);

            return state;
        }, ct);
    }

    private void ScanDirectoryRecursive(
        DirectoryInfo currentDir,
        MediaItemNode parentNode,
        string currentRelativePath,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Subdirectories
        DirectoryInfo[] subDirs;
        try
        {
            subDirs = currentDir.GetDirectories();
        }
        catch
        {
            subDirs = Array.Empty<DirectoryInfo>();
        }

        foreach (var dir in subDirs)
        {
            ct.ThrowIfCancellationRequested();
            string subRelativePath = $"{currentRelativePath}/{dir.Name}";

            var folderNode = new MediaItemNode
            {
                Name = dir.Name,
                IsDirectory = true,
                RelativePath = subRelativePath,
                Children = new List<MediaItemNode>()
            };

            parentNode.Children ??= new List<MediaItemNode>();
            parentNode.Children.Add(folderNode);

            ScanDirectoryRecursive(dir, folderNode, subRelativePath, progress, ct);
        }

        // Files
        FileInfo[] files;
        try
        {
            files = currentDir.GetFiles();
        }
        catch
        {
            files = Array.Empty<FileInfo>();
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            // Skip metadata json files themselves if stored in same root
            if (file.Name.Equals("iphone_state.json", StringComparison.OrdinalIgnoreCase) ||
                file.Name.Equals("laptop_state.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fileNode = new MediaItemNode
            {
                Name = file.Name,
                IsDirectory = false,
                Size = file.Length,
                RelativePath = $"{currentRelativePath}/{file.Name}"
            };

            parentNode.Children ??= new List<MediaItemNode>();
            parentNode.Children.Add(fileNode);
            parentNode.Size += file.Length;
        }
    }

    public async Task SaveStateJsonAsync(string filePath, DeviceState state)
    {
        await _stateFileLock.WaitAsync();
        try
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(state, JsonOptions);
            await File.WriteAllTextAsync(filePath, json);
        }
        finally
        {
            _stateFileLock.Release();
        }
    }

    public async Task<DeviceState?> LoadStateJsonAsync(string filePath)
    {
        if (!File.Exists(filePath)) return null;

        await _stateFileLock.WaitAsync();
        try
        {
            string json = await File.ReadAllTextAsync(filePath);
            return JsonSerializer.Deserialize<DeviceState>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
        finally
        {
            _stateFileLock.Release();
        }
    }

    public async Task AppendOrUpdateLaptopStateAsync(string stateFilePath, string deviceName, MediaItemNode copiedFile)
    {
        await _stateFileLock.WaitAsync();
        try
        {
            DeviceState? state = null;
            if (File.Exists(stateFilePath))
            {
                try
                {
                    string json = await File.ReadAllTextAsync(stateFilePath);
                    state = JsonSerializer.Deserialize<DeviceState>(json, JsonOptions);
                }
                catch { }
            }

            state ??= new DeviceState
            {
                DeviceName = deviceName,
                ScanDate = DateTime.UtcNow,
                Root = new MediaItemNode
                {
                    Name = "DCIM",
                    IsDirectory = true,
                    RelativePath = "DCIM",
                    Children = new List<MediaItemNode>()
                }
            };

            state.ScanDate = DateTime.UtcNow;

            // Insert into the tree based on relative path parts
            var parts = copiedFile.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            MediaItemNode current = state.Root;

            // Skip first part if it is "DCIM"
            int startIndex = (parts.Length > 0 && parts[0].Equals("DCIM", StringComparison.OrdinalIgnoreCase)) ? 1 : 0;

            for (int i = startIndex; i < parts.Length - 1; i++)
            {
                string folderName = parts[i];
                current.Children ??= new List<MediaItemNode>();
                var nextFolder = current.Children.FirstOrDefault(c => c.IsDirectory && c.Name.Equals(folderName, StringComparison.OrdinalIgnoreCase));
                if (nextFolder == null)
                {
                    nextFolder = new MediaItemNode
                    {
                        Name = folderName,
                        IsDirectory = true,
                        RelativePath = string.Join("/", parts.Take(i + 1)),
                        Children = new List<MediaItemNode>()
                    };
                    current.Children.Add(nextFolder);
                }
                current = nextFolder;
            }

            // Insert or update file node
            current.Children ??= new List<MediaItemNode>();
            var existingFile = current.Children.FirstOrDefault(c => !c.IsDirectory && c.Name.Equals(copiedFile.Name, StringComparison.OrdinalIgnoreCase));
            if (existingFile != null)
            {
                existingFile.Size = copiedFile.Size;
                existingFile.Hash = copiedFile.Hash;
            }
            else
            {
                current.Children.Add(new MediaItemNode
                {
                    Name = copiedFile.Name,
                    IsDirectory = false,
                    Size = copiedFile.Size,
                    RelativePath = copiedFile.RelativePath,
                    Hash = copiedFile.Hash
                });
            }

            // Recalculate root size
            RecalculateSizes(state.Root);

            string updatedJson = JsonSerializer.Serialize(state, JsonOptions);
            await File.WriteAllTextAsync(stateFilePath, updatedJson);
        }
        finally
        {
            _stateFileLock.Release();
        }
    }

    private static long RecalculateSizes(MediaItemNode node)
    {
        if (!node.IsDirectory) return node.Size;

        long sum = 0;
        if (node.Children != null)
        {
            foreach (var child in node.Children)
            {
                sum += RecalculateSizes(child);
            }
        }
        node.Size = sum;
        return sum;
    }
}
