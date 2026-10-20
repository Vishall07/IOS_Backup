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
using IosBackupApp.Services.Interfaces;

namespace IosBackupApp.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IDeviceService _deviceService;
    private readonly ILocalStorageService _localStorageService;
    private readonly IDiffEngine _diffEngine;
    private readonly ITransferEngine _transferEngine;

    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _transferCts;

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
    private string _iphoneStateFilePath = string.Empty;

    [ObservableProperty]
    private string _laptopStateFilePath = string.Empty;

    [ObservableProperty]
    private ObservableCollection<string> _activityLogs = new();

    private FileDiffNodeViewModel? _rootDiffNode;

    public string SelectedSizeFormatted => StorageMetrics.FormatBytes(SelectedFilesBytes);

    public MainViewModel(
        IDeviceService deviceService,
        ILocalStorageService localStorageService,
        IDiffEngine diffEngine,
        ITransferEngine transferEngine)
    {
        _deviceService = deviceService;
        _localStorageService = localStorageService;
        _diffEngine = diffEngine;
        _transferEngine = transferEngine;

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
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        var scanProgress = new Progress<string>(msg =>
        {
            StatusMessage = msg;
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
    public async Task StartBackupAsync()
    {
        if (_rootDiffNode == null)
        {
            MessageBox.Show("Please scan the iPhone first.", "No Files Scanned", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var filesToTransfer = _rootDiffNode.GetSelectedFiles().ToList();
        if (filesToTransfer.Count == 0)
        {
            MessageBox.Show("No files are selected for backup. Please check at least one file.", "Nothing Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (SelectedDevice == null)
        {
            MessageBox.Show("Selected device is no longer connected.", "Device Disconnected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsTransferring = true;
        IsPaused = false;
        _transferCts = new CancellationTokenSource();

        Log($"Initiating backup of {filesToTransfer.Count} files ({StorageMetrics.FormatBytes(filesToTransfer.Sum(f => f.Size))}) with {Concurrency} concurrent stream(s)...");

        var progress = new Progress<TransferProgressInfo>(info =>
        {
            ProgressInfo = info;
            StatusMessage = info.StatusMessage;
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
                            Log($"Backed up: {file.Name} ({file.FormattedSize})");
                        }
                        else
                        {
                            Log($"Failed to copy {file.Name}: {err}");
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
            file.IsChecked = file.Status == FileDiffStatus.PendingBackup;
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

    private void ApplyFilter()
    {
        if (_rootDiffNode == null) return;
        ApplyFilterRecursive(_rootDiffNode, SearchText, FilterMode);
        UpdateTreeDisplay();
    }

    private static bool ApplyFilterRecursive(FileDiffNodeViewModel node, string search, string filterMode)
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

            node.IsVisible = matchesSearch && matchesStatus;
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

    private void Log(string message)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        string entry = $"[{timestamp}] {message}";
        ActivityLogs.Insert(0, entry);
        if (ActivityLogs.Count > 150)
        {
            ActivityLogs.RemoveAt(ActivityLogs.Count - 1);
        }
    }
}
