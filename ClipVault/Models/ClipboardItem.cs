using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using ClipVault.Helpers;

namespace ClipVault.Models
{
    public class ClipboardItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // 全局敏感信息遮罩开关状态 (与 AppSettings.AutoMaskSensitiveData 实时同步)
        public static bool GlobalAutoMaskEnabled { get; set; } = true;

        public int Id { get; set; }
        public string ItemType { get; set; } = "Text"; // Text, Code, Image, File, Color

        private string _content = string.Empty;
        public string Content
        {
            get => _content;
            set
            {
                _content = value ?? string.Empty;
                RecomputeSensitiveState();
            }
        }

        private string _summary = string.Empty;
        public string Summary
        {
            get => _summary;
            set
            {
                _summary = value ?? string.Empty;
                RecomputeSensitiveState();
            }
        }

        public string SourceApp { get; set; } = string.Empty; // 例如 "Google Chrome", "VS Code"
        public string SourceProcess { get; set; } = string.Empty; // 例如 "chrome.exe"
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime Timestamp => CreatedAt;
        public bool IsPinned { get; set; }
        public int CharCount { get; set; }
        public string Sha256 { get; set; } = string.Empty;

        // ==================== v2.2.1 敏感信息自动遮罩属性 ====================
        private bool _isSensitive;
        private string _sensitiveBadgeKey = string.Empty;
        private string _maskedContent = string.Empty;
        private string _maskedSummary = string.Empty;
        private bool _isMaskRevealed;

        private void RecomputeSensitiveState()
        {
            if (IsImage || ItemType == "File" || string.IsNullOrEmpty(_content))
            {
                _isSensitive = false;
                _sensitiveBadgeKey = string.Empty;
                _maskedContent = _content;
                _maskedSummary = _summary;
                return;
            }

            var (sens, badgeKey, masked) = SensitiveDataHelper.InspectAndMask(_content);
            _isSensitive = sens;
            _sensitiveBadgeKey = badgeKey;
            _maskedContent = masked;

            if (sens && !string.IsNullOrEmpty(_summary))
            {
                var (_, _, mSum) = SensitiveDataHelper.InspectAndMask(_summary);
                _maskedSummary = mSum;
            }
            else
            {
                _maskedSummary = _summary;
            }
        }

        public bool IsSensitive => _isSensitive;
        public bool ShowSensitiveBadge => GlobalAutoMaskEnabled && _isSensitive;

        public string SensitiveTypeTag => string.IsNullOrEmpty(_sensitiveBadgeKey)
            ? string.Empty
            : ClipVault.Services.LocalizationService.Instance.Get(_sensitiveBadgeKey);

        public bool IsMaskRevealed
        {
            get => _isMaskRevealed;
            set
            {
                if (_isMaskRevealed == value) return;
                _isMaskRevealed = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayContent));
                OnPropertyChanged(nameof(DisplaySummary));
                OnPropertyChanged(nameof(MaskToggleIcon));
                OnPropertyChanged(nameof(MaskToggleTooltip));
            }
        }

        public string DisplayContent => (GlobalAutoMaskEnabled && _isSensitive && !_isMaskRevealed)
            ? _maskedContent
            : _content;

        public string DisplaySummary => (GlobalAutoMaskEnabled && _isSensitive && !_isMaskRevealed)
            ? _maskedSummary
            : _summary;

        public string MaskToggleIcon => _isMaskRevealed ? "🙈" : "👁";

        public string MaskToggleTooltip => _isMaskRevealed
            ? ClipVault.Services.LocalizationService.Instance.Get("MaskHideTip")
            : ClipVault.Services.LocalizationService.Instance.Get("MaskShowTip");

        public void NotifyMaskStateChanged()
        {
            OnPropertyChanged(nameof(ShowSensitiveBadge));
            OnPropertyChanged(nameof(SensitiveTypeTag));
            OnPropertyChanged(nameof(DisplayContent));
            OnPropertyChanged(nameof(DisplaySummary));
            OnPropertyChanged(nameof(MaskToggleIcon));
            OnPropertyChanged(nameof(MaskToggleTooltip));
        }

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
                        AppSettings.GetAppRootDirectory(),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CopyBox", "Data"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipVault", "Data"),
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
                    bmp.DecodePixelWidth = 400; // 核心内存优化：卡片缩略图约束至 400px 解码，内存占用骤降 95%+
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze(); // 允许跨线程安全访问与极速渲染

                    // 存量历史截图全透明自愈：如果图片所有 Alpha 均为 0，现场纠正为正常不透明位图并异步修正磁盘文件
                    var fixedBmp = ClipVault.Services.ClipboardMonitorService.FixDIBAlphaChannel(bmp);
                    if (!ReferenceEquals(fixedBmp, bmp))
                    {
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            try
                            {
                                using var outFs = new FileStream(imgPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(fixedBmp));
                                encoder.Save(outFs);
                            }
                            catch { }
                        });
                        _cachedImage = fixedBmp;
                    }
                    else
                    {
                        _cachedImage = bmp;
                    }
                    return _cachedImage;
                }
                catch
                {
                    return null;
                }
            }
        }

        public void ClearImageCache()
        {
            _cachedImage = null;
        }
    }
}
