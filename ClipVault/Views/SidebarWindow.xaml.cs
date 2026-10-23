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
        private readonly DispatcherTimer _edgeDetectTimer;

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
        private bool _isSavingOwnSettings = false;
        private DispatcherTimer? _saveSettingsDebounceTimer;
        private string? _cachedWallpaperPath;
        private BitmapImage? _cachedWallpaperBitmap;

        // 贴边悬停唤醒与平滑动画防重入状态
        private int _edgeHoverTicks = 0;
        private int _outsideHoverTicks = 0;
        private bool _mouseHasEnteredAfterShow = false;
        private DateTime _lastHideTime = DateTime.MinValue;
        private bool _isModalOrMenuOpen = false;
        private int _animGeneration = 0;
        private bool _isHiding = false;

        #region 全局外部点击监听、贴边悬停自动唤醒与智能失焦收起

        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int WM_NCRBUTTONDOWN = 0x00A4;
        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

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

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

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
                    if (this.IsVisible && !_isHiding && !_isPinned && !_isModalOrMenuOpen)
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
                                    if (this.IsVisible && !_isHiding && !_isPinned && !_isModalOrMenuOpen)
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

        /// <summary>
        /// 贴边悬停自动唤醒与滑出巡检引擎 (每 50ms 探测一次屏幕右边缘光标位置)
        /// </summary>
        private void OnEdgeDetectTimerTick(object? sender, EventArgs e)
        {
            try
            {
                if (_settingsService == null || _settingsService.CurrentSettings.WindowMode != "Sidebar")
                {
                    _edgeHoverTicks = 0;
                    _outsideHoverTicks = 0;
                    return;
                }

                if (_isModalOrMenuOpen || _isDraggingCard)
                {
                    _edgeHoverTicks = 0;
                    _outsideHoverTicks = 0;
                    return;
                }

                if (!GetCursorPos(out POINT pt)) return;

                int screenW = GetSystemMetrics(SM_CXSCREEN);
                int screenH = GetSystemMetrics(SM_CYSCREEN);
                if (screenW <= 0 || screenH <= 0) return;

                // 场景 1：当侧边栏处于贴边收起隐藏状态时，鼠标移至屏幕最右侧边缘即刻平滑滑出显示
                if (!this.IsVisible || _isHiding)
                {
                    _outsideHoverTicks = 0;

                    // 刚收起后的 350ms 冷却期内不立即重新弹出，防止点击右上角关闭按钮后瞬间误触
                    if ((DateTime.UtcNow - _lastHideTime).TotalMilliseconds < 350)
                    {
                        _edgeHoverTicks = 0;
                        return;
                    }

                    // 探测屏幕右边缘 6px 热区 (避开最顶端关闭按钮角与最底端系统时间托盘角)
                    bool atRightEdge = pt.x >= (screenW - 6) && pt.y >= 48 && pt.y <= (screenH - 48);
                    if (atRightEdge)
                    {
                        _edgeHoverTicks++;
                        if (_edgeHoverTicks >= 2) // 悬停约 100ms 即刻丝滑唤起
                        {
                            _edgeHoverTicks = 0;
                            ShowDocked();
                        }
                    }
                    else
                    {
                        _edgeHoverTicks = 0;
                    }
                    return;
                }

                // 场景 2：当侧边栏已显示且处于未锁定状态 (!_isPinned) 时，若鼠标进入过侧边栏后移出左侧区域，自动平滑贴边收起
                _edgeHoverTicks = 0;
                if (!_isPinned)
                {
                    IntPtr hwnd = new WindowInteropHelper(this).Handle;
                    if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out RECT rect))
                    {
                        bool insideWindow = pt.x >= rect.Left && pt.x <= rect.Right && pt.y >= rect.Top && pt.y <= rect.Bottom;
                        if (insideWindow)
                        {
                            _mouseHasEnteredAfterShow = true;
                            _outsideHoverTicks = 0;
                        }
                        else if (_mouseHasEnteredAfterShow &&
                                 pt.x < (rect.Left - 35) &&
                                 (SettingsViewPanel == null || SettingsViewPanel.Visibility != Visibility.Visible) &&
                                 (AddActionOverlay == null || AddActionOverlay.Visibility != Visibility.Visible))
                        {
                            _outsideHoverTicks++;
                            if (_outsideHoverTicks >= 9) // 移出抽屉约 450ms 自动贴边收起
                            {
                                _outsideHoverTicks = 0;
                                SlideOutAndHide();
                            }
                        }
                    }
                }
            }
            catch { }
        }

        protected override void OnClosed(EventArgs e)
        {
            _edgeDetectTimer?.Stop();
            StopGlobalMouseHook();
            base.OnClosed(e);
        }

        #endregion

        public SidebarWindow(DatabaseService dbService, SettingsService settingsService, ClipboardMonitorService monitorService, PasteSimulator pasteSimulator)
        {
            _isInternalSettingUpdate = true;
            _dbService = dbService;
            _settingsService = settingsService;
            _monitorService = monitorService;
            _pasteSimulator = pasteSimulator;
            _mediaService = new SystemMediaService();

            InitializeComponent();

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
                    UpdateSideStorageInfo();
                });
            };

            // 监听失焦自动收起 (图钉锁定状态下常驻，未锁定状态下失焦自动收起)
            this.Deactivated += (s, e) =>
            {
                if (!_isPinned && this.IsVisible && !_isHiding && !_isModalOrMenuOpen &&
                    (AddActionOverlay == null || AddActionOverlay.Visibility != Visibility.Visible))
                {
                    SlideOutAndHide();
                }
            };

            // 启动屏幕右边缘悬停唤醒定时器 (常驻监听贴边唤起)
            _edgeDetectTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _edgeDetectTimer.Tick += OnEdgeDetectTimerTick;
            _edgeDetectTimer.Start();

            // 监听多语言全局变更
            LocalizationService.Instance.LanguageChanged += () =>
            {
                Dispatcher.Invoke(() =>
                {
                    UpdateLocalizedTexts();
                    RefreshCards();
                    UpdateSideStorageInfo();
                });
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

        private string GetWallpapersDirectory()
        {
            string baseDir = _settingsService?.CurrentSettings.StorageDirectory ?? string.Empty;
            if (string.IsNullOrWhiteSpace(baseDir))
            {
                baseDir = AppSettings.GetAppRootDirectory();
            }
            string dir = Path.Combine(baseDir, "Wallpapers");
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        /// <summary>
        /// 确保默认星空暗调壁纸存在，如不存在则利用 GDI+ 绘制高质量渐变星夜壁纸并保存至本地存储目录
        /// </summary>
        private string EnsureDefaultWallpaperExists()
        {
            try
            {
                string wallpapersDir = GetWallpapersDirectory();
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
        /// 安全加载壁纸图片为冻结的 BitmapImage (带内存缓存，防止滑动滑块或重复切换时反复解码磁盘图片)
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
                    if (_cachedWallpaperBitmap != null && string.Equals(_cachedWallpaperPath, targetPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return _cachedWallpaperBitmap;
                    }

                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(targetPath, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();
                    _cachedWallpaperPath = targetPath;
                    _cachedWallpaperBitmap = bmp;
                    return bmp;
                }
            }
            catch { }
            return null;
        }

        private void SetBrushAlphaInPlace(string key, byte alpha)
        {
            if (this.Resources[key] is SolidColorBrush brush && !brush.IsFrozen)
            {
                var c = brush.Color;
                if (c.A != alpha)
                {
                    brush.Color = Color.FromArgb(alpha, c.R, c.G, c.B);
                }
            }
        }

        /// <summary>
        /// 滑块拖动期间 0 分配极速更新背景透明度 (直接原位修改画刷 Alpha 通道，不触发 DynamicResource 字典重建与磁盘 I/O)
        /// </summary>
        private void UpdateBackgroundOpacityLive(double bgOpacityFactor)
        {
            double clamped = Math.Clamp(bgOpacityFactor, 0.15, 1.0);
            byte bgAlpha = (byte)Math.Clamp((int)Math.Round(clamped * 255), 35, 255);
            byte headerAlpha = (byte)Math.Clamp((int)Math.Round(Math.Min(1.0, clamped + 0.04) * 255), 45, 255);
            byte cardAlpha = (byte)Math.Clamp((int)Math.Round((0.55 + 0.45 * clamped) * 255), 140, 255);

            string theme = _settingsService?.CurrentSettings.ThemeMode ?? "Dark";
            if (string.Equals(theme, "CustomImage", StringComparison.OrdinalIgnoreCase))
            {
                if (BgImageContainer != null)
                {
                    BgImageContainer.Opacity = clamped;
                }
                return;
            }

            SetBrushAlphaInPlace("SidebarBgBrush", bgAlpha);
            SetBrushAlphaInPlace("HeaderBgBrush", headerAlpha);
            SetBrushAlphaInPlace("CardBgBrush", cardAlpha);
            SetBrushAlphaInPlace("TileBgBrush", cardAlpha);
            SetBrushAlphaInPlace("TabContainerBgBrush", cardAlpha);
        }

        private void ScheduleDebouncedSaveSettings()
        {
            if (_saveSettingsDebounceTimer == null)
            {
                _saveSettingsDebounceTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(250)
                };
                _saveSettingsDebounceTimer.Tick += (s, e) =>
                {
                    _saveSettingsDebounceTimer.Stop();
                    SaveOwnSettingsSafely();
                };
            }
            _saveSettingsDebounceTimer.Stop();
            _saveSettingsDebounceTimer.Start();
        }

        private void SaveOwnSettingsSafely()
        {
            if (_settingsService == null) return;
            _isSavingOwnSettings = true;
            try
            {
                _settingsService.SaveSettings();
            }
            finally
            {
                _isSavingOwnSettings = false;
            }
        }

        /// <summary>
        /// 实时应用外观主题与背景透明度 (0% 透明度 = 100% 不透明底色，前景文字始终保持 100% 清晰锐利)
        /// </summary>
        public void ApplyTheme(string theme)
        {
            double bgOpacityFactor = Math.Clamp(_settingsService?.CurrentSettings.WindowOpacity ?? 0.92, 0.15, 1.0);
            byte bgAlpha = (byte)Math.Clamp((int)Math.Round(bgOpacityFactor * 255), 35, 255);
            byte headerAlpha = (byte)Math.Clamp((int)Math.Round(Math.Min(1.0, bgOpacityFactor + 0.04) * 255), 45, 255);
            byte cardAlpha = (byte)Math.Clamp((int)Math.Round((0.55 + 0.45 * bgOpacityFactor) * 255), 140, 255);

            switch (theme)
            {
                case "Light":
                    if (BgImageContainer != null) BgImageContainer.Visibility = Visibility.Collapsed;
                    this.Resources["SidebarBgBrush"] = new SolidColorBrush(Color.FromArgb(bgAlpha, 0xFA, 0xFB, 0xFD));
                    this.Resources["SidebarBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));
                    this.Resources["HeaderBgBrush"] = new SolidColorBrush(Color.FromArgb(headerAlpha, 0xFF, 0xFF, 0xFF));
                    this.Resources["CardBgBrush"] = new SolidColorBrush(Color.FromArgb(cardAlpha, 0xFF, 0xFF, 0xFF));
                    this.Resources["CardHoverBgBrush"] = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));
                    this.Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));
                    this.Resources["TextPrimaryBrush"] = new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A));
                    this.Resources["TextSecondaryBrush"] = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69));
                    this.Resources["TileBgBrush"] = new SolidColorBrush(Color.FromArgb(cardAlpha, 0xF1, 0xF5, 0xF9));
                    this.Resources["TileHoverBgBrush"] = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0));
                    this.Resources["TabContainerBgBrush"] = new SolidColorBrush(Color.FromArgb(cardAlpha, 0xEE, 0xF2, 0xF6));
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
                    this.Resources["SidebarBgBrush"] = new SolidColorBrush(Color.FromArgb(bgAlpha, 0x0B, 0x1A, 0x17));
                    this.Resources["SidebarBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x13, 0x4E, 0x4A));
                    this.Resources["HeaderBgBrush"] = new SolidColorBrush(Color.FromArgb(headerAlpha, 0x07, 0x14, 0x12));
                    this.Resources["CardBgBrush"] = new SolidColorBrush(Color.FromArgb(cardAlpha, 0x10, 0x26, 0x23));
                    this.Resources["CardHoverBgBrush"] = new SolidColorBrush(Color.FromRgb(0x16, 0x38, 0x33));
                    this.Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x1B, 0x4D, 0x45));
                    this.Resources["TextPrimaryBrush"] = new SolidColorBrush(Color.FromRgb(0xEC, 0xFD, 0xF5));
                    this.Resources["TextSecondaryBrush"] = new SolidColorBrush(Color.FromRgb(0x6E, 0xE7, 0xB7));
                    this.Resources["TileBgBrush"] = new SolidColorBrush(Color.FromArgb(cardAlpha, 0x0F, 0x24, 0x21));
                    this.Resources["TileHoverBgBrush"] = new SolidColorBrush(Color.FromRgb(0x17, 0x36, 0x31));
                    this.Resources["TabContainerBgBrush"] = new SolidColorBrush(Color.FromArgb(cardAlpha, 0x0A, 0x1C, 0x1A));
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
                    this.Resources["SidebarBgBrush"] = new SolidColorBrush(Color.FromArgb(bgAlpha, 0x1C, 0x12, 0x20));
                    this.Resources["SidebarBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x4C, 0x1D, 0x44));
                    this.Resources["HeaderBgBrush"] = new SolidColorBrush(Color.FromArgb(headerAlpha, 0x16, 0x0D, 0x19));
                    this.Resources["CardBgBrush"] = new SolidColorBrush(Color.FromArgb(cardAlpha, 0x2A, 0x18, 0x2E));
                    this.Resources["CardHoverBgBrush"] = new SolidColorBrush(Color.FromRgb(0x3B, 0x22, 0x40));
                    this.Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x53, 0x26, 0x58));
                    this.Resources["TextPrimaryBrush"] = new SolidColorBrush(Color.FromRgb(0xFF, 0xF1, 0xF2));
                    this.Resources["TextSecondaryBrush"] = new SolidColorBrush(Color.FromRgb(0xF4, 0x72, 0xB6));
                    this.Resources["TileBgBrush"] = new SolidColorBrush(Color.FromArgb(cardAlpha, 0x23, 0x13, 0x27));
                    this.Resources["TileHoverBgBrush"] = new SolidColorBrush(Color.FromRgb(0x33, 0x1B, 0x38));
                    this.Resources["TabContainerBgBrush"] = new SolidColorBrush(Color.FromArgb(cardAlpha, 0x19, 0x0C, 0x1D));
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
                    this.Resources["SidebarBgBrush"] = new SolidColorBrush(Color.FromArgb(bgAlpha, 0x0A, 0x13, 0x26));
                    this.Resources["SidebarBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x1E, 0x3B, 0x62));
                    this.Resources["HeaderBgBrush"] = new SolidColorBrush(Color.FromArgb(headerAlpha, 0x07, 0x0E, 0x1E));
                    this.Resources["CardBgBrush"] = new SolidColorBrush(Color.FromArgb(cardAlpha, 0x10, 0x21, 0x3D));
                    this.Resources["CardHoverBgBrush"] = new SolidColorBrush(Color.FromRgb(0x17, 0x2E, 0x54));
                    this.Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x1D, 0x3E, 0x6F));
                    this.Resources["TextPrimaryBrush"] = new SolidColorBrush(Color.FromRgb(0xF0, 0xF9, 0xFF));
                    this.Resources["TextSecondaryBrush"] = new SolidColorBrush(Color.FromRgb(0x7D, 0xD3, 0xFC));
                    this.Resources["TileBgBrush"] = new SolidColorBrush(Color.FromArgb(cardAlpha, 0x0C, 0x1A, 0x33));
                    this.Resources["TileHoverBgBrush"] = new SolidColorBrush(Color.FromRgb(0x13, 0x27, 0x4A));
                    this.Resources["TabContainerBgBrush"] = new SolidColorBrush(Color.FromArgb(cardAlpha, 0x08, 0x12, 0x23));
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
                        BgImageContainer.Opacity = bgOpacityFactor;
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
                    this.Resources["SidebarBgBrush"] = new SolidColorBrush(Color.FromArgb(bgAlpha, 0x14, 0x17, 0x22));
                    this.Resources["SidebarBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x2E, 0x3A, 0x4E));
                    this.Resources["HeaderBgBrush"] = new SolidColorBrush(Color.FromArgb(headerAlpha, 0x0F, 0x12, 0x1A));
                    this.Resources["CardBgBrush"] = new SolidColorBrush(Color.FromArgb(cardAlpha, 0x1C, 0x22, 0x30));
                    this.Resources["CardHoverBgBrush"] = new SolidColorBrush(Color.FromRgb(0x28, 0x32, 0x46));
                    this.Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x2D, 0x39, 0x4D));
                    this.Resources["TextPrimaryBrush"] = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC));
                    this.Resources["TextSecondaryBrush"] = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
                    this.Resources["TileBgBrush"] = new SolidColorBrush(Color.FromArgb(cardAlpha, 0x1B, 0x22, 0x30));
                    this.Resources["TileHoverBgBrush"] = new SolidColorBrush(Color.FromRgb(0x28, 0x33, 0x48));
                    this.Resources["TabContainerBgBrush"] = new SolidColorBrush(Color.FromArgb(cardAlpha, 0x15, 0x1B, 0x26));
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

            // 随手记与连续粘贴队列多语言
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

            // 偏好设置页面 (与 1.0 完全对齐)
            if (TxtSettingsHeaderTitle != null) TxtSettingsHeaderTitle.Text = loc.Get("SidebarPreferences");
            if (TxtSetModeTitle != null) TxtSetModeTitle.Text = loc.Get("WindowModeTitle");
            if (RadioModeSidebar2 != null) RadioModeSidebar2.Content = loc.Get("WindowModeSidebar");
            if (RadioModeCentered2 != null) RadioModeCentered2.Content = loc.Get("WindowModeCentered");
            if (TxtSetLangTitle != null) TxtSetLangTitle.Text = loc.Get("LangSectionTitle");
            if (RadioLangZh2 != null) RadioLangZh2.Content = loc.Get("LangZh");
            if (RadioLangEn2 != null) RadioLangEn2.Content = loc.Get("LangEn");
            if (TxtSetThemeTitle != null) TxtSetThemeTitle.Text = loc.Get("AppearanceTitle");
            if (TxtThemeDarkShort2 != null) TxtThemeDarkShort2.Text = loc.Get("ThemeDarkShort");
            if (TxtThemeLightShort2 != null) TxtThemeLightShort2.Text = loc.Get("ThemeLightShort");
            if (TxtThemeAuroraShort2 != null) TxtThemeAuroraShort2.Text = loc.Get("ThemeAuroraShort");
            if (TxtThemeSunsetShort2 != null) TxtThemeSunsetShort2.Text = loc.Get("ThemeSunsetShort");
            if (TxtThemeOceanShort2 != null) TxtThemeOceanShort2.Text = loc.Get("ThemeOceanShort");
            if (TxtThemeCustomShort2 != null) TxtThemeCustomShort2.Text = loc.Get("ThemeCustomShort");
            if (TxtSetOpacityTitle != null) TxtSetOpacityTitle.Text = loc.Get("OpacityLabel");
            if (TxtSetStorageTitle != null) TxtSetStorageTitle.Text = loc.Get("StoragePathTitle");
            if (BtnBrowseStorage != null) BtnBrowseStorage.Content = loc.Get("BrowseButton");
            if (TxtSideCloseActionTitle != null) TxtSideCloseActionTitle.Text = loc.Get("CloseActionTitle");
            if (RadioSideCloseMinimize != null) RadioSideCloseMinimize.Content = loc.Get("CloseActionTray");
            if (RadioSideCloseExit != null) RadioSideCloseExit.Content = loc.Get("CloseActionExit");
            if (TxtSetInteractionTitle != null) TxtSetInteractionTitle.Text = loc.Get("DoubleClickTitle");
            if (RadioSideDoubleClickCopy != null) RadioSideDoubleClickCopy.Content = loc.Get("DoubleClickCopy");
            if (RadioSideDoubleClickPaste != null) RadioSideDoubleClickPaste.Content = loc.Get("DoubleClickPaste");
            if (ChkHideOnDeactivate2 != null)
            {
                ChkHideOnDeactivate2.Content = "📌 " + loc.Get("PinSettingTitle");
                ChkHideOnDeactivate2.ToolTip = loc.Get("PinSettingTooltip");
            }
            if (BtnClearHistory != null) BtnClearHistory.Content = "🗑️ " + loc.Get("TrayClear");
            if (BtnExitApp != null) BtnExitApp.Content = loc.Get("BtnExitApp");

            // 底部提示与状态
            if (TxtStatusRightTip != null) TxtStatusRightTip.Text = loc.Get("SidebarAltVTip");
            if (TxtStatusLeft != null) TxtStatusLeft.Text = loc.Get("SidebarRecordCount", DisplayCards.Count);
            if (BtnPin != null) BtnPin.ToolTip = _isPinned ? loc.Get("PinLockedTip") : loc.Get("PinUnlockedTip");
            UpdateTabVisuals(ClipboardViewPanel?.Visibility == Visibility.Visible);
            UpdateSideStorageInfo();
        }

        #endregion

        #region 窗口生命周期与抽屉平滑动画 (带代数防重入保护)

        /// <summary>
        /// 精准吸附至屏幕最右侧工作区全高，并执行顺滑贴边滑入显现
        /// </summary>
        public void ShowDocked()
        {
            int gen = ++_animGeneration;
            _isHiding = false;
            _mouseHasEnteredAfterShow = false;
            _outsideHoverTicks = 0;

            _pasteSimulator.RecordTargetWindow();

            var workArea = SystemParameters.WorkArea;
            this.Width = 380;
            this.Height = workArea.Height;
            this.Top = workArea.Top;
            this.Left = workArea.Right - this.Width;
            this.WindowState = WindowState.Normal;
            this.Topmost = true;

            RefreshCards();
            UpdateSideStorageInfo();
            
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

            this.BeginAnimation(UIElement.OpacityProperty, null);
            DrawerTranslate?.BeginAnimation(TranslateTransform.XProperty, null);

            this.Show();
            this.Activate();

            // 顺畅贴边滑入 + 渐显动画，前景保持 100% 清晰，背景透明度由画刷承载
            this.Opacity = 0;
            var fadeAnim = new DoubleAnimation(0, 1.0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            fadeAnim.Completed += (s, e) =>
            {
                if (gen != _animGeneration) return;
                this.BeginAnimation(UIElement.OpacityProperty, null);
                this.Opacity = 1.0;
            };
            this.BeginAnimation(UIElement.OpacityProperty, fadeAnim);

            if (DrawerTranslate != null)
            {
                DrawerTranslate.X = 48;
                var slideAnim = new DoubleAnimation(48, 0, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                slideAnim.Completed += (s, e) =>
                {
                    if (gen != _animGeneration) return;
                    DrawerTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                    DrawerTranslate.X = 0;
                };
                DrawerTranslate.BeginAnimation(TranslateTransform.XProperty, slideAnim);
            }

            if (ClipboardViewPanel.Visibility == Visibility.Visible)
            {
                TxtSearch?.Focus();
            }
        }

        /// <summary>
        /// 平滑向右收起抽屉并隐藏
        /// </summary>
        public void SlideOutAndHide()
        {
            if (!this.IsVisible || _isHiding) return;

            int gen = ++_animGeneration;
            _isHiding = true;
            _lastHideTime = DateTime.UtcNow;

            StopGlobalMouseHook();
            _brightnessSyncTimer.Stop();

            double currentOp = this.Opacity > 0.05 ? this.Opacity : 1.0;
            var fadeAnim = new DoubleAnimation(currentOp, 0, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            fadeAnim.Completed += (s, e) =>
            {
                if (gen != _animGeneration) return;
                _isHiding = false;
                this.Hide();
                this.BeginAnimation(UIElement.OpacityProperty, null);
                this.Opacity = 1.0;
                if (DrawerTranslate != null)
                {
                    DrawerTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                    DrawerTranslate.X = 0;
                }
            };
            this.BeginAnimation(UIElement.OpacityProperty, fadeAnim);

            if (DrawerTranslate != null)
            {
                var slideAnim = new DoubleAnimation(DrawerTranslate.X, 48, TimeSpan.FromMilliseconds(150))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                };
                DrawerTranslate.BeginAnimation(TranslateTransform.XProperty, slideAnim);
            }
        }

        public void ToggleDocked()
        {
            if (this.IsVisible && !_isHiding && this.Opacity > 0.1)
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
        /// 在侧边栏内部直接展示偏好设置页 (与 1.0 共享同一套设置源)
        /// </summary>
        public void ShowSettings()
        {
            try
            {
                if (ClipboardViewPanel.Visibility == Visibility.Visible)
                {
                    _previousView = "Clipboard";
                }
                else if (SystemActionsPanel.Visibility == Visibility.Visible)
                {
                    _previousView = "SystemActions";
                }

                LoadSettingsToSidebarUI();

                ClipboardViewPanel.Visibility = Visibility.Collapsed;
                SystemActionsPanel.Visibility = Visibility.Collapsed;
                SettingsViewPanel.Visibility = Visibility.Visible;

                HeaderNormalBar.Visibility = Visibility.Collapsed;
                HeaderSettingsBar.Visibility = Visibility.Visible;
                TabSwitcherRow.Visibility = Visibility.Collapsed;

                if (!this.IsVisible || _isHiding)
                {
                    ShowDocked();
                }
            }
            catch { }
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
                bool prev = _isInternalSettingUpdate;
                _isInternalSettingUpdate = true;
                try
                {
                    ChkHideOnDeactivate2.IsChecked = _isPinned;
                }
                finally
                {
                    _isInternalSettingUpdate = prev;
                }
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            if (_settingsService?.CurrentSettings.CloseAction == "ExitApp")
            {
                if (Application.Current is App app)
                {
                    app.ExitApplication();
                }
            }
            else
            {
                SlideOutAndHide();
            }
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

        #region 自定义快捷动作网格管理 (支持自动提取程序原生高清图标 + 自定义 Emoji 覆盖)

        private static readonly System.Collections.Generic.Dictionary<string, ImageSource?> _programIconCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 智能解析命令或路径对应的真实可执行程序物理路径 (支持完整路径、引号路径、系统内置命令如 calc/taskmgr/notepad 等)
        /// </summary>
        private static string? ResolveExecutablePath(string? cmdOrPath)
        {
            if (string.IsNullOrWhiteSpace(cmdOrPath)) return null;
            string raw = cmdOrPath.Trim();

            // URL 或 URI 协议 (如 ms-screenclip:, https://) 无本地 exe 图标
            if (raw.Contains("://") || (raw.EndsWith(":") && !raw.Contains("\\") && !raw.Contains("/")))
            {
                return null;
            }

            // 去除首尾引号或提取带参数的第一个可执行文件路径
            string candidate = raw;
            if (raw.StartsWith("\""))
            {
                int endQuote = raw.IndexOf('"', 1);
                if (endQuote > 1)
                {
                    candidate = raw.Substring(1, endQuote - 1);
                }
            }
            else if (File.Exists(raw))
            {
                candidate = raw;
            }
            else
            {
                int exeIdx = raw.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (exeIdx > 0)
                {
                    candidate = raw.Substring(0, exeIdx + 4).Trim();
                }
                else
                {
                    int spaceIdx = raw.IndexOf(' ');
                    if (spaceIdx > 0) candidate = raw.Substring(0, spaceIdx).Trim();
                }
            }

            try
            {
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }

                // 尝试补全 .exe 后缀並在 System32 / Windows / PATH 中检索
                string withExe = candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? candidate : candidate + ".exe";
                string sys32 = Environment.SystemDirectory;
                string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

                string p1 = Path.Combine(sys32, withExe);
                if (File.Exists(p1)) return p1;

                string p2 = Path.Combine(winDir, withExe);
                if (File.Exists(p2)) return p2;

                string? pathEnv = Environment.GetEnvironmentVariable("PATH");
                if (!string.IsNullOrEmpty(pathEnv))
                {
                    foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        try
                        {
                            string p = Path.Combine(dir.Trim(), withExe);
                            if (File.Exists(p)) return p;
                        }
                        catch { }
                    }
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// 从目标程序提取关联原生图标并转换为冻结的高清 ImageSource
        /// </summary>
        private static ImageSource? ExtractProgramIcon(string? cmdOrPath)
        {
            if (string.IsNullOrWhiteSpace(cmdOrPath)) return null;
            string key = cmdOrPath.Trim();

            if (_programIconCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            try
            {
                string? resolved = ResolveExecutablePath(key);
                if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved))
                {
                    using var ico = System.Drawing.Icon.ExtractAssociatedIcon(resolved);
                    if (ico != null)
                    {
                        using var bmp = ico.ToBitmap();
                        using var ms = new MemoryStream();
                        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        ms.Position = 0;

                        var bitmapImage = new BitmapImage();
                        bitmapImage.BeginInit();
                        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                        bitmapImage.StreamSource = ms;
                        bitmapImage.EndInit();
                        bitmapImage.Freeze();

                        _programIconCache[key] = bitmapImage;
                        return bitmapImage;
                    }
                }
            }
            catch { }

            _programIconCache[key] = null;
            return null;
        }

        private void RefreshCustomActions()
        {
            if (CustomActionsPanel == null || _settingsService == null) return;

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
            borderFactory.Name = "tileBd";
            borderFactory.SetResourceReference(Border.BackgroundProperty, "TileBgBrush");
            borderFactory.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));

            var stack = new FrameworkElementFactory(typeof(StackPanel));
            stack.SetValue(StackPanel.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            stack.SetValue(StackPanel.VerticalAlignmentProperty, VerticalAlignment.Center);

            // 判断是否使用程序原生图标：当未设置自定义 Emoji (或为默认占位符 🚀/🧮/📊) 且目标是可提取图标的程序时，默认优先展示程序原生图标！
            string rawIcon = (action.Icon ?? string.Empty).Trim();
            bool hasCustomEmoji = !string.IsNullOrEmpty(rawIcon) && rawIcon != "🚀" && rawIcon != "🧮" && rawIcon != "📊";
            ImageSource? nativeIcon = !hasCustomEmoji ? ExtractProgramIcon(action.CommandOrPath) : null;

            if (nativeIcon != null)
            {
                var imgFactory = new FrameworkElementFactory(typeof(Image));
                imgFactory.SetValue(Image.SourceProperty, nativeIcon);
                imgFactory.SetValue(Image.WidthProperty, 20.0);
                imgFactory.SetValue(Image.HeightProperty, 20.0);
                imgFactory.SetValue(Image.StretchProperty, Stretch.Uniform);
                imgFactory.SetValue(Image.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                imgFactory.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.HighQuality);
                stack.AppendChild(imgFactory);
            }
            else
            {
                var iconTxt = new FrameworkElementFactory(typeof(TextBlock));
                iconTxt.SetValue(TextBlock.TextProperty, string.IsNullOrEmpty(rawIcon) ? "🚀" : rawIcon);
                iconTxt.SetValue(TextBlock.FontSizeProperty, 16.0);
                iconTxt.SetResourceReference(TextBlock.ForegroundProperty, "AccentBlueBrush");
                iconTxt.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                stack.AppendChild(iconTxt);
            }

            var nameTxt = new FrameworkElementFactory(typeof(TextBlock));
            nameTxt.SetValue(TextBlock.TextProperty, action.Name);
            nameTxt.SetValue(TextBlock.FontSizeProperty, 10.5);
            nameTxt.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            nameTxt.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            nameTxt.SetValue(TextBlock.MarginProperty, new Thickness(0, 3, 0, 0));
            nameTxt.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            nameTxt.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            nameTxt.SetValue(TextBlock.MaxWidthProperty, 70.0);

            stack.AppendChild(nameTxt);
            borderFactory.AppendChild(stack);
            template.VisualTree = borderFactory;

            // 悬停高亮触发器
            var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("TileHoverBgBrush"), "tileBd"));
            hoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("AccentBlueBrush"), "tileBd"));
            template.Triggers.Add(hoverTrigger);

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
                    _isModalOrMenuOpen = true;
                    try
                    {
                        MessageBox.Show($"启动失败: {ex.Message}", "快捷动作", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    finally
                    {
                        _isModalOrMenuOpen = false;
                    }
                }
            };

            btn.MouseRightButtonUp += (s, e) =>
            {
                _isModalOrMenuOpen = true;
                try
                {
                    var result = MessageBox.Show($"确定删除快捷动作【{action.Name}】吗？", "删除快捷动作", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (result == MessageBoxResult.Yes)
                    {
                        _settingsService.CurrentSettings.CustomActions.RemoveAt(index);
                        _settingsService.SaveSettings();
                        RefreshCustomActions();
                    }
                }
                finally
                {
                    _isModalOrMenuOpen = false;
                }
            };

            return btn;
        }

        private void UpdateNewActionIconPreview()
        {
            if (ImgNewActionIconPreview == null || TxtNewActionIconPreview == null) return;
            string customEmoji = TxtNewActionIcon?.Text?.Trim() ?? string.Empty;
            string cmd = TxtNewActionCommand?.Text?.Trim() ?? string.Empty;

            if (!string.IsNullOrEmpty(customEmoji))
            {
                ImgNewActionIconPreview.Visibility = Visibility.Collapsed;
                TxtNewActionIconPreview.Visibility = Visibility.Visible;
                TxtNewActionIconPreview.Text = customEmoji;
                return;
            }

            var nativeIcon = ExtractProgramIcon(cmd);
            if (nativeIcon != null)
            {
                ImgNewActionIconPreview.Source = nativeIcon;
                ImgNewActionIconPreview.Visibility = Visibility.Visible;
                TxtNewActionIconPreview.Visibility = Visibility.Collapsed;
            }
            else
            {
                ImgNewActionIconPreview.Visibility = Visibility.Collapsed;
                TxtNewActionIconPreview.Visibility = Visibility.Visible;
                TxtNewActionIconPreview.Text = "🚀";
            }
        }

        private void TxtNewActionCommand_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateNewActionIconPreview();
        }

        private void TxtNewActionIcon_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateNewActionIconPreview();
        }

        private void BtnAddNewAction_Click(object sender, RoutedEventArgs e)
        {
            TxtNewActionName.Text = "";
            TxtNewActionIcon.Text = ""; // 默认留空以自动拉取目标程序图标，用户也可自行输入 Emoji
            TxtNewActionCommand.Text = "";
            UpdateNewActionIconPreview();
            AddActionOverlay.Visibility = Visibility.Visible;
            TxtNewActionCommand.Focus();
        }

        private void BtnCancelAddAction_Click(object sender, RoutedEventArgs e)
        {
            AddActionOverlay.Visibility = Visibility.Collapsed;
        }

        private void BtnBrowseActionFile_Click(object sender, RoutedEventArgs e)
        {
            _isModalOrMenuOpen = true;
            try
            {
                var dlg = new OpenFileDialog
                {
                    Title = "选择可执行程序或文件",
                    Filter = "应用程序 (*.exe;*.lnk;*.bat;*.cmd)|*.exe;*.lnk;*.bat;*.cmd|所有文件 (*.*)|*.*"
                };
                if (dlg.ShowDialog() == true)
                {
                    TxtNewActionCommand.Text = dlg.FileName;
                    if (string.IsNullOrEmpty(TxtNewActionName.Text))
                    {
                        TxtNewActionName.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
                    }
                    UpdateNewActionIconPreview();
                }
            }
            finally
            {
                _isModalOrMenuOpen = false;
            }
        }

        private void BtnConfirmAddAction_Click(object sender, RoutedEventArgs e)
        {
            string name = TxtNewActionName.Text.Trim();
            string cmd = TxtNewActionCommand.Text.Trim();
            string icon = TxtNewActionIcon.Text.Trim();

            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(cmd))
            {
                _isModalOrMenuOpen = true;
                try
                {
                    MessageBox.Show("请填写名称和执行目标！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                finally
                {
                    _isModalOrMenuOpen = false;
                }
                return;
            }

            // 若用户未输入自定义 Emoji，保持 Icon 为空字符串以优先使用提取到的程序原生图标
            _settingsService.CurrentSettings.CustomActions.Add(new CustomActionItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                CommandOrPath = cmd,
                Icon = icon
            });
            _settingsService.SaveSettings();

            AddActionOverlay.Visibility = Visibility.Collapsed;
            RefreshCustomActions();
        }

        #endregion

        #region 侧边栏偏好设置中心联动 (与 1.0 居中模式 100% 同源双向实时同步)

        public void SyncSettingsFromService(bool refreshCards)
        {
            if (_isSavingOwnSettings || _isInternalSettingUpdate) return;
            LoadSettingsToSidebarUI();
            if (refreshCards)
            {
                RefreshCards();
            }
            UpdateSideStorageInfo();
        }

        private void UpdateSideStorageInfo()
        {
            try
            {
                if (TxtSideStorageStatusInfo == null || _dbService == null) return;
                var (_, totalCount, pinnedCount, imgCount, _) = _dbService.GetStats();
                TxtSideStorageStatusInfo.Text = LocalizationService.Instance.Get("StatusInfoFormat", totalCount, pinnedCount, imgCount);
            }
            catch { }
        }

        private void LoadSettingsToSidebarUI()
        {
            if (_settingsService == null) return;
            bool prev = _isInternalSettingUpdate;
            _isInternalSettingUpdate = true;
            try
            {
                var s = _settingsService.CurrentSettings;
                if (TxtSideStoragePath != null) TxtSideStoragePath.Text = s.StorageDirectory;

                // 1. 模式单选
                if (RadioModeCentered2 != null && RadioModeSidebar2 != null)
                {
                    if (s.WindowMode == "Centered") RadioModeCentered2.IsChecked = true;
                    else RadioModeSidebar2.IsChecked = true;
                }

                // 2. 语言单选
                if (RadioLangEn2 != null && RadioLangZh2 != null)
                {
                    if (s.Language == "en-US") RadioLangEn2.IsChecked = true;
                    else RadioLangZh2.IsChecked = true;
                }

                // 3. 主题单选回显与壁纸状态
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

                // 4. 背景透明度回显 (0% = 完全不透明 WindowOpacity 1.0；向右拉增大透明度，最高 85% 即 WindowOpacity 0.15)
                if (SliderSideOpacity != null)
                {
                    int transparencyPct = Math.Clamp((int)Math.Round((1.0 - s.WindowOpacity) * 100), 0, 85);
                    SliderSideOpacity.Value = transparencyPct;
                    if (TxtSideOpacityVal != null) TxtSideOpacityVal.Text = $"{transparencyPct}%";
                }

                // 立即应用当前主题与背景透明度
                ApplyTheme(s.ThemeMode);

                // 5. 关闭按钮行为单选
                if (RadioSideCloseExit != null && RadioSideCloseMinimize != null)
                {
                    if (s.CloseAction == "ExitApp") RadioSideCloseExit.IsChecked = true;
                    else RadioSideCloseMinimize.IsChecked = true;
                }

                // 6. 双击卡片行为与图钉常驻状态
                if (RadioSideDoubleClickPaste != null && RadioSideDoubleClickCopy != null)
                {
                    if (s.DoubleClickAction == "PasteAndHide") RadioSideDoubleClickPaste.IsChecked = true;
                    else RadioSideDoubleClickCopy.IsChecked = true;
                }

                _isPinned = !s.HideOnDeactivate;
                UpdatePinUiState();
                UpdateSideStorageInfo();
            }
            catch { }
            finally
            {
                _isInternalSettingUpdate = prev;
            }
        }

        private void WindowModeRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_isInternalSettingUpdate || _settingsService == null || RadioModeSidebar2 == null || RadioModeCentered2 == null) return;
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
            if (_isInternalSettingUpdate || _settingsService == null) return;
            string newLang = RadioLangEn2?.IsChecked == true ? "en-US" : "zh-CN";
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

            if (_settingsService.CurrentSettings.ThemeMode != tag)
            {
                _settingsService.CurrentSettings.ThemeMode = tag;
                if (BdCustomImageSettings != null)
                {
                    BdCustomImageSettings.Visibility = (tag == "CustomImage") ? Visibility.Visible : Visibility.Collapsed;
                }
                ApplyTheme(tag);
                _settingsService.SaveSettings();
            }
        }

        private void BtnUploadCustomBg_Click(object sender, RoutedEventArgs e)
        {
            _isModalOrMenuOpen = true;
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

                    string wallpapersDir = GetWallpapersDirectory();
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
            finally
            {
                _isModalOrMenuOpen = false;
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
            int intVal = (int)Math.Round(e.NewValue);
            double opacity = intVal / 100.0;
            _settingsService.CurrentSettings.BackgroundOverlayOpacity = opacity;

            if (TxtBgOverlayOpacityVal != null)
            {
                TxtBgOverlayOpacityVal.Text = $"{intVal}%";
            }

            if (BdCustomBgOverlay != null)
            {
                BdCustomBgOverlay.Opacity = opacity;
            }

            ScheduleDebouncedSaveSettings();
        }

        private void SliderSideOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isInternalSettingUpdate || _settingsService == null) return;
            int transparencyPct = Math.Clamp((int)Math.Round(e.NewValue), 0, 85);
            double windowOpacity = Math.Clamp(1.0 - (transparencyPct / 100.0), 0.15, 1.0);

            if (TxtSideOpacityVal != null) TxtSideOpacityVal.Text = $"{transparencyPct}%";

            _settingsService.CurrentSettings.WindowOpacity = windowOpacity;
            UpdateBackgroundOpacityLive(windowOpacity);
            ScheduleDebouncedSaveSettings();
        }

        private void SideCloseActionRadio_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInternalSettingUpdate || _settingsService == null) return;
            string newAction = RadioSideCloseExit?.IsChecked == true ? "ExitApp" : "MinimizeToTray";
            if (_settingsService.CurrentSettings.CloseAction != newAction)
            {
                _settingsService.CurrentSettings.CloseAction = newAction;
                _settingsService.SaveSettings();
            }
        }

        private void SideDoubleClickRadio_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInternalSettingUpdate || _settingsService == null) return;
            string newAction = RadioSideDoubleClickPaste?.IsChecked == true ? "PasteAndHide" : "CopyOnly";
            if (_settingsService.CurrentSettings.DoubleClickAction != newAction)
            {
                _settingsService.CurrentSettings.DoubleClickAction = newAction;
                _settingsService.SaveSettings();
            }
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

        private void BtnBrowseStorage_Click(object sender, RoutedEventArgs e)
        {
            _isModalOrMenuOpen = true;
            try
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
                        if (TxtSideStoragePath != null) TxtSideStoragePath.Text = _settingsService.CurrentSettings.StorageDirectory;
                        RefreshCards();
                        UpdateSideStorageInfo();
                        MessageBox.Show(loc.Get("StorageMigratedMsg", _settingsService.CurrentSettings.StorageDirectory), loc.Get("StorageMigratedTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            finally
            {
                _isModalOrMenuOpen = false;
            }
        }

        private void BtnClearHistory_Click(object sender, RoutedEventArgs e)
        {
            _isModalOrMenuOpen = true;
            try
            {
                var loc = LocalizationService.Instance;
                var res = MessageBox.Show(loc.Get("ConfirmClearMsg"), loc.Get("ConfirmClearTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res == MessageBoxResult.Yes)
                {
                    _dbService.ClearAll();
                    RefreshCards();
                    UpdateSideStorageInfo();
                    _settingsService.SaveSettings();
                }
            }
            finally
            {
                _isModalOrMenuOpen = false;
            }
        }

        private void BtnExitApp_Click(object sender, RoutedEventArgs e)
        {
            _isModalOrMenuOpen = true;
            try
            {
                var loc = LocalizationService.Instance;
                var res = MessageBox.Show(loc.Get("ConfirmExitMsg"), loc.Get("ConfirmExitTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res == MessageBoxResult.Yes && Application.Current is App app)
                {
                    app.ExitApplication();
                }
            }
            finally
            {
                _isModalOrMenuOpen = false;
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
