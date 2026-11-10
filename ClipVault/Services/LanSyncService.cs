using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using ClipVault.Models;

namespace ClipVault.Services
{
    /// <summary>
    /// v3.0 局域网离线双向快传与点对点加密同步服务
    /// 采用用户态 TcpListener 构建轻量 HTTP/1.1 微服务（无需管理员权限、零外网依赖、关闭后 0MB 内存驻留）
    /// 支持：
    /// 1. 手机相机/浏览器扫码免安装网页双向快传（手机发文字/相册照片直达电脑剪贴板；电脑最近 20 条记录同步至手机复制/存图）
    /// 2. 局域网多台电脑 AES-256-GCM 端到端加密剪贴板广播同步
    /// </summary>
    public class LanSyncService : IDisposable
    {
        private readonly DatabaseService _dbService;
        private readonly SettingsService _settingsService;
        private readonly ClipboardMonitorService _monitorService;

        private TcpListener? _tcpListener;
        private UdpClient? _udpClient;
        private CancellationTokenSource? _cts;

        public bool IsRunning { get; private set; }
        public int ActivePort { get; private set; }
        public string ActiveIp { get; private set; } = "127.0.0.1";
        public string SessionToken { get; private set; } = string.Empty;
        public string PairingUrl => $"http://{ActiveIp}:{ActivePort}/?t={SessionToken}";

        private const int DefaultHttpPort = 28560;
        private const int DefaultUdpPort = 28569;
        private static readonly string NodeInstanceId = Guid.NewGuid().ToString("N").Substring(0, 12);

        public event Action<ClipboardItem>? ItemReceivedFromLan;
        public event Action<string>? StatusMessageChanged;

        public LanSyncService(DatabaseService dbService, SettingsService settingsService, ClipboardMonitorService monitorService)
        {
            _dbService = dbService;
            _settingsService = settingsService;
            _monitorService = monitorService;
        }

        /// <summary>
        /// 按需启动局域网快传服务（自动探测空闲端口与最佳物理局域网 IPv4）
        /// </summary>
        public bool Start(int preferredPort = DefaultHttpPort)
        {
            if (IsRunning) return true;

            try
            {
                ActiveIp = GetBestLanIpAddress();
                SessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
                _cts = new CancellationTokenSource();

                int boundPort = -1;
                for (int p = preferredPort; p <= preferredPort + 15; p++)
                {
                    try
                    {
                        var listener = new TcpListener(IPAddress.Any, p);
                        listener.Start();
                        _tcpListener = listener;
                        boundPort = p;
                        break;
                    }
                    catch
                    {
                        // 端口被占用则自动递增尝试下一端口
                    }
                }

                if (_tcpListener == null || boundPort < 0)
                {
                    return false;
                }

                ActivePort = boundPort;
                IsRunning = true;

                _ = Task.Run(() => AcceptHttpClientsLoopAsync(_cts.Token));
                StartUdpPeerSyncLoop(_cts.Token);

                return true;
            }
            catch
            {
                Stop();
                return false;
            }
        }

        /// <summary>
        /// 停止局域网快传服务并彻底释放 Socket 与缓冲区资源（恢复 0MB 闲置占用）
        /// </summary>
        public void Stop()
        {
            IsRunning = false;
            try { _cts?.Cancel(); } catch { }
            try { _tcpListener?.Stop(); } catch { }
            try { _udpClient?.Close(); _udpClient?.Dispose(); } catch { }
            _tcpListener = null;
            _udpClient = null;
            _cts?.Dispose();
            _cts = null;
        }

        /// <summary>
        /// 智能优选真实物理无线网卡 (Wi-Fi) 或以太网卡的局域网 IPv4 地址，排除 WSL/Hyper-V/VMware 虚拟网卡
        /// </summary>
        public static string GetBestLanIpAddress()
        {
            try
            {
                string? fallbackIp = null;
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                    string desc = (ni.Name + " " + ni.Description).ToLowerInvariant();
                    bool isVirtual = desc.Contains("virtual") || desc.Contains("vmware") ||
                                     desc.Contains("vbox") || desc.Contains("wsl") ||
                                     desc.Contains("hyper-v") || desc.Contains("vethernet") ||
                                     desc.Contains("tap") || desc.Contains("tun");

                    var ipProps = ni.GetIPProperties();
                    bool hasGateway = ipProps.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(g.Address));

                    foreach (var ua in ipProps.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string ip = ua.Address.ToString();
                        if (ip.StartsWith("127.") || ip.StartsWith("169.254.")) continue;

                        if (!isVirtual && hasGateway && (ip.StartsWith("192.168.") || ip.StartsWith("10.") || ip.StartsWith("172.")))
                        {
                            return ip;
                        }
                        fallbackIp ??= ip;
                    }
                }
                if (!string.IsNullOrEmpty(fallbackIp)) return fallbackIp;
            }
            catch { }
            return "127.0.0.1";
        }

        private async Task AcceptHttpClientsLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _tcpListener != null)
            {
                try
                {
                    TcpClient client = await _tcpListener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                    _ = Task.Run(() => HandleHttpClientAsync(client, ct), ct);
                }
                catch
                {
                    if (ct.IsCancellationRequested) break;
                }
            }
        }

        private async Task HandleHttpClientAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                client.ReceiveTimeout = 10000;
                client.SendTimeout = 10000;
                using NetworkStream stream = client.GetStream();

                try
                {
                    // 读取 HTTP 请求头 (最大 16KB 头部)
                    byte[] buffer = new byte[65536];
                    using var ms = new MemoryStream();
                    int headerEndIndex = -1;

                    while (headerEndIndex < 0 && ms.Length < 16384)
                    {
                        int read = await stream.ReadAsync(buffer, 0, Math.Min(buffer.Length, 4096), ct).ConfigureAwait(false);
                        if (read <= 0) return;
                        ms.Write(buffer, 0, read);
                        headerEndIndex = FindHeaderEnd(ms.GetBuffer(), (int)ms.Length);
                    }

                    if (headerEndIndex < 0) return;

                    byte[] allRead = ms.ToArray();
                    string headerText = Encoding.ASCII.GetString(allRead, 0, headerEndIndex);
                    string[] headerLines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                    if (headerLines.Length == 0) return;

                    string[] reqLineParts = headerLines[0].Split(' ');
                    if (reqLineParts.Length < 2) return;

                    string method = reqLineParts[0].ToUpperInvariant();
                    string rawUrl = reqLineParts[1];

                    int contentLength = 0;
                    foreach (var line in headerLines)
                    {
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        {
                            int.TryParse(line.Substring(15).Trim(), out contentLength);
                        }
                    }

                    // 限制最大上传体积 15MB，防止恶意请求耗尽内存
                    if (contentLength < 0 || contentLength > 15 * 1024 * 1024)
                    {
                        await WriteHttpResponseAsync(stream, "413 Payload Too Large", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Payload Too Large")).ConfigureAwait(false);
                        return;
                    }

                    int bodyStart = headerEndIndex + 4;
                    int alreadyReadBody = allRead.Length - bodyStart;
                    byte[] bodyBytes = new byte[contentLength];
                    if (alreadyReadBody > 0 && contentLength > 0)
                    {
                        Buffer.BlockCopy(allRead, bodyStart, bodyBytes, 0, Math.Min(alreadyReadBody, contentLength));
                    }

                    int remaining = contentLength - alreadyReadBody;
                    int offset = Math.Max(0, alreadyReadBody);
                    while (remaining > 0)
                    {
                        int r = await stream.ReadAsync(bodyBytes, offset, remaining, ct).ConfigureAwait(false);
                        if (r <= 0) break;
                        offset += r;
                        remaining -= r;
                    }

                    // 解析路径与查询参数
                    string path = rawUrl;
                    string query = string.Empty;
                    int qIdx = rawUrl.IndexOf('?');
                    if (qIdx >= 0)
                    {
                        path = rawUrl.Substring(0, qIdx);
                        query = rawUrl.Substring(qIdx + 1);
                    }

                    var queryParams = ParseQueryString(query);
                    queryParams.TryGetValue("t", out string? reqToken);

                    // 严格校验一次性会话 Token
                    if (!IsValidToken(reqToken))
                    {
                        await WriteHttpResponseAsync(stream, "403 Forbidden", "text/plain; charset=utf-8",
                            Encoding.UTF8.GetBytes("403 Unauthorized Session Token. Please re-scan the QR code on CopyBox.")).ConfigureAwait(false);
                        return;
                    }

                    if (method == "GET" && (path == "/" || path == "/index.html"))
                    {
                        string html = BuildMobileWebAppHtml(SessionToken);
                        await WriteHttpResponseAsync(stream, "200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html)).ConfigureAwait(false);
                    }
                    else if (method == "GET" && path == "/api/items")
                    {
                        var recent = _dbService.GetRecentItems("", "all", 20);
                        var dtoList = recent.Select(it => new
                        {
                            id = it.Id,
                            type = it.ItemType,
                            text = it.IsImage ? it.DisplaySummary : it.DisplayContent,
                            summary = it.DisplaySummary,
                            time = it.CreatedAt.ToString("HH:mm:ss"),
                            isImage = it.IsImage,
                            ocr = it.OcrText ?? string.Empty
                        }).ToList();

                        string json = JsonSerializer.Serialize(dtoList);
                        await WriteHttpResponseAsync(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json)).ConfigureAwait(false);
                    }
                    else if (method == "GET" && path == "/api/img")
                    {
                        if (queryParams.TryGetValue("id", out string? idStr) && int.TryParse(idStr, out int imgId))
                        {
                            var item = _dbService.GetRecentItems("", "all", 60).FirstOrDefault(x => x.Id == imgId && x.IsImage);
                            if (item != null && !string.IsNullOrEmpty(item.ResolvedImagePath) && File.Exists(item.ResolvedImagePath))
                            {
                                byte[] imgBytes = await File.ReadAllBytesAsync(item.ResolvedImagePath, ct).ConfigureAwait(false);
                                await WriteHttpResponseAsync(stream, "200 OK", "image/png", imgBytes).ConfigureAwait(false);
                                return;
                            }
                        }
                        await WriteHttpResponseAsync(stream, "404 Not Found", "text/plain", Encoding.UTF8.GetBytes("Not Found")).ConfigureAwait(false);
                    }
                    else if (method == "POST" && path == "/api/text")
                    {
                        string receivedText = Encoding.UTF8.GetString(bodyBytes).Trim();
                        if (!string.IsNullOrEmpty(receivedText))
                        {
                            IngestReceivedTextFromPeer(receivedText, "📱 手机扫码快传");
                            await WriteHttpResponseAsync(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"ok\":true}")).ConfigureAwait(false);
                        }
                        else
                        {
                            await WriteHttpResponseAsync(stream, "400 Bad Request", "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"ok\":false}")).ConfigureAwait(false);
                        }
                    }
                    else if (method == "POST" && path == "/api/image")
                    {
                        if (bodyBytes.Length > 16)
                        {
                            await IngestReceivedImageBytesAsync(bodyBytes, "📱 手机相册快传").ConfigureAwait(false);
                            await WriteHttpResponseAsync(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"ok\":true}")).ConfigureAwait(false);
                        }
                        else
                        {
                            await WriteHttpResponseAsync(stream, "400 Bad Request", "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"ok\":false}")).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        await WriteHttpResponseAsync(stream, "404 Not Found", "text/plain", Encoding.UTF8.GetBytes("Not Found")).ConfigureAwait(false);
                    }
                }
                catch { }
            }
        }

        private bool IsValidToken(string? provided)
        {
            if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(SessionToken)) return false;
            byte[] a = Encoding.UTF8.GetBytes(provided);
            byte[] b = Encoding.UTF8.GetBytes(SessionToken);
            return CryptographicOperations.FixedTimeEquals(a, b);
        }

        private static int FindHeaderEnd(byte[] buf, int len)
        {
            for (int i = 0; i <= len - 4; i++)
            {
                if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n')
                {
                    return i;
                }
            }
            return -1;
        }

        private static Dictionary<string, string> ParseQueryString(string query)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(query)) return dict;
            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq > 0)
                {
                    string k = Uri.UnescapeDataString(part.Substring(0, eq));
                    string v = Uri.UnescapeDataString(part.Substring(eq + 1));
                    dict[k] = v;
                }
            }
            return dict;
        }

        private static async Task WriteHttpResponseAsync(NetworkStream stream, string status, string contentType, byte[] body)
        {
            string headers =
                $"HTTP/1.1 {status}\r\n" +
                $"Content-Type: {contentType}\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Cache-Control: no-store, no-cache\r\n" +
                "Connection: close\r\n\r\n";
            byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
            await stream.WriteAsync(headerBytes, 0, headerBytes.Length).ConfigureAwait(false);
            if (body.Length > 0)
            {
                await stream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
            }
            await stream.FlushAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// 将手机或局域网对端发来的文本入库并写入电脑系统剪贴板
        /// </summary>
        public void IngestReceivedTextFromPeer(string text, string sourceLabel)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            string hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
            string cleanSummary = text.Trim().Replace("\r", " ").Replace("\n", " ");
            if (cleanSummary.Length > 80) cleanSummary = cleanSummary.Substring(0, 80) + "...";

            var item = new ClipboardItem
            {
                CreatedAt = DateTime.Now,
                ItemType = "Text",
                Content = text,
                Summary = cleanSummary,
                SourceApp = sourceLabel,
                SourceProcess = "CopyBox.LanSync",
                Sha256 = hash,
                CharCount = text.Length
            };

            _dbService.InsertItem(item);

            if (Application.Current?.Dispatcher != null)
            {
                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        if (_monitorService != null) _monitorService.IsInternalOperation = true;
                        Clipboard.SetText(text);
                    }
                    catch { }
                    finally
                    {
                        if (_monitorService != null)
                        {
                            _ = Task.Delay(500).ContinueWith(_ => _monitorService.IsInternalOperation = false);
                        }
                    }
                    ItemReceivedFromLan?.Invoke(item);
                    StatusMessageChanged?.Invoke($"✓ 已收到来自【{sourceLabel}】的文本 ({text.Length} 字)");
                });
            }
            else
            {
                ItemReceivedFromLan?.Invoke(item);
            }
        }

        /// <summary>
        /// 将手机上传的图片字节流入库、落盘、写入系统剪贴板并自动触发后台离线 OCR
        /// </summary>
        public async Task IngestReceivedImageBytesAsync(byte[] imgBytes, string sourceLabel)
        {
            if (imgBytes == null || imgBytes.Length < 16) return;

            string imagesDir = Path.Combine(_settingsService.CurrentSettings.StorageDirectory, "images");
            Directory.CreateDirectory(imagesDir);

            string hash = Convert.ToHexString(SHA256.HashData(imgBytes)).ToLowerInvariant();
            string relPath = Path.Combine("images", $"{hash}.png");
            string fullPath = Path.Combine(_settingsService.CurrentSettings.StorageDirectory, relPath);

            if (!File.Exists(fullPath))
            {
                await File.WriteAllBytesAsync(fullPath, imgBytes).ConfigureAwait(false);
            }

            string ocrText = await NativeOcrService.RecognizeImageBytesAsync(imgBytes).ConfigureAwait(false);

            var item = new ClipboardItem
            {
                CreatedAt = DateTime.Now,
                ItemType = "Image",
                Content = relPath,
                FullImagePath = fullPath,
                Summary = $"[手机快传图片] {(imgBytes.Length / 1024.0):F1} KB",
                SourceApp = sourceLabel,
                SourceProcess = "CopyBox.LanSync",
                Sha256 = hash,
                CharCount = imgBytes.Length,
                OcrText = ocrText
            };

            _dbService.InsertItem(item);

            if (Application.Current?.Dispatcher != null)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        _monitorService?.SuppressInternalWrite(1500);
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.UriSource = new Uri(fullPath, UriKind.Absolute);
                        bmp.EndInit();
                        bmp.Freeze();
                        _monitorService?.NotifyInternalImageWrite(bmp.PixelWidth, bmp.PixelHeight, 2500);
                        Clipboard.SetImage(bmp);
                    }
                    catch { }
                    finally
                    {
                        if (_monitorService != null)
                        {
                            _ = Task.Delay(900).ContinueWith(_ => _monitorService.IsInternalOperation = false);
                        }
                    }
                    ItemReceivedFromLan?.Invoke(item);
                    StatusMessageChanged?.Invoke($"✓ 已收到来自【{sourceLabel}】的图片");
                });
            }
            else
            {
                ItemReceivedFromLan?.Invoke(item);
            }
        }

        #region PC 间局域网 UDP + AES-256-GCM 端到端加密同步

        private void StartUdpPeerSyncLoop(CancellationToken ct)
        {
            try
            {
                _udpClient = new UdpClient();
                _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, DefaultUdpPort));
                _udpClient.EnableBroadcast = true;

                _ = Task.Run(async () =>
                {
                    while (!ct.IsCancellationRequested && _udpClient != null)
                    {
                        try
                        {
                            var result = await _udpClient.ReceiveAsync(ct).ConfigureAwait(false);
                            TryDecryptAndIngestUdpPacket(result.Buffer);
                        }
                        catch
                        {
                            if (ct.IsCancellationRequested) break;
                        }
                    }
                }, ct);
            }
            catch { }
        }

        /// <summary>
        /// 使用 AES-256-GCM 将文本加密广播给同一局域网下拥有相同配对码的 PC
        /// </summary>
        public static byte[] EncryptPeerPacket(string text, string sharedPin, string senderId = "")
        {
            string actualSender = string.IsNullOrEmpty(senderId) ? NodeInstanceId : senderId;
            byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes("CopyBox_v3_AES_" + (sharedPin ?? "0000")));
            byte[] nonce = RandomNumberGenerator.GetBytes(12);
            byte[] plainBytes = Encoding.UTF8.GetBytes($"{actualSender}|{text}");
            byte[] cipherBytes = new byte[plainBytes.Length];
            byte[] tag = new byte[16];

            using (var aes = new AesGcm(key, 16))
            {
                aes.Encrypt(nonce, plainBytes, cipherBytes, tag);
            }

            // Packet format: [4B Magic 'CBV3'][12B Nonce][16B Tag][N Bytes Ciphertext]
            byte[] packet = new byte[4 + 12 + 16 + cipherBytes.Length];
            packet[0] = (byte)'C'; packet[1] = (byte)'B'; packet[2] = (byte)'V'; packet[3] = (byte)'3';
            Buffer.BlockCopy(nonce, 0, packet, 4, 12);
            Buffer.BlockCopy(tag, 0, packet, 16, 16);
            Buffer.BlockCopy(cipherBytes, 0, packet, 32, cipherBytes.Length);
            return packet;
        }

        public static (bool ok, string senderId, string text) TryDecryptPeerPacket(byte[] packet, string sharedPin)
        {
            if (packet == null || packet.Length <= 32) return (false, string.Empty, string.Empty);
            if (packet[0] != 'C' || packet[1] != 'B' || packet[2] != 'V' || packet[3] != '3') return (false, string.Empty, string.Empty);

            try
            {
                byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes("CopyBox_v3_AES_" + (sharedPin ?? "0000")));
                byte[] nonce = new byte[12];
                byte[] tag = new byte[16];
                int cipherLen = packet.Length - 32;
                byte[] cipherBytes = new byte[cipherLen];
                byte[] plainBytes = new byte[cipherLen];

                Buffer.BlockCopy(packet, 4, nonce, 0, 12);
                Buffer.BlockCopy(packet, 16, tag, 0, 16);
                Buffer.BlockCopy(packet, 32, cipherBytes, 0, cipherLen);

                using (var aes = new AesGcm(key, 16))
                {
                    aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
                }

                string decoded = Encoding.UTF8.GetString(plainBytes);
                int sep = decoded.IndexOf('|');
                if (sep <= 0) return (false, string.Empty, string.Empty);
                return (true, decoded.Substring(0, sep), decoded.Substring(sep + 1));
            }
            catch
            {
                return (false, string.Empty, string.Empty);
            }
        }

        private void TryDecryptAndIngestUdpPacket(byte[] packet)
        {
            var (ok, senderId, text) = TryDecryptPeerPacket(packet, SessionToken);
            if (ok && senderId != NodeInstanceId && !string.IsNullOrWhiteSpace(text))
            {
                IngestReceivedTextFromPeer(text, "💻 局域网电脑加密同步");
            }
        }

        #endregion

        private static string BuildMobileWebAppHtml(string token)
        {
            return @"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no"">
<title>CopyBox v3.0 局域网双向快传</title>
<style>
*{box-sizing:border-box;margin:0;padding:0;-webkit-tap-highlight-color:transparent;}
body{background:#0f172a;color:#f8fafc;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;padding:16px;max-width:560px;margin:0 auto;}
.hdr{display:flex;align-items:center;justify-content:space-between;margin-bottom:14px;padding-bottom:10px;border-bottom:1px solid #1e293b;}
.brand{display:flex;align-items:center;gap:8px;font-weight:700;font-size:17px;}
.badge{background:#1e3a8a;color:#60a5fa;font-size:11px;padding:2px 7px;border-radius:6px;}
.card{background:#1e293b;border:1px solid #334155;border-radius:12px;padding:14px;margin-bottom:14px;}
.title{font-size:13.5px;font-weight:600;color:#94a3b8;margin-bottom:10px;display:flex;justify-content:space-between;align-items:center;}
textarea{width:100%;height:96px;background:#0f172a;color:#f8fafc;border:1px solid #334155;border-radius:8px;padding:10px;font-size:14px;resize:none;outline:none;}
textarea:focus{border-color:#38bdf8;}
.btn-row{display:flex;gap:8px;margin-top:10px;}
.btn{flex:1;padding:10px 12px;border-radius:8px;border:none;font-weight:600;font-size:13.5px;cursor:pointer;text-align:center;}
.btn-pri{background:#0284c7;color:#fff;}
.btn-sec{background:#334155;color:#e2e8f0;}
.item{background:#0f172a;border:1px solid #334155;border-radius:9px;padding:10px 12px;margin-bottom:8px;}
.item-top{display:flex;justify-content:space-between;align-items:center;font-size:11px;color:#64748b;margin-bottom:6px;}
.item-txt{font-size:13px;color:#e2e8f0;word-break:break-all;white-space:pre-wrap;max-height:110px;overflow:hidden;}
.item-img{max-width:100%;max-height:180px;border-radius:6px;display:block;margin:4px 0;}
.copy-btn{background:#1e3a8a;color:#93c5fd;border:none;padding:4px 10px;border-radius:5px;font-size:11.5px;font-weight:600;cursor:pointer;}
#toast{position:fixed;bottom:20px;left:50%;transform:translateX(-50%);background:#10b981;color:#fff;padding:8px 18px;border-radius:20px;font-size:13px;font-weight:600;display:none;box-shadow:0 4px 14px rgba(0,0,0,0.4);}
</style>
</head>
<body>
<div class=""hdr"">
  <div class=""brand"">📋 CopyBox 局域网快传 <span class=""badge"">v3.0 离线直连</span></div>
  <button class=""copy-btn"" onclick=""loadItems()"">🔄 刷新</button>
</div>

<div class=""card"">
  <div class=""title""><span>📤 发送到电脑剪贴板</span></div>
  <textarea id=""txtInput"" placeholder=""在此输入或粘贴文字，点击下方按钮秒传至电脑剪贴板...""></textarea>
  <div class=""btn-row"">
    <button class=""btn btn-pri"" onclick=""sendText()"">🚀 发送文字到电脑</button>
    <label class=""btn btn-sec"">📷 发送手机图片<input type=""file"" accept=""image/*"" style=""display:none"" onchange=""sendImage(this)""></label>
  </div>
</div>

<div class=""card"">
  <div class=""title""><span>💻 电脑最近剪贴板记录</span><span style=""font-size:11px;color:#64748b"">点击一键复制或长按存图</span></div>
  <div id=""list"">正在加载电脑剪贴板...</div>
</div>

<div id=""toast""></div>

<script>
const TOKEN = '" + token + @"';
function showToast(msg){
  const t=document.getElementById('toast');
  t.innerText=msg;t.style.display='block';
  setTimeout(()=>t.style.display='none',2000);
}
async function sendText(){
  const el=document.getElementById('txtInput');
  const val=el.value.trim();
  if(!val){showToast('请输入要发送的文字');return;}
  const res=await fetch('/api/text?t='+TOKEN,{method:'POST',body:val});
  if(res.ok){el.value='';showToast('✓ 已发送至电脑剪贴板！');loadItems();}
}
function sendImage(input){
  if(!input.files||!input.files[0])return;
  const file=input.files[0];
  showToast('正在处理并发送图片...');
  const img=new Image();
  const url=URL.createObjectURL(file);
  img.onload=function(){
    let w=img.width,h=img.height,max=2400;
    if(w>max||h>max){if(w>h){h=Math.round(h*max/w);w=max;}else{w=Math.round(w*max/h);h=max;}}
    const cv=document.createElement('canvas');
    cv.width=w;cv.height=h;
    cv.getContext('2d').drawImage(img,0,0,w,h);
    URL.revokeObjectURL(url);
    cv.toBlob(async function(blob){
      const res=await fetch('/api/image?t='+TOKEN,{method:'POST',body:blob});
      if(res.ok){showToast('✓ 图片已直达电脑剪贴板！');loadItems();}
      input.value='';
    },'image/png');
  };
  img.src=url;
}
function copyText(txt){
  if(navigator.clipboard&&navigator.clipboard.writeText){
    navigator.clipboard.writeText(txt).then(()=>showToast('✓ 已复制到手机剪贴板'));
    return;
  }
  const ta=document.createElement('textarea');
  ta.value=txt;document.body.appendChild(ta);ta.select();
  document.execCommand('copy');document.body.removeChild(ta);
  showToast('✓ 已复制到手机剪贴板');
}
async function loadItems(){
  try{
    const res=await fetch('/api/items?t='+TOKEN);
    const items=await res.json();
    const box=document.getElementById('list');
    box.innerHTML='';
    items.forEach(it=>{
      const div=document.createElement('div');
      div.className='item';
      const top=document.createElement('div');
      top.className='item-top';
      top.innerHTML='<span>'+it.type+' · '+it.time+'</span>';
      const btn=document.createElement('button');
      btn.className='copy-btn';
      btn.innerText=it.isImage?(it.ocr?'📋 复制图中文字':'🖼 长按下方存图'):'📋 复制';
      if(!it.isImage || it.ocr){
        btn.onclick=()=>copyText(it.isImage?it.ocr:it.text);
      }
      top.appendChild(btn);
      div.appendChild(top);
      if(it.isImage){
        const im=document.createElement('img');
        im.className='item-img';
        im.src='/api/img?id='+it.id+'&t='+TOKEN;
        div.appendChild(im);
      }else{
        const p=document.createElement('div');
        p.className='item-txt';
        p.innerText=it.text;
        div.appendChild(p);
      }
      box.appendChild(div);
    });
  }catch(e){}
}
loadItems();
setInterval(loadItems,4000);
</script>
</body>
</html>";
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
