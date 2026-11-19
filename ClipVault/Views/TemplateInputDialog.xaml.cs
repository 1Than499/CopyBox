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
                TxtDialogTitle.Text = "⚡ Fill Template Variables";
                TxtDialogSub.Text = "Enter values below and press Enter to expand & paste:";
                BtnCancel.Content = "Cancel (Esc)";
                BtnConfirm.Content = "Confirm & Paste (Enter)";
            }

            foreach (var p in prompts)
            {
                var label = new TextBlock
                {
                    Text = p.Label,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)),
                    Margin = new Thickness(0, 0, 0, 4)
                };
                var box = new TextBox
                {
                    Text = p.DefaultValue ?? string.Empty,
                    FontSize = 12,
                    Padding = new Thickness(8, 5, 8, 5),
                    Margin = new Thickness(0, 0, 0, 10),
                    Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B)),
                    Foreground = Brushes.White,
                    CaretBrush = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69)),
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
