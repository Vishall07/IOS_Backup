using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IosBackupApp.Models;
using IosBackupApp.Services;
using IosBackupApp.Services.Interfaces;

namespace IosBackupApp.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IDeviceService _deviceService;
    private readonly ILocalStorageService _localStorageService;
    private readonly IDiffEngine _diffEngine;
    private readonly ITransferEngine _transferEngine;
    private readonly IThumbnailService _thumbnailService;

    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _transferCts;
    private CancellationTokenSource? _galleryThumbnailCts;
    private readonly object _logLock = new();

    [ObservableProperty]
    private ObservableCollection<DeviceInfoModel> _devices = new();

    [ObservableProperty]
    private DeviceInfoModel? _selectedDevice;

    [ObservableProperty]
    private string _destinationFolder = string.Empty;

    [ObservableProperty]
    private StorageMetrics _iPhoneStorage = new() { Title = "iPhone Storage", Subtitle = "Checking device..." };

    [ObservableProperty]
    private StorageMetrics _laptopStorage = new() { Title = "Laptop Storage", Subtitle = "Destination Drive" };

    [ObservableProperty]
    private ObservableCollection<FileDiffNodeViewModel> _treeItems = new();

    [ObservableProperty]
    private int _concurrency = 1;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _isTransferring;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private string _statusMessage = "Ready. Connect an iPhone via USB or select Demo Mode to begin.";

    [ObservableProperty]
    private TransferProgressInfo _progressInfo = new();

    [ObservableProperty]
    private int _totalFilesCount;

    [ObservableProperty]
    private int _pendingFilesCount;

    [ObservableProperty]
    private int _backedUpFilesCount;

    [ObservableProperty]
    private int _selectedFilesCount;

    [ObservableProperty]
    private long _selectedFilesBytes;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _filterMode = "All"; // "All", "PendingOnly", "BackedUpOnly"

    [ObservableProperty]
    private bool _skipLivePhotoVideos = true;

    [ObservableProperty]
    private string _iphoneStateFilePath = string.Empty;

    [ObservableProperty]
    private string _laptopStateFilePath = string.Empty;

    [ObservableProperty]
    private ObservableCollection<string> _activityLogs = new();

    [ObservableProperty]
    private ObservableCollection<LogEntry> _processLogs = new();

    [ObservableProperty]
    private bool _isProcessModalOpen;

    [ObservableProperty]
    private string _modalTitle = "Internal Process Monitor";

    [ObservableProperty]
    private string _modalSubtitle = "Real-time diagnostic trace";

    #region Gallery & Lightbox Properties
    [ObservableProperty]
    private bool _isGalleryView;

    [ObservableProperty]
    private ObservableCollection<GalleryItemViewModel> _galleryItems = new();

    [ObservableProperty]
    private ObservableCollection<GalleryItemViewModel> _filteredGalleryItems = new();

    [ObservableProperty]
    private string _galleryFilter = "All"; // "All", "Photos", "Videos", "LivePhotos"

    [ObservableProperty]
    private string _gallerySearchText = string.Empty;

    [ObservableProperty]
    private int _galleryCardSize = 160;

    [ObservableProperty]
    private bool _isLoadingGallery;

    [ObservableProperty]
    private int _galleryTotalPhotos;

    [ObservableProperty]
    private int _galleryTotalVideos;

    [ObservableProperty]
    private int _galleryTotalLivePhotos;

    [ObservableProperty]
    private bool _isLightboxOpen;

    [ObservableProperty]
    private GalleryItemViewModel? _selectedGalleryItem;

    [ObservableProperty]
    private System.Windows.Media.Imaging.BitmapSource? _lightboxImage;

    [ObservableProperty]
    private bool _isLightboxLoadingImage;

    [ObservableProperty]
    private bool _isLightboxLivePhotoPlaying;

    [ObservableProperty]
    private bool _isLightboxVideo;

    [ObservableProperty]
    private string? _activeVideoUri;

    [ObservableProperty]
    private int _selectedGalleryIndex;

    [ObservableProperty]
    private string _lightboxCounter = string.Empty;
    #endregion

    private FileDiffNodeViewModel? _rootDiffNode;

    public string SelectedSizeFormatted => StorageMetrics.FormatBytes(SelectedFilesBytes);

    public MainViewModel(
        IDeviceService deviceService,
        ILocalStorageService localStorageService,
        IDiffEngine diffEngine,
        ITransferEngine transferEngine,
        IThumbnailService? thumbnailService = null)
    {
        _deviceService = deviceService;
        _localStorageService = localStorageService;
        _diffEngine = diffEngine;
        _transferEngine = transferEngine;
        _thumbnailService = thumbnailService ?? new ThumbnailService();

        // Default destination folder: ~/Pictures/iPhone_Backup
        string picturesDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        DestinationFolder = Path.Combine(picturesDir, "iPhone_Backup");

        _deviceService.DevicesChanged += OnDevicesChanged;
        _deviceService.StartMonitoring();

        Log("iPhone Media Backup Application initialized.");
    }


    public async Task InitializeAsync()
    {
        UpdateLaptopStorageMetrics();
        await RefreshDevicesAsync();
    }

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        Action action = async () =>
        {
            Log("USB device state change detected.");
            await RefreshDevicesAsync();
        };

        if (Application.Current?.Dispatcher != null)
        {
            Application.Current.Dispatcher.InvokeAsync(action);
        }
        else
        {
            action();
        }
    }

    [RelayCommand]
    public async Task RefreshDevicesAsync()
    {
        try
        {
            var previousSelectedId = SelectedDevice?.Id;
            var list = await _deviceService.GetAvailableDevicesAsync();

            Devices.Clear();
            foreach (var dev in list)
            {
                Devices.Add(dev);
            }

            if (!string.IsNullOrEmpty(previousSelectedId))
            {
                SelectedDevice = Devices.FirstOrDefault(d => d.Id == previousSelectedId);
            }

            SelectedDevice ??= Devices.FirstOrDefault();
            await OnSelectedDeviceChangedAsync();
        }
        catch (Exception ex)
        {
            Log($"Error enumerating devices: {ex.Message}");
        }
    }

    partial void OnSelectedDeviceChanged(DeviceInfoModel? value)
    {
        _ = OnSelectedDeviceChangedAsync();
    }

    private async Task OnSelectedDeviceChangedAsync()
    {
        if (SelectedDevice == null)
        {
            IPhoneStorage.Title = "iPhone Storage";
            IPhoneStorage.Subtitle = "No device selected";
            IPhoneStorage.TotalBytes = 0;
            IPhoneStorage.FreeBytes = 0;
            IPhoneStorage.NotifyMetricsChanged();
            return;
        }

        try
        {
            IPhoneStorage.Title = SelectedDevice.Name;
            IPhoneStorage.Subtitle = SelectedDevice.DisplayName;

            var (total, free) = await _deviceService.GetStorageMetricsAsync(SelectedDevice.Id);
            IPhoneStorage.TotalBytes = total;
            IPhoneStorage.FreeBytes = free;
            IPhoneStorage.NotifyMetricsChanged();

            Log($"Selected {SelectedDevice.Name} (Total: {IPhoneStorage.TotalFormatted}, Free: {IPhoneStorage.FreeFormatted})");
        }
        catch (Exception ex)
        {
            Log($"Warning: could not read storage metrics for {SelectedDevice.Name}: {ex.Message}");
        }
    }

    partial void OnDestinationFolderChanged(string value)
    {
        UpdateLaptopStorageMetrics();
    }

    private void UpdateLaptopStorageMetrics()
    {
        try
        {
            var (total, free) = _localStorageService.GetDriveStorageMetrics(DestinationFolder);
            LaptopStorage.TotalBytes = total;
            LaptopStorage.FreeBytes = free;
            string root = Path.GetPathRoot(Path.GetFullPath(DestinationFolder)) ?? "Drive";
            LaptopStorage.Subtitle = $"Drive {root} - Destination";
            LaptopStorage.NotifyMetricsChanged();
        }
        catch { }
    }

    [RelayCommand]
    public void BrowseDestination()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Destination Folder for iPhone Backup",
            InitialDirectory = Directory.Exists(DestinationFolder) ? DestinationFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };

        if (dialog.ShowDialog() == true)
        {
            DestinationFolder = dialog.FolderName;
            UpdateLaptopStorageMetrics();
            Log($"Destination changed to: {DestinationFolder}");
        }
    }

    [RelayCommand]
    public async Task ScanAsync()
    {
        if (SelectedDevice == null)
        {
            MessageBox.Show("Please select an iPhone device or Demo Mode first.", "No Device", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(DestinationFolder))
        {
            MessageBox.Show("Please select a valid destination folder.", "Invalid Destination", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsScanning = true;
        StatusMessage = "Starting media scan...";
        ModalTitle = "Scanning iPhone Media";
        ModalSubtitle = $"Discovering photos and videos on {SelectedDevice.DisplayName}...";
        IsProcessModalOpen = true;

        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        var scanProgress = new Progress<string>(msg =>
        {
            StatusMessage = msg;
            ModalSubtitle = msg;
            Log(msg, LogLevel.Scan, "SCAN");
        });

        try
        {
            Log($"Starting scan for {SelectedDevice.Name}...");

            // 1. Recursive scan on iPhone (via WPD MTP)
            var iphoneTask = _deviceService.ScanDeviceAsync(SelectedDevice.Id, scanProgress, ct);

            // 2. Recursive scan on Local Destination
            var laptopTask = _localStorageService.ScanLocalDirectoryAsync(DestinationFolder, scanProgress, ct);

            await Task.WhenAll(iphoneTask, laptopTask);

            var iphoneState = await iphoneTask;
            var laptopState = await laptopTask;

            // 3. Save JSON state files
            string stateDir = Path.Combine(DestinationFolder, ".backup_metadata");
            IphoneStateFilePath = Path.Combine(stateDir, "iphone_state.json");
            LaptopStateFilePath = Path.Combine(stateDir, "laptop_state.json");

            await _localStorageService.SaveStateJsonAsync(IphoneStateFilePath, iphoneState);
            await _localStorageService.SaveStateJsonAsync(LaptopStateFilePath, laptopState);
            Log($"Saved state JSON files to {stateDir}");

            // 4. Run Diff Engine
            StatusMessage = "Comparing iPhone media with local backup...";
            var diffResult = _diffEngine.Compare(iphoneState, laptopState, DestinationFolder);

            _rootDiffNode = diffResult.RootNode;
            HookCheckboxEvents(_rootDiffNode);

            TotalFilesCount = diffResult.TotalFilesCount;
            PendingFilesCount = diffResult.PendingFilesCount;
            BackedUpFilesCount = diffResult.BackedUpFilesCount;

            UpdateTreeDisplay();
            RecalculateSelectionStats();

            StatusMessage = $"Scan completed. Found {TotalFilesCount} items ({PendingFilesCount} pending backup, {BackedUpFilesCount} already backed up).";
            Log(StatusMessage);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan cancelled.";
            Log("Scan cancelled by user.");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scan failed: {ex.Message}";
            Log($"Scan error: {ex.Message}");
            MessageBox.Show($"Scan failed: {ex.Message}\n\nTip: Make sure the iPhone is unlocked and you've tapped 'Trust This Computer' if prompted.", "Scan Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsScanning = false;
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    [RelayCommand]
    public void CancelScan()
    {
        _scanCts?.Cancel();
    }

    private void HookCheckboxEvents(FileDiffNodeViewModel node)
    {
        node.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(FileDiffNodeViewModel.IsChecked))
            {
                RecalculateSelectionStats();
            }
        };

        foreach (var child in node.Children)
        {
            HookCheckboxEvents(child);
        }
    }

    private void RecalculateSelectionStats()
    {
        if (_rootDiffNode == null)
        {
            SelectedFilesCount = 0;
            SelectedFilesBytes = 0;
            OnPropertyChanged(nameof(SelectedSizeFormatted));
            return;
        }

        var selected = _rootDiffNode.GetSelectedFiles().ToList();
        SelectedFilesCount = selected.Count;
        SelectedFilesBytes = selected.Sum(f => f.Size);
        OnPropertyChanged(nameof(SelectedSizeFormatted));
    }

    [RelayCommand]
    public async Task BackupNotBackedUpAsync()
    {
        if (IsTransferring || IsScanning) return;

        if (SelectedDevice == null)
        {
            MessageBox.Show("Please select an iPhone device or Demo Mode first.", "No Device", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(DestinationFolder))
        {
            MessageBox.Show("Please choose a backup destination folder first.", "Destination Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 1. If media has not been scanned or diff not generated, run scan & diff comparison between iPhone and laptop JSON
        if (_rootDiffNode == null)
        {
            Log("Computing diff between iPhone and laptop backup JSON to discover unbacked files...", LogLevel.Info, "DIFF");
            await ScanAsync();
            if (_rootDiffNode == null) return;
        }

        // 2. Select strictly only PendingBackup items and uncheck all already backed-up items ("rest should not be backedUp")
        SelectAllPending();

        // 3. Switch filter mode to show only pending files and expand all folders so user sees the files
        FilterMode = "PendingOnly";
        SetExpansionRecursive(_rootDiffNode, true);

        var filesToTransfer = _rootDiffNode.GetSelectedFiles()
            .Where(f => f.Status == FileDiffStatus.PendingBackup && (!SkipLivePhotoVideos || !f.IsLivePhotoVideo))
            .ToList();

        if (filesToTransfer.Count == 0)
        {
            string emptyMsg = SkipLivePhotoVideos 
                ? "All iPhone photos and standalone videos are already backed up!\n\n(Live Photo .MOV video clips are currently excluded. Uncheck 'Exclude Live Photo Clips' if you wish to back up 3-second motion clips too.)"
                : "All iPhone media files are already backed up!\n\nNo unbacked photos or videos were found between iPhone and laptop JSON.";
            Log("Diff check complete: All media files on iPhone already exist in laptop backup (0 files to back up).", LogLevel.Success, "DIFF");
            MessageBox.Show(emptyMsg, "Everything Backed Up", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Log($"Diff result: Found {filesToTransfer.Count} unbacked media file(s) ({StorageMetrics.FormatBytes(filesToTransfer.Sum(f => f.Size))}). Initiating backup of unbacked files only...", LogLevel.Info, "DIFF");

        // 4. Begin backup transfer of strictly those unbacked files
        await ExecuteTransferAsync(filesToTransfer);
    }

    [RelayCommand]
    public async Task FindDiffAndShowPendingAsync()
    {
        if (IsScanning || IsTransferring) return;

        if (SelectedDevice == null)
        {
            MessageBox.Show("Please select an iPhone device or Demo Mode first.", "No Device", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(DestinationFolder))
        {
            MessageBox.Show("Please choose a backup destination folder first.", "Destination Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_rootDiffNode == null)
        {
            Log("Comparing iPhone and laptop JSON to compute media diff...", LogLevel.Info, "DIFF");
            await ScanAsync();
            if (_rootDiffNode == null) return;
        }

        SelectAllPending();
        FilterMode = "PendingOnly";
        SetExpansionRecursive(_rootDiffNode, true);
        Log($"Diff view: Showing {PendingFilesCount} unbacked media file(s) ({SelectedSizeFormatted}) needing backup.", LogLevel.Info, "DIFF");
    }

    [RelayCommand]
    public async Task StartBackupAsync()
    {
        if (SelectedDevice == null)
        {
            MessageBox.Show("Please select an iPhone device or Demo Mode first.", "No Device", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(DestinationFolder))
        {
            MessageBox.Show("Please choose a backup destination folder first.", "Destination Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // If not scanned yet, automatically scan iPhone first!
        if (_rootDiffNode == null)
        {
            Log("No media scanned yet. Automatically scanning iPhone before backup...");
            await ScanAsync();

            if (_rootDiffNode == null)
            {
                return;
            }
        }

        var filesToTransfer = _rootDiffNode.GetSelectedFiles().ToList();
        if (filesToTransfer.Count == 0)
        {
            SelectAllPending();
            filesToTransfer = _rootDiffNode.GetSelectedFiles().ToList();
            if (filesToTransfer.Count == 0)
            {
                MessageBox.Show("All media files are already backed up! There are no pending files to transfer.", "Backup Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        await ExecuteTransferAsync(filesToTransfer);
    }

    private async Task ExecuteTransferAsync(List<FileDiffNodeViewModel> filesToTransfer)
    {
        if (SelectedDevice == null) return;

        IsTransferring = true;
        IsPaused = false;
        ModalTitle = "Backing Up iPhone Media";
        ModalSubtitle = $"Streaming {filesToTransfer.Count} file(s) to destination...";
        IsProcessModalOpen = true;

        _transferCts = new CancellationTokenSource();

        Log($"Initiating backup of {filesToTransfer.Count} files ({StorageMetrics.FormatBytes(filesToTransfer.Sum(f => f.Size))}) with {Concurrency} concurrent stream(s)...", LogLevel.Transfer, "XFER");

        var progress = new Progress<TransferProgressInfo>(info =>
        {
            ProgressInfo = info;
            StatusMessage = info.StatusMessage;
            ModalSubtitle = $"{info.StatusMessage} ({info.OverallPercentage:F1}%)";
            IsPaused = info.State == TransferState.Paused;
        });

        try
        {
            await _transferEngine.StartTransferAsync(
                filesToTransfer,
                SelectedDevice.Id,
                DestinationFolder,
                LaptopStateFilePath,
                SelectedDevice.Name,
                Concurrency,
                progress,
                onFileCompleted: (file, success, err) =>
                {
                    Action update = () =>
                    {
                        if (success)
                        {
                            PendingFilesCount = Math.Max(0, PendingFilesCount - 1);
                            BackedUpFilesCount++;
                            Log($"Backed up: {file.Name} ({file.FormattedSize})", LogLevel.Success, "XFER");
                        }
                        else
                        {
                            Log($"Failed to copy {file.Name}: {err}", LogLevel.Error, "XFER");
                        }
                    };

                    if (Application.Current?.Dispatcher != null)
                    {
                        Application.Current.Dispatcher.InvokeAsync(update);
                    }
                    else
                    {
                        update();
                    }
                },
                _transferCts.Token);

            Log($"Backup operation finished! Successfully processed files.");
            UpdateLaptopStorageMetrics();
            await OnSelectedDeviceChangedAsync();
        }
        catch (OperationCanceledException)
        {
            Log("Backup cancelled by user.");
        }
        catch (Exception ex)
        {
            Log($"Transfer engine encountered error: {ex.Message}");
            MessageBox.Show($"Backup halted: {ex.Message}", "Transfer Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsTransferring = false;
            IsPaused = false;
            _transferCts?.Dispose();
            _transferCts = null;
            RecalculateSelectionStats();
        }
    }

    [RelayCommand]
    public void PauseResume()
    {
        if (!IsTransferring) return;

        if (_transferEngine.IsPaused)
        {
            _transferEngine.Resume();
            IsPaused = false;
            Log("Backup resumed.");
        }
        else
        {
            _transferEngine.Pause();
            IsPaused = true;
            Log("Backup paused.");
        }
    }

    [RelayCommand]
    public void CancelBackup()
    {
        if (IsTransferring)
        {
            _transferEngine.Cancel();
            _transferCts?.Cancel();
            Log("Cancelling backup...");
        }
    }

    [RelayCommand]
    public void SelectAllPending()
    {
        if (_rootDiffNode == null) return;
        foreach (var file in _rootDiffNode.GetAllFiles())
        {
            if (SkipLivePhotoVideos && file.IsLivePhotoVideo)
            {
                file.IsChecked = false;
            }
            else
            {
                file.IsChecked = file.Status == FileDiffStatus.PendingBackup;
            }
        }
        RecalculateSelectionStats();
    }

    [RelayCommand]
    public void SelectAll()
    {
        if (_rootDiffNode == null) return;
        _rootDiffNode.IsChecked = true;
        RecalculateSelectionStats();
    }

    [RelayCommand]
    public void DeselectAll()
    {
        if (_rootDiffNode == null) return;
        _rootDiffNode.IsChecked = false;
        RecalculateSelectionStats();
    }

    [RelayCommand]
    public void ExpandAll()
    {
        SetExpansionRecursive(_rootDiffNode, true);
    }

    [RelayCommand]
    public void CollapseAll()
    {
        SetExpansionRecursive(_rootDiffNode, false);
    }

    private static void SetExpansionRecursive(FileDiffNodeViewModel? node, bool expand)
    {
        if (node == null) return;
        node.IsExpanded = expand;
        foreach (var child in node.Children)
        {
            SetExpansionRecursive(child, expand);
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
    }

    partial void OnFilterModeChanged(string value)
    {
        ApplyFilter();
    }

    partial void OnSkipLivePhotoVideosChanged(bool value)
    {
        SelectAllPending();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (_rootDiffNode == null) return;
        ApplyFilterRecursive(_rootDiffNode, SearchText, FilterMode);
        UpdateTreeDisplay();
    }

    private bool ApplyFilterRecursive(FileDiffNodeViewModel node, string search, string filterMode)
    {
        if (!node.IsDirectory)
        {
            bool matchesSearch = string.IsNullOrWhiteSpace(search) || node.Name.Contains(search, StringComparison.OrdinalIgnoreCase);
            bool matchesStatus = filterMode switch
            {
                "PendingOnly" => node.Status == FileDiffStatus.PendingBackup,
                "BackedUpOnly" => node.Status == FileDiffStatus.BackedUp || node.Status == FileDiffStatus.Success,
                _ => true
            };
            bool matchesLivePhoto = !SkipLivePhotoVideos || !node.IsLivePhotoVideo;

            node.IsVisible = matchesSearch && matchesStatus && matchesLivePhoto;
            return node.IsVisible;
        }

        bool anyChildVisible = false;
        foreach (var child in node.Children)
        {
            if (ApplyFilterRecursive(child, search, filterMode))
            {
                anyChildVisible = true;
            }
        }

        node.IsVisible = anyChildVisible;
        return node.IsVisible;
    }

    private void UpdateTreeDisplay()
    {
        TreeItems.Clear();
        if (_rootDiffNode != null)
        {
            // Show either root DCIM or its subfolders (100APPLE, etc.)
            foreach (var child in _rootDiffNode.Children.Where(c => c.IsVisible))
            {
                TreeItems.Add(child);
            }
        }
    }

    [RelayCommand]
    public void OpenDestinationFolder()
    {
        try
        {
            if (!Directory.Exists(DestinationFolder))
            {
                Directory.CreateDirectory(DestinationFolder);
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = DestinationFolder,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open directory: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    public void OpenStateJsonFolder()
    {
        try
        {
            string stateDir = Path.Combine(DestinationFolder, ".backup_metadata");
            if (!Directory.Exists(stateDir))
            {
                Directory.CreateDirectory(stateDir);
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = stateDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    public void OpenProcessModal()
    {
        IsProcessModalOpen = true;
    }

    [RelayCommand]
    public void CloseProcessModal()
    {
        IsProcessModalOpen = false;
    }

    [RelayCommand]
    public void ClearLogs()
    {
        lock (_logLock)
        {
            ProcessLogs.Clear();
            ActivityLogs.Clear();
        }
        Log("Diagnostic logs cleared.", LogLevel.Info, "SYSTEM");
    }

    [RelayCommand]
    public void CopyLogs()
    {
        try
        {
            string text;
            lock (_logLock)
            {
                text = string.Join(Environment.NewLine, ProcessLogs.Select(l => l.FullText));
            }
            Clipboard.SetText(text);
            Log("Logs copied to clipboard.", LogLevel.Success, "SYSTEM");
        }
        catch { }
    }

    public void Log(string message, LogLevel level = LogLevel.Info, string category = "SYSTEM")
    {
        var entry = new LogEntry
        {
            Level = level,
            Category = category,
            Message = message
        };

        void Append()
        {
            lock (_logLock)
            {
                ProcessLogs.Add(entry);
                ActivityLogs.Insert(0, entry.ToString());
                if (ActivityLogs.Count > 300) ActivityLogs.RemoveAt(ActivityLogs.Count - 1);
                if (ProcessLogs.Count > 1500) ProcessLogs.RemoveAt(0);
            }
        }

        if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.InvokeAsync(Append);
        }
        else
        {
            Append();
        }
    }

    #region Gallery & Lightbox Methods
    partial void OnGalleryFilterChanged(string value) => ApplyGalleryFilter();
    partial void OnGallerySearchTextChanged(string value) => ApplyGalleryFilter();

    [RelayCommand]
    public async Task SwitchViewModeAsync(string mode)
    {
        bool targetGallery = mode.Equals("Gallery", StringComparison.OrdinalIgnoreCase);
        IsGalleryView = targetGallery;
        Log($"Switched view mode to: {(IsGalleryView ? "Gallery" : "Tree View")}", LogLevel.Info, "UI");

        if (IsGalleryView && GalleryItems.Count == 0)
        {
            await RefreshGalleryAsync();
        }
    }

    [RelayCommand]
    public async Task RefreshGalleryAsync()
    {
        if (IsLoadingGallery) return;
        IsLoadingGallery = true;
        Log("Scanning backup destination folder for media gallery...", LogLevel.Info, "GALLERY");

        try
        {
            _galleryThumbnailCts?.Cancel();
            _galleryThumbnailCts = new CancellationTokenSource();
            var ct = _galleryThumbnailCts.Token;

            string targetDir = DestinationFolder;
            if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
            {
                Log($"Destination folder not accessible: {targetDir}", LogLevel.Warning, "GALLERY");
                IsLoadingGallery = false;
                return;
            }

            string dcimDir = Path.Combine(targetDir, "DCIM");
            string scanDir = Directory.Exists(dcimDir) ? dcimDir : targetDir;

            var items = await Task.Run(() => ScanDirectoryForGallery(scanDir, ct), ct);

            GalleryItems.Clear();
            foreach (var item in items)
            {
                GalleryItems.Add(item);
            }

            GalleryTotalPhotos = GalleryItems.Count(i => !i.IsVideo && !i.IsLivePhoto);
            GalleryTotalLivePhotos = GalleryItems.Count(i => i.IsLivePhoto);
            GalleryTotalVideos = GalleryItems.Count(i => i.IsVideo);

            ApplyGalleryFilter();
            Log($"Gallery loaded {GalleryItems.Count} items ({GalleryTotalPhotos} photos, {GalleryTotalLivePhotos} live photos, {GalleryTotalVideos} videos).", LogLevel.Success, "GALLERY");

            StartLazyThumbnailLoading();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log($"Failed to load gallery: {ex.Message}", LogLevel.Error, "GALLERY");
        }
        finally
        {
            IsLoadingGallery = false;
        }
    }

    private static List<GalleryItemViewModel> ScanDirectoryForGallery(string scanRoot, CancellationToken ct)
    {
        var result = new List<GalleryItemViewModel>();
        if (!Directory.Exists(scanRoot)) return result;

        var supportedExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".heic", ".heif", ".jpg", ".jpeg", ".png", ".mov", ".mp4", ".m4v"
        };

        var allFiles = new DirectoryInfo(scanRoot)
            .EnumerateFiles("*.*", SearchOption.AllDirectories)
            .Where(f => supportedExts.Contains(f.Extension))
            .ToList();

        // Group files by directory + name without extension to detect Apple Live Photo pairs
        var dirGroups = allFiles.GroupBy(f => f.DirectoryName ?? string.Empty);

        foreach (var dirGroup in dirGroups)
        {
            ct.ThrowIfCancellationRequested();

            var baseGroups = dirGroup.GroupBy(f => Path.GetFileNameWithoutExtension(f.Name), StringComparer.OrdinalIgnoreCase);

            foreach (var baseGroup in baseGroups)
            {
                var files = baseGroup.ToList();
                var stillFile = files.FirstOrDefault(f => f.Extension.Equals(".heic", StringComparison.OrdinalIgnoreCase)
                                                       || f.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                                                       || f.Extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                                                       || f.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase));

                var movFile = files.FirstOrDefault(f => f.Extension.Equals(".mov", StringComparison.OrdinalIgnoreCase)
                                                     || f.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase));

                // 1. Paired Apple Live Photo (Still + MOV companion)
                if (stillFile != null && movFile != null)
                {
                    bool isHeic = stillFile.Extension.Equals(".heic", StringComparison.OrdinalIgnoreCase)
                               || stillFile.Extension.Equals(".heif", StringComparison.OrdinalIgnoreCase);

                    result.Add(new GalleryItemViewModel
                    {
                        FilePath = stillFile.FullName,
                        FileName = stillFile.Name,
                        RelativePath = Path.GetRelativePath(scanRoot, stillFile.FullName),
                        Folder = stillFile.Directory?.Name ?? string.Empty,
                        FileSize = stillFile.Length + movFile.Length,
                        DateModified = stillFile.LastWriteTime,
                        MediaType = GalleryMediaType.LivePhoto,
                        IsLivePhoto = true,
                        IsHeic = isHeic,
                        CompanionVideoPath = movFile.FullName,
                        DurationFormatted = "0:03"
                    });
                }
                // 2. Standalone Still Photo
                else if (stillFile != null)
                {
                    bool isHeic = stillFile.Extension.Equals(".heic", StringComparison.OrdinalIgnoreCase)
                               || stillFile.Extension.Equals(".heif", StringComparison.OrdinalIgnoreCase);

                    result.Add(new GalleryItemViewModel
                    {
                        FilePath = stillFile.FullName,
                        FileName = stillFile.Name,
                        RelativePath = Path.GetRelativePath(scanRoot, stillFile.FullName),
                        Folder = stillFile.Directory?.Name ?? string.Empty,
                        FileSize = stillFile.Length,
                        DateModified = stillFile.LastWriteTime,
                        MediaType = GalleryMediaType.Photo,
                        IsLivePhoto = false,
                        IsHeic = isHeic
                    });
                }
                // 3. Standalone Video (.MOV, .MP4)
                else if (movFile != null)
                {
                    result.Add(new GalleryItemViewModel
                    {
                        FilePath = movFile.FullName,
                        FileName = movFile.Name,
                        RelativePath = Path.GetRelativePath(scanRoot, movFile.FullName),
                        Folder = movFile.Directory?.Name ?? string.Empty,
                        FileSize = movFile.Length,
                        DateModified = movFile.LastWriteTime,
                        MediaType = GalleryMediaType.Video,
                        IsVideo = true,
                        IsLivePhoto = false
                    });
                }
            }
        }

        // Sort descending by modified date (latest photos first, just like iPhone)
        return result.OrderByDescending(r => r.DateModified).ToList();
    }

    public void ApplyGalleryFilter()
    {
        var query = GalleryItems.AsEnumerable();

        // 1. Media Type Filter
        query = GalleryFilter switch
        {
            "Photos" => query.Where(i => i.MediaType is GalleryMediaType.Photo or GalleryMediaType.LivePhoto),
            "Videos" => query.Where(i => i.MediaType is GalleryMediaType.Video or GalleryMediaType.LivePhoto),
            "LivePhotos" => query.Where(i => i.MediaType == GalleryMediaType.LivePhoto),
            _ => query
        };

        // 2. Search Text
        if (!string.IsNullOrWhiteSpace(GallerySearchText))
        {
            string term = GallerySearchText.Trim();
            query = query.Where(i => i.FileName.Contains(term, StringComparison.OrdinalIgnoreCase)
                                  || i.Folder.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        FilteredGalleryItems.Clear();
        foreach (var item in query)
        {
            FilteredGalleryItems.Add(item);
        }
    }

    private void StartLazyThumbnailLoading()
    {
        var ct = _galleryThumbnailCts?.Token ?? CancellationToken.None;
        var snapshot = FilteredGalleryItems.ToList();

        Task.Run(async () =>
        {
            foreach (var item in snapshot)
            {
                if (ct.IsCancellationRequested) break;
                if (item.Thumbnail != null) continue;

                item.IsThumbnailLoading = true;
                try
                {
                    var thumb = await _thumbnailService.GetThumbnailAsync(item.FilePath, targetSize: 280, ct);
                    if (thumb != null)
                    {
                        if (Application.Current?.Dispatcher != null)
                        {
                            Application.Current.Dispatcher.Invoke(() => item.Thumbnail = thumb);
                        }
                        else
                        {
                            item.Thumbnail = thumb;
                        }
                    }
                }
                catch { }
                finally
                {
                    item.IsThumbnailLoading = false;
                }
            }
        }, ct);
    }

    [RelayCommand]
    public async Task OpenGalleryItemAsync(GalleryItemViewModel? item)
    {
        if (item == null) return;

        SelectedGalleryItem = item;
        IsLightboxOpen = true;
        IsLightboxLivePhotoPlaying = false;

        int idx = FilteredGalleryItems.IndexOf(item);
        SelectedGalleryIndex = idx >= 0 ? idx : 0;
        LightboxCounter = $"{SelectedGalleryIndex + 1} of {FilteredGalleryItems.Count}";

        if (item.IsVideo)
        {
            IsLightboxVideo = true;
            ActiveVideoUri = item.FilePath;
            LightboxImage = item.Thumbnail;
        }
        else
        {
            IsLightboxVideo = false;
            ActiveVideoUri = null;
            LightboxImage = item.Thumbnail; // Instant responsive preview
            IsLightboxLoadingImage = true;

            try
            {
                var fullBmp = await _thumbnailService.GetFullImageAsync(item.FilePath);
                if (SelectedGalleryItem == item && fullBmp != null)
                {
                    LightboxImage = fullBmp;
                }
            }
            finally
            {
                IsLightboxLoadingImage = false;
            }
        }
    }

    [RelayCommand]
    public void CloseLightbox()
    {
        IsLightboxOpen = false;
        IsLightboxVideo = false;
        IsLightboxLivePhotoPlaying = false;
        ActiveVideoUri = null;
        LightboxImage = null;
        SelectedGalleryItem = null;
    }

    [RelayCommand]
    public async Task NextGalleryItemAsync()
    {
        if (FilteredGalleryItems.Count == 0) return;
        int next = (SelectedGalleryIndex + 1) % FilteredGalleryItems.Count;
        await OpenGalleryItemAsync(FilteredGalleryItems[next]);
    }

    [RelayCommand]
    public async Task PreviousGalleryItemAsync()
    {
        if (FilteredGalleryItems.Count == 0) return;
        int prev = (SelectedGalleryIndex - 1 + FilteredGalleryItems.Count) % FilteredGalleryItems.Count;
        await OpenGalleryItemAsync(FilteredGalleryItems[prev]);
    }

    [RelayCommand]
    public void PlayLivePhoto()
    {
        if (SelectedGalleryItem == null || !SelectedGalleryItem.IsLivePhoto) return;
        if (string.IsNullOrEmpty(SelectedGalleryItem.CompanionVideoPath) || !File.Exists(SelectedGalleryItem.CompanionVideoPath)) return;

        if (!IsLightboxLivePhotoPlaying)
        {
            IsLightboxLivePhotoPlaying = true;
            ActiveVideoUri = SelectedGalleryItem.CompanionVideoPath;
            IsLightboxVideo = true;
        }
        else
        {
            StopLivePhotoVideo();
        }
    }

    public void StopLivePhotoVideo()
    {
        IsLightboxLivePhotoPlaying = false;
        IsLightboxVideo = false;
        ActiveVideoUri = null;
    }

    [RelayCommand]
    public void OpenSelectedInExplorer()
    {
        if (SelectedGalleryItem == null || !File.Exists(SelectedGalleryItem.FilePath)) return;
        try
        {
            Process.Start("explorer.exe", $"/select,\"{SelectedGalleryItem.FilePath}\"");
        }
        catch { }
    }

    [RelayCommand]
    public async Task PreviewTreeItemAsync(FileDiffNodeViewModel? node)
    {
        if (node == null || node.IsDirectory) return;

        string targetPath = !string.IsNullOrEmpty(node.FullPathDest) && File.Exists(node.FullPathDest)
            ? node.FullPathDest
            : string.Empty;

        if (string.IsNullOrEmpty(targetPath))
        {
            Log($"Cannot preview '{node.Name}': File has not been backed up to laptop disk yet.", LogLevel.Warning, "PREVIEW");
            return;
        }

        // Find or synthesize gallery item
        var existing = GalleryItems.FirstOrDefault(g => g.FilePath.Equals(targetPath, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            await OpenGalleryItemAsync(existing);
            return;
        }

        string ext = Path.GetExtension(targetPath).ToLowerInvariant();
        bool isVid = ext is ".mov" or ".mp4" or ".m4v";
        bool isHeic = ext is ".heic" or ".heif";

        var synth = new GalleryItemViewModel
        {
            FilePath = targetPath,
            FileName = node.Name,
            RelativePath = node.RelativePath,
            FileSize = node.Size,
            DateModified = File.GetLastWriteTime(targetPath),
            IsVideo = isVid,
            IsHeic = isHeic,
            MediaType = isVid ? GalleryMediaType.Video : (node.IsLivePhotoStill ? GalleryMediaType.LivePhoto : GalleryMediaType.Photo),
            IsLivePhoto = node.IsLivePhotoStill
        };

        await OpenGalleryItemAsync(synth);
    }
    #endregion
}

