using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipVault.Models;
using ClipVault.Services;
using Microsoft.Win32;

namespace ClipVault
{
    public partial class MainWindow : Window
    {
        private readonly DatabaseService _dbService;
        private readonly SettingsService _settingsService;
        private readonly ClipboardMonitorService _monitorService;
        private readonly PasteSimulator _pasteSimulator;

        private string _currentCategory = "all";
        public ObservableCollection<ClipboardItem> DisplayCards { get; } = new();

        public MainWindow(DatabaseService dbService, SettingsService settingsService, ClipboardMonitorService monitorService, PasteSimulator pasteSimulator)
        {
            InitializeComponent();
            _dbService = dbService;
            _settingsService = settingsService;
            _monitorService = monitorService;
            _pasteSimulator = pasteSimulator;

            // 加载应用图标
            try
            {
                string icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "app.ico");
                if (File.Exists(icoPath))
                {
                    this.Icon = new BitmapImage(new Uri(icoPath, UriKind.Absolute));
                }
            }
            catch { }

            CardsListBox.ItemsSource = DisplayCards;

            // 加载配置、主题与卡片
            LoadSettingsToUI();
            RefreshCards();

            // 监听后台新捕获的剪贴板条目
            _monitorService.ItemCaptured += (item) =>
            {
                Dispatcher.Invoke(() =>
                {
                    RefreshCards();
                    UpdateStorageInfo();
                });
            };

            // 失去焦点时自动隐藏（设置面板展开时除外，防止修改路径时意外关闭）
            this.Deactivated += (s, e) =>
            {
                if (SettingsOverlayCard.Visibility != Visibility.Visible)
                {
                    this.Hide();
                }
            };
        }

        private void LoadSettingsToUI()
        {
            var s = _settingsService.CurrentSettings;
            TxtSettingsStoragePath.Text = s.StorageDirectory;

            // 1. 关闭行为加载
            if (s.CloseAction == "ExitApp")
            {
                RadioCloseExit.IsChecked = true;
            }
            else
            {
                RadioCloseMinimize.IsChecked = true;
            }

            // 2. 主题与透明度加载
            bool isDark = s.ThemeMode != "Light";
            if (isDark) RadioThemeDark.IsChecked = true; else RadioThemeLight.IsChecked = true;

            SliderOpacity.Value = Math.Clamp(s.WindowOpacity, 0.55, 1.00);
            TxtOpacityValue.Text = $"{(int)(SliderOpacity.Value * 100)}%";

            ApplyTheme(isDark);
            ApplyOpacity(SliderOpacity.Value);

            UpdateStorageInfo();
        }

        private void UpdateStorageInfo()
        {
            var (dbSize, totalCount, pinnedCount, imgCount, imgSize) = _dbService.GetStats();
            TxtStorageStatusInfo.Text = $"ℹ 本地 SQLite 状态正常：已记录 {totalCount} 条 (置顶 {pinnedCount})，图片 {imgCount} 张，数据 100% 物理留存于本地磁盘。";
        }

        public void ShowAndActivate()
        {
            // 记录当前活跃的工作窗口句柄，供选定后自动模拟粘贴
            _pasteSimulator.RecordTargetWindow();

            SearchInputBox.Text = string.Empty;
            SettingsOverlayCard.Visibility = Visibility.Collapsed;
            RefreshCards();

            this.Show();
            this.Activate();
            SearchInputBox.Focus();
        }

        public void RefreshCards()
        {
            DisplayCards.Clear();
            string keyword = SearchInputBox.Text.Trim();
            var items = _dbService.GetRecentItems(keyword, _currentCategory, 60);

            for (int i = 0; i < items.Count; i++)
            {
                if (i < 9)
                {
                    items[i].DisplayBadge = (i + 1).ToString();
                }
                DisplayCards.Add(items[i]);
            }

            if (DisplayCards.Count > 0)
            {
                CardsListBox.SelectedIndex = 0;
            }
        }

        private async void TriggerPaste(ClipboardItem item)
        {
            if (item == null) return;
            await _pasteSimulator.PasteItemAsync(item, () => this.Hide());
        }

        // ================= 外观：白天/夜间主题与透明度 =================

        private void ApplyTheme(bool isDark)
        {
            _settingsService.CurrentSettings.ThemeMode = isDark ? "Dark" : "Light";
            _settingsService.SaveSettings();

            BtnQuickTheme.Content = isDark ? "☀️" : "🌙";
            BtnQuickTheme.ToolTip = isDark ? "切换为白天明亮模式" : "切换为暗黑夜间模式";
            if (isDark) RadioThemeDark.IsChecked = true; else RadioThemeLight.IsChecked = true;

            byte alpha = (byte)(_settingsService.CurrentSettings.WindowOpacity * 255);
            MainBorder.Background = isDark 
                ? new SolidColorBrush(Color.FromArgb(alpha, 22, 25, 34)) 
                : new SolidColorBrush(Color.FromArgb(alpha, 248, 250, 252));

            this.Resources["CardBgBrush"] = isDark ? new SolidColorBrush(Color.FromRgb(24, 28, 37)) : new SolidColorBrush(Color.FromRgb(255, 255, 255));
            this.Resources["CardBorderBrush"] = isDark ? new SolidColorBrush(Color.FromRgb(40, 50, 66)) : new SolidColorBrush(Color.FromRgb(226, 232, 240));
            this.Resources["CardTextBrush"] = isDark ? new SolidColorBrush(Color.FromRgb(226, 232, 240)) : new SolidColorBrush(Color.FromRgb(15, 23, 42));
            this.Resources["CodeBgBrush"] = isDark ? new SolidColorBrush(Color.FromRgb(17, 20, 27)) : new SolidColorBrush(Color.FromRgb(241, 245, 249));
            this.Resources["CodeTextBrush"] = isDark ? new SolidColorBrush(Color.FromRgb(52, 211, 153)) : new SolidColorBrush(Color.FromRgb(5, 150, 105));
            this.Resources["SubTextBrush"] = isDark ? new SolidColorBrush(Color.FromRgb(113, 128, 150)) : new SolidColorBrush(Color.FromRgb(100, 116, 139));
            this.Resources["TitleTextBrush"] = isDark ? Brushes.White : new SolidColorBrush(Color.FromRgb(15, 23, 42));
            this.Resources["WindowBorderBrush"] = isDark ? new SolidColorBrush(Color.FromRgb(51, 64, 80)) : new SolidColorBrush(Color.FromRgb(203, 213, 225));
            this.Resources["SearchBgBrush"] = isDark ? new SolidColorBrush(Color.FromRgb(19, 22, 31)) : new SolidColorBrush(Color.FromRgb(255, 255, 255));
            this.Resources["SearchBorderBrush"] = isDark ? new SolidColorBrush(Color.FromRgb(40, 50, 66)) : new SolidColorBrush(Color.FromRgb(203, 213, 225));
            this.Resources["HoverBgBrush"] = isDark 
                ? new SolidColorBrush(Color.FromArgb(32, 255, 255, 255)) 
                : new SolidColorBrush(Color.FromArgb(20, 0, 0, 0));
            this.Resources["PressedBgBrush"] = isDark 
                ? new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)) 
                : new SolidColorBrush(Color.FromArgb(35, 0, 0, 0));
            this.Resources["InfoBgBrush"] = isDark 
                ? new SolidColorBrush(Color.FromRgb(22, 34, 52)) 
                : new SolidColorBrush(Color.FromRgb(239, 246, 255));
            this.Resources["InfoBorderBrush"] = isDark 
                ? new SolidColorBrush(Color.FromRgb(30, 58, 138)) 
                : new SolidColorBrush(Color.FromRgb(191, 219, 254));
            this.Resources["InfoTextBrush"] = isDark 
                ? new SolidColorBrush(Color.FromRgb(96, 165, 250)) 
                : new SolidColorBrush(Color.FromRgb(29, 78, 216));

            UpdateCloseButtonToolTip();
            UpdateCategoryTabsAppearance();
        }

        private void UpdateCloseButtonToolTip()
        {
            if (BtnCloseWindow != null && _settingsService != null)
            {
                BtnCloseWindow.ToolTip = _settingsService.CurrentSettings.CloseAction == "ExitApp"
                    ? "关闭并彻底退出 ClipVault"
                    : "关闭窗口 (收起到托盘，快捷键可唤起)";
            }
        }

        private void ApplyOpacity(double opacity)
        {
            _settingsService.CurrentSettings.WindowOpacity = opacity;
            _settingsService.SaveSettings();

            if (TxtOpacityValue != null)
            {
                TxtOpacityValue.Text = $"{(int)(opacity * 100)}%";
            }

            bool isDark = _settingsService.CurrentSettings.ThemeMode != "Light";
            byte alpha = (byte)(opacity * 255);
            MainBorder.Background = isDark 
                ? new SolidColorBrush(Color.FromArgb(alpha, 22, 25, 34)) 
                : new SolidColorBrush(Color.FromArgb(alpha, 248, 250, 252));
        }

        private void BtnQuickTheme_Click(object sender, RoutedEventArgs e)
        {
            bool willBeDark = _settingsService.CurrentSettings.ThemeMode == "Light";
            ApplyTheme(willBeDark);
        }

        private void ThemeRadio_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingsService == null) return;
            ApplyTheme(RadioThemeDark.IsChecked == true);
        }

        private void SliderOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_settingsService == null || MainBorder == null) return;
            ApplyOpacity(e.NewValue);
        }

        // ================= 窗口控制与退出行为 =================

        private void BtnMinimizeToTray_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
        }

        private void BtnCloseWindow_Click(object sender, RoutedEventArgs e)
        {
            if (_settingsService.CurrentSettings.CloseAction == "ExitApp")
            {
                ExitApplication();
            }
            else
            {
                this.Hide();
            }
        }

        private void CloseActionRadio_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingsService == null) return;

            if (RadioCloseExit.IsChecked == true)
            {
                _settingsService.CurrentSettings.CloseAction = "ExitApp";
            }
            else
            {
                _settingsService.CurrentSettings.CloseAction = "MinimizeToTray";
            }
            _settingsService.SaveSettings();
            UpdateCloseButtonToolTip();
        }

        private void BtnDirectExit_Click(object sender, RoutedEventArgs e)
        {
            var res = MessageBox.Show("确认要彻底退出 ClipVault 吗？退出后后台将停止监听剪贴板与热键。", "确认退出", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes)
            {
                ExitApplication();
            }
        }

        private void ExitApplication()
        {
            _monitorService?.Stop();
            _dbService?.Dispose();
            Application.Current.Shutdown();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_settingsService.CurrentSettings.CloseAction == "ExitApp")
            {
                ExitApplication();
            }
            else
            {
                e.Cancel = true;
                this.Hide();
            }
        }

        // ================= 鼠标拖拽与分类操作 =================

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && e.OriginalSource is not TextBox && e.OriginalSource is not Button && e.OriginalSource is not Slider)
            {
                try { this.DragMove(); } catch { }
            }
        }

        private void BtnToggleSettings_Click(object sender, RoutedEventArgs e)
        {
            SettingsOverlayCard.Visibility = SettingsOverlayCard.Visibility == Visibility.Visible 
                ? Visibility.Collapsed 
                : Visibility.Visible;
        }

        private void BtnCloseSettingsOverlay_Click(object sender, RoutedEventArgs e)
        {
            SettingsOverlayCard.Visibility = Visibility.Collapsed;
        }

        private void TabCategory_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string cat)
            {
                _currentCategory = cat;
                UpdateCategoryTabsAppearance();
                RefreshCards();
            }
        }

        private void UpdateCategoryTabsAppearance()
        {
            if (TabAll == null) return;
            var activeBg = new SolidColorBrush(Color.FromRgb(0, 120, 212));
            var inactiveBg = (Brush)this.Resources["CardBgBrush"];
            var activeFg = Brushes.White;
            var inactiveFg = (Brush)this.Resources["SubTextBrush"];

            UpdateButtonTab(TabAll, _currentCategory == "all", activeBg, inactiveBg, activeFg, inactiveFg);
            UpdateButtonTab(TabText, _currentCategory == "Text", activeBg, inactiveBg, activeFg, inactiveFg);
            UpdateButtonTab(TabImages, _currentCategory == "Image", activeBg, inactiveBg, activeFg, inactiveFg);
            UpdateButtonTab(TabCode, _currentCategory == "Code", activeBg, inactiveBg, activeFg, inactiveFg);
            UpdateButtonTab(TabStarred, _currentCategory == "pinned", activeBg, inactiveBg, activeFg, inactiveFg);
        }

        private void UpdateButtonTab(Button btn, bool isActive, Brush activeBg, Brush inactiveBg, Brush activeFg, Brush inactiveFg)
        {
            if (btn == null) return;
            btn.Background = isActive ? activeBg : inactiveBg;
            btn.Foreground = isActive ? activeFg : inactiveFg;
            btn.FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal;
        }

        private void SearchInputBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (SearchPlaceholder != null)
            {
                SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchInputBox.Text) 
                    ? Visibility.Visible 
                    : Visibility.Collapsed;
            }
            RefreshCards();
        }

        private void SearchInputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                this.Hide();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter)
            {
                if (CardsListBox.SelectedItem is ClipboardItem selected)
                {
                    TriggerPaste(selected);
                }
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Down)
            {
                if (CardsListBox.SelectedIndex < DisplayCards.Count - 1)
                {
                    CardsListBox.SelectedIndex++;
                    CardsListBox.ScrollIntoView(CardsListBox.SelectedItem);
                }
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Up)
            {
                if (CardsListBox.SelectedIndex > 0)
                {
                    CardsListBox.SelectedIndex--;
                    CardsListBox.ScrollIntoView(CardsListBox.SelectedItem);
                }
                e.Handled = true;
                return;
            }

            // 数字键 1~9 秒贴
            if (string.IsNullOrEmpty(SearchInputBox.Text))
            {
                int index = -1;
                if (e.Key >= Key.D1 && e.Key <= Key.D9) index = e.Key - Key.D1;
                else if (e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9) index = e.Key - Key.NumPad1;

                if (index >= 0 && index < DisplayCards.Count)
                {
                    TriggerPaste(DisplayCards[index]);
                    e.Handled = true;
                }
            }
        }

        private void CardsListBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                this.Hide();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && CardsListBox.SelectedItem is ClipboardItem selected)
            {
                TriggerPaste(selected);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && CardsListBox.SelectedItem is ClipboardItem toDel)
            {
                _dbService.DeleteItem(toDel.Id);
                RefreshCards();
                UpdateStorageInfo();
                e.Handled = true;
            }
            else if (e.Key == Key.P && CardsListBox.SelectedItem is ClipboardItem toPin)
            {
                _dbService.TogglePin(toPin.Id);
                RefreshCards();
                UpdateStorageInfo();
                e.Handled = true;
            }
        }

        private void CardsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (CardsListBox.SelectedItem is ClipboardItem selected)
            {
                TriggerPaste(selected);
            }
        }

        private void PinItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int id)
            {
                _dbService.TogglePin(id);
                RefreshCards();
                UpdateStorageInfo();
            }
        }

        private void DeleteItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int id)
            {
                _dbService.DeleteItem(id);
                RefreshCards();
                UpdateStorageInfo();
            }
        }

        private void BtnBrowseStorage_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择 ClipVault 剪贴板本地数据保存目录",
                InitialDirectory = _settingsService.CurrentSettings.StorageDirectory
            };

            if (dialog.ShowDialog() == true)
            {
                string chosenPath = dialog.FolderName;
                if (_settingsService.UpdateStorageDirectory(chosenPath, true))
                {
                    _dbService.SwitchStorageDirectory(chosenPath);
                    TxtSettingsStoragePath.Text = chosenPath;
                    RefreshCards();
                    UpdateStorageInfo();

                    MessageBox.Show($"存储路径已成功切换为：\n{chosenPath}\n\n现有历史数据与图片已平滑迁移！", "路径切换成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }
    }
}