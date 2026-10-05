using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

            CardsListBox.ItemsSource = DisplayCards;

            // 加载配置
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

            // 痛点 1 配置加载
            if (s.CloseAction == "ExitApp")
            {
                RadioCloseExit.IsChecked = true;
            }
            else
            {
                RadioCloseMinimize.IsChecked = true;
            }

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

        // ================= 痛点 1：窗口控制与退出行为 =================

        private void BtnMinimizeToTray_Click(object sender, RoutedEventArgs e)
        {
            // 显式最小化到托盘
            this.Hide();
        }

        private void BtnCloseWindow_Click(object sender, RoutedEventArgs e)
        {
            // 根据用户的自选偏好决定是退出还是最小化到托盘
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

        // ================= 痛点 2：UI 交互与双列卡片操作 =================

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // 支持拖拽窗口
            if (e.LeftButton == MouseButtonState.Pressed && e.OriginalSource is not TextBox && e.OriginalSource is not Button)
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

                // 更新高亮胶囊样式
                var activeBg = new SolidColorBrush(Color.FromRgb(0, 120, 212));
                var inactiveBg = new SolidColorBrush(Color.FromRgb(29, 34, 44));
                var activeFg = Brushes.White;
                var inactiveFg = new SolidColorBrush(Color.FromRgb(153, 164, 181));

                UpdateButtonTab(TabAll, cat == "all", activeBg, inactiveBg, activeFg, inactiveFg);
                UpdateButtonTab(TabText, cat == "Text", activeBg, inactiveBg, activeFg, inactiveFg);
                UpdateButtonTab(TabImages, cat == "Image", activeBg, inactiveBg, activeFg, inactiveFg);
                UpdateButtonTab(TabCode, cat == "Code", activeBg, inactiveBg, activeFg, inactiveFg);
                UpdateButtonTab(TabStarred, cat == "pinned", activeBg, inactiveBg, activeFg, inactiveFg);

                RefreshCards();
            }
        }

        private void UpdateButtonTab(Button btn, bool isActive, Brush activeBg, Brush inactiveBg, Brush activeFg, Brush inactiveFg)
        {
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