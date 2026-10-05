using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ClipVault.Helpers
{
    public static class CodeHighlighter
    {
        public static readonly DependencyProperty HighlightCodeProperty =
            DependencyProperty.RegisterAttached(
                "HighlightCode",
                typeof(string),
                typeof(CodeHighlighter),
                new PropertyMetadata(null, OnHighlightCodeChanged));

        public static string GetHighlightCode(DependencyObject obj) => (string)obj.GetValue(HighlightCodeProperty);
        public static void SetHighlightCode(DependencyObject obj, string value) => obj.SetValue(HighlightCodeProperty, value);

        private static void OnHighlightCodeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TextBlock tb) return;
            string text = e.NewValue as string ?? string.Empty;
            tb.Inlines.Clear();
            if (string.IsNullOrEmpty(text)) return;

            // 限制前 1000 字符展示，保障极致秒级流畅渲染
            if (text.Length > 1000)
            {
                text = text.Substring(0, 1000) + "...";
            }

            // 语法高亮微扫描器：精准支持 JSON 与主流代码语法 (1:1 概念图质感)
            var pattern = @"(?<Key>""[^""\r\n]+""\s*:)|(?<String>""[^""\r\n]*"")|(?<Brace>[{}[\],:])|(?<Number>\b\d+(\.\d+)?\b)|(?<Keyword>\b(true|false|null|const|let|var|function|return|if|else|import|export)\b)|(?<Other>[^""{}[\],:\d\s]+|\s+)";
            var matches = Regex.Matches(text, pattern, RegexOptions.Compiled);

            var keyBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248));      // #38BDF8 天蓝键名
            var strBrush = new SolidColorBrush(Color.FromRgb(251, 146, 60));      // #FB923C 浅橙字符串
            var braceBrush = new SolidColorBrush(Color.FromRgb(251, 191, 36));    // #FBBF24 金黄括号
            var numBrush = new SolidColorBrush(Color.FromRgb(167, 139, 250));     // #A78BFA 淡紫数字/布尔
            var defBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225));     // #CBD5E1 浅灰标点

            foreach (Match m in matches)
            {
                if (m.Groups["Key"].Success)
                {
                    string kVal = m.Value;
                    int colonIdx = kVal.LastIndexOf(':');
                    if (colonIdx > 0)
                    {
                        tb.Inlines.Add(new Run(kVal.Substring(0, colonIdx)) { Foreground = keyBrush });
                        tb.Inlines.Add(new Run(":") { Foreground = defBrush });
                    }
                    else
                    {
                        tb.Inlines.Add(new Run(kVal) { Foreground = keyBrush });
                    }
                }
                else if (m.Groups["String"].Success)
                {
                    tb.Inlines.Add(new Run(m.Value) { Foreground = strBrush });
                }
                else if (m.Groups["Brace"].Success)
                {
                    tb.Inlines.Add(new Run(m.Value) { Foreground = braceBrush, FontWeight = FontWeights.SemiBold });
                }
                else if (m.Groups["Number"].Success || m.Groups["Keyword"].Success)
                {
                    tb.Inlines.Add(new Run(m.Value) { Foreground = numBrush });
                }
                else
                {
                    tb.Inlines.Add(new Run(m.Value) { Foreground = defBrush });
                }
            }
        }
    }
}
