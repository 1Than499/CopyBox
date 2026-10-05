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

        public bool IsImage => ItemType == "Image";
        public bool IsCode => ItemType == "Code";
        public bool IsText => ItemType == "Text";

        public string FormattedTime
        {
            get
            {
                var span = DateTime.Now - CreatedAt;
                if (span.TotalSeconds < 60) return "刚刚";
                if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} 分钟前";
                if (span.TotalHours < 24) return $"{(int)span.TotalHours} 小时前";
                if (span.TotalDays < 7) return $"{(int)span.TotalDays} 天前";
                return CreatedAt.ToString("yyyy-MM-dd HH:mm");
            }
        }

        public string FullImagePath { get; set; } = string.Empty;
    }
}
