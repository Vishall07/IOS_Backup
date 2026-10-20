using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IosBackupApp.Models;
using IosBackupApp.Services;
using IosBackupApp.Services.Interfaces;
using Xunit;

namespace IosBackupApp.Tests;

public class AppTests
{
    [Fact]
    public void UnifiedJsonSchema_SerializesAndDeserializesCorrectly()
    {
        var originalState = new DeviceState
        {
            DeviceName = "iPhone 15",
            ScanDate = new DateTime(2026, 10, 20, 13, 4, 0, DateTimeKind.Utc),
            Root = new MediaItemNode
            {
                Name = "DCIM",
                IsDirectory = true,
                Size = 3500000,
                Children = new List<MediaItemNode>
                {
                    new MediaItemNode
                    {
                        Name = "100APPLE",
                        IsDirectory = true,
                        Size = 3500000,
                        Children = new List<MediaItemNode>
                        {
                            new MediaItemNode
                            {
                                Name = "IMG_0001.HEIC",
                                IsDirectory = false,
                                Size = 3500000,
                                Hash = "optional_md5",
                                RelativePath = "DCIM/100APPLE/IMG_0001.HEIC"
                            }
                        }
                    }
                }
            }
        };

        string json = JsonSerializer.Serialize(originalState, new JsonSerializerOptions { WriteIndented = true });
        Assert.Contains("\"DeviceName\": \"iPhone 15\"", json);
        Assert.Contains("\"Root\":", json);
        Assert.Contains("\"IMG_0001.HEIC\"", json);

        var deserialized = JsonSerializer.Deserialize<DeviceState>(json);
        Assert.NotNull(deserialized);
        Assert.Equal("iPhone 15", deserialized.DeviceName);
        Assert.Equal("DCIM", deserialized.Root.Name);
        Assert.Single(deserialized.Root.Children!);
        Assert.Equal("100APPLE", deserialized.Root.Children![0].Name);
        Assert.Equal("IMG_0001.HEIC", deserialized.Root.Children![0].Children![0].Name);
        Assert.Equal(3500000, deserialized.Root.Children![0].Children![0].Size);
        Assert.Equal("optional_md5", deserialized.Root.Children![0].Children![0].Hash);
    }

    [Fact]
    public void DiffEngine_IdentifiesPendingAndBackedUpFilesAccurately()
    {
        var iphoneState = new DeviceState
        {
            DeviceName = "iPhone 15 Pro",
            Root = new MediaItemNode
            {
                Name = "DCIM",
                IsDirectory = true,
                RelativePath = "DCIM",
                Children = new List<MediaItemNode>
                {
                    new MediaItemNode
                    {
                        Name = "100APPLE",
                        IsDirectory = true,
                        RelativePath = "DCIM/100APPLE",
                        Children = new List<MediaItemNode>
                        {
                            // 1. Existing identical file (BackedUp)
                            new MediaItemNode { Name = "IMG_0001.HEIC", IsDirectory = false, Size = 2000, RelativePath = "DCIM/100APPLE/IMG_0001.HEIC" },
                            // 2. Missing file (PendingBackup)
                            new MediaItemNode { Name = "IMG_0002.HEIC", IsDirectory = false, Size = 3000, RelativePath = "DCIM/100APPLE/IMG_0002.HEIC" },
                            // 3. Existing but modified size (PendingBackup)
                            new MediaItemNode { Name = "IMG_0003.MOV", IsDirectory = false, Size = 15000, RelativePath = "DCIM/100APPLE/IMG_0003.MOV" }
                        }
                    }
                }
            }
        };

        var laptopState = new DeviceState
        {
            DeviceName = "Laptop",
            Root = new MediaItemNode
            {
                Name = "DCIM",
                IsDirectory = true,
                RelativePath = "DCIM",
                Children = new List<MediaItemNode>
                {
                    new MediaItemNode
                    {
                        Name = "100APPLE",
                        IsDirectory = true,
                        RelativePath = "DCIM/100APPLE",
                        Children = new List<MediaItemNode>
                        {
                            new MediaItemNode { Name = "IMG_0001.HEIC", IsDirectory = false, Size = 2000, RelativePath = "DCIM/100APPLE/IMG_0001.HEIC" },
                            new MediaItemNode { Name = "IMG_0003.MOV", IsDirectory = false, Size = 5000, RelativePath = "DCIM/100APPLE/IMG_0003.MOV" } // Different size
                        }
                    }
                }
            }
        };

        var diffEngine = new DiffEngine();
        var diffResult = diffEngine.Compare(iphoneState, laptopState, @"C:\Backup");

        Assert.Equal(3, diffResult.TotalFilesCount);
        Assert.Equal(2, diffResult.PendingFilesCount);
        Assert.Equal(1, diffResult.BackedUpFilesCount);

        var folderNode = diffResult.RootNode.Children[0];
        var file1 = folderNode.Children.First(c => c.Name == "IMG_0001.HEIC");
        var file2 = folderNode.Children.First(c => c.Name == "IMG_0002.HEIC");
        var file3 = folderNode.Children.First(c => c.Name == "IMG_0003.MOV");

        Assert.Equal(FileDiffStatus.BackedUp, file1.Status);
        Assert.False(file1.IsChecked); // Already backed up, should default to unchecked

        Assert.Equal(FileDiffStatus.PendingBackup, file2.Status);
        Assert.True(file2.IsChecked); // Missing, should default to checked

        Assert.Equal(FileDiffStatus.PendingBackup, file3.Status);
        Assert.True(file3.IsChecked); // Modified size, should default to checked

        // Folder with mixed children should have indeterminate state
        Assert.Null(folderNode.IsChecked);
    }

    [Fact]
    public void CheckboxCascading_ParentToChildrenAndChildrenToParent()
    {
        var root = new FileDiffNodeViewModel { Name = "Root", IsDirectory = true };
        var folder = new FileDiffNodeViewModel { Name = "100APPLE", IsDirectory = true, Parent = root };
        root.Children.Add(folder);

        var file1 = new FileDiffNodeViewModel { Name = "File1.jpg", IsDirectory = false, Parent = folder };
        var file2 = new FileDiffNodeViewModel { Name = "File2.jpg", IsDirectory = false, Parent = folder };
        folder.Children.Add(file1);
        folder.Children.Add(file2);

        // 1. Setting folder to true cascades true to all files
        folder.IsChecked = true;
        Assert.True(file1.IsChecked);
        Assert.True(file2.IsChecked);
        Assert.True(folder.IsChecked);

        // 2. Unchecking one child makes folder indeterminate (null)
        file1.IsChecked = false;
        Assert.Null(folder.IsChecked);

        // 3. Unchecking all children makes folder false
        file2.IsChecked = false;
        Assert.False(folder.IsChecked);

        // 4. Checking folder sets all children to true
        folder.IsChecked = true;
        Assert.True(file1.IsChecked);
        Assert.True(file2.IsChecked);
    }

    [Fact]
    public void SpeedTracker_CalculatesAccurateRateAndEta()
    {
        var tracker = new MovingAverageSpeedTracker(TimeSpan.FromSeconds(5));

        long targetTotal = 100_000_000; // 100 MB
        tracker.RecordBytes(0);
        Thread.Sleep(50);
        tracker.RecordBytes(10_000_000); // 10 MB in ~50ms

        var (speed, eta) = tracker.GetMetrics(targetTotal, 10_000_000);
        Assert.True(speed > 0);
        Assert.NotNull(eta);
        Assert.True(eta.Value.TotalSeconds > 0);
    }

    [Fact]
    public async Task LocalStorageService_AppendOrUpdateLaptopState_MaintainsIntegrity()
    {
        var service = new LocalStorageService();
        string tempDir = Path.Combine(Path.GetTempPath(), "IosBackupAppTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string stateFile = Path.Combine(tempDir, "laptop_state.json");

        try
        {
            var node1 = new MediaItemNode
            {
                Name = "IMG_0001.HEIC",
                IsDirectory = false,
                Size = 2500000,
                RelativePath = "DCIM/100APPLE/IMG_0001.HEIC"
            };

            await service.AppendOrUpdateLaptopStateAsync(stateFile, "TestMachine", node1);
            Assert.True(File.Exists(stateFile));

            var loaded1 = await service.LoadStateJsonAsync(stateFile);
            Assert.NotNull(loaded1);
            Assert.Equal("DCIM", loaded1.Root.Name);
            Assert.Single(loaded1.Root.Children!);
            Assert.Equal("100APPLE", loaded1.Root.Children![0].Name);
            Assert.Single(loaded1.Root.Children![0].Children!);
            Assert.Equal("IMG_0001.HEIC", loaded1.Root.Children![0].Children![0].Name);

            // Add second file
            var node2 = new MediaItemNode
            {
                Name = "IMG_0002.MOV",
                IsDirectory = false,
                Size = 12000000,
                RelativePath = "DCIM/100APPLE/IMG_0002.MOV"
            };

            await service.AppendOrUpdateLaptopStateAsync(stateFile, "TestMachine", node2);
            var loaded2 = await service.LoadStateJsonAsync(stateFile);
            Assert.NotNull(loaded2);
            Assert.Equal(2, loaded2.Root.Children![0].Children!.Count);
            Assert.Equal(14500000, loaded2.Root.Size);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TransferEngine_TransfersFilesWithConcurrencyAndUpdatesState()
    {
        var deviceService = new DeviceService();
        var localStorage = new LocalStorageService();
        var transferEngine = new TransferEngine(deviceService, localStorage);

        string tempDest = Path.Combine(Path.GetTempPath(), "IosBackupTransferTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDest);
        string stateFile = Path.Combine(tempDest, "laptop_state.json");

        try
        {
            var file1 = new FileDiffNodeViewModel
            {
                Name = "IMG_0001.HEIC",
                IsDirectory = false,
                Size = 100_000,
                RelativePath = "DCIM/100APPLE/IMG_0001.HEIC",
                FullPathSource = "DCIM/100APPLE/IMG_0001.HEIC",
                FullPathDest = Path.Combine(tempDest, "DCIM", "100APPLE", "IMG_0001.HEIC"),
                Status = FileDiffStatus.PendingBackup
            };

            var file2 = new FileDiffNodeViewModel
            {
                Name = "IMG_0002.HEIC",
                IsDirectory = false,
                Size = 120_000,
                RelativePath = "DCIM/100APPLE/IMG_0002.HEIC",
                FullPathSource = "DCIM/100APPLE/IMG_0002.HEIC",
                FullPathDest = Path.Combine(tempDest, "DCIM", "100APPLE", "IMG_0002.HEIC"),
                Status = FileDiffStatus.PendingBackup
            };

            var files = new List<FileDiffNodeViewModel> { file1, file2 };
            var progressReports = new List<TransferProgressInfo>();
            var progress = new Progress<TransferProgressInfo>(p => progressReports.Add(p));

            var completedList = new List<string>();

            await transferEngine.StartTransferAsync(
                files,
                DeviceService.SimulatedDeviceId,
                tempDest,
                stateFile,
                "Simulated iPhone 15 Pro",
                concurrency: 2,
                progress: progress,
                onFileCompleted: (node, success, err) =>
                {
                    if (success) completedList.Add(node.Name);
                },
                cancellationToken: CancellationToken.None);

            Assert.Equal(2, completedList.Count);
            Assert.Contains("IMG_0001.HEIC", completedList);
            Assert.Contains("IMG_0002.HEIC", completedList);

            Assert.Equal(FileDiffStatus.Success, file1.Status);
            Assert.Equal(FileDiffStatus.Success, file2.Status);

            Assert.True(File.Exists(file1.FullPathDest));
            Assert.True(File.Exists(file2.FullPathDest));
            Assert.True(File.Exists(stateFile));

            // Verify state file contains both files
            var loadedState = await localStorage.LoadStateJsonAsync(stateFile);
            Assert.NotNull(loadedState);
            Assert.Equal(2, loadedState.Root.Children![0].Children!.Count);
        }
        finally
        {
            if (Directory.Exists(tempDest))
            {
                Directory.Delete(tempDest, recursive: true);
            }
        }
    }

    [Fact]
    public async Task MainViewModel_EndToEndScanAndSelection_WorksCorrectly()
    {
        var deviceService = new DeviceService();
        var localStorage = new LocalStorageService();
        var diffEngine = new DiffEngine();
        var transferEngine = new TransferEngine(deviceService, localStorage);

        string tempDest = Path.Combine(Path.GetTempPath(), "IosBackupVmTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDest);

        try
        {
            var vm = new ViewModels.MainViewModel(deviceService, localStorage, diffEngine, transferEngine)
            {
                DestinationFolder = tempDest
            };

            await vm.InitializeAsync();

            // Check that devices list contains the simulated device
            Assert.NotEmpty(vm.Devices);
            var simDev = vm.Devices.First(d => d.Id == DeviceService.SimulatedDeviceId);
            vm.SelectedDevice = simDev;

            Assert.Equal("iPhone 15 Pro", vm.IPhoneStorage.Title);
            Assert.True(vm.IPhoneStorage.TotalBytes > 0);

            // Execute Scan
            await vm.ScanAsync();

            Assert.True(vm.TotalFilesCount > 0);
            Assert.Equal(vm.TotalFilesCount, vm.PendingFilesCount);
            Assert.Equal(0, vm.BackedUpFilesCount);
            Assert.NotEmpty(vm.TreeItems);

            // Test Selection Commands
            Assert.Equal(vm.TotalFilesCount, vm.SelectedFilesCount);

            vm.DeselectAll();
            Assert.Equal(0, vm.SelectedFilesCount);

            vm.SelectAllPending();
            Assert.Equal(vm.TotalFilesCount, vm.SelectedFilesCount);

            vm.SelectAll();
            Assert.Equal(vm.TotalFilesCount, vm.SelectedFilesCount);
        }
        finally
        {
            if (Directory.Exists(tempDest))
            {
                Directory.Delete(tempDest, recursive: true);
            }
        }
    }

    [Fact]
    public async Task MainViewModel_BackupOnlyNotBackedUp_CalculatesDiffAndTransfersOnlyPending()
    {
        var deviceService = new DeviceService();
        var localStorage = new LocalStorageService();
        var diffEngine = new DiffEngine();
        var transferEngine = new TransferEngine(deviceService, localStorage);

        string tempDest = Path.Combine(Path.GetTempPath(), "IosBackupUnbackedOnlyTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDest);

        try
        {
            var vm = new ViewModels.MainViewModel(deviceService, localStorage, diffEngine, transferEngine)
            {
                DestinationFolder = tempDest
            };

            await vm.InitializeAsync();
            var simDev = vm.Devices.First(d => d.Id == DeviceService.SimulatedDeviceId);
            vm.SelectedDevice = simDev;

            // 1. Test FindDiffAndShowPendingAsync without prior manual scan
            await vm.FindDiffAndShowPendingAsync();

            Assert.Equal("PendingOnly", vm.FilterMode);
            Assert.True(vm.PendingFilesCount > 0);
            Assert.Equal(vm.PendingFilesCount, vm.SelectedFilesCount);

            // Verify all selected files are strictly PendingBackup
            var selectedFiles = vm.TreeItems.SelectMany(folder => folder.GetSelectedFiles()).ToList();
            Assert.NotEmpty(selectedFiles);
            Assert.All(selectedFiles, f => Assert.Equal(Models.FileDiffStatus.PendingBackup, f.Status));

            // 2. Perform BackupNotBackedUpAsync
            await vm.BackupNotBackedUpAsync();

            // After transfer completes, pending files should decrease and files exist in destination
            Assert.Equal(0, vm.PendingFilesCount);
            Assert.Equal(vm.TotalFilesCount, vm.BackedUpFilesCount);

            // Verify files actually copied to disk
            string dcimDir = Path.Combine(tempDest, "DCIM");
            Assert.True(Directory.Exists(dcimDir));
            var copiedFiles = Directory.GetFiles(dcimDir, "*.*", SearchOption.AllDirectories);
            Assert.Equal(vm.TotalFilesCount, copiedFiles.Length);
        }
        finally
        {
            if (Directory.Exists(tempDest))
            {
                Directory.Delete(tempDest, recursive: true);
            }
        }
    }
}
