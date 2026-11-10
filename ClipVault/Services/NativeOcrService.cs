using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace ClipVault.Services
{
    /// <summary>
    /// v3.0 Windows 原生离线 OCR 识别服务
    /// 直接调用 Windows 10/11 系统内置 Windows.Media.Ocr 引擎：
    /// 0MB 外部依赖体积、100% 本地离线运算、自动释放 SoftwareBitmap 非托管内存
    /// </summary>
    public static class NativeOcrService
    {
        private static OcrEngine? _cachedEngine;
        private static bool _engineInitialized = false;
        private static readonly object _initLock = new();
        private static readonly SemaphoreSlim _ocrConcurrencyGate = new(1, 1);

        // 消除 Windows OCR 中文单字之间多余空格的正则（仅合并连续汉字之间的空格，保留英文单词空格）
        private static readonly Regex CjkInterCharSpaceRegex = new(
            @"(?<=[\u4e00-\u9fff\u3000-\u303f\uff00-\uffef])\s+(?=[\u4e00-\u9fff\u3000-\u303f\uff00-\uffef])",
            RegexOptions.Compiled);

        private static OcrEngine? GetOrCreateEngine()
        {
            if (_engineInitialized) return _cachedEngine;

            lock (_initLock)
            {
                if (_engineInitialized) return _cachedEngine;
                try
                {
                    // 优先尝试优先匹配简体中文或当前用户系统语言配置
                    _cachedEngine = OcrEngine.TryCreateFromUserProfileLanguages();

                    if (_cachedEngine == null)
                    {
                        var available = OcrEngine.AvailableRecognizerLanguages;
                        var zhLang = available?.FirstOrDefault(l => l.LanguageTag.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
                        if (zhLang != null)
                        {
                            _cachedEngine = OcrEngine.TryCreateFromLanguage(zhLang);
                        }
                        else if (available != null && available.Count > 0)
                        {
                            _cachedEngine = OcrEngine.TryCreateFromLanguage(available[0]);
                        }
                    }
                }
                catch
                {
                    _cachedEngine = null;
                }
                finally
                {
                    _engineInitialized = true;
                }
                return _cachedEngine;
            }
        }

        /// <summary>
        /// 异步从本地图片文件路径提取离线 OCR 文字（异常安全，失败返回空字符串）
        /// </summary>
        public static async Task<string> RecognizeImageFileAsync(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                return string.Empty;
            }

            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(imagePath).ConfigureAwait(false);
                return await RecognizeImageBytesAsync(bytes).ConfigureAwait(false);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 异步从图片字节流提取离线 OCR 文字（带并发门限保护与大尺寸防溢出检查）
        /// </summary>
        public static async Task<string> RecognizeImageBytesAsync(byte[] imageBytes)
        {
            if (imageBytes == null || imageBytes.Length == 0) return string.Empty;

            var engine = GetOrCreateEngine();
            if (engine == null) return string.Empty;

            await _ocrConcurrencyGate.WaitAsync().ConfigureAwait(false);
            try
            {
                using var ms = new MemoryStream(imageBytes, writable: false);
                using IRandomAccessStream ras = ms.AsRandomAccessStream();

                var decoder = await BitmapDecoder.CreateAsync(ras);
                uint width = decoder.PixelWidth;
                uint height = decoder.PixelHeight;

                // Windows.Media.Ocr 要求宽高大于 4px 且不超过 MaxImageDimension (通常为 4096)
                uint maxDim = OcrEngine.MaxImageDimension;
                if (width < 4 || height < 4 || width > maxDim || height > maxDim)
                {
                    return string.Empty;
                }

                using SoftwareBitmap softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied);

                var ocrResult = await engine.RecognizeAsync(softwareBitmap);
                if (ocrResult == null || ocrResult.Lines == null || ocrResult.Lines.Count == 0)
                {
                    return string.Empty;
                }

                var sb = new StringBuilder();
                foreach (var line in ocrResult.Lines)
                {
                    if (string.IsNullOrWhiteSpace(line.Text)) continue;
                    string cleanedLine = CjkInterCharSpaceRegex.Replace(line.Text.Trim(), string.Empty);
                    if (!string.IsNullOrEmpty(cleanedLine))
                    {
                        if (sb.Length > 0) sb.AppendLine();
                        sb.Append(cleanedLine);
                    }
                }

                return sb.ToString().Trim();
            }
            catch
            {
                return string.Empty;
            }
            finally
            {
                _ocrConcurrencyGate.Release();
            }
        }
    }
}
