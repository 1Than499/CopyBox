using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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

        // 连续粘贴队列 (杀手级功能 3：顺序连续流)
        private readonly ObservableCollection<ClipboardItem> _pasteQueue = new();

        private string _currentCategory = "all";
        private bool _isPinned = true;
        private bool _isBluetoothActive = true;
        private Point _cardDragStartPoint;
        private bool _isDraggingCard = false;
        private ClipboardItem? _draggedItem;

        private string _previousView = "Clipboard";
        private bool _isUpdatingBrightnessUi = true;
        private bool _isUpdatingVolumeUi = true;
        private bool _isInternalSettingUpdate = false;

        #region 全局外部点击监听与智能失焦收起 (图钉锁定/解锁即刻生效核心引擎)

        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int WM_NCRBUTTONDOWN = 0x00A4;

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
        private LowLevelMouseProc? _mouseProc;
        private IntPtr _mouseHookHandle = IntPtr.Zero;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private void StartGlobalMouseHook()
        {
            if (_mouseHookHandle != IntPtr.Zero) return;
            try
            {
                _mouseProc = HookCallback;
                using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
                using var curModule = curProcess.MainModule;
                IntPtr hMod = curModule != null ? GetModuleHandle(curModule.ModuleName) : IntPtr.Zero;
                _mouseHookHandle = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hMod, 0);
            }
            catch { }
        }

        private void StopGlobalMouseHook()
        {
            if (_mouseHookHandle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHookHandle);
                _mouseHookHandle = IntPtr.Zero;
                _mouseProc = null;
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_NCLBUTTONDOWN || msg == WM_NCRBUTTONDOWN)
                {
                    // 仅当侧边栏可见且为未锁定状态时进行检测
                    if (this.IsVisible && !_isPinned)
                    {
                        var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                        int clickX = hookStruct.pt.x;
                        int clickY = hookStruct.pt.y;

                        IntPtr hwnd = new WindowInteropHelper(this).Handle;
                        if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out RECT rect))
                        {
                            bool inside = clickX >= rect.Left && clickX <= rect.Right && clickY >= rect.Top && clickY <= rect.Bottom;
                            if (!inside)
                            {
                                // 点击落在侧边栏物理边界外部，且当前没有模态或自定义动作弹窗
                                Dispatcher.InvokeAsync(() =>
                                {
                                    if (this.IsVisible && !_isPinned)
                                    {
                                        if (AddActionOverlay == null || AddActionOverlay.Visibility != Visibility.Visible)
                                        {
                                            SlideOutAndHide();
                                        }
                                    }
                                });
                            }
                        }
                    }
                }
            }
            return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
        }

        protected override void OnClosed(EventArgs e)
        {
            StopGlobalMouseHook();
            base.OnClosed(e);
        }

        #endregion

        public SidebarWindow(DatabaseService dbService, SettingsService settingsService, ClipboardMonitorService monitorService, PasteSimulator pasteSimulator)
        {
            _isInternalSettingUpdate = true;
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

            // 纯净化 Gamma 色彩，杜绝残留压暗色彩失真
            SystemControlService.ResetGammaRamp();

            // 初始化系统亮度与系统主音量双向同步定时器 (每 600ms 检查一次 Windows 系统外部调节，双向无缝对齐)
            _brightnessSyncTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(600)
            };
            _brightnessSyncTimer.Tick += (s, e) =>
            {
                if (this.IsVisible && SystemActionsPanel.Visibility == Visibility.Visible)
                {
                    // 1. 同步系统屏幕真实亮度 (当用户未正在拖动时才进行外部变化同步)
                    if (!_isUpdatingBrightnessUi && SliderBrightness != null && !SliderBrightness.IsMouseCaptureWithin)
                    {
                        try
                        {
                            int currentSys = SystemControlService.GetCurrentBrightness();
                            if (Math.Abs(currentSys - (int)SliderBrightness.Value) >= 1)
                            {
                                try
                                {
                                    _isUpdatingBrightnessUi = true;
                                    SliderBrightness.Value = currentSys;
                                    if (TxtBrightnessValue != null) TxtBrightnessValue.Text = $"{currentSys}%";
                                }
                                finally
                                {
                                    _isUpdatingBrightnessUi = false;
                                }

                                if (!SystemControlService.HasHardwareSupport)
                                {
                                    SystemControlService.ApplyVisualBrightness(currentSys);
                                }
                                else
                                {
                                    SystemControlService.ResetGammaRamp();
                                }
                            }
                            UpdateBrightnessIconText(currentSys);
                        }
                        catch { }
                    }

                    // 2. 同步系统真实主音量 (当用户未正在拖动时才进行外部变化同步)
                    if (!_isUpdatingVolumeUi && SliderVolume != null && !SliderVolume.IsMouseCaptureWithin)
                    {
                        try
                        {
                            int currentVol = SystemControlService.GetMasterVolume();
                            if (Math.Abs(currentVol - (int)SliderVolume.Value) >= 1)
                            {
                                try
                                {
                                    _isUpdatingVolumeUi = true;
                                    SliderVolume.Value = currentVol;
                                    if (TxtVolumeValue != null) TxtVolumeValue.Text = $"{currentVol}%";
                                }
                                finally
                                {
                                    _isUpdatingVolumeUi = false;
                                }
                            }
                            bool isMuted = SystemControlService.GetIsMuted();
                            UpdateVolumeIconText(currentVol, isMuted);
                        }
                        catch { }
                    }
                }
            };

            // 加载自定义动作磁贴
            RefreshCustomActions();

            // 实时同步当前系统屏幕真实亮度与系统真实主音量 (首次启动 100% 贴合系统当前真实值)
            try
            {
                _isUpdatingBrightnessUi = true;
                _isUpdatingVolumeUi = true;

                int sysBrightness = SystemControlService.GetCurrentBrightness();
                if (SliderBrightness != null)
                {
                    SliderBrightness.Value = sysBrightness;
                    if (TxtBrightnessValue != null) TxtBrightnessValue.Text = $"{sysBrightness}%";
                }
                UpdateBrightnessIconText(sysBrightness);

                int sysVol = SystemControlService.GetMasterVolume();
                if (SliderVolume != null)
                {
                    SliderVolume.Value = sysVol;
                    if (TxtVolumeValue != null) TxtVolumeValue.Text = $"{sysVol}%";
                }
                bool isMuted = SystemControlService.GetIsMuted();
                UpdateVolumeIconText(sysVol, isMuted);
            }
            catch { }
            finally
            {
                _isUpdatingBrightnessUi = false;
                _isUpdatingVolumeUi = false;
            }

            // 恢复随手记轻量便签内容 (杀手级功能 4)
            try
            {
                if (TxtScratchpad != null && !string.IsNullOrEmpty(_settingsService.CurrentSettings.ScratchpadContent))
                {
                    TxtScratchpad.Text = _settingsService.CurrentSettings.ScratchpadContent;
                    if (BdScratchpadInput != null) BdScratchpadInput.Visibility = Visibility.Visible;
                    if (IconScratchpadToggle != null) IconScratchpadToggle.Text = "▾";
                }
            }
            catch { }

            // 加载设置项与主题到侧边栏
            LoadSettingsToSidebarUI();

            // 首次应用多语言
            UpdateLocalizedTexts();

            _isInternalSettingUpdate = false;
        }

        private void UpdateBrightnessIconText(int brightness)
        {
            if (TxtBrightnessIcon != null)
            {
                TxtBrightnessIcon.Text = "\uE706"; // Segoe MDL2 Assets 统一高质感太阳矢量图标
            }
        }

        private void UpdateVolumeIconText(int vol, bool isMuted)
        {
            if (TxtVolumeIcon != null)
            {
                if (isMuted) TxtVolumeIcon.Text = "\uE74F"; // 静音
                else if (vol == 0) TxtVolumeIcon.Text = "\uE992"; // 0音量
                else if (vol < 35) TxtVolumeIcon.Text = "\uE993"; // 低音量
                else if (vol < 70) TxtVolumeIcon.Text = "\uE994"; // 中音量
                else TxtVolumeIcon.Text = "\uE767"; // 高音量
            }
        }

        #region 外观主题与多语言热更新 (支持深色/明亮/极光/暮色/深海/自定义壁纸)

        /// <summary>
        /// 确保默认星空暗调壁纸存在，如不存在则利用 GDI+ 绘制高质量渐变星夜壁纸并保存
        /// </summary>
        private string EnsureDefaultWallpaperExists()
        {
            try
            {
                string wallpapersDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CopyBox", "Wallpapers");
                Directory.CreateDirectory(wallpapersDir);
                string defaultPath = Path.Combine(wallpapersDir, "default_wallpaper.jpg");
                if (File.Exists(defaultPath)) return defaultPath;

                int width = 1920;
                int height = 1080;
                using var bmp = new System.Drawing.Bitmap(width, height);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                    // 绘制深空背景渐变: 从深邃墨蓝到暗夜黑
                    using (var lgb = new System.Drawing.Drawing2D.LinearGradientBrush(
                        new System.Drawing.Rectangle(0, 0, width, height),
                        System.Drawing.Color.FromArgb(12, 19, 36),
                        System.Drawing.Color.FromArgb(5, 7, 15),
                        45f))
                    {
                        g.FillRectangle(lgb, 0, 0, width, height);
                    }

                    // 叠加冰蓝极光漫射光晕
                    using (var path = new System.Drawing.Drawing2D.GraphicsPath())
                    {
                        path.AddEllipse(200, -100, 1200, 700);
                        using var pgb = new System.Drawing.Drawing2D.PathGradientBrush(path)
                        {
                            CenterColor = System.Drawing.Color.FromArgb(45, 14, 165, 233),
                            SurroundColors = new[] { System.Drawing.Color.Transparent }
                        };
                        g.FillPath(pgb, path);
                    }

                    // 叠加紫罗兰星云
                    using (var path2 = new System.Drawing.Drawing2D.GraphicsPath())
                    {
                        path2.AddEllipse(width - 900, height - 600, 1000, 700);
                        using var pgb2 = new System.Drawing.Drawing2D.PathGradientBrush(path2)
                        {
                            CenterColor = System.Drawing.Color.FromArgb(40, 168, 85, 247),
                            SurroundColors = new[] { System.Drawing.Color.Transparent }
                        };
                        g.FillPath(pgb2, path2);
                    }

                    // 随机微光星粒点缀
                    var rnd = new Random(42);
                    for (int i = 0; i < 200; i++)
                    {
                        int x = rnd.Next(width);
                        int y = rnd.Next(height);
                        int size = rnd.Next(1, 4);
                        int alpha = rnd.Next(40, 190);
                        using var starBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(alpha, 240, 248, 255));
                        g.FillEllipse(starBrush, x, y, size, size);
                    }
                }
                bmp.Save(defaultPath, System.Drawing.Imaging.ImageFormat.Jpeg);
                return defaultPath;
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// 安全加载壁纸图片为冻结的 BitmapImage
        /// </summary>
        private BitmapImage? LoadWallpaperBitmap(string? path)
        {
            try
            {
                string targetPath = path ?? "";
                if (string.IsNullOrEmpty(targetPath) || !File.Exists(targetPath))
                {
                    targetPath = EnsureDefaultWallpaperExists();
                }

                if (File.Exists(targetPath))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(targetPath, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 实时应用外观主题 (深色 / 明亮 / 极光 / 暮色 / 深海 / 自定义壁纸)，通过修改 Resources 实现 DynamicResource 毫秒级刷新
        /// </summary>
        public void ApplyTheme(string theme)
        {
            switch (theme)
            {
                case "Light":
                    if (BgImageContainer != null) BgImageContainer.Visibility = Visibility.Collapsed;
                    this.Resources["SidebarBgBrush"] = new SolidColorBrush(Color.FromArgb(0xFA, 0xFA, 0xFB, 0xFD));
                    this.Resources["SidebarBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));
                    this.Resources["HeaderBgBrush"] = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
                    this.Resources["CardBgBrush"] = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
                    this.Resources["CardHoverBgBrush"] = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));
                    this.Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));
                    this.Resources["TextPrimaryBrush"] = new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A));
                    this.Resources["TextSecondaryBrush"] = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69));
                    this.Resources["TileBgBrush"] = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));
                    this.Resources["TileHoverBgBrush"] = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0));
                    this.Resources["TabContainerBgBrush"] = new SolidColorBrush(Color.FromRgb(0xEE, 0xF2, 0xF6));
                    this.Resources["CodeBoxBgBrush"] = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));
                    this.Resources["CodeTextBrush"] = new SolidColorBrush(Color.FromRgb(0x02, 0x84, 0xC7));
                    this.Resources["AccentBlueBrush"] = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD7));
                    this.Resources["SliderTrackBgBrush"] = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));
                    this.Resources["MenuBgBrush"] = new SolidColorBrush(Color.FromArgb(0xFA, 0xFD, 0xFE, 0xFE));
                    this.Resources["MenuBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));
                    this.Resources["MenuTextBrush"] = new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A));
                    this.Resources["SelectedBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD7));
                    this.Resources["ScratchpadInputBgBrush"] = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));
                    this.Resources["ScratchpadInputBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));
                    this.Resources["QueueBarBgBrush"] = new SolidColorBrush(Color.FromRgb(0xEE, 0xF2, 0xF6));
                    break;

                case "Aurora": // 极光星空 (翠绿极光)
                    if (BgImageContainer != null) BgImageContainer.Visibility = Visibility.Collapsed;
                    this.Resources["SidebarBgBrush"] = new SolidColorBrush(Color.FromArgb(0xF4, 0x0B, 0x1A, 0x17));
                    this.Resources["SidebarBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x13, 0x4E, 0x4A));
                    this.Resources["HeaderBgBrush"] = new SolidColorBrush(Color.FromArgb(0xF6, 0x07, 0x14, 0x12));
                    this.Resources["CardBgBrush"] = new SolidColorBrush(Color.FromRgb(0x10, 0x26, 0x23));
                    this.Resources["CardHoverBgBrush"] = new SolidColorBrush(Color.FromRgb(0x16, 0x38, 0x33));
                    this.Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x1B, 0x4D, 0x45));
                    this.Resources["TextPrimaryBrush"] = new SolidColorBrush(Color.FromRgb(0xEC, 0xFD, 0xF5));
                    this.Resources["TextSecondaryBrush"] = new SolidColorBrush(Color.FromRgb(0x6E, 0xE7, 0xB7));
                    this.Resources["TileBgBrush"] = new SolidColorBrush(Color.FromRgb(0x0F, 0x24, 0x21));
                    this.Resources["TileHoverBgBrush"] = new SolidColorBrush(Color.FromRgb(0x17, 0x36, 0x31));
                    this.Resources["TabContainerBgBrush"] = new SolidColorBrush(Color.FromRgb(0x0A, 0x1C, 0x1A));
                    this.Resources["CodeBoxBgBrush"] = new SolidColorBrush(Color.FromRgb(0x08, 0x16, 0x14));
                    this.Resources["CodeTextBrush"] = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
                    this.Resources["AccentBlueBrush"] = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
                    this.Resources["SliderTrackBgBrush"] = new SolidColorBrush(Color.FromRgb(0x1E, 0x3D, 0x38));
                    this.Resources["MenuBgBrush"] = new SolidColorBrush(Color.FromArgb(0xF4, 0x0F, 0x24, 0x21));
                    this.Resources["MenuBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x13, 0x4E, 0x4A));
                    this.Resources["MenuTextBrush"] = new SolidColorBrush(Color.FromRgb(0xEC, 0xFD, 0xF5));
                    this.Resources["SelectedBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
                    this.Resources["ScratchpadInputBgBrush"] = new SolidColorBrush(Color.FromRgb(0x09, 0x19, 0x17));
                    this.Resources["ScratchpadInputBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x13, 0x4E, 0x4A));
                    this.Resources["QueueBarBgBrush"] = new SolidColorBrush(Color.FromRgb(0x11, 0x2E, 0x28));
                    break;

                case "Sunset": // 暮色霞光 (紫橙夕阳)
                    if (BgImageContainer != null) BgImageContainer.Visibility = Visibility.Collapsed;
                    this.Resources["SidebarBgBrush"] = new SolidColorBrush(Color.FromArgb(0xF4, 0x1C, 0x12, 0x20));
                    this.Resources["SidebarBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x4C, 0x1D, 0x44));
                    this.Resources["HeaderBgBrush"] = new SolidColorBrush(Color.FromArgb(0xF6, 0x16, 0x0D, 0x19));
                    this.Resources["CardBgBrush"] = new SolidColorBrush(Color.FromRgb(0x2A, 0x18, 0x2E));
                    this.Resources["CardHoverBgBrush"] = new SolidColorBrush(Color.FromRgb(0x3B, 0x22, 0x40));
                    this.Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x53, 0x26, 0x58));
                    this.Resources["TextPrimaryBrush"] = new SolidColorBrush(Color.FromRgb(0xFF, 0xF1, 0xF2));
                    this.Resources["TextSecondaryBrush"] = new SolidColorBrush(Color.FromRgb(0xF4, 0x72, 0xB6));
                    this.Resources["TileBgBrush"] = new SolidColorBrush(Color.FromRgb(0x23, 0x13, 0x27));
                    this.Resources["TileHoverBgBrush"] = new SolidColorBrush(Color.FromRgb(0x33, 0x1B, 0x38));
                    this.Resources["TabContainerBgBrush"] = new SolidColorBrush(Color.FromRgb(0x19, 0x0C, 0x1D));
                    this.Resources["CodeBoxBgBrush"] = new SolidColorBrush(Color.FromRgb(0x13, 0x08, 0x16));
                    this.Resources["CodeTextBrush"] = new SolidColorBrush(Color.FromRgb(0xFB, 0x71, 0x85));
                    this.Resources["AccentBlueBrush"] = new SolidColorBrush(Color.FromRgb(0xF9, 0x73, 0x16));
                    this.Resources["SliderTrackBgBrush"] = new SolidColorBrush(Color.FromRgb(0x4A, 0x20, 0x48));
                    this.Resources["MenuBgBrush"] = new SolidColorBrush(Color.FromArgb(0xF4, 0x23, 0x13, 0x27));
                    this.Resources["MenuBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x4C, 0x1D, 0x44));
                    this.Resources["MenuTextBrush"] = new SolidColorBrush(Color.FromRgb(0xFF, 0xF1, 0xF2));
                    this.Resources["SelectedBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xF9, 0x73, 0x16));
                    this.Resources["ScratchpadInputBgBrush"] = new SolidColorBrush(Color.FromRgb(0x15, 0x0A, 0x18));
                    this.Resources["ScratchpadInputBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x4C, 0x1D, 0x44));
                    this.Resources["QueueBarBgBrush"] = new SolidColorBrush(Color.FromRgb(0x2D, 0x16, 0x32));
                    break;

                case "Ocean": // 深海之渊 (深海天青)
                    if (BgImageContainer != null) BgImageContainer.Visibility = Visibility.Collapsed;
                    this.Resources["SidebarBgBrush"] = new SolidColorBrush(Color.FromArgb(0xF4, 0x0A, 0x13, 0x26));
                    this.Resources["SidebarBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x1E, 0x3B, 0x62));
                    this.Resources["HeaderBgBrush"] = new SolidColorBrush(Color.FromArgb(0xF6, 0x07, 0x0E, 0x1E));
                    this.Resources["CardBgBrush"] = new SolidColorBrush(Color.FromRgb(0x10, 0x21, 0x3D));
                    this.Resources["CardHoverBgBrush"] = new SolidColorBrush(Color.FromRgb(0x17, 0x2E, 0x54));
                    this.Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x1D, 0x3E, 0x6F));
                    this.Resources["TextPrimaryBrush"] = new SolidColorBrush(Color.FromRgb(0xF0, 0xF9, 0xFF));
                    this.Resources["TextSecondaryBrush"] = new SolidColorBrush(Color.FromRgb(0x7D, 0xD3, 0xFC));
                    this.Resources["TileBgBrush"] = new SolidColorBrush(Color.FromRgb(0x0C, 0x1A, 0x33));
                    this.Resources["TileHoverBgBrush"] = new SolidColorBrush(Color.FromRgb(0x13, 0x27, 0x4A));
                    this.Resources["TabContainerBgBrush"] = new SolidColorBrush(Color.FromRgb(0x08, 0x12, 0x23));
                    this.Resources["CodeBoxBgBrush"] = new SolidColorBrush(Color.FromRgb(0x05, 0x0E, 0x1B));
                    this.Resources["CodeTextBrush"] = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
                    this.Resources["AccentBlueBrush"] = new SolidColorBrush(Color.FromRgb(0x06, 0xB6, 0xD4));
                    this.Resources["SliderTrackBgBrush"] = new SolidColorBrush(Color.FromRgb(0x1A, 0x32, 0x54));
                    this.Resources["MenuBgBrush"] = new SolidColorBrush(Color.FromArgb(0xF4, 0x0C, 0x1A, 0x33));
                    this.Resources["MenuBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x1E, 0x3B, 0x62));
                    this.Resources["MenuTextBrush"] = new SolidColorBrush(Color.FromRgb(0xF0, 0xF9, 0xFF));
                    this.Resources["SelectedBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x06, 0xB6, 0xD4));
                    this.Resources["ScratchpadInputBgBrush"] = new SolidColorBrush(Color.FromRgb(0x06, 0x10, 0x1F));
                    this.Resources["ScratchpadInputBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x1E, 0x3B, 0x62));
                    this.Resources["QueueBarBgBrush"] = new SolidColorBrush(Color.FromRgb(0x12, 0x25, 0x43));
                    break;

                case "CustomImage": // 自定义图片背景 + 磨砂亚克力层级全面适配
                    if (BgImageContainer != null)
                    {
                        BgImageContainer.Visibility = Visibility.Visible;
                        if (ImgCustomBg != null)
                        {
                            ImgCustomBg.Source = LoadWallpaperBitmap(_settingsService?.CurrentSettings.CustomBackgroundImagePath);
                        }
                        if (BdCustomBgOverlay != null)
                        {
                            BdCustomBgOverlay.Opacity = _settingsService?.CurrentSettings.BackgroundOverlayOpacity ?? 0.75;
                        }
                    }

                    // 抽屉背景全透，显现图片与遮罩
                    this.Resources["SidebarBgBrush"] = Brushes.Transparent;
                    this.Resources["SidebarBorderBrush"] = new SolidColorBrush(Color.FromArgb(0x70, 0x64, 0x74, 0x8B));
                    this.Resources["HeaderBgBrush"] = new SolidColorBrush(Color.FromArgb(0x65, 0x0F, 0x17, 0x2A));
                    // 剪贴板卡片：微透明深色磨砂亚克力
                    this.Resources["CardBgBrush"] = new SolidColorBrush(Color.FromArgb(0x55, 0x1E, 0x29, 0x3B));
                    this.Resources["CardHoverBgBrush"] = new SolidColorBrush(Color.FromArgb(0x80, 0x33, 0x41, 0x55));
                    this.Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromArgb(0x55, 0x94, 0xA3, 0xB8));
                    this.Resources["TextPrimaryBrush"] = Brushes.White;
                    this.Resources["TextSecondaryBrush"] = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));
                    this.Resources["TileBgBrush"] = new SolidColorBrush(Color.FromArgb(0x55, 0x1E, 0x29, 0x3B));
                    this.Resources["TileHoverBgBrush"] = new SolidColorBrush(Color.FromArgb(0x80, 0x33, 0x41, 0x55));
                    this.Resources["TabContainerBgBrush"] = new SolidColorBrush(Color.FromArgb(0x75, 0x0F, 0x17, 0x2A));
                    this.Resources["CodeBoxBgBrush"] = new SolidColorBrush(Color.FromArgb(0x80, 0x0F, 0x17, 0x2A));
                    this.Resources["CodeTextBrush"] = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
                    this.Resources["AccentBlueBrush"] = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
                    this.Resources["SliderTrackBgBrush"] = new SolidColorBrush(Color.FromArgb(0x70, 0x64, 0x74, 0x8B));
                    this.Resources["MenuBgBrush"] = new SolidColorBrush(Color.FromArgb(0xF2, 0x1E, 0x29, 0x3B));
                    this.Resources["MenuBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69));
                    this.Resources["MenuTextBrush"] = Brushes.White;
                    this.Resources["SelectedBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
                    this.Resources["ScratchpadInputBgBrush"] = new SolidColorBrush(Color.FromArgb(0x75, 0x0F, 0x17, 0x2A));
                    this.Resources["ScratchpadInputBorderBrush"] = new SolidColorBrush(Color.FromArgb(0x60, 0x94, 0xA3, 0xB8));
                    this.Resources["QueueBarBgBrush"] = new SolidColorBrush(Color.FromArgb(0x80, 0x1E, 0x29, 0x3B));
                    break;

                default: // 默认暗夜深色 (极夜蓝黑)
                    if (BgImageContainer != null) BgImageContainer.Visibility = Visibility.Collapsed;
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
                    this.Resources["AccentBlueBrush"] = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD7));
                    this.Resources["SliderTrackBgBrush"] = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55));
                    this.Resources["MenuBgBrush"] = new SolidColorBrush(Color.FromArgb(0xF2, 0x1A, 0x20, 0x2C));
                    this.Resources["MenuBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55));
                    this.Resources["MenuTextBrush"] = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC));
                    this.Resources["SelectedBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD7));
                    this.Resources["ScratchpadInputBgBrush"] = new SolidColorBrush(Color.FromRgb(0x11, 0x15, 0x1F));
                    this.Resources["ScratchpadInputBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x23, 0x2C, 0x3D));
                    this.Resources["QueueBarBgBrush"] = new SolidColorBrush(Color.FromRgb(0x1E, 0x2A, 0x42));
                    break;
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

            // 随手记与连续粘贴队列多语言 (杀手级新功能)
            if (TxtScratchpadTitle != null) TxtScratchpadTitle.Text = loc.Get("ScratchpadTitle");
            if (BtnScratchpadCopy != null) BtnScratchpadCopy.ToolTip = loc.Get("ScratchpadCopy");
            if (BtnScratchpadClear != null) BtnScratchpadClear.ToolTip = loc.Get("ScratchpadClear");
            if (BtnPasteNextInQueue != null) BtnPasteNextInQueue.Content = loc.Get("QueuePasteNext");
            if (BtnClearQueue != null) BtnClearQueue.ToolTip = loc.Get("QueueClear");
            UpdateQueueUi();

            // 快捷看板各板块
            if (TxtSectionCoreShortcuts != null) TxtSectionCoreShortcuts.Text = loc.Get("SidebarCoreActions");
            if (TxtSectionCustomActions != null) TxtSectionCustomActions.Text = loc.Get("SidebarCustomActions");
            if (TxtTipRightClickDelete != null) TxtTipRightClickDelete.Text = loc.Get("SidebarRightClickDelete");
            if (TxtBtnAddAction != null) TxtBtnAddAction.Text = loc.Get("SidebarAddAction");
            if (TxtBrightnessTitle != null) TxtBrightnessTitle.Text = loc.Get("SidebarBrightness");
            if (TxtVolumeTitle != null) TxtVolumeTitle.Text = loc.Get("SidebarVolume");

            // 偏好设置页面
            if (TxtSettingsHeaderTitle != null) TxtSettingsHeaderTitle.Text = loc.Get("SidebarPreferences");
            if (TxtSetModeTitle != null) TxtSetModeTitle.Text = loc.Get("WindowModeTitle");
            if (RadioModeSidebar2 != null) RadioModeSidebar2.Content = loc.Get("WindowModeSidebar");
            if (RadioModeCentered2 != null) RadioModeCentered2.Content = loc.Get("WindowModeCentered");
            if (TxtSetLangTitle != null) TxtSetLangTitle.Text = loc.Get("LangSectionTitle");
            if (RadioLangZh2 != null) RadioLangZh2.Content = loc.Get("LangZh");
            if (RadioLangEn2 != null) RadioLangEn2.Content = loc.Get("LangEn");
            if (TxtSetThemeTitle != null) TxtSetThemeTitle.Text = loc.Get("AppearanceTitle");
            if (TxtSetOpacityTitle != null) TxtSetOpacityTitle.Text = loc.Get("OpacityLabel");
            if (TxtSetStorageTitle != null) TxtSetStorageTitle.Text = loc.Get("StoragePathTitle");
            if (BtnBrowseStorage != null) BtnBrowseStorage.Content = loc.Get("BrowseButton");
            if (TxtSetInteractionTitle != null) TxtSetInteractionTitle.Text = loc.Get("DoubleClickTitle");
            if (ChkHideOnDeactivate2 != null)
            {
                ChkHideOnDeactivate2.Content = "📌 " + loc.Get("PinSettingTitle");
                ChkHideOnDeactivate2.ToolTip = loc.Get("PinSettingTooltip");
            }
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

            // 实时校准图钉状态与设置项保持绝对一致
            if (_settingsService != null)
            {
                _isPinned = !_settingsService.CurrentSettings.HideOnDeactivate;
                UpdatePinUiState();
            }

            // 同步当前系统真实亮度与主音量
            try
            {
                _isUpdatingBrightnessUi = true;
                _isUpdatingVolumeUi = true;

                int sysBrightness = SystemControlService.GetCurrentBrightness();
                if (SliderBrightness != null)
                {
                    SliderBrightness.Value = sysBrightness;
                    if (TxtBrightnessValue != null) TxtBrightnessValue.Text = $"{sysBrightness}%";
                }
                UpdateBrightnessIconText(sysBrightness);

                int sysVol = SystemControlService.GetMasterVolume();
                if (SliderVolume != null)
                {
                    SliderVolume.Value = sysVol;
                    if (TxtVolumeValue != null) TxtVolumeValue.Text = $"{sysVol}%";
                }
                bool isMuted = SystemControlService.GetIsMuted();
                UpdateVolumeIconText(sysVol, isMuted);
            }
            catch { }
            finally
            {
                _isUpdatingBrightnessUi = false;
                _isUpdatingVolumeUi = false;
            }

            _brightnessSyncTimer.Start();

            if (!_isPinned)
            {
                StartGlobalMouseHook();
            }

            this.Show();
            this.Activate();

            // 顺畅渐变显现，动画完成后解除时钟绑定，使滑块手动调节实时生效
            double finalOpacity = Math.Max(0.08, _settingsService?.CurrentSettings?.WindowOpacity ?? 1.0);
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
            StopGlobalMouseHook();
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

            if (this.IsVisible)
            {
                if (!_isPinned)
                {
                    StartGlobalMouseHook();
                }
                else
                {
                    StopGlobalMouseHook();
                }
            }
        }

        private void UpdatePinUiState()
        {
            var loc = LocalizationService.Instance;
            if (TxtPinIcon != null) TxtPinIcon.Text = _isPinned ? "📌" : "🔓";
            if (BtnPin != null) BtnPin.ToolTip = _isPinned ? loc.Get("PinLockedTip") : loc.Get("PinUnlockedTip");
            if (ChkHideOnDeactivate2 != null)
            {
                _isInternalSettingUpdate = true;
                ChkHideOnDeactivate2.IsChecked = _isPinned;
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

                // 智能空状态反馈
                if (EmptyCardsStatePanel != null)
                {
                    EmptyCardsStatePanel.Visibility = DisplayCards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    if (TxtEmptyCategoryTip != null)
                    {
                        if (!string.IsNullOrEmpty(query))
                        {
                            TxtEmptyCategoryTip.Text = "未找到与搜索匹配的内容";
                        }
                        else
                        {
                            string catName = _currentCategory.ToLowerInvariant() switch
                            {
                                "text" => "文本",
                                "image" => "图片",
                                "code" => "代码",
                                "pinned" => "置顶",
                                _ => "全部"
                            };
                            TxtEmptyCategoryTip.Text = $"当前【{catName}】分类暂无记录";
                        }
                    }
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
                if (CardsListBox != null && CardsListBox.Items.Count > 0)
                {
                    CardsListBox.ScrollIntoView(CardsListBox.Items[0]);
                }
            }
        }

        private void TxtSearch_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            this.Activate();
        }

        private void CardsListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _cardDragStartPoint = e.GetPosition(null);
            _isDraggingCard = false;
            _draggedItem = null;

            // 检查点击的源控件：如果是操作按钮（置顶、进队列、复制、删除、三点菜单），不捕获拖拽
            var original = e.OriginalSource as DependencyObject;
            if (FindVisualParent<Button>(original) != null)
            {
                return;
            }

            // 从点击源向上查找到所在的 ListBoxItem 与 ClipboardItem
            var lbi = FindVisualParent<ListBoxItem>(original);
            if (lbi != null && lbi.DataContext is ClipboardItem item)
            {
                _draggedItem = item;
                lbi.IsSelected = true;
                CardsListBox.SelectedItem = item;
            }
        }

        private void CardsListBox_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && !_isDraggingCard && _draggedItem != null)
            {
                Point currentPos = e.GetPosition(null);
                Vector diff = _cardDragStartPoint - currentPos;

                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    var targetItem = _draggedItem;
                    _isDraggingCard = true;
                    try
                    {
                        var data = new DataObject();
                        if (targetItem.IsImage && targetItem.ImagePreview is BitmapSource bmp)
                        {
                            // 1. 标准图片位图格式 (供 Word/富文本/图形设计软件直接接收)
                            data.SetImage(bmp);

                            // 2. 物理临时文件格式 (供微信、QQ、浏览器文件上传、桌面/文件夹直接释放)
                            try
                            {
                                string tempDir = Path.Combine(Path.GetTempPath(), "CopyBoxDrag");
                                Directory.CreateDirectory(tempDir);
                                string tempPath = Path.Combine(tempDir, $"clip_{DateTime.Now:yyyyMMdd_HHmmss}_{targetItem.Id}.png");
                                using (var fs = new FileStream(tempPath, FileMode.Create))
                                {
                                    var enc = new PngBitmapEncoder();
                                    enc.Frames.Add(BitmapFrame.Create(bmp));
                                    enc.Save(fs);
                                }
                                var files = new System.Collections.Specialized.StringCollection { tempPath };
                                data.SetFileDropList(files);
                            }
                            catch { }
                        }
                        else if (!string.IsNullOrEmpty(targetItem.Content))
                        {
                            // 1. Unicode 纯文本格式 (供所有现代应用、VS Code、记事本、浏览器、聊天窗口接收)
                            data.SetText(targetItem.Content, TextDataFormat.UnicodeText);
                            data.SetData(DataFormats.UnicodeText, targetItem.Content);
                            data.SetData(DataFormats.Text, targetItem.Content);
                            data.SetData(DataFormats.StringFormat, targetItem.Content);
                        }

                        // 执行系统级 OLE 拖拽 (支持 Copy 与 Move)
                        DragDrop.DoDragDrop(CardsListBox, data, DragDropEffects.Copy | DragDropEffects.Move);
                    }
                    catch { }
                    finally
                    {
                        _isDraggingCard = false;
                        _draggedItem = null;
                    }
                }
            }
        }

        private async void CardsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (CardsListBox.SelectedItem is ClipboardItem selected)
            {
                if (_settingsService.CurrentSettings.DoubleClickAction == "PasteAndHide")
                {
                    await _pasteSimulator.PasteItemAsync(selected, () =>
                    {
                        SlideOutAndHide();
                    });
                }
                else
                {
                    // 仅复制到剪贴板，保持窗口打开 (智能识别图像实体与纯文本，杜绝图片路径被作为文本二次收录)
                    CopyItemToClipboard(selected);
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

        private void ListBoxItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListBoxItem lbi && lbi.DataContext is ClipboardItem item)
            {
                CardsListBox.SelectedItem = item;
            }
        }

        private void ListBoxItem_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListBoxItem lbi && lbi.DataContext is ClipboardItem item)
            {
                e.Handled = true;
                ShowCardContextMenu(item, lbi, isFromRightClick: true);
            }
        }

        private void BtnCardMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int id)
            {
                var item = DisplayCards.FirstOrDefault(x => x.Id == id);
                if (item != null)
                {
                    ShowCardContextMenu(item, btn, isFromRightClick: false);
                }
            }
        }

        private void BtnCardEnqueue_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement elem && elem.DataContext is ClipboardItem item)
            {
                EnqueueCard(item);
            }
        }

        private void ShowCardContextMenu(ClipboardItem item, FrameworkElement placementTarget, bool isFromRightClick)
        {
            if (item == null) return;
            CardsListBox.SelectedItem = item;

            var loc = LocalizationService.Instance;
            var menu = new ContextMenu();
            if (TryFindResource("ModernContextMenuStyle") is Style ctxStyle)
            {
                menu.Style = ctxStyle;
            }

            Style? itemStyle = TryFindResource("ModernMenuItemStyle") as Style;

            // 1. 置顶 / 取消置顶 (★)
            var pinIcon = new TextBlock 
            { 
                Text = "★", 
                Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11)), 
                FontSize = 13, 
                FontWeight = FontWeights.Bold, 
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var pinItem = new MenuItem 
            { 
                Icon = pinIcon,
                Header = item.IsPinned ? loc.Get("MenuUnpin") : loc.Get("MenuPin") 
            };
            if (itemStyle != null) pinItem.Style = itemStyle;
            pinItem.Click += (s, args) =>
            {
                _dbService.TogglePin(item.Id);
                RefreshCards();
            };

            // 2. 加入连续粘贴队列 (⮑)
            var queueIcon = new TextBlock 
            { 
                Text = "⮑", 
                Foreground = new SolidColorBrush(Color.FromRgb(96, 165, 250)), 
                FontSize = 13, 
                FontWeight = FontWeights.Bold, 
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var queueItem = new MenuItem 
            { 
                Icon = queueIcon,
                Header = loc.Get("MenuEnqueue") 
            };
            if (itemStyle != null) queueItem.Style = itemStyle;
            queueItem.Click += (s, args) =>
            {
                EnqueueCard(item);
            };

            // 3. 复制到剪贴板 (❐)
            var copyIcon = new TextBlock 
            { 
                Text = "❐", 
                Foreground = new SolidColorBrush(Color.FromRgb(0, 120, 212)), 
                FontSize = 13, 
                FontWeight = FontWeights.Bold, 
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var copyItem = new MenuItem 
            { 
                Icon = copyIcon,
                Header = loc.Get("MenuCopy") 
            };
            if (itemStyle != null) copyItem.Style = itemStyle;
            copyItem.Click += (s, args) =>
            {
                CopyItemToClipboard(item);
            };

            // 4. 删除 (✕)
            var delIcon = new TextBlock 
            { 
                Text = "✕", 
                Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)), 
                FontSize = 13, 
                FontWeight = FontWeights.Bold, 
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var delItem = new MenuItem 
            { 
                Icon = delIcon,
                Header = loc.Get("MenuDelete") 
            };
            if (itemStyle != null) delItem.Style = itemStyle;
            delItem.Click += (s, args) =>
            {
                _dbService.DeleteItem(item.Id);
                RefreshCards();
            };

            menu.Items.Add(pinItem);
            menu.Items.Add(queueItem);
            menu.Items.Add(copyItem);

            var sep = new Separator();
            if (TryFindResource("ModernMenuSeparatorStyle") is Style sepStyle)
            {
                sep.Style = sepStyle;
            }
            menu.Items.Add(sep);
            menu.Items.Add(delItem);

            // 关键：确保右键点击卡片弹出的面板与点击三点菜单完全一致 (毛玻璃圆角、主题色、阴影)
            menu.PlacementTarget = placementTarget;
            menu.Resources = this.Resources;

            if (isFromRightClick)
            {
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            }
            else
            {
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.HorizontalOffset = -130;
                menu.VerticalOffset = 2;
            }

            menu.IsOpen = true;
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
            if (sender is FrameworkElement elem && elem.DataContext is ClipboardItem item)
            {
                CopyItemToClipboard(item);
            }
        }

        /// <summary>
        /// 统一安全剪贴板写入引擎：区分图片实体与纯文本，安全托管 internal 操作标记，杜绝图片物理路径被作为纯文本二次捕获
        /// </summary>
        private void CopyItemToClipboard(ClipboardItem item)
        {
            if (item == null) return;
            try
            {
                if (_monitorService != null) _monitorService.IsInternalOperation = true;

                if (item.IsImage && !string.IsNullOrEmpty(item.ResolvedImagePath) && File.Exists(item.ResolvedImagePath))
                {
                    var data = new DataObject();
                    if (item.ImagePreview is BitmapSource bmp)
                    {
                        data.SetImage(bmp);
                    }
                    else
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.UriSource = new Uri(item.ResolvedImagePath, UriKind.Absolute);
                        bitmap.EndInit();
                        bitmap.Freeze();
                        data.SetImage(bitmap);
                    }

                    var files = new System.Collections.Specialized.StringCollection { item.ResolvedImagePath };
                    data.SetFileDropList(files);
                    Clipboard.SetDataObject(data, true);
                }
                else if (item.ItemType == "File")
                {
                    var sc = new System.Collections.Specialized.StringCollection();
                    var lines = item.Content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var l in lines) sc.Add(l);
                    Clipboard.SetFileDropList(sc);
                }
                else
                {
                    Clipboard.SetText(item.Content ?? string.Empty);
                }
            }
            catch { }
            finally
            {
                if (_monitorService != null)
                {
                    Task.Delay(500).ContinueWith(_ => _monitorService.IsInternalOperation = false);
                }
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

        #region 杀手级功能 3：连续粘贴队列 (Paste Queue / 顺序流)

        private void EnqueueCard(ClipboardItem item)
        {
            if (item == null) return;
            _pasteQueue.Add(item);
            UpdateQueueUi();
        }

        private void UpdateQueueUi()
        {
            if (PasteQueueBar == null || TxtQueueInfo == null) return;
            if (_pasteQueue.Count > 0)
            {
                PasteQueueBar.Visibility = Visibility.Visible;
                var loc = LocalizationService.Instance;
                TxtQueueInfo.Text = $"{loc.Get("QueueTitle")} ({string.Format(loc.Get("QueueCount"), _pasteQueue.Count)})";
            }
            else
            {
                PasteQueueBar.Visibility = Visibility.Collapsed;
            }
        }

        private async void BtnPasteNextInQueue_Click(object sender, RoutedEventArgs e)
        {
            if (_pasteQueue.Count == 0)
            {
                UpdateQueueUi();
                return;
            }

            var nextItem = _pasteQueue[0];
            _pasteQueue.RemoveAt(0);
            UpdateQueueUi();

            await _pasteSimulator.PasteItemAsync(nextItem, () =>
            {
                if (_pasteQueue.Count == 0 && !_isPinned)
                {
                    SlideOutAndHide();
                }
            });
        }

        private void BtnClearQueue_Click(object sender, RoutedEventArgs e)
        {
            _pasteQueue.Clear();
            UpdateQueueUi();
        }

        #endregion

        #region 杀手级功能 4：顶部随手记轻量便签 (Scratchpad)

        private void ScratchpadHeader_Click(object sender, MouseButtonEventArgs e)
        {
            if (BdScratchpadInput == null || IconScratchpadToggle == null || TxtScratchpad == null) return;
            bool willShow = BdScratchpadInput.Visibility != Visibility.Visible;
            BdScratchpadInput.Visibility = willShow ? Visibility.Visible : Visibility.Collapsed;
            IconScratchpadToggle.Text = willShow ? "▾" : "▸";
            if (willShow)
            {
                this.Activate();
                TxtScratchpad.Focus();
            }
        }

        private void TxtScratchpad_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            this.Activate();
        }

        private void TxtScratchpad_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_settingsService == null || TxtScratchpad == null) return;
            _settingsService.CurrentSettings.ScratchpadContent = TxtScratchpad.Text;
            _settingsService.SaveSettings();
        }

        private void BtnScratchpadCopy_Click(object sender, RoutedEventArgs e)
        {
            if (TxtScratchpad != null && !string.IsNullOrEmpty(TxtScratchpad.Text))
            {
                Clipboard.SetText(TxtScratchpad.Text);
            }
        }

        private void BtnScratchpadClear_Click(object sender, RoutedEventArgs e)
        {
            if (TxtScratchpad != null)
            {
                TxtScratchpad.Text = string.Empty;
            }
        }

        #endregion

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
            UpdateBrightnessIconText(val);
        }

        private void TxtBrightnessIcon_Click(object sender, MouseButtonEventArgs e)
        {
            int current = SystemControlService.GetCurrentBrightness();
            int target = current >= 90 ? 50 : 100;
            if (SliderBrightness != null) SliderBrightness.Value = target;
        }

        private void SliderBrightness_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            int delta = e.Delta > 0 ? 5 : -5;
            if (SliderBrightness != null)
            {
                SliderBrightness.Value = Math.Clamp(SliderBrightness.Value + delta, 0, 100);
            }
            e.Handled = true;
        }

        // 系统主音量真实滑动调节 (与系统双向保持一致，范围 0~100)
        private void SliderVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingVolumeUi) return;
            int val = (int)e.NewValue;
            if (TxtVolumeValue != null) TxtVolumeValue.Text = $"{val}%";
            SystemControlService.SetMasterVolume(val);
            UpdateVolumeIconText(val, false);
        }

        private void SliderVolume_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            int delta = e.Delta > 0 ? 2 : -2;
            if (SliderVolume != null)
            {
                SliderVolume.Value = Math.Clamp(SliderVolume.Value + delta, 0, 100);
            }
            e.Handled = true;
        }

        private void TxtVolumeIcon_Click(object sender, MouseButtonEventArgs e)
        {
            bool isMuted = SystemControlService.ToggleMute();
            int currentVol = SystemControlService.GetMasterVolume();
            UpdateVolumeIconText(currentVol, isMuted);
            if (!isMuted && SliderVolume != null)
            {
                _isUpdatingVolumeUi = true;
                SliderVolume.Value = currentVol;
                if (TxtVolumeValue != null) TxtVolumeValue.Text = $"{currentVol}%";
                _isUpdatingVolumeUi = false;
            }
        }

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
            borderFactory.SetResourceReference(Border.BackgroundProperty, "TileBgBrush");
            borderFactory.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));

            var stack = new FrameworkElementFactory(typeof(StackPanel));
            stack.SetValue(StackPanel.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            stack.SetValue(StackPanel.VerticalAlignmentProperty, VerticalAlignment.Center);

            var iconTxt = new FrameworkElementFactory(typeof(TextBlock));
            iconTxt.SetValue(TextBlock.TextProperty, string.IsNullOrEmpty(action.Icon) ? "🚀" : action.Icon);
            iconTxt.SetValue(TextBlock.FontSizeProperty, 16.0);
            iconTxt.SetResourceReference(TextBlock.ForegroundProperty, "AccentBlueBrush");
            iconTxt.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);

            var nameTxt = new FrameworkElementFactory(typeof(TextBlock));
            nameTxt.SetValue(TextBlock.TextProperty, action.Name);
            nameTxt.SetValue(TextBlock.FontSizeProperty, 10.5);
            nameTxt.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            nameTxt.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
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

            // 主题单选回显与壁纸状态
            try
            {
                _isInternalSettingUpdate = true;
                switch (s.ThemeMode)
                {
                    case "Light":
                        if (RadioThemeLight2 != null) RadioThemeLight2.IsChecked = true;
                        break;
                    case "Aurora":
                        if (RadioThemeAurora2 != null) RadioThemeAurora2.IsChecked = true;
                        break;
                    case "Sunset":
                        if (RadioThemeSunset2 != null) RadioThemeSunset2.IsChecked = true;
                        break;
                    case "Ocean":
                        if (RadioThemeOcean2 != null) RadioThemeOcean2.IsChecked = true;
                        break;
                    case "CustomImage":
                        if (RadioThemeCustom2 != null) RadioThemeCustom2.IsChecked = true;
                        break;
                    default:
                        if (RadioThemeDark2 != null) RadioThemeDark2.IsChecked = true;
                        break;
                }

                if (BdCustomImageSettings != null)
                {
                    BdCustomImageSettings.Visibility = (s.ThemeMode == "CustomImage") ? Visibility.Visible : Visibility.Collapsed;
                }
                if (SliderBgOverlayOpacity != null)
                {
                    int overlayVal = (int)Math.Round(s.BackgroundOverlayOpacity * 100);
                    SliderBgOverlayOpacity.Value = Math.Clamp(overlayVal, 30, 95);
                    if (TxtBgOverlayOpacityVal != null) TxtBgOverlayOpacityVal.Text = $"{SliderBgOverlayOpacity.Value}%";
                }
                if (TxtCustomBgPath != null)
                {
                    TxtCustomBgPath.Text = string.IsNullOrEmpty(s.CustomBackgroundImagePath)
                        ? "未选择本地图片 (当前使用内置极光壁纸)"
                        : Path.GetFileName(s.CustomBackgroundImagePath);
                }
            }
            finally
            {
                _isInternalSettingUpdate = false;
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
            if (_isInternalSettingUpdate || _settingsService == null) return;
            if (sender is not RadioButton rb || rb.Tag is not string tag) return;
            if (rb.IsChecked != true) return;

            _settingsService.CurrentSettings.ThemeMode = tag;
            _settingsService.SaveSettings();

            if (BdCustomImageSettings != null)
            {
                BdCustomImageSettings.Visibility = (tag == "CustomImage") ? Visibility.Visible : Visibility.Collapsed;
            }

            // 立即生效外观切换
            ApplyTheme(tag);
        }

        private void BtnUploadCustomBg_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "选择自定义背景壁纸图片",
                    Filter = "图片文件 (*.jpg;*.jpeg;*.png;*.bmp;*.webp)|*.jpg;*.jpeg;*.png;*.bmp;*.webp|所有文件 (*.*)|*.*",
                    Multiselect = false
                };

                if (dialog.ShowDialog() == true)
                {
                    string sourcePath = dialog.FileName;
                    if (!File.Exists(sourcePath)) return;

                    string wallpapersDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CopyBox", "Wallpapers");
                    Directory.CreateDirectory(wallpapersDir);

                    string ext = Path.GetExtension(sourcePath);
                    string destFileName = $"custom_bg_{DateTime.Now:yyyyMMdd_HHmmss}{ext}";
                    string destPath = Path.Combine(wallpapersDir, destFileName);

                    File.Copy(sourcePath, destPath, true);

                    _settingsService.CurrentSettings.CustomBackgroundImagePath = destPath;
                    _settingsService.CurrentSettings.ThemeMode = "CustomImage";
                    _settingsService.SaveSettings();

                    if (TxtCustomBgPath != null)
                    {
                        TxtCustomBgPath.Text = Path.GetFileName(sourcePath);
                    }

                    if (RadioThemeCustom2 != null)
                    {
                        RadioThemeCustom2.IsChecked = true;
                    }

                    ApplyTheme("CustomImage");
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"设置壁纸失败: {ex.Message}", "壁纸设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnResetCustomBg_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _settingsService.CurrentSettings.CustomBackgroundImagePath = "";
                _settingsService.SaveSettings();

                if (TxtCustomBgPath != null)
                {
                    TxtCustomBgPath.Text = "未选择本地图片 (当前使用内置极光壁纸)";
                }

                ApplyTheme("CustomImage");
            }
            catch { }
        }

        private void SliderBgOverlayOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isInternalSettingUpdate || _settingsService == null) return;
            int intVal = (int)e.NewValue;
            double opacity = intVal / 100.0;
            _settingsService.CurrentSettings.BackgroundOverlayOpacity = opacity;
            _settingsService.SaveSettings();

            if (TxtBgOverlayOpacityVal != null)
            {
                TxtBgOverlayOpacityVal.Text = $"{intVal}%";
            }

            if (BdCustomBgOverlay != null)
            {
                BdCustomBgOverlay.Opacity = opacity;
            }
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
            bool isPinned = ChkHideOnDeactivate2.IsChecked == true;
            _isPinned = isPinned;
            _settingsService.CurrentSettings.HideOnDeactivate = !_isPinned;
            _settingsService.SaveSettings();

            UpdatePinUiState();

            if (this.IsVisible)
            {
                if (!_isPinned)
                {
                    StartGlobalMouseHook();
                }
                else
                {
                    StopGlobalMouseHook();
                }
            }
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

        #region 视觉树辅助函数

        private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T parent) return parent;
                child = VisualTreeHelper.GetParent(child);
            }
            return null;
        }

        #endregion
    }
}
