using System;
using System.IO;

namespace ClipVault.Models
{
    public class ClipboardItem
    {
        public int Id { get; set; }
        public string ItemType { get; set; } = "Text"; // Text, Code, Image, File, Color
        public string Content { get; set; } = string.Empty; // 文本内容 或 本地图片相对路径
        public string Summary { get; set; } = string.Empty; // 标题/摘要
        public string SourceApp { get; set; } = string.Empty; // 例如 "Google Chrome", "VS Code"
        public string SourceProcess { get; set; } = string.Empty; // 例如 "chrome.exe"
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public bool IsPinned { get; set; }
        public int CharCount { get; set; }
        public string Sha256 { get; set; } = string.Empty;

        // UI 绑定展示辅助属性
        public string DisplayBadge { get; set; } = string.Empty; // [1], [2] 等数字快捷键
        public bool HasBadge => !string.IsNullOrEmpty(DisplayBadge);

        public bool IsImage => ItemType == "Image";
        public bool IsCode => ItemType == "Code";
        public bool IsText => ItemType == "Text";

        public string MenuTooltip => ClipVault.Services.LocalizationService.Instance.Get("MenuTooltip");

        public string FormattedTime
        {
            get
            {
                var loc = ClipVault.Services.LocalizationService.Instance;
                var span = DateTime.Now - CreatedAt;
                if (span.TotalSeconds < 60) return loc.Get("TimeJustNow");
                if (span.TotalMinutes < 60) return loc.Get("TimeMinsAgo", (int)span.TotalMinutes);
                if (span.TotalHours < 24) return loc.Get("TimeHoursAgo", (int)span.TotalHours);
                if (span.TotalDays < 7) return loc.Get("TimeDaysAgo", (int)span.TotalDays);
                return CreatedAt.ToString("yyyy-MM-dd HH:mm");
            }
        }

        public string FullImagePath { get; set; } = string.Empty;

        /// <summary>
        /// 具备全路径智能探测与自愈能力的图片物理路径解析器
        /// </summary>
        public string ResolvedImagePath
        {
            get
            {
                if (!string.IsNullOrEmpty(FullImagePath) && File.Exists(FullImagePath))
                    return FullImagePath;

                if (string.IsNullOrEmpty(Content)) return string.Empty;

                if (Path.IsPathRooted(Content) && File.Exists(Content))
                    return Content;

                try
                {
                    string fileName = Path.GetFileName(Content);
                    var candidates = new[]
                    {
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipVault", "Data"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CopyBox", "Data"),
                        AppDomain.CurrentDomain.BaseDirectory
                    };

                    foreach (var root in candidates)
                    {
                        string test1 = Path.Combine(root, Content);
                        if (File.Exists(test1)) return test1;

                        string test2 = Path.Combine(root, "images", fileName);
                        if (File.Exists(test2)) return test2;
                    }
                }
                catch { }

                return FullImagePath;
            }
        }

        private System.Windows.Media.ImageSource? _cachedImage;
        public System.Windows.Media.ImageSource? ImagePreview
        {
            get
            {
                if (_cachedImage != null) return _cachedImage;
                if (!IsImage) return null;

                string imgPath = ResolvedImagePath;
                if (string.IsNullOrEmpty(imgPath) || !File.Exists(imgPath)) return null;

                try
                {
                    // 使用非排他 FileShare.ReadWrite 读取内存流，彻底杜绝文件锁死并提升加载性能
                    using var fs = new FileStream(imgPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var ms = new MemoryStream();
                    fs.CopyTo(ms);
                    ms.Position = 0;

                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze(); // 允许跨线程安全访问与极速渲染
                    _cachedImage = bmp;
                    return _cachedImage;
                }
                catch
                {
                    return null;
                }
            }
        }
    }
}
