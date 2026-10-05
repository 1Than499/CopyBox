using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ClipVault.Interop;
using ClipVault.Models;

namespace ClipVault.Services
{
    public class ClipboardMonitorService : IDisposable
    {
        private HwndSource? _hwndSource;
        private readonly DatabaseService _dbService;
        private readonly SettingsService _settingsService;

        public bool IsPaused { get; set; } = false;
        public bool IsInternalOperation { get; set; } = false;

        public event Action<ClipboardItem>? ItemCaptured;

        // 知名密码管理器进程黑名单
        private static readonly HashSet<string> PasswordManagerProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            "1Password",
            "Bitwarden",
            "KeePass",
            "KeePassXC",
            "LastPass",
            "Dashlane",
            "Enpass",
            "RoboForm",
            "NordPass",
            "Proton Pass"
        };

        public ClipboardMonitorService(DatabaseService dbService, SettingsService settingsService)
        {
            _dbService = dbService;
            _settingsService = settingsService;
        }

        public void Start()
        {
            if (_hwndSource != null) return;

            // 创建消息专用的无边框隐藏 HwndSource 用于接收系统 WM_CLIPBOARDUPDATE
            var parameters = new HwndSourceParameters("ClipVaultMonitorHwnd")
            {
                WindowStyle = 0,
                Width = 0,
                Height = 0,
                PositionX = -1000,
                PositionY = -1000
            };

            _hwndSource = new HwndSource(parameters);
            _hwndSource.AddHook(HwndHook);

            NativeMethods.AddClipboardFormatListener(_hwndSource.Handle);
        }

        public void Stop()
        {
            if (_hwndSource != null)
            {
                NativeMethods.RemoveClipboardFormatListener(_hwndSource.Handle);
                _hwndSource.RemoveHook(HwndHook);
                _hwndSource.Dispose();
                _hwndSource = null;
            }
        }

        public void Pause() => IsPaused = true;
        public void Resume() => IsPaused = false;

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_CLIPBOARDUPDATE)
            {
                handled = true;
                OnClipboardChanged();
            }
            return IntPtr.Zero;
        }

        private void OnClipboardChanged()
        {
            if (IsPaused) return;

            if (IsInternalOperation)
            {
                // 内部模拟粘贴写入的剪贴板，跳过本次监听并重置标记
                IsInternalOperation = false;
                return;
            }

            // 获取来源窗口与进程
            string sourceProcess = "Unknown";
            string sourceApp = "Unknown";
            try
            {
                IntPtr fgHwnd = NativeMethods.GetForegroundWindow();
                if (fgHwnd != IntPtr.Zero)
                {
                    NativeMethods.GetWindowThreadProcessId(fgHwnd, out uint pid);
                    if (pid > 0)
                    {
                        using var proc = Process.GetProcessById((int)pid);
                        sourceProcess = proc.ProcessName;
                        sourceApp = string.IsNullOrWhiteSpace(proc.MainWindowTitle) ? proc.ProcessName : proc.MainWindowTitle;
                    }
                }
            }
            catch { }

            // 密码管理器隐私黑名单校验
            if (_settingsService.CurrentSettings.IgnorePasswordManagers)
            {
                if (PasswordManagerProcesses.Contains(sourceProcess))
                {
                    Debug.WriteLine($"[Privacy] Ignored copy from password manager: {sourceProcess}");
                    return;
                }
            }

            // 读取剪贴板内容（带重试机制，防止被其他软件独占锁住）
            ClipboardItem? capturedItem = null;
            for (int i = 0; i < 4; i++)
            {
                try
                {
                    capturedItem = ExtractClipboardData(sourceApp, sourceProcess);
                    if (capturedItem != null) break;
                }
                catch
                {
                    Thread.Sleep(25);
                }
            }

            if (capturedItem == null) return;

            // 重复项快速过滤
            string? latestHash = _dbService.GetLatestItemHash();
            if (!string.IsNullOrEmpty(latestHash) && latestHash == capturedItem.Sha256)
            {
                return;
            }

            // 写入本地数据库并触发事件
            _dbService.InsertItem(capturedItem);
            ItemCaptured?.Invoke(capturedItem);
        }

        private ClipboardItem? ExtractClipboardData(string sourceApp, string sourceProcess)
        {
            var dataObject = Clipboard.GetDataObject();
            if (dataObject == null) return null;

            // 1. 图片格式识别
            if (dataObject.GetDataPresent(DataFormats.Bitmap) || Clipboard.ContainsImage())
            {
                var bitmap = Clipboard.GetImage();
                if (bitmap != null)
                {
                    string imagesDir = Path.Combine(_settingsService.CurrentSettings.StorageDirectory, "images");
                    if (!Directory.Exists(imagesDir)) Directory.CreateDirectory(imagesDir);

                    // 计算图片哈希与文件名
                    byte[] pngBytes;
                    using (var ms = new MemoryStream())
                    {
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        encoder.Save(ms);
                        pngBytes = ms.ToArray();
                    }

                    string hash = ComputeHash(pngBytes);
                    string relPath = Path.Combine("images", $"{hash}.png");
                    string fullPath = Path.Combine(_settingsService.CurrentSettings.StorageDirectory, relPath);

                    if (!File.Exists(fullPath))
                    {
                        File.WriteAllBytes(fullPath, pngBytes);
                    }

                    return new ClipboardItem
                    {
                        ItemType = "Image",
                        Content = relPath,
                        FullImagePath = fullPath,
                        Summary = $"[截图/图片] {bitmap.PixelWidth} × {bitmap.PixelHeight} px",
                        SourceApp = sourceApp,
                        SourceProcess = sourceProcess,
                        CreatedAt = DateTime.Now,
                        Sha256 = hash,
                        CharCount = pngBytes.Length
                    };
                }
            }

            // 2. 文件拖拽列表
            if (dataObject.GetDataPresent(DataFormats.FileDrop))
            {
                var files = Clipboard.GetFileDropList();
                if (files != null && files.Count > 0)
                {
                    var sb = new StringBuilder();
                    foreach (var f in files) sb.AppendLine(f);
                    string fileContent = sb.ToString().TrimEnd();
                    string hash = ComputeHash(Encoding.UTF8.GetBytes(fileContent));

                    string summary = files.Count == 1 
                        ? $"[文件] {Path.GetFileName(files[0])}" 
                        : $"[文件列表] {Path.GetFileName(files[0])} 等 {files.Count} 个文件";

                    return new ClipboardItem
                    {
                        ItemType = "File",
                        Content = fileContent,
                        Summary = summary,
                        SourceApp = sourceApp,
                        SourceProcess = sourceProcess,
                        CreatedAt = DateTime.Now,
                        Sha256 = hash,
                        CharCount = fileContent.Length
                    };
                }
            }

            // 3. 纯文本与代码格式
            if (dataObject.GetDataPresent(DataFormats.UnicodeText) || dataObject.GetDataPresent(DataFormats.Text))
            {
                string text = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(text)) return null;

                string hash = ComputeHash(Encoding.UTF8.GetBytes(text));
                bool isCode = DetectIsCode(text);

                string cleanSummary = text.Trim();
                cleanSummary = Regex.Replace(cleanSummary, @"\s+", " ");
                if (cleanSummary.Length > 80)
                {
                    cleanSummary = cleanSummary.Substring(0, 80) + "...";
                }

                return new ClipboardItem
                {
                    ItemType = isCode ? "Code" : "Text",
                    Content = text,
                    Summary = isCode ? $"[代码] {cleanSummary}" : cleanSummary,
                    SourceApp = sourceApp,
                    SourceProcess = sourceProcess,
                    CreatedAt = DateTime.Now,
                    Sha256 = hash,
                    CharCount = text.Length
                };
            }

            return null;
        }

        private static bool DetectIsCode(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var trimmed = text.Trim();

            // 常见 JSON 或 XML/HTML
            if ((trimmed.StartsWith("{") && trimmed.EndsWith("}")) ||
                (trimmed.StartsWith("[") && trimmed.EndsWith("]")) ||
                (trimmed.StartsWith("<") && trimmed.EndsWith(">")))
            {
                return true;
            }

            // 常见编程语言关键字匹配
            string[] codeKeywords = {
                "function", "class ", "public ", "private ", "protected ", "return ",
                "const ", "let ", "var ", "import ", "export ", "from ", "def ",
                "if (", "for (", "while (", "SELECT ", "INSERT INTO", "UPDATE ",
                "namespace ", "using System", "console.log", "printf", "std::"
            };

            foreach (var kw in codeKeywords)
            {
                if (text.Contains(kw, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string ComputeHash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(bytes);
            var sb = new StringBuilder();
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
