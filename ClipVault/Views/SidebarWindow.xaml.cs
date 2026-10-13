using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using ClipVault.Models;
using ClipVault.Services;
using Microsoft.Win32;

namespace ClipVault.Views
{
    public partial class SidebarWindow : Window
    {
        private readonly DatabaseService _dbService;
        private readonly SettingsService _settingsService;
        private readonly ClipboardMonitorService _monitorService;
        private readonly PasteSimulator _pasteSimulator;
        private readonly SystemMediaService _mediaService;

        public ObservableCollection<ClipboardItem> DisplayCards { get; } = new();

        private string _currentCategory = "all";
        private bool _isPinned = true;
        private bool _isBluetoothActive = true;
        private Point _cardDragStartPoint;
        private bool _isDraggingCard = false;

        // 记录进入设置前的上一个视图 (用于按返回按钮切回)
        private string _previousView = "Clipboard";

        public SidebarWindow(DatabaseService dbService, SettingsService settingsService, ClipboardMonitorService monitorService, PasteSimulator pasteSimulator)
        {
            InitializeComponent();
            _dbService = dbService;
            _settingsService = settingsService;
            _monitorService = monitorService;
            _pasteSimulator = pasteSimulator;
            _mediaService = new SystemMediaService();

            CardsListBox.ItemsSource = DisplayCards;

            // 加载图标
            try
            {
                string icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "app.ico");
                if (File.Exists(icoPath))
                {
                    this.Icon = new BitmapImage(new Uri(icoPath, UriKind.Absolute));
                }
            }
            catch { }

            // 监听底层剪贴板捕获
            _monitorService.ItemCaptured += (item) =>
            {
                Dispatcher.Invoke(() =>
                {
                    RefreshCards();
                });
            };

            // 监听失焦自动收起 (若未钉住)
            this.Deactivated += (s, e) =>
            {
                if (!_isPinned && this.IsVisible && (AddActionOverlay.Visibility != Visibility.Visible))
                {
                    SlideOutAndHide();
                }
            };

            // 初始化媒体播放控制服务
            _mediaService.MediaInfoChanged += info =>
            {
                Dispatcher.Invoke(() => UpdateMediaUI(info));
            };
            _ = _mediaService.InitializeAsync();

            // 加载自定义动作磁贴
            RefreshCustomActions();

            // 加载设置项到侧边栏设置页
            LoadSettingsToSidebarUI();
        }

        #region 窗口生命周期与抽屉平滑动画

        /// <summary>
        /// 精准吸附至屏幕最右侧工作区全高，并执行顺滑显现
        /// </summary>
        public void ShowDocked()
        {
            _pasteSimulator.RecordTargetWindow();

            var workArea = SystemParameters.WorkArea;
            this.Width = 360;
            this.Height = workArea.Height;
            this.Top = workArea.Top;
            this.Left = workArea.Right - this.Width;
            this.WindowState = WindowState.Normal;
            this.Topmost = true;

            RefreshCards();
            _ = _mediaService.RefreshMediaInfoAsync();

            this.Show();
            this.Activate();

            // 顺畅渐变显现，绝无越界裁剪问题
            this.Opacity = 0;
            var anim = new DoubleAnimation(0, _settingsService.CurrentSettings.WindowOpacity, TimeSpan.FromMilliseconds(200));
            this.BeginAnimation(UIElement.OpacityProperty, anim);

            if (ClipboardViewPanel.Visibility == Visibility.Visible)
            {
                TxtSearch?.Focus();
            }
        }

        /// <summary>
        /// 平滑收起抽屉并隐藏
        /// </summary>
        public void SlideOutAndHide()
        {
            var anim = new DoubleAnimation(this.Opacity, 0, TimeSpan.FromMilliseconds(160));
            anim.Completed += (s, e) =>
            {
                this.Hide();
                this.Opacity = _settingsService.CurrentSettings.WindowOpacity;
            };
            this.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        /// <summary>
        /// 切换展开/收起侧边栏抽屉
        /// </summary>
        public void ToggleDocked()
        {
            if (this.IsVisible)
            {
                SlideOutAndHide();
            }
            else
            {
                ShowDocked();
            }
        }

        #endregion

        #region 顶栏视图切换 (剪贴板 / 快捷看板 / 内嵌设置)

        private void TabBtnClipboard_Click(object sender, RoutedEventArgs e)
        {
            SwitchToClipboardView();
        }

        private void TabBtnSystem_Click(object sender, RoutedEventArgs e)
        {
            SwitchToSystemActionsView();
        }

        private void SwitchToClipboardView()
        {
            _previousView = "Clipboard";
            ClipboardViewPanel.Visibility = Visibility.Visible;
            SystemActionsPanel.Visibility = Visibility.Collapsed;
            SettingsViewPanel.Visibility = Visibility.Collapsed;
            HeaderNormalBar.Visibility = Visibility.Visible;
            HeaderSettingsBar.Visibility = Visibility.Collapsed;
            TabSwitcherBorder.Visibility = Visibility.Visible;

            TabBtnClipboard.Background = (SolidColorBrush)FindResource("AccentBlueBrush");
            TabBtnClipboard.Foreground = Brushes.White;

            TabBtnSystem.Background = Brushes.Transparent;
            TabBtnSystem.Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush");

            TxtSearch?.Focus();
        }

        private void SwitchToSystemActionsView()
        {
            _previousView = "SystemActions";
            ClipboardViewPanel.Visibility = Visibility.Collapsed;
            SystemActionsPanel.Visibility = Visibility.Visible;
            SettingsViewPanel.Visibility = Visibility.Collapsed;
            HeaderNormalBar.Visibility = Visibility.Visible;
            HeaderSettingsBar.Visibility = Visibility.Collapsed;
            TabSwitcherBorder.Visibility = Visibility.Visible;

            TabBtnSystem.Background = (SolidColorBrush)FindResource("AccentBlueBrush");
            TabBtnSystem.Foreground = Brushes.White;

            TabBtnClipboard.Background = Brushes.Transparent;
            TabBtnClipboard.Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush");

            _ = _mediaService.RefreshMediaInfoAsync();
        }

        /// <summary>
        /// 在侧边栏内部直接展示偏好设置页 (彻底解耦 1.0 居中窗口)
        /// </summary>
        public void ShowSettings()
        {
            if (ClipboardViewPanel.Visibility == Visibility.Visible)
            {
                _previousView = "Clipboard";
            }
            else if (SystemActionsPanel.Visibility == Visibility.Visible)
            {
                _previousView = "SystemActions";
            }

            ClipboardViewPanel.Visibility = Visibility.Collapsed;
            SystemActionsPanel.Visibility = Visibility.Collapsed;
            SettingsViewPanel.Visibility = Visibility.Visible;

            HeaderNormalBar.Visibility = Visibility.Collapsed;
            HeaderSettingsBar.Visibility = Visibility.Visible;
            TabSwitcherBorder.Visibility = Visibility.Collapsed;

            LoadSettingsToSidebarUI();

            if (!this.IsVisible)
            {
                ShowDocked();
            }
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            ShowSettings();
        }

        private void BtnBackFromSettings_Click(object sender, RoutedEventArgs e)
        {
            if (_previousView == "SystemActions")
            {
                SwitchToSystemActionsView();
            }
            else
            {
                SwitchToClipboardView();
            }
        }

        private void BtnPin_Click(object sender, RoutedEventArgs e)
        {
            _isPinned = !_isPinned;
            TxtPinIcon.Text = _isPinned ? "📌" : "🔓";
            BtnPin.ToolTip = _isPinned ? "图钉已锁定 (常驻不自动隐藏)" : "已解除锁定 (失焦自动贴边隐藏)";
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            SlideOutAndHide();
        }

        #endregion

        #region 剪贴板中心搜索与卡片交互

        public void RefreshCards()
        {
            try
            {
                var query = TxtSearch?.Text?.Trim() ?? string.Empty;
                var allItems = _dbService.GetRecentItems(query, _currentCategory, 100);

                DisplayCards.Clear();
                int idx = 1;
                foreach (var item in allItems)
                {
                    if (idx <= 9)
                    {
                        item.DisplayBadge = $"[{idx}]";
                    }
                    else
                    {
                        item.DisplayBadge = string.Empty;
                    }
                    DisplayCards.Add(item);
                    idx++;
                }

                if (TxtStatusLeft != null)
                {
                    TxtStatusLeft.Text = $"已存 {allItems.Count} 条 · SQLite WAL";
                }
            }
            catch { }
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            bool hasText = !string.IsNullOrEmpty(TxtSearch.Text);
            TxtSearchPlaceholder.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
            BtnClearSearch.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
            RefreshCards();
        }

        private void BtnClearSearch_Click(object sender, RoutedEventArgs e)
        {
            TxtSearch.Text = string.Empty;
        }

        private void Filter_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag is string cat)
            {
                _currentCategory = cat;
                RefreshCards();
            }
        }

        private async void Card_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                _cardDragStartPoint = e.GetPosition(this);
                _isDraggingCard = true;

                if (sender is FrameworkElement elem && elem.DataContext is ClipboardItem item)
                {
                    // 单击直接秒贴入之前的主工作窗口
                    await _pasteSimulator.PasteItemAsync(item, () =>
                    {
                        if (!_isPinned) SlideOutAndHide();
                    });
                }
            }
        }

        private void Card_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingCard || e.LeftButton != MouseButtonState.Pressed) return;

            Point currentPoint = e.GetPosition(this);
            Vector diff = _cardDragStartPoint - currentPoint;

            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                _isDraggingCard = false;
                if (sender is FrameworkElement elem && elem.DataContext is ClipboardItem item)
                {
                    DataObject dragData = new DataObject();
                    if (item.IsImage && !string.IsNullOrEmpty(item.FullImagePath) && File.Exists(item.FullImagePath))
                    {
                        var files = new System.Collections.Specialized.StringCollection { item.FullImagePath };
                        dragData.SetFileDropList(files);
                        dragData.SetData(DataFormats.Bitmap, item.ImagePreview);
                    }
                    else
                    {
                        dragData.SetText(item.Content ?? string.Empty);
                    }

                    try
                    {
                        DragDrop.DoDragDrop(elem, dragData, DragDropEffects.Copy);
                    }
                    catch { }
                }
            }
        }

        private async void CardsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (CardsListBox.SelectedItem is ClipboardItem selected)
            {
                await _pasteSimulator.PasteItemAsync(selected, () =>
                {
                    if (!_isPinned) SlideOutAndHide();
                });
            }
        }

        private async void CardsListBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key >= Key.D1 && e.Key <= Key.D9)
            {
                int index = e.Key - Key.D1;
                if (index < DisplayCards.Count)
                {
                    var item = DisplayCards[index];
                    await _pasteSimulator.PasteItemAsync(item, () =>
                    {
                        if (!_isPinned) SlideOutAndHide();
                    });
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Enter && CardsListBox.SelectedItem is ClipboardItem selected)
            {
                await _pasteSimulator.PasteItemAsync(selected, () =>
                {
                    if (!_isPinned) SlideOutAndHide();
                });
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                SlideOutAndHide();
                e.Handled = true;
            }
        }

        private void BtnCardPin_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement elem && elem.DataContext is ClipboardItem item)
            {
                _dbService.TogglePin(item.Id);
                RefreshCards();
            }
        }

        private void BtnCardCopy_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement elem && elem.DataContext is ClipboardItem item && !string.IsNullOrEmpty(item.Content))
            {
                Clipboard.SetText(item.Content);
            }
        }

        private void BtnCardDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement elem && elem.DataContext is ClipboardItem item)
            {
                _dbService.DeleteItem(item.Id);
                RefreshCards();
            }
        }

        #endregion

        #region 系统快捷看板：媒体音源播控与系统控制

        private void UpdateMediaUI(MediaInfo info)
        {
            if (info.HasMedia)
            {
                TxtMediaTitle.Text = info.Title;
                TxtMediaArtist.Text = string.IsNullOrEmpty(info.Artist) ? "未知艺术家" : info.Artist;
                TxtMediaStatusTag.Text = info.IsPlaying ? "正在播放" : "已暂停";
                BtnMediaPlayPause.Content = info.IsPlaying ? "⏸" : "▶";

                if (info.Thumbnail != null)
                {
                    ImgMediaThumb.Source = info.Thumbnail;
                    ImgMediaThumb.Visibility = Visibility.Visible;
                    TxtMediaIconPlaceholder.Visibility = Visibility.Collapsed;
                }
                else
                {
                    ImgMediaThumb.Visibility = Visibility.Collapsed;
                    TxtMediaIconPlaceholder.Visibility = Visibility.Visible;
                }
            }
            else
            {
                TxtMediaTitle.Text = "未检测到正在播放的音源";
                TxtMediaArtist.Text = "支持网易云/QQ音乐/Spotify/浏览器";
                TxtMediaStatusTag.Text = "播放器待机";
                BtnMediaPlayPause.Content = "▶";
                ImgMediaThumb.Visibility = Visibility.Collapsed;
                TxtMediaIconPlaceholder.Visibility = Visibility.Visible;
            }
        }

        private async void BtnMediaPlayPause_Click(object sender, RoutedEventArgs e)
        {
            await _mediaService.TogglePlayPauseAsync();
        }

        private async void BtnMediaPrev_Click(object sender, RoutedEventArgs e)
        {
            await _mediaService.SkipPreviousAsync();
        }

        private async void BtnMediaNext_Click(object sender, RoutedEventArgs e)
        {
            await _mediaService.SkipNextAsync();
        }

        // 四大核心功能联动
        private void TileWifi_Click(object sender, RoutedEventArgs e)
        {
            SystemControlService.OpenAvailableNetworks();
        }

        private void TileBluetooth_Click(object sender, RoutedEventArgs e)
        {
            _isBluetoothActive = !_isBluetoothActive;
            TileBluetooth.Background = _isBluetoothActive 
                ? (SolidColorBrush)FindResource("AccentBlueBrush") 
                : (SolidColorBrush)FindResource("TileBgBrush");
            SystemControlService.OpenBluetoothSettings();
        }

        private void TileSystemSettings_Click(object sender, RoutedEventArgs e)
        {
            SystemControlService.OpenSystemSettings();
        }

        private void TileScreenClip_Click(object sender, RoutedEventArgs e)
        {
            SystemControlService.OpenUri("ms-screenclip:");
        }

        // 真实屏幕亮度调节 (GDI Gamma / 硬件)
        private void SliderBrightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            int val = (int)e.NewValue;
            if (TxtBrightnessValue != null) TxtBrightnessValue.Text = $"{val}%";
            SystemControlService.SetBrightness(val);
        }

        // 系统音量调节
        private void BtnVolDown_Click(object sender, RoutedEventArgs e) => SystemControlService.VolumeDown();
        private void BtnVolMute_Click(object sender, RoutedEventArgs e) => SystemControlService.VolumeMute();
        private void BtnVolUp_Click(object sender, RoutedEventArgs e) => SystemControlService.VolumeUp();

        #endregion

        #region 自定义快捷动作网格管理 (用户可自行添加和管理)

        private void RefreshCustomActions()
        {
            if (CustomActionsPanel == null) return;

            // 保留最后一个常驻的 BtnAddNewAction
            var itemsToRemove = CustomActionsPanel.Children.OfType<UIElement>().Where(c => c != BtnAddNewAction).ToList();
            foreach (var item in itemsToRemove)
            {
                CustomActionsPanel.Children.Remove(item);
            }

            var actions = _settingsService.CurrentSettings.CustomActions;
            int insertIndex = 0;

            foreach (var act in actions)
            {
                var btn = CreateActionTileButton(act);
                CustomActionsPanel.Children.Insert(insertIndex++, btn);
            }
        }

        private Button CreateActionTileButton(CustomActionItem act)
        {
            var btn = new Button
            {
                Width = 76,
                Height = 56,
                Margin = new Thickness(3),
                Tag = act,
                Cursor = Cursors.Hand,
                ToolTip = $"{act.Name} ({act.CommandOrPath})\n右键可删除"
            };

            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(new TextBlock
            {
                Text = act.Icon,
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            stack.Children.Add(new TextBlock
            {
                Text = act.Name,
                FontSize = 10,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
                Margin = new Thickness(0, 3, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 68
            });

            btn.Content = stack;
            btn.Style = (Style)FindResource("ActionCenterTileStyle");

            // 单击执行动作
            btn.Click += (s, e) =>
            {
                SystemControlService.ExecuteAction(act.CommandOrPath);
                if (!_isPinned) SlideOutAndHide();
            };

            // 右键删除菜单
            var contextMenu = new ContextMenu();
            var delItem = new MenuItem
            {
                Header = $"🗑️ 删除 \"{act.Name}\"",
                Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68))
            };
            delItem.Click += (s, e) =>
            {
                _settingsService.CurrentSettings.CustomActions.Remove(act);
                _settingsService.SaveSettings();
                RefreshCustomActions();
            };
            contextMenu.Items.Add(delItem);
            btn.ContextMenu = contextMenu;

            return btn;
        }

        private void BtnAddNewAction_Click(object sender, RoutedEventArgs e)
        {
            TxtNewActionName.Text = string.Empty;
            TxtNewActionIcon.Text = "🚀";
            TxtNewActionCommand.Text = string.Empty;
            AddActionOverlay.Visibility = Visibility.Visible;
            TxtNewActionName.Focus();
        }

        private void BtnCancelAddAction_Click(object sender, RoutedEventArgs e)
        {
            AddActionOverlay.Visibility = Visibility.Collapsed;
        }

        private void BtnBrowseActionFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择要启动的程序或文件",
                Filter = "所有可执行程序与文件|*.exe;*.bat;*.cmd;*.lnk;*.*|可执行程序 (*.exe)|*.exe|所有文件 (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                TxtNewActionCommand.Text = dlg.FileName;
                if (string.IsNullOrWhiteSpace(TxtNewActionName.Text))
                {
                    TxtNewActionName.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
                }
            }
        }

        private void BtnConfirmAddAction_Click(object sender, RoutedEventArgs e)
        {
            string name = TxtNewActionName.Text.Trim();
            string icon = string.IsNullOrWhiteSpace(TxtNewActionIcon.Text) ? "⚡" : TxtNewActionIcon.Text.Trim();
            string cmd = TxtNewActionCommand.Text.Trim();

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(cmd))
            {
                MessageBox.Show("请填写动作名称与执行目标！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var newItem = new CustomActionItem
            {
                Name = name,
                Icon = icon,
                CommandOrPath = cmd
            };

            _settingsService.CurrentSettings.CustomActions.Add(newItem);
            _settingsService.SaveSettings();

            AddActionOverlay.Visibility = Visibility.Collapsed;
            RefreshCustomActions();
        }

        #endregion

        #region 侧边栏内置设置中心联动 (100% 独立于 1.0)

        private void LoadSettingsToSidebarUI()
        {
            var s = _settingsService.CurrentSettings;
            if (TxtSideStoragePath != null) TxtSideStoragePath.Text = s.StorageDirectory;

            // 模式单选
            if (RadioModeCentered2 != null && RadioModeSidebar2 != null)
            {
                if (s.WindowMode == "Centered") RadioModeCentered2.IsChecked = true;
                else RadioModeSidebar2.IsChecked = true;
            }

            // 语言单选
            if (RadioLangEn2 != null && RadioLangZh2 != null)
            {
                if (s.Language == "en-US") RadioLangEn2.IsChecked = true;
                else RadioLangZh2.IsChecked = true;
            }

            // 主题单选与透明度
            if (RadioThemeDark2 != null && RadioThemeLight2 != null)
            {
                if (s.ThemeMode == "Light") RadioThemeLight2.IsChecked = true;
                else RadioThemeDark2.IsChecked = true;
            }

            if (SliderSideOpacity != null)
            {
                SliderSideOpacity.Value = (int)(s.WindowOpacity * 100);
                if (TxtSideOpacityVal != null) TxtSideOpacityVal.Text = $"{(int)SliderSideOpacity.Value}%";
            }

            if (ChkHideOnDeactivate2 != null) ChkHideOnDeactivate2.IsChecked = s.HideOnDeactivate;
            if (ChkPasteOnDblClick != null) ChkPasteOnDblClick.IsChecked = s.DoubleClickAction == "PasteAndHide";
        }

        private void WindowModeRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_settingsService == null || RadioModeSidebar2 == null || RadioModeCentered2 == null) return;
            string newMode = RadioModeSidebar2.IsChecked == true ? "Sidebar" : "Centered";
            if (_settingsService.CurrentSettings.WindowMode != newMode)
            {
                _settingsService.CurrentSettings.WindowMode = newMode;
                _settingsService.SaveSettings();

                if (Application.Current is App app)
                {
                    app.OnWindowModeChanged(newMode);
                }
            }
        }

        private void LangRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_settingsService == null) return;
            string newLang = RadioLangEn2.IsChecked == true ? "en-US" : "zh-CN";
            if (_settingsService.CurrentSettings.Language != newLang)
            {
                _settingsService.CurrentSettings.Language = newLang;
                _settingsService.SaveSettings();
                LocalizationService.Instance.SetLanguage(newLang);
            }
        }

        private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_settingsService == null) return;
            string newTheme = RadioThemeLight2.IsChecked == true ? "Light" : "Dark";
            _settingsService.CurrentSettings.ThemeMode = newTheme;
            _settingsService.SaveSettings();
        }

        private void SliderSideOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_settingsService == null) return;
            double val = e.NewValue / 100.0;
            _settingsService.CurrentSettings.WindowOpacity = val;
            _settingsService.SaveSettings();

            if (TxtSideOpacityVal != null) TxtSideOpacityVal.Text = $"{(int)e.NewValue}%";
            this.Opacity = val;
        }

        private void ChkHideOnDeactivate_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingsService == null || ChkHideOnDeactivate2 == null) return;
            _settingsService.CurrentSettings.HideOnDeactivate = ChkHideOnDeactivate2.IsChecked == true;
            _settingsService.SaveSettings();
        }

        private void ChkPasteOnDblClick_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingsService == null || ChkPasteOnDblClick == null) return;
            _settingsService.CurrentSettings.DoubleClickAction = ChkPasteOnDblClick.IsChecked == true ? "PasteAndHide" : "CopyOnly";
            _settingsService.SaveSettings();
        }

        private void BtnBrowseStorage_Click(object sender, RoutedEventArgs e)
        {
            var loc = LocalizationService.Instance;
            var dialog = new OpenFolderDialog
            {
                Title = loc.Get("FolderDialogTitle"),
                InitialDirectory = _settingsService.CurrentSettings.StorageDirectory
            };

            if (dialog.ShowDialog() == true)
            {
                string chosenPath = dialog.FolderName;
                if (_settingsService.UpdateStorageDirectory(chosenPath, true))
                {
                    _dbService.SwitchStorageDirectory(chosenPath);
                    if (TxtSideStoragePath != null) TxtSideStoragePath.Text = chosenPath;
                    RefreshCards();
                    MessageBox.Show(loc.Get("StorageMigratedMsg", chosenPath), loc.Get("StorageMigratedTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        private void BtnClearHistory_Click(object sender, RoutedEventArgs e)
        {
            var loc = LocalizationService.Instance;
            var res = MessageBox.Show(loc.Get("ConfirmClearMsg"), loc.Get("ConfirmClearTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes)
            {
                _dbService.ClearAll();
                RefreshCards();
            }
        }

        private void BtnExitApp_Click(object sender, RoutedEventArgs e)
        {
            var loc = LocalizationService.Instance;
            var res = MessageBox.Show(loc.Get("ConfirmExitMsg"), loc.Get("ConfirmExitTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes)
            {
                if (Application.Current is App app)
                {
                    app.ExitApplication();
                }
            }
        }

        #endregion
    }
}
