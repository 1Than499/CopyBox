using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using ClipVault.Models;
using ClipVault.Services;
using Microsoft.Win32;

namespace ClipVault
{
    public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
    {
        private readonly DatabaseService _dbService;
        private readonly SettingsService _settingsService;
        private readonly ClipboardMonitorService _monitorService;
        public ObservableCollection<ClipboardItem> HistoryItems { get; } = new();

        public MainWindow(DatabaseService dbService, SettingsService settingsService, ClipboardMonitorService monitorService)
        {
            InitializeComponent();
            _dbService = dbService;
            _settingsService = settingsService;
            _monitorService = monitorService;

            MainHistoryList.ItemsSource = HistoryItems;

            LoadSettingsToUI();
            RefreshHistoryList();
            UpdateStorageStats();

            // 监听数据变动，自动更新统计
            _monitorService.ItemCaptured += (item) =>
            {
                Dispatcher.Invoke(() =>
                {
                    RefreshHistoryList();
                    UpdateStorageStats();
                });
            };
        }

        private void LoadSettingsToUI()
        {
            var s = _settingsService.CurrentSettings;
            StoragePathTextBox.Text = s.StorageDirectory;
            ChkIgnorePassword.IsChecked = s.IgnorePasswordManagers;
            ChkStartWithWindows.IsChecked = s.StartWithWindows;
        }

        public void RefreshHistoryList()
        {
            HistoryItems.Clear();
            string keyword = MainSearchBox.Text.Trim();
            var items = _dbService.GetRecentItems(keyword, null, 200);
            foreach (var it in items)
            {
                HistoryItems.Add(it);
            }
        }

        private void UpdateStorageStats()
        {
            var (dbSize, totalCount, pinnedCount, imgCount, imgSize) = _dbService.GetStats();
            TxtDbSize.Text = $"{(dbSize / 1024.0 / 1024.0):F2} MB";
            TxtDbCounts.Text = $"{totalCount} 条 (置顶 {pinnedCount} 条)";
            TxtImgSize.Text = $"{(imgSize / 1024.0 / 1024.0):F2} MB";
            TxtImgCounts.Text = $"{imgCount} 张图片";
        }

        private void MainSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshHistoryList();
        }

        private void MainHistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MainHistoryList.SelectedItem is ClipboardItem item)
            {
                DetailHeaderTitle.Text = item.Summary;
                DetailMetaApp.Text = $"来源应用: {item.SourceApp} ({item.SourceProcess})";
                DetailMetaTime.Text = $"捕获时间: {item.CreatedAt:yyyy-MM-dd HH:mm:ss}";
                DetailMetaChars.Text = item.IsImage ? $"大小: {item.CharCount / 1024} KB" : $"字符数: {item.CharCount}";

                if (item.IsImage && File.Exists(item.FullImagePath))
                {
                    DetailContentText.Visibility = Visibility.Collapsed;
                    DetailContentImage.Visibility = Visibility.Visible;
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.UriSource = new Uri(item.FullImagePath, UriKind.Absolute);
                        bmp.EndInit();
                        DetailContentImage.Source = bmp;
                    }
                    catch
                    {
                        DetailContentImage.Source = null;
                    }
                }
                else
                {
                    DetailContentImage.Visibility = Visibility.Collapsed;
                    DetailContentText.Visibility = Visibility.Visible;
                    DetailContentText.Text = item.Content;
                }
            }
        }

        private void BtnDetailCopy_Click(object sender, RoutedEventArgs e)
        {
            if (MainHistoryList.SelectedItem is ClipboardItem item)
            {
                _monitorService.IsInternalOperation = true;
                if (item.IsImage && File.Exists(item.FullImagePath))
                {
                    var bmp = new BitmapImage(new Uri(item.FullImagePath, UriKind.Absolute));
                    Clipboard.SetImage(bmp);
                }
                else
                {
                    Clipboard.SetText(item.Content);
                }
                MessageBox.Show("已成功将选定条目复制回系统剪贴板！", "ClipVault", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnDetailPin_Click(object sender, RoutedEventArgs e)
        {
            if (MainHistoryList.SelectedItem is ClipboardItem item)
            {
                _dbService.TogglePin(item.Id);
                RefreshHistoryList();
                UpdateStorageStats();
            }
        }

        private void BtnDetailDelete_Click(object sender, RoutedEventArgs e)
        {
            if (MainHistoryList.SelectedItem is ClipboardItem item)
            {
                _dbService.DeleteItem(item.Id);
                RefreshHistoryList();
                UpdateStorageStats();
                DetailHeaderTitle.Text = "请选择左侧条目查看详情";
                DetailContentText.Text = string.Empty;
                DetailContentImage.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnBrowseStoragePath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择 ClipVault 剪贴板本地数据保存目录",
                InitialDirectory = _settingsService.CurrentSettings.StorageDirectory
            };

            if (dialog.ShowDialog() == true)
            {
                string chosenPath = dialog.FolderName;
                bool migrate = ChkAutoMigrate.IsChecked == true;

                if (_settingsService.UpdateStorageDirectory(chosenPath, migrate))
                {
                    // 重新连接数据库
                    _dbService.SwitchStorageDirectory(chosenPath);
                    StoragePathTextBox.Text = chosenPath;
                    RefreshHistoryList();
                    UpdateStorageStats();

                    MessageBox.Show($"存储路径已成功切换为：\n{chosenPath}\n\n{(migrate ? "现有历史数据与图片已完成同步迁移。" : "已在该目录初始化全新数据存储。")}",
                        "路径切换成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        private void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
        {
            var s = _settingsService.CurrentSettings;
            s.IgnorePasswordManagers = ChkIgnorePassword.IsChecked == true;
            s.StartWithWindows = ChkStartWithWindows.IsChecked == true;
            _settingsService.SaveSettings();

            MessageBox.Show("配置已成功保存生效！", "ClipVault", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnClearAll_Click(object sender, RoutedEventArgs e)
        {
            var res = MessageBox.Show("确认要清空所有未置顶的本地历史记录吗？此操作无法撤销。", "警告", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res == MessageBoxResult.Yes)
            {
                _dbService.ClearAll();
                RefreshHistoryList();
                UpdateStorageStats();
                MessageBox.Show("已成功清理！", "ClipVault", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        // 窗口关闭时改为隐藏，避免主进程意外退出
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            this.Hide();
        }
    }
}