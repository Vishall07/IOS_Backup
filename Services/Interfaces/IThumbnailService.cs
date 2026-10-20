using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace IosBackupApp.Services.Interfaces;

public interface IThumbnailService
{
    Task<BitmapSource?> GetThumbnailAsync(string filePath, int targetSize = 280, CancellationToken ct = default);
    Task<BitmapSource?> GetFullImageAsync(string filePath, int maxWidth = 2560, int maxHeight = 1600, CancellationToken ct = default);
    void ClearMemoryCache();
}
