using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ImageMagick;
using IosBackupApp.Services.Interfaces;

namespace IosBackupApp.Services;

public class ThumbnailService : IThumbnailService
{
    private readonly ConcurrentDictionary<string, BitmapSource> _memoryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _throttle = new(Math.Max(2, Environment.ProcessorCount / 2));
    private readonly string _diskCacheDir;

    public ThumbnailService()
    {
        _diskCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IosBackupApp",
            "Thumbnails");

        try
        {
            if (!Directory.Exists(_diskCacheDir))
            {
                Directory.CreateDirectory(_diskCacheDir);
            }
        }
        catch { }
    }

    public void ClearMemoryCache()
    {
        _memoryCache.Clear();
    }

    public async Task<BitmapSource?> GetThumbnailAsync(string filePath, int targetSize = 280, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        string cacheKey = $"{filePath}_{targetSize}";
        if (_memoryCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        await _throttle.WaitAsync(ct);
        try
        {
            if (_memoryCache.TryGetValue(cacheKey, out cached))
            {
                return cached;
            }

            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var bmp = LoadOrGenerateThumbnail(filePath, targetSize);
                if (bmp != null)
                {
                    _memoryCache[cacheKey] = bmp;
                }
                return bmp;
            }, ct);
        }
        finally
        {
            _throttle.Release();
        }
    }

    public async Task<BitmapSource?> GetFullImageAsync(string filePath, int maxWidth = 2560, int maxHeight = 1600, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        string fullKey = $"FULL_{filePath}_{maxWidth}x{maxHeight}";
        if (_memoryCache.TryGetValue(fullKey, out var cached))
        {
            return cached;
        }

        await _throttle.WaitAsync(ct);
        try
        {
            if (_memoryCache.TryGetValue(fullKey, out cached))
            {
                return cached;
            }

            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var bmp = LoadFullResolutionImage(filePath, maxWidth, maxHeight);
                if (bmp != null)
                {
                    _memoryCache[fullKey] = bmp;
                }
                return bmp;
            }, ct);
        }
        finally
        {
            _throttle.Release();
        }
    }

    private static BitmapImage? LoadWicThumbnail(string filePath, int targetSize)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(filePath, UriKind.Absolute);
            bmp.DecodePixelWidth = targetSize;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private BitmapSource? LoadOrGenerateThumbnail(string filePath, int targetSize)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        string diskCacheFile = GetDiskCachePath(filePath, targetSize);

        // 1. Check if thumbnail already cached on disk
        if (File.Exists(diskCacheFile))
        {
            try
            {
                var fi = new FileInfo(diskCacheFile);
                if (fi.Length > 0)
                {
                    var cachedBmp = LoadFrozenBitmapFromFile(diskCacheFile);
                    if (cachedBmp != null) return cachedBmp;
                }
                File.Delete(diskCacheFile);
            }
            catch
            {
                // Corrupted cache file; delete and re-generate
                try { File.Delete(diskCacheFile); } catch { }
            }
        }

        // 2. Videos (.MOV, .MP4, .AVI)
        if (ext is ".mov" or ".mp4" or ".m4v" or ".avi")
        {
            // Check for companion Live Photo still image first
            string dir = Path.GetDirectoryName(filePath) ?? string.Empty;
            string baseName = Path.GetFileNameWithoutExtension(filePath);
            string companionHeic = Path.Combine(dir, $"{baseName}.HEIC");
            string companionJpg = Path.Combine(dir, $"{baseName}.JPG");
            string companionJpeg = Path.Combine(dir, $"{baseName}.JPEG");

            if (File.Exists(companionHeic)) return LoadOrGenerateThumbnail(companionHeic, targetSize);
            if (File.Exists(companionJpg)) return LoadOrGenerateThumbnail(companionJpg, targetSize);
            if (File.Exists(companionJpeg)) return LoadOrGenerateThumbnail(companionJpeg, targetSize);

            // Standalone video: extract shell thumbnail
            var shellBmp = ExtractShellThumbnail(filePath, targetSize);
            if (shellBmp != null)
            {
                SaveBitmapToDisk(shellBmp, diskCacheFile);
                return shellBmp;
            }

            // Fallback: try MagickImage frame 0
            try
            {
                using var image = new MagickImage(filePath + "[0]");
                image.AutoOrient();
                image.Thumbnail(new MagickGeometry((uint)targetSize, (uint)targetSize) { IgnoreAspectRatio = false });
                using var ms = new MemoryStream();
                image.Write(ms, MagickFormat.Jpeg);
                ms.Position = 0;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                SaveBitmapToDisk(bmp, diskCacheFile);
                return bmp;
            }
            catch { }

            return null;
        }

        // 3. Standard Photos (.JPG, .JPEG, .PNG, .BMP) - Native WIC first
        if (ext is ".jpg" or ".jpeg" or ".png" or ".bmp")
        {
            var wicBmp = LoadWicThumbnail(filePath, targetSize);
            if (wicBmp != null)
            {
                SaveBitmapToDisk(wicBmp, diskCacheFile);
                return wicBmp;
            }
        }

        // 4. Apple HEIC / HEIF / RAW photos (or when WIC failed) - MagickImage
        try
        {
            using var image = new MagickImage(filePath);
            image.AutoOrient();
            image.Thumbnail(new MagickGeometry((uint)targetSize, (uint)targetSize) { IgnoreAspectRatio = false });

            using var ms = new MemoryStream();
            image.Quality = 85;
            image.Write(ms, MagickFormat.Jpeg);
            ms.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();

            try { File.WriteAllBytes(diskCacheFile, ms.ToArray()); } catch { }
            return bmp;
        }
        catch { }

        // 5. Shell Thumbnail fallback (uses Windows HEIF / Photos codec if installed)
        var shellFallback = ExtractShellThumbnail(filePath, targetSize);
        if (shellFallback != null)
        {
            SaveBitmapToDisk(shellFallback, diskCacheFile);
            return shellFallback;
        }

        // 6. Generic WIC fallback
        var genericWic = LoadWicThumbnail(filePath, targetSize);
        if (genericWic != null)
        {
            SaveBitmapToDisk(genericWic, diskCacheFile);
            return genericWic;
        }

        return null;
    }

    private BitmapSource? LoadFullResolutionImage(string filePath, int maxWidth, int maxHeight)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();

        // 1. Standard JPG/PNG: use native WIC with DecodePixelWidth
        if (ext is ".jpg" or ".jpeg" or ".png")
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(filePath, UriKind.Absolute);
                bmp.DecodePixelWidth = maxWidth;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { }
        }

        // 2. MagickImage for HEIC, TIFF, and other formats
        try
        {
            using var image = new MagickImage(filePath);
            image.AutoOrient();

            if (image.Width > maxWidth || image.Height > maxHeight)
            {
                image.Resize(new MagickGeometry((uint)maxWidth, (uint)maxHeight) { IgnoreAspectRatio = false });
            }

            image.Quality = 90;
            using var ms = new MemoryStream();
            image.Write(ms, MagickFormat.Jpeg); // Writing as JPEG instead of huge uncompressed BMP!
            ms.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { }

        // 3. Windows Shell High-Res Extraction
        try
        {
            var shellBmp = ExtractShellThumbnail(filePath, Math.Min(maxWidth, 1920));
            if (shellBmp != null) return shellBmp;
        }
        catch { }

        // 4. Generic WIC Fallback
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(filePath, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { }

        return null;
    }

    private BitmapSource? ExtractShellThumbnail(string filePath, int targetSize)
    {
        try
        {
            Guid factoryGuid = new("bcc18b79-ba16-442f-80c4-8a59c07c4e45");
            int hr = SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref factoryGuid, out object obj);
            if (hr == 0 && obj is IShellItemImageFactory factory)
            {
                // Try 0x00 (RESIZETOFIT)
                hr = factory.GetImage(new POINT { X = targetSize, Y = targetSize }, 0x0, out IntPtr hBitmap);
                // If not successful, try 0x01 (BIGGERSIZEOK)
                if (hr != 0 || hBitmap == IntPtr.Zero)
                {
                    hr = factory.GetImage(new POINT { X = targetSize, Y = targetSize }, 0x01, out hBitmap);
                }

                if (hr == 0 && hBitmap != IntPtr.Zero)
                {
                    try
                    {
                        var bs = Imaging.CreateBitmapSourceFromHBitmap(
                            hBitmap,
                            IntPtr.Zero,
                            Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        bs.Freeze();
                        return bs;
                    }
                    finally
                    {
                        DeleteObject(hBitmap);
                    }
                }
            }
        }
        catch { }

        return null;
    }

    private string GetDiskCachePath(string filePath, int targetSize)
    {
        try
        {
            var fi = new FileInfo(filePath);
            string raw = $"{filePath}_{fi.Length}_{fi.LastWriteTimeUtc.Ticks}_{targetSize}";
            byte[] hashBytes = MD5.HashData(Encoding.UTF8.GetBytes(raw));
            string hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
            return Path.Combine(_diskCacheDir, $"{hash}.jpg");
        }
        catch
        {
            byte[] hashBytes = MD5.HashData(Encoding.UTF8.GetBytes(filePath));
            return Path.Combine(_diskCacheDir, $"{Convert.ToHexString(hashBytes).ToLowerInvariant()}.jpg");
        }
    }

    private static BitmapImage? LoadFrozenBitmapFromFile(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveBitmapToDisk(BitmapSource bitmap, string targetPath)
    {
        try
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
            encoder.Save(fs);
        }
        catch { }
    }

    #region Win32 Shell Thumbnail Interop
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c07c4e45")]
    public interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage([In] POINT size, [In] int flags, [Out] out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [In] ref Guid riid,
        [Out, MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);
    #endregion
}
