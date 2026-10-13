using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using ClipVault.Interop;
using ClipVault.Models;
using ClipVault.Services;

namespace ClipVault.Views
{
    public partial class SidebarWindow : Window
    {
        private readonly DatabaseService _dbService;
        private readonly SettingsService _settingsService;
        private readonly ClipboardMonitorService _monitorService;
        private readonly PasteSimulator _pasteSimulator;

        public ObservableCollection<ClipboardItem> DisplayCards { get; } = new();

        private string _currentCategory = "all";
        private bool _isPinned = true;
        private bool _isBluetoothActive = true;
        private Point _cardDragStartPoint;
        private bool _isDraggingCard = false;

        public SidebarWindow(DatabaseService dbService, SettingsService settingsService, ClipboardMonitorService monitorService, PasteSimulator pasteSimulator)
        {
            InitializeComponent();
            _dbService = dbService;
            _settingsService = settingsService;
            _monitorService = monitorService;
            _pasteSimulator = pasteSimulator;

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
                if (!_isPinned && this.IsVisible)
                {
                    SlideOutAndHide();
                }
            };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // 注入 WS_EX_NOACTIVATE 扩展样式，避免点击本窗口夺取用户当前活动软件的光标焦点
            var helper = new WindowInteropHelper(this);
            if (helper.Handle != IntPtr.Zero)
            {
                NativeMethods.SetNoActivate(helper.Handle);
            }
        }

        /// <summary>
        /// 精准吸附至屏幕最右侧工作区全高，并执行滑入动效
        /// </summary>
        public void ShowDocked()
        {
            var workArea = SystemParameters.WorkArea;
            this.Left = workArea.Right - this.Width;
            this.Top = workArea.Top;
            this.Height = workArea.Height;

            RefreshCards();

            this.Show();

            // 滑入动效
            var anim = new DoubleAnimation
            {
                From = this.Width,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(260),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            WindowTranslate.BeginAnimation(TranslateTransform.XProperty, anim);
        }

        /// <summary>
        /// 平滑收起抽屉并隐藏
        /// </summary>
        public void SlideOutAndHide()
        {
            var anim = new DoubleAnimation
            {
                From = WindowTranslate.X,
                To = this.Width,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            anim.Completed += (s, e) =>
            {
                this.Hide();
            };
            WindowTranslate.BeginAnimation(TranslateTransform.XProperty, anim);
        }

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

        #region 顶部交互与双模切换

        private void BtnPin_Click(object sender, RoutedEventArgs e)
        {
            _isPinned = !_isPinned;
            TxtPinIcon.Text = _isPinned ? "📌" : "🔓";
            BtnPin.ToolTip = _isPinned ? "图钉已锁定 (常驻不自动隐藏)" : "已解除锁定 (失焦自动贴边隐藏)";
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 打开应用主设置窗口
                Process.Start(new ProcessStartInfo
                {
                    FileName = "ms-settings:",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            SlideOutAndHide();
        }

        private void TabBtnClipboard_Click(object sender, RoutedEventArgs e)
        {
            ClipboardViewPanel.Visibility = Visibility.Visible;
            SystemActionsPanel.Visibility = Visibility.Collapsed;

            TabBtnClipboard.Background = (SolidColorBrush)FindResource("AccentBlueBrush");
            TabBtnClipboard.Foreground = Brushes.White;

            TabBtnSystem.Background = Brushes.Transparent;
            TabBtnSystem.Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush");
        }

        private void TabBtnSystem_Click(object sender, RoutedEventArgs e)
        {
            ClipboardViewPanel.Visibility = Visibility.Collapsed;
            SystemActionsPanel.Visibility = Visibility.Visible;

            TabBtnSystem.Background = (SolidColorBrush)FindResource("AccentBlueBrush");
            TabBtnSystem.Foreground = Brushes.White;

            TabBtnClipboard.Background = Brushes.Transparent;
            TabBtnClipboard.Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush");
        }

        #endregion

        #region 剪贴板中心搜索与卡片交互

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
                    // 单击直接秒贴入之前的主工作窗口 (WS_EX_NOACTIVATE 保证焦点未丢)
                    await _pasteSimulator.PasteItemAsync(item, () =>
                    {
                        if (!_isPinned) SlideOutAndHide();
                    });
                }
            }
        }

        private void Card_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingCard && e.LeftButton == MouseButtonState.Pressed)
            {
                Point currentPoint = e.GetPosition(this);
                Vector diff = _cardDragStartPoint - currentPoint;

                if (Math.Abs(diff.X) > 8 || Math.Abs(diff.Y) > 8)
                {
                    _isDraggingCard = false;
                    if (sender is FrameworkElement elem && elem.DataContext is ClipboardItem item)
                    {
                        if (!string.IsNullOrEmpty(item.Content))
                        {
                            var dragData = new DataObject(DataFormats.UnicodeText, item.Content);
                            DragDrop.DoDragDrop(elem, dragData, DragDropEffects.Copy);
                        }
                    }
                }
            }
        }

        private async void CardsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (CardsListBox.SelectedItem is ClipboardItem item)
            {
                await _pasteSimulator.PasteItemAsync(item, () =>
                {
                    if (!_isPinned) SlideOutAndHide();
                });
            }
        }

        private async void CardsListBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key >= Key.D1 && e.Key <= Key.D9)
            {
                int index = (int)e.Key - (int)Key.D1;
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

        #region 仿生系统操作中心快捷磁贴联动

        private void OpenUri(string uri)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = uri,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private void BtnManageNotifications_Click(object sender, RoutedEventArgs e) => OpenUri("ms-settings:notifications");

        private void BtnTriggerWinA_Click(object sender, RoutedEventArgs e)
        {
            // 通过 Win32 模拟按键 Win + A
            try
            {
                var inputs = new NativeMethods.INPUT[4];
                inputs[0] = new NativeMethods.INPUT { type = NativeMethods.INPUT_KEYBOARD, U = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT { wVk = 0x5B } } }; // LWin
                inputs[1] = new NativeMethods.INPUT { type = NativeMethods.INPUT_KEYBOARD, U = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT { wVk = 0x41 } } }; // 'A'
                inputs[2] = new NativeMethods.INPUT { type = NativeMethods.INPUT_KEYBOARD, U = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT { wVk = 0x41, dwFlags = NativeMethods.KEYEVENTF_KEYUP } } };
                inputs[3] = new NativeMethods.INPUT { type = NativeMethods.INPUT_KEYBOARD, U = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT { wVk = 0x5B, dwFlags = NativeMethods.KEYEVENTF_KEYUP } } };
                NativeMethods.SendInput(4, inputs, System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.INPUT)));
            }
            catch { }
        }

        private void BtnCollapseTiles_MouseDown(object sender, MouseButtonEventArgs e)
        {
            TilesGrid.Visibility = TilesGrid.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            if (sender is TextBlock tb)
            {
                tb.Text = TilesGrid.Visibility == Visibility.Visible ? "折叠" : "展开";
            }
        }

        private void TileLocation_Click(object sender, RoutedEventArgs e) => OpenUri("ms-settings:privacy-location");

        private void TileBatterySaver_Click(object sender, RoutedEventArgs e) => OpenUri("ms-settings:batterysaver");

        private void TileBluetooth_Click(object sender, RoutedEventArgs e)
        {
            _isBluetoothActive = !_isBluetoothActive;
            TileBluetooth.Background = _isBluetoothActive 
                ? (SolidColorBrush)FindResource("AccentBlueBrush") 
                : (SolidColorBrush)FindResource("TileBgBrush");
            OpenUri("ms-settings:bluetooth");
        }

        private void TileNightLight_Click(object sender, RoutedEventArgs e) => OpenUri("ms-settings:nightlight");

        private void TileHotspot_Click(object sender, RoutedEventArgs e) => OpenUri("ms-settings:network-mobilehotspot");

        private void TileAirplane_Click(object sender, RoutedEventArgs e) => OpenUri("ms-settings:network-airplanemode");

        private void TileNearbySharing_Click(object sender, RoutedEventArgs e) => OpenUri("ms-settings:crossdevice");

        private void TileSettings_Click(object sender, RoutedEventArgs e) => OpenUri("ms-settings:");

        private void TileNetwork_Click(object sender, RoutedEventArgs e) => OpenUri("ms-settings:network");

        private void TileConnect_Click(object sender, RoutedEventArgs e) => OpenUri("ms-settings-connectabledevices:devicediscovery");

        private void TileProject_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo { FileName = "DisplaySwitch.exe", UseShellExecute = true }); } catch { }
        }

        private void TileVPN_Click(object sender, RoutedEventArgs e) => OpenUri("ms-settings:network-vpn");

        private void TileFocusAssist_Click(object sender, RoutedEventArgs e) => OpenUri("ms-settings:quiethours");

        private void TileScreenClip_Click(object sender, RoutedEventArgs e) => OpenUri("ms-screenclip:");

        #endregion
    }
}
