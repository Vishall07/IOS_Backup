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

    [Fact]
    public void HeicDecoding_WithMagickNet_LoadsAndResizesCorrectly()
    {
        string sampleHeic = @"C:\Users\Admin\Pictures\iPhone_Backup\DCIM\202406__\IMG_0284.HEIC";
        if (!File.Exists(sampleHeic))
        {
            return; // Skip if environment does not have the sample file
        }

        using var image = new ImageMagick.MagickImage(sampleHeic);
        Assert.True(image.Width > 0);
        Assert.True(image.Height > 0);

        image.AutoOrient();
        image.Thumbnail(new ImageMagick.MagickGeometry(300, 300) { IgnoreAspectRatio = false });

        using var ms = new MemoryStream();
        image.Write(ms, ImageMagick.MagickFormat.Bmp);
        Assert.True(ms.Length > 0);

        ms.Position = 0;
        var bitmap = new System.Windows.Media.Imaging.BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        bitmap.StreamSource = ms;
        bitmap.EndInit();
        bitmap.Freeze();

        Assert.NotNull(bitmap);
        Assert.True(bitmap.PixelWidth > 0);
        Assert.True(bitmap.PixelHeight > 0);
    }

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    [System.Runtime.InteropServices.Guid("bcc18b79-ba16-442f-80c4-8a59c07c4e45")]
    public interface IShellItemImageFactory
    {
        [System.Runtime.InteropServices.PreserveSig]
        int GetImage([System.Runtime.InteropServices.In] POINT size, [System.Runtime.InteropServices.In] int flags, [System.Runtime.InteropServices.Out] out IntPtr phbm);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern int SHCreateItemFromParsingName(
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [System.Runtime.InteropServices.In] ref Guid riid,
        [System.Runtime.InteropServices.Out, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Interface)] out object ppv);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [Fact]
    public void ShellThumbnail_ExtractsForVideo()
    {
        string sampleMov = @"C:\Users\Admin\Pictures\iPhone_Backup\DCIM\202406__\IMG_0284.MOV";
        if (!File.Exists(sampleMov)) return;

        Guid factoryGuid = new Guid("bcc18b79-ba16-442f-80c4-8a59c07c4e45");
        int hr = SHCreateItemFromParsingName(sampleMov, IntPtr.Zero, ref factoryGuid, out object obj);
        if (hr == 0 && obj is IShellItemImageFactory factory)
        {
            hr = factory.GetImage(new POINT { X = 256, Y = 256 }, 0x0, out IntPtr hBitmap);
            if (hr == 0 && hBitmap != IntPtr.Zero)
            {
                try
                {
                    var bs = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap,
                        IntPtr.Zero,
                        System.Windows.Int32Rect.Empty,
                        System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    bs.Freeze();
                    Assert.True(bs.PixelWidth > 0);
                }
                finally
                {
                    DeleteObject(hBitmap);
                }
            }
        }
    }

    [Fact]
    public async Task ThumbnailService_GeneratesAndCachesThumbnails()
    {
        string sampleHeic = @"C:\Users\Admin\Pictures\iPhone_Backup\DCIM\202406__\IMG_0284.HEIC";
        if (!File.Exists(sampleHeic)) return;

        var service = new ThumbnailService();

        // 1. First fetch generates thumbnail
        var thumb1 = await service.GetThumbnailAsync(sampleHeic, targetSize: 200);
        Assert.NotNull(thumb1);
        Assert.True(thumb1.PixelWidth > 0);
        Assert.True(thumb1.PixelHeight > 0);

        // 2. Second fetch retrieves from memory cache (same reference)
        var thumb2 = await service.GetThumbnailAsync(sampleHeic, targetSize: 200);
        Assert.Same(thumb1, thumb2);

        // 3. Full-resolution load
        var full = await service.GetFullImageAsync(sampleHeic, maxWidth: 1920, maxHeight: 1080);
        Assert.NotNull(full);
        Assert.True(full.PixelWidth > thumb1.PixelWidth);
    }

    [Fact]
    public async Task Gallery_ScansDestinationAndPairsLivePhotos()
    {
        string backupDir = @"C:\Users\Admin\Pictures\iPhone_Backup\DCIM";
        if (!Directory.Exists(backupDir)) return;

        var deviceService = new DeviceService();
        var localStorage = new LocalStorageService();
        var diffEngine = new DiffEngine();
        var transferEngine = new TransferEngine(deviceService, localStorage);
        var thumbnailService = new ThumbnailService();

        var vm = new ViewModels.MainViewModel(deviceService, localStorage, diffEngine, transferEngine, thumbnailService)
        {
            DestinationFolder = @"C:\Users\Admin\Pictures\iPhone_Backup"
        };

        // Switch to gallery view and refresh
        await vm.SwitchViewModeAsync("Gallery");
        Assert.True(vm.IsGalleryView);
        Assert.NotEmpty(vm.GalleryItems);

        // Verify Live Photo pairing:
        // Live Photo should pair IMG_0284.HEIC and IMG_0284.MOV into a single item with IsLivePhoto == true
        var livePhotoItem = vm.GalleryItems.FirstOrDefault(i => i.IsLivePhoto);
        Assert.NotNull(livePhotoItem);
        Assert.True(livePhotoItem.IsLivePhoto);
        Assert.NotNull(livePhotoItem.CompanionVideoPath);
        Assert.True(File.Exists(livePhotoItem.CompanionVideoPath));
        Assert.Equal(GalleryMediaType.LivePhoto, livePhotoItem.MediaType);

        // Verify counts
        Assert.True(vm.GalleryTotalPhotos > 0 || vm.GalleryTotalLivePhotos > 0);
        Assert.Equal(vm.GalleryItems.Count, vm.FilteredGalleryItems.Count);
    }

    [Fact]
    public async Task Gallery_FilteringAndLightboxNavigationWorks()
    {
        string backupDir = @"C:\Users\Admin\Pictures\iPhone_Backup\DCIM";
        if (!Directory.Exists(backupDir)) return;

        var deviceService = new DeviceService();
        var localStorage = new LocalStorageService();
        var diffEngine = new DiffEngine();
        var transferEngine = new TransferEngine(deviceService, localStorage);
        var thumbnailService = new ThumbnailService();

        var vm = new ViewModels.MainViewModel(deviceService, localStorage, diffEngine, transferEngine, thumbnailService)
        {
            DestinationFolder = @"C:\Users\Admin\Pictures\iPhone_Backup"
        };

        await vm.RefreshGalleryAsync();
        Assert.NotEmpty(vm.GalleryItems);

        // 1. Filter by Live Photos
        vm.GalleryFilter = "LivePhotos";
        Assert.All(vm.FilteredGalleryItems, i => Assert.Equal(GalleryMediaType.LivePhoto, i.MediaType));

        // 2. Filter by Videos
        vm.GalleryFilter = "Videos";
        Assert.All(vm.FilteredGalleryItems, i => Assert.True(i.IsVideo || i.IsLivePhoto));

        // 3. Search text filter
        vm.GalleryFilter = "All";
        vm.GallerySearchText = "0284";
        Assert.All(vm.FilteredGalleryItems, i => Assert.Contains("0284", i.FileName));

        // 4. Lightbox opening and navigation
        var firstItem = vm.FilteredGalleryItems.First();
        await vm.OpenGalleryItemAsync(firstItem);

        Assert.True(vm.IsLightboxOpen);
        Assert.Equal(firstItem, vm.SelectedGalleryItem);
        Assert.NotNull(vm.LightboxImage);

        // Test Next navigation
        if (vm.FilteredGalleryItems.Count > 1)
        {
            var expectedNext = vm.FilteredGalleryItems[1];
            await vm.NextGalleryItemAsync();
            Assert.Equal(expectedNext, vm.SelectedGalleryItem);
        }

        // Test Live Photo playback
        if (vm.SelectedGalleryItem?.IsLivePhoto == true)
        {
            vm.PlayLivePhoto();
            Assert.True(vm.IsLightboxLivePhotoPlaying);
            Assert.True(vm.IsLightboxVideo);
            Assert.Equal(vm.SelectedGalleryItem.CompanionVideoPath, vm.ActiveVideoUri);

            vm.StopLivePhotoVideo();
            Assert.False(vm.IsLightboxLivePhotoPlaying);
            Assert.False(vm.IsLightboxVideo);
        }

        // Close Lightbox
        vm.CloseLightbox();
        Assert.False(vm.IsLightboxOpen);
        Assert.Null(vm.SelectedGalleryItem);
    }
}




