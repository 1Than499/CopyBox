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
using System.Windows.Threading;
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
        private readonly DispatcherTimer _brightnessSyncTimer;

        public ObservableCollection<ClipboardItem> DisplayCards { get; } = new();

        private string _currentCategory = "all";
        private bool _isPinned = true;
        private bool _isBluetoothActive = true;
        private Point _cardDragStartPoint;
        private bool _isDraggingCard = false;

        private string _previousView = "Clipboard";
        private bool _isUpdatingBrightnessUi = false;
        private bool _isInternalSettingUpdate = false;

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

            // 监听失焦自动收起 (图钉锁定状态下常驻，未锁定状态下失焦自动收起)
            this.Deactivated += (s, e) =>
            {
                if (!_isPinned && this.IsVisible && (AddActionOverlay == null || AddActionOverlay.Visibility != Visibility.Visible))
                {
                    SlideOutAndHide();
                }
            };

            // 监听多语言全局变更
            LocalizationService.Instance.LanguageChanged += () =>
            {
                Dispatcher.Invoke(() => UpdateLocalizedTexts());
            };

            // 初始化媒体播放控制服务联动
            _mediaService.MediaInfoChanged += info =>
            {
                Dispatcher.Invoke(() => UpdateMediaUI(info));
            };

            // 初始化系统亮度双向同步定时器 (每 800ms 检查一次 Windows 系统外部调节)
            _brightnessSyncTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(800)
            };
            _brightnessSyncTimer.Tick += (s, e) =>
            {
                if (this.IsVisible && SystemActionsPanel.Visibility == Visibility.Visible && !_isUpdatingBrightnessUi)
                {
                    try
                    {
                        int currentSys = SystemControlService.GetCurrentBrightness();
                        if (Math.Abs(currentSys - (int)SliderBrightness.Value) >= 1)
                        {
                            _isUpdatingBrightnessUi = true;
                            SliderBrightness.Value = currentSys;
                            if (TxtBrightnessValue != null) TxtBrightnessValue.Text = $"{currentSys}%";
                            // 关键修复：系统外部调高/调低亮度时，实际屏幕视觉亮度立即同步刷新！
                            SystemControlService.ApplyVisualBrightness(currentSys);
                            _isUpdatingBrightnessUi = false;
                        }
                    }
                    catch { }
                }
            };

            // 加载自定义动作磁贴
            RefreshCustomActions();

            // 实时同步当前系统屏幕真实亮度
            try
            {
                int sysBrightness = SystemControlService.GetCurrentBrightness();
                if (SliderBrightness != null)
                {
                    _isUpdatingBrightnessUi = true;
                    SliderBrightness.Value = sysBrightness;
                    if (TxtBrightnessValue != null) TxtBrightnessValue.Text = $"{sysBrightness}%";
                    _isUpdatingBrightnessUi = false;
                }
            }
            catch { }

            // 加载设置项与主题到侧边栏
            LoadSettingsToSidebarUI();

            // 首次应用多语言
            UpdateLocalizedTexts();
        }

        #region 外观主题与多语言热更新 (支持深色/白天浅色热切换)

        /// <summary>
        /// 实时应用外观主题 (深色 / 浅色)，通过修改 Resources 实现 DynamicResource 毫秒级刷新
        /// </summary>
        public void ApplyTheme(string theme)
        {
            bool isLight = theme.Equals("Light", StringComparison.OrdinalIgnoreCase);

            if (isLight)
            {
                // 白天浅色 Fluent 毛玻璃高级质感 (高不透明度象牙白，杜绝刺眼叠影；浅灰底盘彻底告别黑块)
                this.Resources["SidebarBgBrush"] = new SolidColorBrush(Color.FromArgb(0xFA, 0xFA, 0xFB, 0xFD));
                this.Resources["SidebarBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));
                this.Resources["HeaderBgBrush"] = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
                this.Resources["CardBgBrush"] = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
                this.Resources["CardHoverBgBrush"] = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));
                this.Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0));
                this.Resources["TextPrimaryBrush"] = new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A));
                this.Resources["TextSecondaryBrush"] = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69));
                this.Resources["TileBgBrush"] = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC));
                this.Resources["TileHoverBgBrush"] = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));
                this.Resources["TabContainerBgBrush"] = new SolidColorBrush(Color.FromRgb(0xEE, 0xF2, 0xF6));
                this.Resources["CodeBoxBgBrush"] = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));
                this.Resources["CodeTextBrush"] = new SolidColorBrush(Color.FromRgb(0x02, 0x84, 0xC7));
                this.Resources["SliderTrackBgBrush"] = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));
            }
            else
            {
                // 暗夜深色 Fluent 极简质感
                this.Resources["SidebarBgBrush"] = new SolidColorBrush(Color.FromArgb(0xF2, 0x14, 0x17, 0x22));
                this.Resources["SidebarBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x2E, 0x3A, 0x4E));
                this.Resources["HeaderBgBrush"] = new SolidColorBrush(Color.FromArgb(0xF5, 0x0F, 0x12, 0x1A));
                this.Resources["CardBgBrush"] = new SolidColorBrush(Color.FromRgb(0x1C, 0x22, 0x30));
                this.Resources["CardHoverBgBrush"] = new SolidColorBrush(Color.FromRgb(0x28, 0x32, 0x46));
                this.Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x2D, 0x39, 0x4D));
                this.Resources["TextPrimaryBrush"] = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC));
                this.Resources["TextSecondaryBrush"] = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
                this.Resources["TileBgBrush"] = new SolidColorBrush(Color.FromRgb(0x1B, 0x22, 0x30));
                this.Resources["TileHoverBgBrush"] = new SolidColorBrush(Color.FromRgb(0x28, 0x33, 0x48));
                this.Resources["TabContainerBgBrush"] = new SolidColorBrush(Color.FromRgb(0x15, 0x1B, 0x26));
                this.Resources["CodeBoxBgBrush"] = new SolidColorBrush(Color.FromRgb(0x11, 0x14, 0x1C));
                this.Resources["CodeTextBrush"] = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
                this.Resources["SliderTrackBgBrush"] = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55));
            }

            // 更新视图切换栏指示与高亮
            UpdateTabVisuals(ClipboardViewPanel?.Visibility == Visibility.Visible);

            // 重新刷新自定义磁贴列表的色彩
            RefreshCustomActions();
        }

        /// <summary>
        /// 全局多语言更新：动态翻译侧边栏所有文本与占位符
        /// </summary>
        public void UpdateLocalizedTexts()
        {
            var loc = LocalizationService.Instance;

            // 药丸 Tab 按钮
            if (TxtTabBtnClipboard != null) TxtTabBtnClipboard.Text = loc.Get("SidebarClipboard");
            if (TxtTabBtnSystem != null) TxtTabBtnSystem.Text = loc.Get("SidebarActions");

            // 搜索占位符与分类胶囊
            if (TxtSearchPlaceholder != null) TxtSearchPlaceholder.Text = loc.Get("SidebarSearchPlaceholder");
            if (RadioAll != null) RadioAll.Content = loc.Get("SidebarFilterAll");
            if (RadioText != null) RadioText.Content = loc.Get("SidebarFilterText");
            if (RadioImages != null) RadioImages.Content = loc.Get("SidebarFilterImage");
            if (RadioCode != null) RadioCode.Content = loc.Get("SidebarFilterCode");
            if (RadioPinned != null) RadioPinned.Content = loc.Get("SidebarFilterPinned");

            // 快捷看板各板块
            if (TxtSectionCoreShortcuts != null) TxtSectionCoreShortcuts.Text = loc.Get("SidebarCoreActions");
            if (TxtSectionCustomActions != null) TxtSectionCustomActions.Text = loc.Get("SidebarCustomActions");
            if (TxtTipRightClickDelete != null) TxtTipRightClickDelete.Text = loc.Get("SidebarRightClickDelete");
            if (TxtBtnAddAction != null) TxtBtnAddAction.Text = loc.Get("SidebarAddAction");
            if (TxtBrightnessTitle != null) TxtBrightnessTitle.Text = "🔅 " + loc.Get("SidebarBrightness");
            if (TxtVolumeTitle != null) TxtVolumeTitle.Text = "🔊 " + loc.Get("SidebarVolume");
            if (BtnVolDown != null) BtnVolDown.Content = loc.Get("SidebarVolDown");
            if (BtnVolMute != null) BtnVolMute.Content = loc.Get("SidebarVolMute");
            if (BtnVolUp != null) BtnVolUp.Content = loc.Get("SidebarVolUp");

            // 偏好设置页面
            if (TxtSettingsHeaderTitle != null) TxtSettingsHeaderTitle.Text = loc.Get("SidebarPreferences");
            if (TxtSetModeTitle != null) TxtSetModeTitle.Text = loc.Get("WindowModeTitle");
            if (RadioModeSidebar2 != null) RadioModeSidebar2.Content = loc.Get("WindowModeSidebar");
            if (RadioModeCentered2 != null) RadioModeCentered2.Content = loc.Get("WindowModeCentered");
            if (TxtSetLangTitle != null) TxtSetLangTitle.Text = loc.Get("LangSectionTitle");
            if (RadioLangZh2 != null) RadioLangZh2.Content = loc.Get("LangZh");
            if (RadioLangEn2 != null) RadioLangEn2.Content = loc.Get("LangEn");
            if (TxtSetThemeTitle != null) TxtSetThemeTitle.Text = loc.Get("AppearanceTitle");
            if (RadioThemeDark2 != null) RadioThemeDark2.Content = loc.Get("ThemeDark");
            if (RadioThemeLight2 != null) RadioThemeLight2.Content = loc.Get("ThemeLight");
            if (TxtSetOpacityTitle != null) TxtSetOpacityTitle.Text = loc.Get("OpacityLabel");
            if (TxtSetStorageTitle != null) TxtSetStorageTitle.Text = loc.Get("StoragePathTitle");
            if (BtnBrowseStorage != null) BtnBrowseStorage.Content = loc.Get("BrowseButton");
            if (TxtSetInteractionTitle != null) TxtSetInteractionTitle.Text = loc.Get("DoubleClickTitle");
            if (ChkHideOnDeactivate2 != null) ChkHideOnDeactivate2.Content = loc.Get("DeactivateTitle");
            if (ChkPasteOnDblClick != null) ChkPasteOnDblClick.Content = loc.Get("DoubleClickPaste");
            if (BtnClearHistory != null) BtnClearHistory.Content = loc.Get("TrayClear");
            if (BtnExitApp != null) BtnExitApp.Content = loc.Get("BtnExitApp");

            // 底部提示与状态
            if (TxtStatusRightTip != null) TxtStatusRightTip.Text = loc.Get("SidebarAltVTip");
            if (TxtStatusLeft != null) TxtStatusLeft.Text = loc.Get("SidebarRecordCount", DisplayCards.Count);
            if (BtnPin != null) BtnPin.ToolTip = _isPinned ? loc.Get("PinLockedTip") : loc.Get("PinUnlockedTip");
            UpdateTabVisuals(ClipboardViewPanel?.Visibility == Visibility.Visible);
        }

        #endregion

        #region 窗口生命周期与抽屉平滑动画

        /// <summary>
        /// 精准吸附至屏幕最右侧工作区全高，并执行顺滑显现
        /// </summary>
        public void ShowDocked()
        {
            _pasteSimulator.RecordTargetWindow();

            var workArea = SystemParameters.WorkArea;
            this.Width = 380;
            this.Height = workArea.Height;
            this.Top = workArea.Top;
            this.Left = workArea.Right - this.Width;
            this.WindowState = WindowState.Normal;
            this.Topmost = true;

            RefreshCards();
            
            // 立即用当前音源信息刷新 UI，并触发后台重新检测
            UpdateMediaUI(_mediaService.CurrentMedia);
            _ = _mediaService.RefreshMediaAsync();

            // 同步当前系统真实亮度
            try
            {
                int sysBrightness = SystemControlService.GetCurrentBrightness();
                if (SliderBrightness != null)
                {
                    _isUpdatingBrightnessUi = true;
                    SliderBrightness.Value = sysBrightness;
                    if (TxtBrightnessValue != null) TxtBrightnessValue.Text = $"{sysBrightness}%";
                    _isUpdatingBrightnessUi = false;
                }
            }
            catch { }

            _brightnessSyncTimer.Start();

            this.Show();
            this.Activate();

            // 顺畅渐变显现，动画完成后解除时钟绑定，使滑块手动调节实时生效
            double finalOpacity = Math.Max(0.08, _settingsService.CurrentSettings.WindowOpacity);
            this.Opacity = 0;
            var anim = new DoubleAnimation(0, finalOpacity, TimeSpan.FromMilliseconds(200));
            anim.Completed += (s, e) =>
            {
                this.BeginAnimation(UIElement.OpacityProperty, null);
                this.Opacity = finalOpacity;
            };
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
            _brightnessSyncTimer.Stop();

            double currentOp = this.Opacity;
            var anim = new DoubleAnimation(currentOp, 0, TimeSpan.FromMilliseconds(160));
            anim.Completed += (s, e) =>
            {
                this.Hide();
                this.BeginAnimation(UIElement.OpacityProperty, null);
                this.Opacity = Math.Max(0.08, _settingsService.CurrentSettings.WindowOpacity);
            };
            this.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        public void ToggleDocked()
        {
            if (this.IsVisible && this.Opacity > 0.1)
            {
                SlideOutAndHide();
            }
            else
            {
                ShowDocked();
            }
        }

        #endregion

        #region 双模视图切换 (剪贴板中心 vs 系统快捷看板)

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
            TabSwitcherRow.Visibility = Visibility.Visible;

            UpdateTabVisuals(isClipboard: true);

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
            TabSwitcherRow.Visibility = Visibility.Visible;

            UpdateTabVisuals(isClipboard: false);

            // 切换到看板时立即渲染音源 UI
            UpdateMediaUI(_mediaService.CurrentMedia);
            _ = _mediaService.RefreshMediaAsync();
        }

        private void UpdateTabVisuals(bool isClipboard)
        {
            var accentBrush = (Brush)FindResource("AccentBlueBrush");
            var secBrush = (Brush)FindResource("TextSecondaryBrush");

            if (TxtTabBtnClipboard != null)
            {
                TxtTabBtnClipboard.Foreground = isClipboard ? accentBrush : secBrush;
                TxtTabBtnClipboard.FontWeight = isClipboard ? FontWeights.SemiBold : FontWeights.Normal;
            }
            if (TabIndicatorClipboard != null)
            {
                TabIndicatorClipboard.Visibility = isClipboard ? Visibility.Visible : Visibility.Collapsed;
            }

            if (TxtTabBtnSystem != null)
            {
                TxtTabBtnSystem.Foreground = isClipboard ? secBrush : accentBrush;
                TxtTabBtnSystem.FontWeight = isClipboard ? FontWeights.Normal : FontWeights.SemiBold;
            }
            if (TabIndicatorSystem != null)
            {
                TabIndicatorSystem.Visibility = isClipboard ? Visibility.Collapsed : Visibility.Visible;
            }
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
            TabSwitcherRow.Visibility = Visibility.Collapsed;

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
            if (_settingsService != null)
            {
                _settingsService.CurrentSettings.HideOnDeactivate = !_isPinned;
                _settingsService.SaveSettings();
            }
            UpdatePinUiState();
        }

        private void UpdatePinUiState()
        {
            var loc = LocalizationService.Instance;
            if (TxtPinIcon != null) TxtPinIcon.Text = _isPinned ? "📌" : "🔓";
            if (BtnPin != null) BtnPin.ToolTip = _isPinned ? loc.Get("PinLockedTip") : loc.Get("PinUnlockedTip");
            if (ChkHideOnDeactivate2 != null)
            {
                _isInternalSettingUpdate = true;
                ChkHideOnDeactivate2.IsChecked = !_isPinned;
                _isInternalSettingUpdate = false;
            }
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
                    TxtStatusLeft.Text = LocalizationService.Instance.Get("SidebarRecordCount", DisplayCards.Count);
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
            TxtSearch.Focus();
        }

        private void Filter_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag != null)
            {
                _currentCategory = rb.Tag.ToString() ?? "all";
                RefreshCards();
            }
        }

        private void CardsListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _cardDragStartPoint = e.GetPosition(null);
            _isDraggingCard = false;
        }

        private void CardsListBox_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && !_isDraggingCard)
            {
                Point currentPos = e.GetPosition(null);
                Vector diff = _cardDragStartPoint - currentPos;

                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    if (CardsListBox.SelectedItem is ClipboardItem selectedItem)
                    {
                        _isDraggingCard = true;
                        try
                        {
                            var data = new DataObject();
                            if (selectedItem.IsImage && selectedItem.ImagePreview is BitmapSource bmp)
                            {
                                data.SetImage(bmp);
                            }
                            else if (!string.IsNullOrEmpty(selectedItem.Content))
                            {
                                data.SetText(selectedItem.Content);
                            }
                            DragDrop.DoDragDrop(CardsListBox, data, DragDropEffects.Copy);
                        }
                        catch { }
                        finally
                        {
                            _isDraggingCard = false;
                        }
                    }
                }
            }
        }

        private async void CardsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (CardsListBox.SelectedItem is ClipboardItem selected)
            {
                // 关键修复：选择 PasteAndHide 时，双击必须无条件模拟粘贴并平滑收起抽屉！
                if (_settingsService.CurrentSettings.DoubleClickAction == "PasteAndHide")
                {
                    await _pasteSimulator.PasteItemAsync(selected, () =>
                    {
                        SlideOutAndHide();
                    });
                }
                else
                {
                    // 仅复制到剪贴板，保持窗口打开
                    if (!string.IsNullOrEmpty(selected.Content))
                    {
                        Clipboard.SetText(selected.Content);
                    }
                    else if (selected.IsImage && selected.ImagePreview is BitmapSource bmp)
                    {
                        Clipboard.SetImage(bmp);
                    }
                }
            }
        }

        private async void CardsListBox_KeyDown(object sender, KeyEventArgs e)
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
            if (info != null && info.HasMedia)
            {
                TxtMediaTitle.Text = info.Title;
                TxtMediaArtist.Text = string.IsNullOrEmpty(info.Artist) ? "未知艺术家" : info.Artist;
                TxtMediaStatusTag.Text = info.IsPlaying ? "正在播放" : "已暂停";
                
                // 播放时微光高亮标签
                BdMediaStatusTag.Background = info.IsPlaying 
                    ? new SolidColorBrush(Color.FromArgb(0x35, 0x10, 0xB9, 0x81)) 
                    : new SolidColorBrush(Color.FromArgb(0x1A, 0x78, 0xA4, 0xE8));
                TxtMediaStatusTag.Foreground = info.IsPlaying 
                    ? new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)) 
                    : (SolidColorBrush)FindResource("TextSecondaryBrush");

                if (IconPlay != null) IconPlay.Visibility = info.IsPlaying ? Visibility.Collapsed : Visibility.Visible;
                if (IconPause != null) IconPause.Visibility = info.IsPlaying ? Visibility.Visible : Visibility.Collapsed;

                // 高清封面/软件专属图标展示
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
                TxtMediaTitle.Text = "未检测到播放中的音源";
                TxtMediaArtist.Text = "支持网易云/QQ音乐/抖音/Spotify/Edge等";
                TxtMediaStatusTag.Text = "待机中";
                BdMediaStatusTag.Background = new SolidColorBrush(Color.FromArgb(0x1A, 0x78, 0xA4, 0xE8));
                TxtMediaStatusTag.Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush");
                if (IconPlay != null) IconPlay.Visibility = Visibility.Visible;
                if (IconPause != null) IconPause.Visibility = Visibility.Collapsed;
                ImgMediaThumb.Visibility = Visibility.Collapsed;
                TxtMediaIconPlaceholder.Visibility = Visibility.Visible;
            }
        }

        private async void BtnMediaPlayPause_Click(object sender, RoutedEventArgs e)
        {
            await _mediaService.TogglePlayPauseAsync();
            UpdateMediaUI(_mediaService.CurrentMedia);
        }

        private async void BtnMediaPrev_Click(object sender, RoutedEventArgs e)
        {
            await _mediaService.SkipPreviousAsync();
            UpdateMediaUI(_mediaService.CurrentMedia);
        }

        private async void BtnMediaNext_Click(object sender, RoutedEventArgs e)
        {
            await _mediaService.SkipNextAsync();
            UpdateMediaUI(_mediaService.CurrentMedia);
        }

        // 四大核心功能联动 (WLAN、蓝牙、系统设置、屏幕截图)
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

        // 统一系统级屏幕真实亮度调节 (两边实时保持一致)
        private void SliderBrightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingBrightnessUi) return;
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

            while (CustomActionsPanel.Children.Count > 1)
            {
                CustomActionsPanel.Children.RemoveAt(0);
            }

            var actions = _settingsService.CurrentSettings.CustomActions;
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                var btn = CreateActionTile(action, i);
                CustomActionsPanel.Children.Insert(CustomActionsPanel.Children.Count - 1, btn);
            }
        }

        private Button CreateActionTile(CustomActionItem action, int index)
        {
            var btn = new Button
            {
                Width = 80,
                Height = 58,
                Margin = new Thickness(3),
                Cursor = Cursors.Hand,
                ToolTip = $"{action.Name}\n{action.CommandOrPath}\n(右键删除)",
                Tag = index
            };

            var template = new ControlTemplate(typeof(Button));
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.SetValue(Border.BackgroundProperty, (SolidColorBrush)FindResource("TileBgBrush"));
            borderFactory.SetValue(Border.BorderBrushProperty, (SolidColorBrush)FindResource("CardBorderBrush"));
            borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));

            var stack = new FrameworkElementFactory(typeof(StackPanel));
            stack.SetValue(StackPanel.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            stack.SetValue(StackPanel.VerticalAlignmentProperty, VerticalAlignment.Center);

            var iconTxt = new FrameworkElementFactory(typeof(TextBlock));
            iconTxt.SetValue(TextBlock.TextProperty, string.IsNullOrEmpty(action.Icon) ? "🚀" : action.Icon);
            iconTxt.SetValue(TextBlock.FontSizeProperty, 16.0);
            iconTxt.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);

            var nameTxt = new FrameworkElementFactory(typeof(TextBlock));
            nameTxt.SetValue(TextBlock.TextProperty, action.Name);
            nameTxt.SetValue(TextBlock.FontSizeProperty, 10.5);
            nameTxt.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            nameTxt.SetValue(TextBlock.ForegroundProperty, (SolidColorBrush)FindResource("TextPrimaryBrush"));
            nameTxt.SetValue(TextBlock.MarginProperty, new Thickness(0, 3, 0, 0));
            nameTxt.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            nameTxt.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            nameTxt.SetValue(TextBlock.MaxWidthProperty, 70.0);

            stack.AppendChild(iconTxt);
            stack.AppendChild(nameTxt);
            borderFactory.AppendChild(stack);
            template.VisualTree = borderFactory;
            btn.Template = template;

            btn.Click += (s, e) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = action.CommandOrPath,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"启动失败: {ex.Message}", "快捷动作", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };

            btn.MouseRightButtonUp += (s, e) =>
            {
                var result = MessageBox.Show($"确定删除快捷动作【{action.Name}】吗？", "删除快捷动作", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    _settingsService.CurrentSettings.CustomActions.RemoveAt(index);
                    _settingsService.SaveSettings();
                    RefreshCustomActions();
                }
            };

            return btn;
        }

        private void BtnAddNewAction_Click(object sender, RoutedEventArgs e)
        {
            TxtNewActionName.Text = "";
            TxtNewActionIcon.Text = "🚀";
            TxtNewActionCommand.Text = "";
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
                Title = "选择可执行程序或文件",
                Filter = "所有文件 (*.*)|*.*|应用程序 (*.exe)|*.exe"
            };
            if (dlg.ShowDialog() == true)
            {
                TxtNewActionCommand.Text = dlg.FileName;
                if (string.IsNullOrEmpty(TxtNewActionName.Text))
                {
                    TxtNewActionName.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
                }
            }
        }

        private void BtnConfirmAddAction_Click(object sender, RoutedEventArgs e)
        {
            string name = TxtNewActionName.Text.Trim();
            string cmd = TxtNewActionCommand.Text.Trim();
            string icon = TxtNewActionIcon.Text.Trim();

            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(cmd))
            {
                MessageBox.Show("请填写名称和执行目标！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _settingsService.CurrentSettings.CustomActions.Add(new CustomActionItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                CommandOrPath = cmd,
                Icon = string.IsNullOrEmpty(icon) ? "🚀" : icon
            });
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

            // 立即应用当前主题
            ApplyTheme(s.ThemeMode);

            if (SliderSideOpacity != null)
            {
                int opVal = (int)Math.Round(s.WindowOpacity * 100);
                SliderSideOpacity.Value = opVal;
                if (TxtSideOpacityVal != null) TxtSideOpacityVal.Text = $"{opVal}%";
                this.Opacity = Math.Max(0.08, s.WindowOpacity);
            }

            _isPinned = !s.HideOnDeactivate;
            UpdatePinUiState();
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
                UpdateLocalizedTexts();
            }
        }

        private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_settingsService == null) return;
            string newTheme = RadioThemeLight2.IsChecked == true ? "Light" : "Dark";
            _settingsService.CurrentSettings.ThemeMode = newTheme;
            _settingsService.SaveSettings();

            // 立即生效外观切换
            ApplyTheme(newTheme);
        }

        private void SliderSideOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_settingsService == null) return;
            int intVal = (int)e.NewValue;
            double val = intVal / 100.0;
            _settingsService.CurrentSettings.WindowOpacity = val;
            _settingsService.SaveSettings();

            if (TxtSideOpacityVal != null) TxtSideOpacityVal.Text = $"{intVal}%";

            // 清除可能锁住属性的动画时钟，立即应用真实透明度
            this.BeginAnimation(UIElement.OpacityProperty, null);
            this.Opacity = Math.Max(0.08, val);
        }

        private void ChkHideOnDeactivate_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInternalSettingUpdate || _settingsService == null || ChkHideOnDeactivate2 == null) return;
            bool hide = ChkHideOnDeactivate2.IsChecked == true;
            _settingsService.CurrentSettings.HideOnDeactivate = hide;
            _settingsService.SaveSettings();

            _isPinned = !hide;
            UpdatePinUiState();
        }

        private void ChkPasteOnDblClick_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingsService == null || ChkPasteOnDblClick == null) return;
            _settingsService.CurrentSettings.DoubleClickAction = ChkPasteOnDblClick.IsChecked == true ? "PasteAndHide" : "CopyOnly";
            _settingsService.SaveSettings();
        }

        private void BtnBrowseStorage_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择 CopyBox 历史数据存储目录",
                UseDescriptionForTitle = true,
                SelectedPath = _settingsService.CurrentSettings.StorageDirectory
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                string newDir = dlg.SelectedPath;
                if (_settingsService.UpdateStorageDirectory(newDir, true))
                {
                    TxtSideStoragePath.Text = newDir;
                    RefreshCards();
                    MessageBox.Show("数据目录已成功迁移并生效！", "迁移完成", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        private void BtnClearHistory_Click(object sender, RoutedEventArgs e)
        {
            var res = MessageBox.Show("确定要清空全部剪贴板历史记录吗？此操作无法撤销！", "警告", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res == MessageBoxResult.Yes)
            {
                _dbService.ClearAll();
                RefreshCards();
            }
        }

        private void BtnExitApp_Click(object sender, RoutedEventArgs e)
        {
            if (Application.Current is App app)
            {
                app.ExitApplication();
            }
        }

        #endregion
    }
}
