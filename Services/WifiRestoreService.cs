using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IosBackupApp.Models;

namespace IosBackupApp.Services;

public class WifiRestoreService : IDisposable
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private List<GalleryItemViewModel> _stagedItems = new();
    private int _port = 5055;
    private string _localIp = "127.0.0.1";

    public bool IsRunning => _listener?.IsListening ?? false;
    public string LocalIp => _localIp;
    public int Port => _port;
    public string RestoreUrl => $"http://{_localIp}:{_port}/";

    public event Action<string>? LogMessage;
    public event Action<string>? ItemDownloaded;

    public WifiRestoreService()
    {
        _localIp = GetLocalIpAddress();
    }

    public static string GetLocalIpAddress()
    {
        try
        {
            var wifiOrEthernet = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .OrderByDescending(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 2 : 1);

            foreach (var ni in wifiOrEthernet)
            {
                var props = ni.GetIPProperties();
                var ipv4 = props.UnicastAddresses
                    .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork &&
                                         !IPAddress.IsLoopback(u.Address) &&
                                         !u.Address.ToString().StartsWith("169.254."));
                if (ipv4 != null)
                {
                    return ipv4.Address.ToString();
                }
            }
        }
        catch { }

        return "127.0.0.1";
    }

    public void Start(IEnumerable<GalleryItemViewModel> items, int requestedPort = 5055)
    {
        Stop();

        _stagedItems = items.ToList();
        _localIp = GetLocalIpAddress();
        _port = requestedPort;
        _cts = new CancellationTokenSource();

        int attempts = 0;
        while (attempts < 5)
        {
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://*:{_port}/");
                _listener.Start();
                break;
            }
            catch
            {
                try
                {
                    _listener = new HttpListener();
                    _listener.Prefixes.Add($"http://+:{_port}/");
                    _listener.Start();
                    break;
                }
                catch
                {
                    try
                    {
                        _listener = new HttpListener();
                        _listener.Prefixes.Add($"http://localhost:{_port}/");
                        _listener.Prefixes.Add($"http://{_localIp}:{_port}/");
                        _listener.Start();
                        break;
                    }
                    catch
                    {
                        _port++;
                        attempts++;
                    }
                }
            }
        }

        if (_listener == null || !_listener.IsListening)
        {
            throw new InvalidOperationException($"Could not start local Wi-Fi restore server on ports 5055-5060.");
        }

        LogMessage?.Invoke($"[WI-FI RESTORE] Server started at {RestoreUrl} serving {_stagedItems.Count} items.");
        Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try
        {
            if (_listener?.IsListening == true)
            {
                _listener.Stop();
                _listener.Close();
            }
        }
        catch { }
        _listener = null;
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener?.IsListening == true)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(context));
            }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        var req = context.Request;
        var resp = context.Response;

        try
        {
            string path = req.Url?.AbsolutePath.TrimStart('/') ?? string.Empty;

            if (string.IsNullOrEmpty(path) || path.Equals("restore", StringComparison.OrdinalIgnoreCase))
            {
                await ServeHtmlIndexAsync(resp);
            }
            else if (path.StartsWith("file/", StringComparison.OrdinalIgnoreCase))
            {
                string indexStr = path["file/".Length..];
                if (int.TryParse(indexStr, out int idx) && idx >= 0 && idx < _stagedItems.Count)
                {
                    await ServeFileAsync(_stagedItems[idx], resp);
                }
                else
                {
                    resp.StatusCode = (int)HttpStatusCode.NotFound;
                    resp.Close();
                }
            }
            else if (path.Equals("zip", StringComparison.OrdinalIgnoreCase))
            {
                await ServeZipArchiveAsync(resp);
            }
            else
            {
                resp.StatusCode = (int)HttpStatusCode.NotFound;
                resp.Close();
            }
        }
        catch
        {
            try { resp.StatusCode = (int)HttpStatusCode.InternalServerError; resp.Close(); } catch { }
        }
    }

    private async Task ServeHtmlIndexAsync(HttpListenerResponse resp)
    {
        resp.ContentType = "text/html; charset=utf-8";
        resp.StatusCode = (int)HttpStatusCode.OK;

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"UTF-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0, user-scalable=no\">");
        sb.AppendLine("<title>iPhone Restore Assistant</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("* { box-sizing: border-box; -webkit-tap-highlight-color: transparent; }");
        sb.AppendLine("body { font-family: -apple-system, BlinkMacSystemFont, 'SF Pro Display', Roboto, sans-serif; background: #0A0E17; color: #F8FAFC; margin: 0; padding: 20px; text-align: center; }");
        sb.AppendLine(".card { background: #1E293B; border: 1px solid #334155; border-radius: 16px; padding: 20px; max-width: 600px; margin: 0 auto 20px auto; text-align: left; }");
        sb.AppendLine(".title { font-size: 22px; font-weight: 700; margin-bottom: 6px; color: #38BDF8; display: flex; align-items: center; gap: 8px; }");
        sb.AppendLine(".subtitle { font-size: 13px; color: #94A3B8; margin-bottom: 16px; }");
        sb.AppendLine(".btn { display: inline-block; background: #0284C7; color: white; text-decoration: none; padding: 14px 24px; border-radius: 12px; font-weight: bold; font-size: 16px; text-align: center; border: none; cursor: pointer; width: 100%; margin-bottom: 12px; }");
        sb.AppendLine(".btn:active { background: #0369A1; }");
        sb.AppendLine(".btn-secondary { background: #334155; color: #F8FAFC; }");
        sb.AppendLine(".item-list { list-style: none; padding: 0; margin: 12px 0; max-height: 400px; overflow-y: auto; }");
        sb.AppendLine(".item-row { display: flex; align-items: center; justify-content: space-between; padding: 12px; border-bottom: 1px solid #2D3748; }");
        sb.AppendLine(".item-info { display: flex; flex-direction: column; text-align: left; overflow: hidden; }");
        sb.AppendLine(".item-name { font-weight: 600; font-size: 14px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; max-width: 280px; }");
        sb.AppendLine(".item-size { font-size: 12px; color: #94A3B8; margin-top: 2px; }");
        sb.AppendLine(".dl-link { background: #0F172A; border: 1px solid #38BDF8; color: #38BDF8; padding: 6px 12px; border-radius: 8px; text-decoration: none; font-size: 12px; font-weight: 600; }");
        sb.AppendLine(".tip { background: #0F172A; border-left: 4px solid #10B981; padding: 12px; border-radius: 8px; font-size: 13px; color: #E2E8F0; margin-top: 14px; }");
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        sb.AppendLine("<div class=\"card\">");
        sb.AppendLine("<div class=\"title\">📱 Restore to iPhone Camera Roll</div>");
        sb.AppendLine($"<div class=\"subtitle\">Ready to restore <b>{_stagedItems.Count}</b> media items directly to your iPhone.</div>");

        sb.AppendLine("<a href=\"/zip\" class=\"btn\" download=\"iPhone_Restored_Media.zip\">📦 Download All as ZIP (Save to Files/Photos)</a>");

        sb.AppendLine("<div class=\"tip\">");
        sb.AppendLine("<b>💡 How to save to Apple Photos:</b><br>");
        sb.AppendLine("• Tap <b>Download All as ZIP</b>, open in Safari Downloads, tap the file in Files app to unzip, then select all and tap <b>Save X Images</b>.<br>");
        sb.AppendLine("• Or tap individual <b>Download</b> buttons below and tap <b>Save Image</b> in Safari.");
        sb.AppendLine("</div>");

        sb.AppendLine("<ul class=\"item-list\">");
        for (int i = 0; i < _stagedItems.Count; i++)
        {
            var item = _stagedItems[i];
            sb.AppendLine("<li class=\"item-row\">");
            sb.AppendLine("<div class=\"item-info\">");
            sb.AppendLine($"<span class=\"item-name\">{WebUtility.HtmlEncode(item.FileName)}</span>");
            sb.AppendLine($"<span class=\"item-size\">{item.FileSizeFormatted} • {item.BadgeText}</span>");
            sb.AppendLine("</div>");
            sb.AppendLine($"<a href=\"/file/{i}\" download=\"{WebUtility.HtmlEncode(item.FileName)}\" class=\"dl-link\">⬇ Download</a>");
            sb.AppendLine("</li>");
        }
        sb.AppendLine("</ul>");

        sb.AppendLine("</div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes);
        resp.Close();
    }

    private async Task ServeFileAsync(GalleryItemViewModel item, HttpListenerResponse resp)
    {
        if (!File.Exists(item.FilePath))
        {
            resp.StatusCode = (int)HttpStatusCode.NotFound;
            resp.Close();
            return;
        }

        string ext = Path.GetExtension(item.FilePath).ToLowerInvariant();
        resp.ContentType = ext switch
        {
            ".heic" => "image/heic",
            ".heif" => "image/heif",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".mov" => "video/quicktime",
            ".mp4" => "video/mp4",
            _ => "application/octet-stream"
        };

        resp.StatusCode = (int)HttpStatusCode.OK;
        resp.AddHeader("Content-Disposition", $"inline; filename=\"{Path.GetFileName(item.FilePath)}\"");

        ItemDownloaded?.Invoke(item.FileName);
        LogMessage?.Invoke($"[WI-FI RESTORE] Transferred '{item.FileName}' to iPhone.");

        using var fs = File.OpenRead(item.FilePath);
        resp.ContentLength64 = fs.Length;
        await fs.CopyToAsync(resp.OutputStream);
        resp.Close();
    }

    private async Task ServeZipArchiveAsync(HttpListenerResponse resp)
    {
        resp.ContentType = "application/zip";
        resp.StatusCode = (int)HttpStatusCode.OK;
        resp.AddHeader("Content-Disposition", "attachment; filename=\"iPhone_Restored_Media.zip\"");

        LogMessage?.Invoke($"[WI-FI RESTORE] iPhone requested full ZIP bundle of {_stagedItems.Count} items.");

        using var zip = new ZipArchive(resp.OutputStream, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var item in _stagedItems)
        {
            if (File.Exists(item.FilePath))
            {
                zip.CreateEntryFromFile(item.FilePath, item.FileName, CompressionLevel.Fastest);
            }

            // Include companion Live Photo MOV
            if (item.IsLivePhoto && !string.IsNullOrEmpty(item.CompanionVideoPath) && File.Exists(item.CompanionVideoPath))
            {
                string movName = Path.GetFileName(item.CompanionVideoPath);
                zip.CreateEntryFromFile(item.CompanionVideoPath, movName, CompressionLevel.Fastest);
            }
        }

        resp.Close();
        LogMessage?.Invoke($"[WI-FI RESTORE] Full ZIP bundle successfully delivered to iPhone.");
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
