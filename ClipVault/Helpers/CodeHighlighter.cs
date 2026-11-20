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

        public static readonly DependencyProperty HighlightCodeProperty =
            DependencyProperty.RegisterAttached(
                "HighlightCode",
                typeof(string),
                typeof(CodeHighlighter),
                new PropertyMetadata(null, OnHighlightCodeChanged));

        public static string GetHighlightCode(DependencyObject obj) => (string)obj.GetValue(HighlightCodeProperty);
        public static void SetHighlightCode(DependencyObject obj, string value) => obj.SetValue(HighlightCodeProperty, value);

        private static Run MakeRun(string text, string brushKey, bool bold = false)
        {
            var run = new Run(text);
            run.SetResourceReference(TextElement.ForegroundProperty, brushKey);
            if (bold) run.FontWeight = FontWeights.SemiBold;
            return run;
        }

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
                        runs.Add(MakeRun(kVal.Substring(0, colonIdx), "CodeKeyBrush"));
                        runs.Add(MakeRun(":", "TextSecondaryBrush"));
                    }
                    else
                    {
                        runs.Add(MakeRun(kVal, "CodeKeyBrush"));
                    }
                }
                else if (m.Groups["String"].Success)
                {
                    runs.Add(MakeRun(m.Value, "CodeStrBrush"));
                }
                else if (m.Groups["Brace"].Success)
                {
                    runs.Add(MakeRun(m.Value, "CodeBraceBrush", bold: true));
                }
                else if (m.Groups["Number"].Success || m.Groups["Keyword"].Success)
                {
                    runs.Add(MakeRun(m.Value, "CodeNumBrush"));
                }
                else
                {
                    runs.Add(MakeRun(m.Value, "TextPrimaryBrush"));
                }
            }

            tb.Inlines.AddRange(runs);
        }
    }
}
