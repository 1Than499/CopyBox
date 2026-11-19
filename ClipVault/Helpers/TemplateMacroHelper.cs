using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using ClipVault.Services;

namespace ClipVault.Helpers
{
    public sealed record TemplateInputPrompt(string Key, string Label, string DefaultValue);

    /// <summary>
    /// v3.1.5 智能模板动态占位符解析引擎
    /// 支持内置宏 {{date}}, {{time}}, {{datetime}}, {{year}}, {{weekday}}, {{guid}}, {{uuid}}, {{clipboard}}
    /// 以及交互式填空变量 {{input:字段名}} 或 {{input:字段名=默认值}}。
    /// 采用单遍正则替换 (Single-Pass MatchEvaluator)，彻底免疫递归死循环与嵌套变量注入 Bug。
    /// </summary>
    public static class TemplateMacroHelper
    {
        private static readonly Regex MacroRegex = new Regex(
            @"\{\{\s*(?:(date|time|datetime|year|weekday|guid|uuid|clipboard)|input:([^}]+?))\s*\}\}",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool ContainsMacro(string? text)
        {
            if (string.IsNullOrEmpty(text) || !text.Contains("{{"))
                return false;
            return MacroRegex.IsMatch(text);
        }

        public static bool HasInputPrompts(string? text)
        {
            return ExtractInputPrompts(text).Count > 0;
        }

        /// <summary>
        /// 按出现顺序提取所有去重后的 {{input:字段名}} 或 {{input:字段名=默认值}} 填空项
        /// </summary>
        public static List<TemplateInputPrompt> ExtractInputPrompts(string? text)
        {
            var result = new List<TemplateInputPrompt>();
            if (string.IsNullOrEmpty(text) || !text.Contains("{{"))
                return result;

            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in MacroRegex.Matches(text))
            {
                if (!m.Groups[2].Success) continue;
                var (key, label, defVal) = ParseInputSpec(m.Groups[2].Value);
                if (string.IsNullOrWhiteSpace(key) || !seenKeys.Add(key)) continue;
                result.Add(new TemplateInputPrompt(key, label, defVal));
            }
            return result;
        }

        private static (string key, string label, string defaultValue) ParseInputSpec(string rawSpec)
        {
            string spec = rawSpec.Trim();
            int eqIdx = spec.IndexOf('=');
            if (eqIdx > 0)
            {
                string label = spec.Substring(0, eqIdx).Trim();
                string defVal = spec.Substring(eqIdx + 1).Trim();
                return (label, label, defVal);
            }
            return (spec, spec, string.Empty);
        }

        /// <summary>
        /// 单遍安全展开文本中的所有模板变量（包含可选的交互式输入字典）
        /// </summary>
        public static string ExpandMacros(
            string? text,
            string? priorClipboardText = null,
            IReadOnlyDictionary<string, string>? inputValues = null)
        {
            if (string.IsNullOrEmpty(text) || !ContainsMacro(text))
                return text ?? string.Empty;

            DateTime now = DateTime.Now;
            string? cachedClip = priorClipboardText;
            bool clipFetched = priorClipboardText != null;

            return MacroRegex.Replace(text, match =>
            {
                if (match.Groups[2].Success)
                {
                    var (key, _, defVal) = ParseInputSpec(match.Groups[2].Value);
                    if (inputValues != null && inputValues.TryGetValue(key, out string? supplied))
                    {
                        return supplied ?? string.Empty;
                    }
                    return defVal;
                }

                string macroKey = match.Groups[1].Value.Trim().ToLowerInvariant();
                switch (macroKey)
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
