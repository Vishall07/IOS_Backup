using IosBackupApp.Models;

namespace IosBackupApp.Services.Interfaces;

public class DiffResult
{
    public FileDiffNodeViewModel RootNode { get; set; } = new();
    public int TotalFilesCount { get; set; }
    public int PendingFilesCount { get; set; }
    public int BackedUpFilesCount { get; set; }
    public long TotalBytes { get; set; }
    public long PendingBytes { get; set; }
    public long BackedUpBytes { get; set; }
}

public interface IDiffEngine
{
    DiffResult Compare(DeviceState iphoneState, DeviceState? laptopState, string destinationDirectory);
}
