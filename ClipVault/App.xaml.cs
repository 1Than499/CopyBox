using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using ClipVault.Services;

namespace ClipVault
{
    public partial class App : Application
    {
        private SettingsService? _settingsService;
        private DatabaseService? _dbService;
        private ClipboardMonitorService? _monitorService;
        private PasteSimulator? _pasteSimulator;
        private HotkeyService? _hotkeyService;

        private MainWindow? _mainWindow;
        private ClipVault.Views.SidebarWindow? _sidebarWindow;
        private System.Windows.Forms.NotifyIcon? _notifyIcon;
        private Icon? _appIcon;

        private static System.Threading.Mutex? _singleInstanceMutex;

        public static void LogError(string tag, object? ex)
        {
            try
            {
                string logPath = Path.Combine(ClipVault.Models.AppSettings.GetAppRootDirectory(), "crash.log");
                File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{tag}] {ex}\n");
            }
            catch { }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            this.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                LogError("AppDomain Crash", args.ExceptionObject);
            };
            this.DispatcherUnhandledException += (s, args) =>
            {
                LogError("Dispatcher Exception", args.Exception);
                args.Handled = true;
            };

            // 进程防多开检测
            _singleInstanceMutex = new System.Threading.Mutex(true, "CopyBox_SingleInstance_Mutex_98765", out bool isNewInstance);
            if (!isNewInstance)
            {
                var loc = LocalizationService.Instance;
                MessageBox.Show(loc.Get("AlreadyRunningMsg"), loc.Get("AlreadyRunningTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            try
            {
                // 1. 初始化核心后台服务
                _settingsService = new SettingsService();
                ClipVault.Models.ClipboardItem.GlobalAutoMaskEnabled = _settingsService.CurrentSettings.AutoMaskSensitiveData;
                LocalizationService.Instance.SetLanguage(_settingsService.CurrentSettings.Language);
                _dbService = new DatabaseService(_settingsService.CurrentSettings.StorageDirectory);
                _monitorService = new ClipboardMonitorService(_dbService, _settingsService);
                _pasteSimulator = new PasteSimulator(_monitorService);
                _hotkeyService = new HotkeyService(_settingsService);

                // 2. 启动剪贴板与热键监听
                _monitorService.Start();
                _hotkeyService.Start();

                // 3. 按当前形态偏好按需初始化窗口（杜绝同时加载双套完整 XAML 视觉树与全部卡片）
                string startupMode = _settingsService.CurrentSettings.WindowMode;
                if (startupMode == "Centered")
                {
                    GetOrCreateMainWindow();
                }
                else
                {
                    GetOrCreateSidebarWindow();
                }

                // 3.5 全局统一绑定存储路径变更与设置项同步 (确保 1.0 与 2.0 共享同一套数据源与配置状态)
                _settingsService.StorageDirectoryChanged += newDir =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        _dbService?.SwitchStorageDirectory(newDir);
                        _mainWindow?.SyncSettingsFromService(refreshCards: true);
                        _sidebarWindow?.SyncSettingsFromService(refreshCards: true);
                    });
                };

                _settingsService.SettingsChanged += () =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        ClipVault.Models.ClipboardItem.GlobalAutoMaskEnabled = _settingsService.CurrentSettings.AutoMaskSensitiveData;
                        if (_trayMenuDnd != null)
                        {
                            _trayMenuDnd.Checked = _settingsService.CurrentSettings.IsManualDndMode;
                        }
                        _mainWindow?.SyncSettingsFromService(refreshCards: false);
                        _sidebarWindow?.SyncSettingsFromService(refreshCards: false);
                    });
                };

                // 4. 绑定统一全局热键 (Alt + V)，根据当前设置呼出对应模式 (1.0 居中 或 2.0 侧边栏)
                _hotkeyService.HotkeyPressed += () =>
                {
                    ToggleActiveWindow();
                };

                // 5. 初始化 Windows 任务栏系统托盘 (100% 官方原生稳定常驻)
                InitTrayIcon();

                // 6. 启动后根据当前选择的模式唤起界面
                ToggleActiveWindow();

                // 7. 启动后执行内存整理与工作集轻量化收缩
                ClipVault.Helpers.MemoryHelper.TrimWorkingSet();
            }
            catch (Exception ex)
            {
                LogError("Startup Exception", ex);
                MessageBox.Show($"CopyBox 启动失败: {ex.Message}", "启动异常", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }

        private Icon GetAppIcon()
        {
            try
            {
                string icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "app.ico");
                if (File.Exists(icoPath))
                {
                    try { return new Icon(icoPath); } catch { }
                }

                // 内存中动态绘制高质感剪贴板矢量质感蓝金图标 (32x32)
                using var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.Clear(Color.Transparent);

                    // 外圈圆角蓝色背景 (#0078D4)
                    using var path = new GraphicsPath();
                    path.AddArc(1, 1, 10, 10, 180, 90);
                    path.AddArc(21, 1, 10, 10, 270, 90);
                    path.AddArc(21, 21, 10, 10, 0, 90);
                    path.AddArc(1, 21, 10, 10, 90, 90);
                    path.CloseFigure();
                    using var bgBrush = new SolidBrush(Color.FromArgb(0, 120, 212));
                    g.FillPath(bgBrush, path);

                    // 中间白色剪贴板纸张
                    using var whiteBrush = new SolidBrush(Color.White);
                    g.FillRectangle(whiteBrush, 8, 7, 16, 18);

                    // 顶部金黄色夹子 (#FFB900)
                    using var clipBrush = new SolidBrush(Color.FromArgb(255, 185, 0));
                    g.FillRectangle(clipBrush, 12, 4, 8, 4);

                    // 纸上的蓝色横线
                    using var pen = new Pen(Color.FromArgb(0, 120, 212), 1.5f);
                    g.DrawLine(pen, 11, 12, 21, 12);
                    g.DrawLine(pen, 11, 16, 21, 16);
                    g.DrawLine(pen, 11, 20, 18, 20);
                }

                IntPtr hIcon = bmp.GetHicon();
                return Icon.FromHandle(hIcon);
            }
            catch
            {
                return SystemIcons.Application;
            }
        }

        private System.Windows.Forms.ToolStripMenuItem? _trayMenuQuick;
        private System.Windows.Forms.ToolStripMenuItem? _trayMenuPause;
        private System.Windows.Forms.ToolStripMenuItem? _trayMenuDnd;
        private System.Windows.Forms.ToolStripMenuItem? _trayMenuClear;
        private System.Windows.Forms.ToolStripMenuItem? _trayMenuSettings;
        private System.Windows.Forms.ToolStripMenuItem? _trayMenuExit;

        private void InitTrayIcon()
        {
            try
            {
                _appIcon = GetAppIcon();

                _notifyIcon = new System.Windows.Forms.NotifyIcon
                {
                    Icon = _appIcon,
                    Visible = true
                };

                // 创建原生上下文右键菜单 (工业级稳定)
                var contextMenu = new System.Windows.Forms.ContextMenuStrip();

                _trayMenuQuick = new System.Windows.Forms.ToolStripMenuItem();
                _trayMenuQuick.Font = new Font(_trayMenuQuick.Font, System.Drawing.FontStyle.Bold);
                _trayMenuQuick.Click += (s, e) => Dispatcher.Invoke(() => ToggleActiveWindow());

                _trayMenuPause = new System.Windows.Forms.ToolStripMenuItem();
                _trayMenuPause.Click += (s, e) =>
                {
                    if (_monitorService != null)
                    {
                        _monitorService.IsPaused = !_monitorService.IsPaused;
                        _trayMenuPause.Checked = _monitorService.IsPaused;
                        UpdateTrayMenuPauseText();
                    }
                };

                _trayMenuDnd = new System.Windows.Forms.ToolStripMenuItem
                {
                    Checked = _settingsService?.CurrentSettings.IsManualDndMode == true
                };
                _trayMenuDnd.Click += (s, e) =>
                {
                    if (_settingsService != null)
                    {
                        _settingsService.CurrentSettings.IsManualDndMode = !_settingsService.CurrentSettings.IsManualDndMode;
                        _trayMenuDnd.Checked = _settingsService.CurrentSettings.IsManualDndMode;
                        Dispatcher.Invoke(() => _settingsService.SaveSettings());
                    }
                };

                _trayMenuClear = new System.Windows.Forms.ToolStripMenuItem();
                _trayMenuClear.Click += (s, e) =>
                {
                    var loc = LocalizationService.Instance;
                    var res = MessageBox.Show(loc.Get("ConfirmClearMsg"), loc.Get("ConfirmClearTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (res == MessageBoxResult.Yes)
                    {
                        _dbService?.ClearAll();
                        Dispatcher.Invoke(() =>
                        {
                            _mainWindow?.RefreshCards();
                            _sidebarWindow?.RefreshCards();
                        });
                    }
                };

                _trayMenuSettings = new System.Windows.Forms.ToolStripMenuItem();
                _trayMenuSettings.Click += (s, e) => Dispatcher.Invoke(() => OpenSettings());

                _trayMenuExit = new System.Windows.Forms.ToolStripMenuItem();
                _trayMenuExit.Click += (s, e) => Dispatcher.Invoke(() => ExitApplication());

                contextMenu.Items.Add(_trayMenuQuick);
                contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                contextMenu.Items.Add(_trayMenuPause);
                contextMenu.Items.Add(_trayMenuDnd);
                contextMenu.Items.Add(_trayMenuClear);
                contextMenu.Items.Add(_trayMenuSettings);
                contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                contextMenu.Items.Add(_trayMenuExit);

                _notifyIcon.ContextMenuStrip = contextMenu;

                UpdateTrayTexts();
                LocalizationService.Instance.LanguageChanged += UpdateTrayTexts;

                // 单击或双击托盘图标：统一呼出当前设定的模式
                _notifyIcon.MouseClick += (s, e) =>
                {
                    if (e.Button == System.Windows.Forms.MouseButtons.Left)
                    {
                        Dispatcher.Invoke(() => ToggleActiveWindow());
                    }
                };
                _notifyIcon.DoubleClick += (s, e) =>
                {
                    Dispatcher.Invoke(() => ToggleActiveWindow());
                };
            }
            catch (Exception ex)
            {
                LogError("TrayIcon Warning", ex.Message);
            }
        }

        private void UpdateTrayTexts()
        {
            if (_notifyIcon == null) return;
            var loc = LocalizationService.Instance;
            _notifyIcon.Text = loc.Get("TrayTooltip");

            if (_trayMenuQuick != null) _trayMenuQuick.Text = "🚀 " + loc.Get("TrayShow");
            if (_trayMenuDnd != null) _trayMenuDnd.Text = "🎮 " + loc.Get("TrayDndMode");
            if (_trayMenuClear != null) _trayMenuClear.Text = "🗑️ " + loc.Get("TrayClear");
            if (_trayMenuSettings != null) _trayMenuSettings.Text = "⚙️ " + loc.Get("TraySettings");
            if (_trayMenuExit != null) _trayMenuExit.Text = "🛑 " + loc.Get("TrayExit");
            UpdateTrayMenuPauseText();
        }

        private void UpdateTrayMenuPauseText()
        {
            if (_trayMenuPause == null || _monitorService == null) return;
            var loc = LocalizationService.Instance;
            _trayMenuPause.Text = _monitorService.IsPaused 
                ? "▶️ " + loc.Get("TrayResume") 
                : "⏸️ " + loc.Get("TrayPause");
        }

        private MainWindow GetOrCreateMainWindow()
        {
            if (_mainWindow == null)
            {
                _mainWindow = new MainWindow(_dbService!, _settingsService!, _monitorService!, _pasteSimulator!);
            }
            return _mainWindow;
        }

        private ClipVault.Views.SidebarWindow GetOrCreateSidebarWindow()
        {
            if (_sidebarWindow == null)
            {
                _sidebarWindow = new ClipVault.Views.SidebarWindow(_dbService!, _settingsService!, _monitorService!, _pasteSimulator!);
            }
            return _sidebarWindow;
        }

        /// <summary>
        /// 核心多模统一调度：按全局快捷键 (Alt+V) 或点击托盘时，仅呼出当前设置选定的单一模式
        /// </summary>
        public void ToggleActiveWindow()
        {
            try
            {
                string mode = _settingsService?.CurrentSettings.WindowMode ?? "Sidebar";
                if (mode == "Centered")
                {
                    if (_sidebarWindow != null && _sidebarWindow.IsVisible)
                    {
                        _sidebarWindow.SlideOutAndHide();
                    }
                    GetOrCreateMainWindow().ToggleMainWindow();
                }
                else
                {
                    if (_mainWindow != null && _mainWindow.IsVisible)
                    {
                        _mainWindow.Hide();
                    }
                    GetOrCreateSidebarWindow().ToggleDocked();
                }
            }
            catch (Exception ex)
            {
                LogError("ToggleActiveWindow Exception", ex);
            }
        }

        /// <summary>
        /// 设置中切换形态偏好 (1.0 居中 vs 2.0 侧边栏) 时的即时互斥响应
        /// </summary>
        public void OnWindowModeChanged(string newMode)
        {
            Dispatcher.Invoke(() =>
            {
                if (newMode == "Centered")
                {
                    _sidebarWindow?.SlideOutAndHide();
                    var mainWin = GetOrCreateMainWindow();
                    mainWin.SyncSettingsFromService(refreshCards: true);
                    mainWin.ShowAndActivate();
                    mainWin.OpenSettings();
                }
                else
                {
                    _mainWindow?.Hide();
                    var sideWin = GetOrCreateSidebarWindow();
                    sideWin.SyncSettingsFromService(refreshCards: true);
                    sideWin.ShowDocked();
                    sideWin.ShowSettings();
                }
            });
        }

        /// <summary>
        /// 打开偏好设置：若当前是 2.0 侧边栏模式，直接在侧边栏内部展开设置页，绝不弹出 1.0 MainWindow！
        /// 仅当用户处于 1.0 居中模式时，才唤起 1.0 MainWindow 浮层设置。
        /// </summary>
        public void OpenSettings()
        {
            Dispatcher.Invoke(() =>
            {
                string mode = _settingsService?.CurrentSettings.WindowMode ?? "Sidebar";
                if (mode == "Sidebar")
                {
                    if (_mainWindow != null && _mainWindow.IsVisible)
                    {
                        _mainWindow.Hide();
                    }
                    GetOrCreateSidebarWindow().ShowSettings();
                }
                else
                {
                    if (_sidebarWindow != null && _sidebarWindow.IsVisible)
                    {
                        _sidebarWindow.SlideOutAndHide();
                    }
                    var mainWin = GetOrCreateMainWindow();
                    mainWin.ShowAndActivate();
                    mainWin.OpenSettings();
                }
            });
        }

        public void ExitApplication()
        {
            _monitorService?.Stop();
            _hotkeyService?.Stop();
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            _dbService?.Dispose();
            Current.Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _monitorService?.Stop();
            _hotkeyService?.Stop();
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            _dbService?.Dispose();
            base.OnExit(e);
        }
    }
}
