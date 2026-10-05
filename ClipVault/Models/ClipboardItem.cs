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

        private System.Windows.Media.ImageSource? _cachedImage;
        public System.Windows.Media.ImageSource? ImagePreview
        {
            get
            {
                if (_cachedImage != null) return _cachedImage;
                if (!IsImage || string.IsNullOrEmpty(FullImagePath) || !File.Exists(FullImagePath)) return null;
                try
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(FullImagePath, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();
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
