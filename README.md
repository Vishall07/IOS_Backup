# iPhone Media Backup WPF Application

A wired iPhone backup utility built with **WPF (.NET 8.0)**, **CommunityToolkit.Mvvm**, and the **Windows Portable Devices (WPD / MTP)** API via `MediaDevices`.

---

## 🚀 Key Features

- **WPD / MTP Protocol Integration**: Directly accesses iPhone Photos and Videos inside the `DCIM` directory over a USB Lightning / USB-C connection without requiring iTunes or third-party background services.
- **Dual Storage Metrics Dashboard**: Real-time visualization of storage utilization on both the iPhone (used vs. total capacity, battery level) and the target Windows destination drive.
- **Unified JSON Schema**: Both iPhone and laptop directory states are scanned and serialized to standardized JSON documents (`iphone_state.json` and `laptop_state.json`).
- **Smart Diff Engine**: Automatically detects missing or modified media files by comparing relative file paths and file sizes. Pending backups are pre-selected for one-click synchronization.
- **Cascading Checkbox TreeView**: Fully virtualized hierarchical `TreeView` with tri-state parent-child cascading selection (`VirtualizingStackPanel.IsVirtualizing="True"`), capable of handling 10,000+ photos without UI latency.
- **Configurable Parallel Transfer Engine**: Powered by `System.Threading.Tasks.Dataflow` (`ActionBlock<T>`) supporting 1 to N concurrent streams (defaulting to 1 stream to prevent USB 2.0 Lightning MTP pipe contention).
- **Real-Time Speed & ETA Estimation**: Tracks a 5-second moving average byte transfer rate to provide dynamic, stable Estimated Time Remaining (ETA) calculations.
- **Pause, Resume & Cancel**: Fully interruptible transfer loop with safe cancellation and pause gating.
- **Atomic State Persistence**: Incrementally updates `laptop_state.json` as each file successfully writes to disk, ensuring fault tolerance against cable disconnects or crashes.
- **Demo / Simulation Mode**: Built-in simulated iPhone 15 Pro mode allows full testing and demonstration of scans, diffs, tree management, and transfers without a physical device connected.

---

## 🛠️ Architecture & Tech Stack

| Component | Technology | Purpose |
|---|---|---|
| **UI Framework** | WPF (.NET 8.0) | High-performance desktop UI with hardware acceleration |
| **State Management**| `CommunityToolkit.Mvvm` (8.4.2) | Boilerplate-free MVVM (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`) |
| **iOS Integration** | `MediaDevices` (2.0.0) / WPD API | Native Windows Portable Devices communication over USB |
| **JSON Serialization**| `System.Text.Json` | High-throughput serialization of directory trees |
| **Concurrency** | `System.Threading.Tasks.Dataflow` | Parallel file transfer pipeline with configurable concurrency |
| **Testing** | `xUnit` (2.9.3) | Unit and integration test coverage |

---

## 📋 Unified JSON Schema

```json
{
  "DeviceName": "iPhone 15 Pro",
  "ScanDate": "2026-10-20T13:04:00Z",
  "Root": {
    "Name": "DCIM",
    "IsDirectory": true,
    "Size": 3500000,
    "Children": [
      {
        "Name": "100APPLE",
        "IsDirectory": true,
        "Children": [
          {
            "Name": "IMG_0001.HEIC",
            "IsDirectory": false,
            "Size": 3500000,
            "Hash": "optional_md5"
          }
        ]
      }
    ]
  }
}
```

---

## ⚙️ Build and Run Instructions

### Prerequisites
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or higher
- Windows 10/11 (64-bit)

### Build Application
```powershell
dotnet build
```

### Run Tests
```powershell
dotnet test Tests\IosBackupApp.Tests.csproj
```

### Launch Application
```powershell
dotnet run --project IosBackupApp.csproj
```

---

## 💡 Usage Guide

1. **Connect iPhone via USB**:
   - Unlock your iPhone and tap **"Trust This Computer"** when prompted.
   - The application will automatically detect the iPhone, display its device name, storage capacity, and battery percentage.
   - If no physical iPhone is available, select **"[Demo Mode] iPhone 15 Pro"** from the device dropdown.
2. **Choose Destination Folder**:
   - Defaults to `Pictures\iPhone_Backup`. Click **Browse...** to pick any destination folder on your laptop.
3. **Scan Media**:
   - Click **"Scan iPhone Media"**. The app recursively traverses `\Internal Storage\DCIM` and compares it against your backup destination.
   - Both `iphone_state.json` and `laptop_state.json` are generated and saved in `.backup_metadata/`.
4. **Review Differences**:
   - Use the Checkbox TreeView to inspect pending vs. backed up media.
   - Use the quick action buttons: **Select Pending**, **Select All**, **Deselect All**, or filter by search text (`.heic`, `.mov`).
5. **Start Backup**:
   - Adjust the **Concurrency Slider** (1 stream is recommended for Lightning / USB 2.0 to avoid I/O bottlenecks).
   - Click **Start Backup**.
   - Monitor real-time transfer speed (MB/s), Estimated Time Remaining (ETA), and dual progress bars.
   - Use **Pause**, **Resume**, or **Cancel** at any time.

---

## ⚠️ Critical Technical Considerations

- **MTP/PTP File Access**: Windows natively treats iPhones as digital still cameras via the PTP/MTP protocol. The app accesses `Internal Storage\DCIM` directly without needing Apple Mobile Device Support (iTunes background services).
- **HEIC / HEVC Formats**: Media files are transferred in raw fidelity with original creation and modification timestamps preserved. Windows requires the *HEVC Video Extensions* and *HEIF Image Extensions* from the Microsoft Store for native thumbnail previews in Windows Explorer.
- **UI Responsiveness**: Directory traversal, diffing, and file streaming are executed on background threads via `Task.Run` and `ActionBlock<T>`, reporting updates back to the UI thread using `IProgress<T>`.
