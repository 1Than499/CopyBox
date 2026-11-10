using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using ClipVault.Services;

namespace ClipVault.Helpers
{
    /// <summary>
    /// v3.0 智能模板动态占位符解析引擎
    /// 支持 {{date}}, {{time}}, {{datetime}}, {{year}}, {{weekday}}, {{guid}}, {{uuid}}, {{clipboard}}
    /// 采用单遍正则替换 (Single-Pass MatchEvaluator)，彻底免疫递归死循环与嵌套变量注入 Bug。
    /// </summary>
    public static class TemplateMacroHelper
    {
        private static readonly Regex MacroRegex = new Regex(
            @"\{\{\s*(date|time|datetime|year|weekday|guid|uuid|clipboard)\s*\}\}",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool ContainsMacro(string? text)
        {
            if (string.IsNullOrEmpty(text) || !text.Contains("{{"))
                return false;
            return MacroRegex.IsMatch(text);
        }

        /// <summary>
        /// 单遍安全展开文本中的所有模板变量
        /// </summary>
        public static string ExpandMacros(string? text, string? priorClipboardText = null)
        {
            if (string.IsNullOrEmpty(text) || !ContainsMacro(text))
                return text ?? string.Empty;

            DateTime now = DateTime.Now;
            string? cachedClip = priorClipboardText;
            bool clipFetched = priorClipboardText != null;

            return MacroRegex.Replace(text, match =>
            {
                string key = match.Groups[1].Value.Trim().ToLowerInvariant();
                switch (key)
                {
                    case "date":
                        return now.ToString("yyyy-MM-dd");
                    case "time":
                        return now.ToString("HH:mm:ss");
                    case "datetime":
                        return now.ToString("yyyy-MM-dd HH:mm:ss");
                    case "year":
                        return now.Year.ToString();
                    case "weekday":
                        try
                        {
                            string lang = LocalizationService.Instance.CurrentLanguage == "en-US" ? "en-US" : "zh-CN";
                            return now.ToString("dddd", new CultureInfo(lang));
                        }
                        catch
                        {
                            return now.DayOfWeek.ToString();
                        }
                    case "guid":
                    case "uuid":
                        return Guid.NewGuid().ToString();
                    case "clipboard":
                        if (!clipFetched)
                        {
                            clipFetched = true;
                            try
                            {
                                if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
                                {
                                    cachedClip = Application.Current.Dispatcher.Invoke(() =>
                                        Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty);
                                }
                                else
                                {
                                    cachedClip = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
                                }
                            }
                            catch
                            {
                                cachedClip = string.Empty;
                            }
                        }
                        // 防自引用：若当前系统剪贴板恰巧就是该模板原文自身，避免将 {{clipboard}} 原样塞回
                        if (string.Equals(cachedClip, text, StringComparison.Ordinal))
                        {
                            return string.Empty;
                        }
                        return cachedClip ?? string.Empty;
                    default:
                        return match.Value;
                }
            });
        }
    }
}
