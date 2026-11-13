using System;
using System.Collections.Concurrent;
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

        // 静态轻量级结果缓存：彻底消除搜索与刷新时对相同历史卡片重复执行 7 重正则扫描与磁盘图片重解码
        private static readonly ConcurrentDictionary<int, (string contentSample, bool isSens, string badgeKey, string maskedContent, string maskedSummary)> SensitiveStateCache = new();
        private static readonly ConcurrentDictionary<string, System.Windows.Media.ImageSource> ThumbnailMemoryCache = new(StringComparer.OrdinalIgnoreCase);

        public int Id { get; set; }
        public string ItemType { get; set; } = "Text"; // Text, Code, Image, File, Color

        private string _content = string.Empty;
        public string Content
        {
            get => _content;
            set
            {
                _content = value ?? string.Empty;
                _sensitiveEvaluated = false;
            }
        }

        private string _summary = string.Empty;
        public string Summary
        {
            get => _summary;
            set
            {
                _summary = value ?? string.Empty;
                _sensitiveEvaluated = false;
            }
        }

        public string SourceApp { get; set; } = string.Empty; // 例如 "Google Chrome", "VS Code"
        public string SourceProcess { get; set; } = string.Empty; // 例如 "chrome.exe"
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime Timestamp => CreatedAt;
        public bool IsPinned { get; set; }
        public int CharCount { get; set; }
        public string Sha256 { get; set; } = string.Empty;

        // ==================== v3.0 自定义分类标签、模板变量与原生离线 OCR 属性 ====================
        private string _customTag = string.Empty;
        public string CustomTag
        {
            get => _customTag;
            set
            {
                string next = value?.Trim() ?? string.Empty;
                if (_customTag == next) return;
                _customTag = next;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasCustomTag));
                OnPropertyChanged(nameof(CustomTagBadgeText));
            }
        }

        public bool HasCustomTag => !string.IsNullOrWhiteSpace(_customTag);
        public string CustomTagBadgeText => HasCustomTag ? $"🏷 {_customTag}" : string.Empty;

        private string _ocrText = string.Empty;
        public string OcrText
        {
            get => _ocrText;
            set
            {
                string next = value?.Trim() ?? string.Empty;
                if (_ocrText == next) return;
                _ocrText = next;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasOcrText));
                OnPropertyChanged(nameof(OcrExtractBtnText));
                OnPropertyChanged(nameof(OcrExtractTooltip));
            }
        }

        public bool HasOcrText => IsImage && !string.IsNullOrWhiteSpace(_ocrText);
        public string OcrExtractBtnText => ClipVault.Services.LocalizationService.Instance.CurrentLanguage == "en-US" ? "📝 OCR" : "📝 提取文字";
        public string OcrExtractTooltip => HasOcrText
            ? (ClipVault.Services.LocalizationService.Instance.CurrentLanguage == "en-US"
                ? $"Click to copy extracted image text:\n{(_ocrText.Length > 120 ? _ocrText.Substring(0, 120) + "..." : _ocrText)}"
                : $"点击一键复制图中识别文字：\n{(_ocrText.Length > 120 ? _ocrText.Substring(0, 120) + "..." : _ocrText)}")
            : string.Empty;

        private bool _isOcrKeywordMatched;
        public bool IsOcrKeywordMatched
        {
            get => _isOcrKeywordMatched;
            set
            {
                if (_isOcrKeywordMatched == value) return;
                _isOcrKeywordMatched = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(OcrMatchedBadgeText));
            }
        }

        public string OcrMatchedBadgeText => ClipVault.Services.LocalizationService.Instance.CurrentLanguage == "en-US"
            ? "🔍 Matched Image Text"
            : "🔍 命中图中文字";

        public bool HasTemplateMacro => !IsImage && ItemType != "File" && TemplateMacroHelper.ContainsMacro(_content);
        public string TemplateMacroBadgeText => ClipVault.Services.LocalizationService.Instance.CurrentLanguage == "en-US" ? "⚡ Macro" : "⚡ 动态模板";
        public string TemplateMacroTooltip => ClipVault.Services.LocalizationService.Instance.CurrentLanguage == "en-US"
            ? "Contains dynamic template placeholders ({{date}}, {{time}}, {{clipboard}}, etc.) that auto-expand on paste"
            : "包含动态模板变量（如 {{date}}、{{time}}、{{clipboard}}），粘贴时自动实时替换";

        // ==================== v2.2.1 敏感信息自动遮罩属性 (v3.0 升级为按需惰性求值 + O(1) 内存缓存) ====================
        private bool _sensitiveEvaluated;
        private bool _isSensitive;
        private string _sensitiveBadgeKey = string.Empty;
        private string _maskedContent = string.Empty;
        private string _maskedSummary = string.Empty;
        private bool _isMaskRevealed;

        private void EnsureSensitiveEvaluated()
        {
            if (_sensitiveEvaluated) return;
            _sensitiveEvaluated = true;

            if (IsImage || ItemType == "File" || string.IsNullOrEmpty(_content))
            {
                _isSensitive = false;
                _sensitiveBadgeKey = string.Empty;
                _maskedContent = _content;
                _maskedSummary = _summary;
                return;
            }

            // 卡片 UI 最多展示前几行，对超长文本截取前 2500 字符做检测与展示，杜绝几万字日志阻塞 UI 线程
            string sampleContent = _content.Length > 2500 ? _content.Substring(0, 2500) : _content;

            if (Id > 0 && SensitiveStateCache.TryGetValue(Id, out var cached) && cached.contentSample == sampleContent)
            {
                _isSensitive = cached.isSens;
                _sensitiveBadgeKey = cached.badgeKey;
                _maskedContent = cached.maskedContent;
                _maskedSummary = cached.maskedSummary;
                return;
            }

            var (sens, badgeKey, masked) = SensitiveDataHelper.InspectAndMask(sampleContent);
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

            if (Id > 0)
            {
                if (SensitiveStateCache.Count > 500) SensitiveStateCache.Clear();
                SensitiveStateCache[Id] = (sampleContent, _isSensitive, _sensitiveBadgeKey, _maskedContent, _maskedSummary);
            }
        }

        public bool IsSensitive
        {
            get
            {
                EnsureSensitiveEvaluated();
                return _isSensitive;
            }
        }

        public bool ShowSensitiveBadge => GlobalAutoMaskEnabled && IsSensitive;

        public string SensitiveTypeTag
        {
            get
            {
                EnsureSensitiveEvaluated();
                return string.IsNullOrEmpty(_sensitiveBadgeKey)
                    ? string.Empty
                    : ClipVault.Services.LocalizationService.Instance.Get(_sensitiveBadgeKey);
            }
        }

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

        public string DisplayContent
        {
            get
            {
                if (GlobalAutoMaskEnabled && !_isMaskRevealed && IsSensitive)
                {
                    return _maskedContent;
                }
                // 普通文本卡片 UI 展示截断至 1200 字符以内，避免超长文本撑慢 WPF TextBlock 测量（复制/粘贴仍使用完整 Content）
                return _content.Length > 1200 ? _content.Substring(0, 1200) + "..." : _content;
            }
        }

        public string DisplaySummary
        {
            get
            {
                if (GlobalAutoMaskEnabled && !_isMaskRevealed && IsSensitive)
                {
                    return _maskedSummary;
                }
                return _summary;
            }
        }

        /// <summary>
        /// 卡片底栏左侧元信息：图片与文件保留尺寸/文件名摘要；文本与代码展示行数与字符数统计，不再重复显示正文
        /// </summary>
        public string CardFooterText
        {
            get
            {
                if (IsImage || ItemType == "File")
                {
                    return DisplaySummary;
                }

                bool isEn = ClipVault.Services.LocalizationService.Instance.CurrentLanguage == "en-US";
                int len = CharCount > 0 ? CharCount : _content.Length;
                int lines = 1;
                if (!string.IsNullOrEmpty(_content))
                {
                    for (int i = 0; i < _content.Length; i++)
                    {
                        if (_content[i] == '\n') lines++;
                    }
                }

                if (IsCode)
                {
                    return isEn
                        ? (lines > 1 ? $"Code · {lines} lines · {len} chars" : $"Code · {len} chars")
                        : (lines > 1 ? $"代码 · {lines} 行 · {len} 字符" : $"代码 · {len} 字符");
                }

                return isEn
                    ? (lines > 1 ? $"{lines} lines · {len} chars" : $"{len} chars")
                    : (lines > 1 ? $"{lines} 行 · {len} 字符" : $"{len} 字符");
            }
        }

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
            OnPropertyChanged(nameof(CardFooterText));
            OnPropertyChanged(nameof(MaskToggleIcon));
            OnPropertyChanged(nameof(MaskToggleTooltip));
        }

        // UI 绑定展示辅助属性
        private string _displayBadge = string.Empty;
        public string DisplayBadge
        {
            get => _displayBadge;
            set
            {
                string next = value ?? string.Empty;
                if (_displayBadge == next) return;
                _displayBadge = next;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasBadge));
            }
        }
        public bool HasBadge => !string.IsNullOrEmpty(_displayBadge);

        public bool IsImage => ItemType == "Image";
        public bool IsCode => ItemType == "Code";
        public bool IsText => ItemType == "Text" || ItemType == "File";
        public bool IsFile => ItemType == "File";

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

                if (ThumbnailMemoryCache.TryGetValue(imgPath, out var cachedThumb))
                {
                    _cachedImage = cachedThumb;
                    return _cachedImage;
                }

                try
                {
                    // 直接通过非排他 FileStream + OnLoad 解码，省去整图二次拷贝 MemoryStream
                    using var fs = new FileStream(imgPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 400; // 核心内存优化：卡片缩略图约束至 400px 解码，内存占用骤降 95%+
                    bmp.StreamSource = fs;
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

                    if (_cachedImage != null)
                    {
                        if (ThumbnailMemoryCache.Count >= 16) ThumbnailMemoryCache.Clear();
                        ThumbnailMemoryCache[imgPath] = _cachedImage;
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

        public static void ClearThumbnailCache()
        {
            ThumbnailMemoryCache.Clear();
        }
    }
}
