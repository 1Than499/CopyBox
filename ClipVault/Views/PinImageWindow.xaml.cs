using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipVault.Helpers;
using ClipVault.Services;

namespace ClipVault.Views
{
    /// <summary>
    /// v3.1.5 轻量级桌面屏幕贴图悬浮窗（拖拽移动、滚轮缩放、Ctrl+滚轮调透明度、双击/Esc关闭并彻底释放位图）
    /// </summary>
    public partial class PinImageWindow : Window
    {
        private readonly string _imagePath;
        private readonly ClipboardMonitorService? _monitorService;
        private BitmapImage? _bitmap;
        private double _baseWidth;
        private double _baseHeight;
        private double _scale = 1.0;

        public PinImageWindow(string imagePath, ClipboardMonitorService? monitorService = null)
        {
            InitializeComponent();
            _imagePath = imagePath;
            _monitorService = monitorService;
            LoadBitmap();
            BuildContextMenu();
        }

        private void LoadBitmap()
        {
            if (string.IsNullOrWhiteSpace(_imagePath) || !File.Exists(_imagePath))
            {
                Close();
                return;
            }

            try
            {
                using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = fs;
                bmp.EndInit();
                bmp.Freeze();

                _bitmap = bmp;
                PinImage.Source = bmp;

                double maxW = SystemParameters.PrimaryScreenWidth * 0.6;
                double maxH = SystemParameters.PrimaryScreenHeight * 0.6;
                double fitScale = Math.Min(1.0, Math.Min(maxW / Math.Max(1, bmp.PixelWidth), maxH / Math.Max(1, bmp.PixelHeight)));
                _baseWidth = Math.Max(80, bmp.PixelWidth * fitScale);
                _baseHeight = Math.Max(60, bmp.PixelHeight * fitScale);

                ApplyScale(1.0);
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            catch
            {
                Close();
            }
        }

        private void ApplyScale(double newScale)
        {
            _scale = Math.Clamp(newScale, 0.25, 3.5);
            PinImage.Width = Math.Round(_baseWidth * _scale);
            PinImage.Height = Math.Round(_baseHeight * _scale);
            TxtScaleBadge.Text = $"{Math.Round(_scale * 100)}%";
        }

        private void BuildContextMenu()
        {
            var menu = new ContextMenu();
            if (Application.Current?.TryFindResource("ModernContextMenuStyle") is Style ctxStyle) menu.Style = ctxStyle;
            Style? itemStyle = Application.Current?.TryFindResource("ModernMenuItemStyle") as Style;
            Style? sepStyle = Application.Current?.TryFindResource("ModernMenuSeparatorStyle") as Style;
            bool isEn = LocalizationService.Instance.CurrentLanguage == "en-US";

            var copyItem = new MenuItem { Icon = ThemePalette.CreateMenuGlyph("\uE8C8"), Header = isEn ? "Copy Image" : "复制原图" };
            if (itemStyle != null) copyItem.Style = itemStyle;
            copyItem.Click += (_, _) => CopyPinnedImage();

            var resetItem = new MenuItem { Icon = ThemePalette.CreateMenuGlyph("\uE72C"), Header = isEn ? "Reset Size (100%)" : "恢复初始大小" };
            if (itemStyle != null) resetItem.Style = itemStyle;
            resetItem.Click += (_, _) =>
            {
                this.Opacity = 1.0;
                ApplyScale(1.0);
            };

            var halfAlphaItem = new MenuItem { Icon = ThemePalette.CreateMenuGlyph("\uE793"), Header = isEn ? "Toggle 55% Opacity" : "半透明对照" };
            if (itemStyle != null) halfAlphaItem.Style = itemStyle;
            halfAlphaItem.Click += (_, _) =>
            {
                this.Opacity = this.Opacity < 0.85 ? 1.0 : 0.55;
            };

            var closeItem = new MenuItem
            {
                Icon = ThemePalette.CreateMenuGlyph("\uE711", new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44))),
                Header = isEn ? "Close" : "关闭贴图"
            };
            if (itemStyle != null) closeItem.Style = itemStyle;
            closeItem.Click += (_, _) => Close();

            var sep = new Separator();
            if (sepStyle != null) sep.Style = sepStyle;

            menu.Items.Add(copyItem);
            menu.Items.Add(resetItem);
            menu.Items.Add(halfAlphaItem);
            menu.Items.Add(sep);
            menu.Items.Add(closeItem);
            this.ContextMenu = menu;
        }

        private void CopyPinnedImage()
        {
            if (_bitmap == null) return;
            try
            {
                _monitorService?.NotifyInternalImageWrite(_bitmap.PixelWidth, _bitmap.PixelHeight, 2500);
                Clipboard.SetImage(_bitmap);
            }
            catch { }
        }

        private void FrameBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                Close();
                return;
            }
            try
            {
                DragMove();
            }
            catch { }
        }

        private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                double nextOpacity = this.Opacity + (e.Delta > 0 ? 0.08 : -0.08);
                this.Opacity = Math.Clamp(nextOpacity, 0.25, 1.0);
            }
            else
            {
                double step = e.Delta > 0 ? 1.1 : 0.9;
                ApplyScale(_scale * step);
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        }

        private void Window_MouseEnter(object sender, MouseEventArgs e)
        {
            HoverBar.Visibility = Visibility.Visible;
            FrameBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x52, 0x52, 0x5B));
        }

        private void Window_MouseLeave(object sender, MouseEventArgs e)
        {
            HoverBar.Visibility = Visibility.Collapsed;
            FrameBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x27, 0x27, 0x2E));
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            CopyPinnedImage();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            PinImage.Source = null;
            _bitmap = null;
            base.OnClosed(e);
            MemoryHelper.ScheduleIdleTrim(1500);
        }
    }
}
