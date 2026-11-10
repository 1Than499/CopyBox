using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ClipVault.Helpers
{
    public static class CodeHighlighter
    {
        private static readonly Regex CodeTokenRegex = new(
            @"(?<Key>""[^""\r\n]+""\s*:)|(?<String>""[^""\r\n]*"")|(?<Brace>[{}[\],:])|(?<Number>\b\d+(\.\d+)?\b)|(?<Keyword>\b(true|false|null|const|let|var|function|return|if|else|import|export)\b)|(?<Other>[^""{}[\],:\d\s]+|\s+)",
            RegexOptions.Compiled);

        private static readonly SolidColorBrush KeyBrush;
        private static readonly SolidColorBrush StrBrush;
        private static readonly SolidColorBrush BraceBrush;
        private static readonly SolidColorBrush NumBrush;
        private static readonly SolidColorBrush DefBrush;

        static CodeHighlighter()
        {
            KeyBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248));   // #38BDF8 天蓝键名
            StrBrush = new SolidColorBrush(Color.FromRgb(251, 146, 60));   // #FB923C 浅橙字符串
            BraceBrush = new SolidColorBrush(Color.FromRgb(251, 191, 36)); // #FBBF24 金黄括号
            NumBrush = new SolidColorBrush(Color.FromRgb(167, 139, 250));  // #A78BFA 淡紫数字/布尔
            DefBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225));  // #CBD5E1 浅灰标点
            KeyBrush.Freeze();
            StrBrush.Freeze();
            BraceBrush.Freeze();
            NumBrush.Freeze();
            DefBrush.Freeze();
        }

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

            // 卡片可视区域最多展示 4~5 行，截取前 420 字符并一次性批量添加 Run，消除逐个 Add 触发的布局风暴
            if (text.Length > 420)
            {
                text = text.Substring(0, 420) + "...";
            }

            var matches = CodeTokenRegex.Matches(text);
            var runs = new List<Inline>(matches.Count + 2);

            foreach (Match m in matches)
            {
                if (m.Groups["Key"].Success)
                {
                    string kVal = m.Value;
                    int colonIdx = kVal.LastIndexOf(':');
                    if (colonIdx > 0)
                    {
                        runs.Add(new Run(kVal.Substring(0, colonIdx)) { Foreground = KeyBrush });
                        runs.Add(new Run(":") { Foreground = DefBrush });
                    }
                    else
                    {
                        runs.Add(new Run(kVal) { Foreground = KeyBrush });
                    }
                }
                else if (m.Groups["String"].Success)
                {
                    runs.Add(new Run(m.Value) { Foreground = StrBrush });
                }
                else if (m.Groups["Brace"].Success)
                {
                    runs.Add(new Run(m.Value) { Foreground = BraceBrush, FontWeight = FontWeights.SemiBold });
                }
                else if (m.Groups["Number"].Success || m.Groups["Keyword"].Success)
                {
                    runs.Add(new Run(m.Value) { Foreground = NumBrush });
                }
                else
                {
                    runs.Add(new Run(m.Value) { Foreground = DefBrush });
                }
            }

            tb.Inlines.AddRange(runs);
        }
    }
}
