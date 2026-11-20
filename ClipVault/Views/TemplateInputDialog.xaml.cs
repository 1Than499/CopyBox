using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClipVault.Helpers;
using ClipVault.Services;

namespace ClipVault.Views
{
    /// <summary>
    /// v3.1.5 交互式 {{input:字段名}} 动态模板参数采集弹窗
    /// </summary>
    public partial class TemplateInputDialog : Window
    {
        private readonly List<(TemplateInputPrompt Prompt, TextBox Box)> _fields = new();
        public Dictionary<string, string>? CollectedValues { get; private set; }

        public TemplateInputDialog(IReadOnlyList<TemplateInputPrompt> prompts)
        {
            InitializeComponent();
            bool isEn = LocalizationService.Instance.CurrentLanguage == "en-US";
            if (isEn)
            {
                TxtDialogTitle.Text = "Template Variables";
                TxtDialogSub.Text = string.Empty;
                BtnCancel.Content = "Cancel";
                BtnConfirm.Content = "Confirm";
            }

            foreach (var p in prompts)
            {
                var label = new TextBlock
                {
                    Text = p.Label,
                    FontSize = 11,
                    FontWeight = FontWeights.Medium,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x96)),
                    Margin = new Thickness(0, 0, 0, 4)
                };
                var box = new TextBox
                {
                    Text = p.DefaultValue ?? string.Empty,
                    FontSize = 12,
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 0, 0, 10),
                    Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x18)),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF5)),
                    CaretBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x27, 0x27, 0x2E)),
                    BorderThickness = new Thickness(1)
                };
                InputsHost.Children.Add(label);
                InputsHost.Children.Add(box);
                _fields.Add((p, box));
            }

            Loaded += (_, _) =>
            {
                Activate();
                if (_fields.Count > 0)
                {
                    _fields[0].Box.Focus();
                    _fields[0].Box.SelectAll();
                }
            };
        }

        public static bool IsAnyDialogOpen { get; private set; }

        public static Dictionary<string, string>? PromptForInputs(IReadOnlyList<TemplateInputPrompt> prompts)
        {
            if (prompts == null || prompts.Count == 0) return new Dictionary<string, string>();
            IsAnyDialogOpen = true;
            try
            {
                var dlg = new TemplateInputDialog(prompts);
                bool? res = dlg.ShowDialog();
                return res == true ? dlg.CollectedValues : null;
            }
            finally
            {
                IsAnyDialogOpen = false;
            }
        }

        private void BtnConfirm_Click(object sender, RoutedEventArgs e)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (prompt, box) in _fields)
            {
                dict[prompt.Key] = box.Text ?? string.Empty;
            }
            CollectedValues = dict;
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
            }
        }
    }
}
