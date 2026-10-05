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
        private System.Windows.Forms.NotifyIcon? _notifyIcon;
        private Icon? _appIcon;

        private static System.Threading.Mutex? _singleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                File.AppendAllText(@"d:\bank\crash.log", $"[AppDomain Crash] {args.ExceptionObject}\n");
            };
            this.DispatcherUnhandledException += (s, args) =>
            {
                File.AppendAllText(@"d:\bank\crash.log", $"[Dispatcher Crash] {args.Exception}\n");
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
                LocalizationService.Instance.SetLanguage(_settingsService.CurrentSettings.Language);
                _dbService = new DatabaseService(_settingsService.CurrentSettings.StorageDirectory);
                _monitorService = new ClipboardMonitorService(_dbService, _settingsService);
                _pasteSimulator = new PasteSimulator(_monitorService);
                _hotkeyService = new HotkeyService(_settingsService);

                // 2. 启动剪贴板与热键监听
                _monitorService.Start();
                _hotkeyService.Start();

                // 3. 初始化统一的现代 Fluent 双列卡片主窗口 (完全还原设计图)
                _mainWindow = new MainWindow(_dbService, _settingsService, _monitorService, _pasteSimulator);

                // 4. 绑定全局热键 Alt + V
                _hotkeyService.HotkeyPressed += () =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (_mainWindow != null)
                        {
                            if (_mainWindow.IsVisible && _mainWindow.WindowState != WindowState.Minimized)
                            {
                                _mainWindow.Hide();
                            }
                            else
                            {
                                _mainWindow.ShowAndActivate();
                            }
                        }
                    });
                };

                // 5. 初始化 Windows 任务栏系统托盘 (100% 官方原生稳定常驻)
                InitTrayIcon();

                // 6. 启动后直接呼出主窗口
                _mainWindow.ShowAndActivate();
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"d:\bank\crash.log", $"[Startup Exception] {ex}\n");
                MessageBox.Show($"CopyBox 启动失败: {ex.Message}\n{ex.StackTrace}", "启动异常", MessageBoxButton.OK, MessageBoxImage.Error);
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
                _trayMenuQuick.Click += (s, e) => Dispatcher.Invoke(() => _mainWindow?.ShowAndActivate());

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

                _trayMenuClear = new System.Windows.Forms.ToolStripMenuItem();
                _trayMenuClear.Click += (s, e) =>
                {
                    var loc = LocalizationService.Instance;
                    var res = MessageBox.Show(loc.Get("ConfirmClearMsg"), loc.Get("ConfirmClearTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (res == MessageBoxResult.Yes)
                    {
                        _dbService?.ClearAll();
                        Dispatcher.Invoke(() => _mainWindow?.RefreshCards());
                    }
                };

                _trayMenuSettings = new System.Windows.Forms.ToolStripMenuItem();
                _trayMenuSettings.Click += (s, e) => Dispatcher.Invoke(() =>
                {
                    _mainWindow?.ShowAndActivate();
                    _mainWindow?.OpenSettings();
                });

                _trayMenuExit = new System.Windows.Forms.ToolStripMenuItem();
                _trayMenuExit.Click += (s, e) => Dispatcher.Invoke(() => ExitApplication());

                contextMenu.Items.Add(_trayMenuQuick);
                contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                contextMenu.Items.Add(_trayMenuPause);
                contextMenu.Items.Add(_trayMenuClear);
                contextMenu.Items.Add(_trayMenuSettings);
                contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                contextMenu.Items.Add(_trayMenuExit);

                _notifyIcon.ContextMenuStrip = contextMenu;

                UpdateTrayTexts();
                LocalizationService.Instance.LanguageChanged += UpdateTrayTexts;

                // 单击或双击托盘图标：快速唤醒主窗口
                _notifyIcon.MouseClick += (s, e) =>
                {
                    if (e.Button == System.Windows.Forms.MouseButtons.Left)
                    {
                        Dispatcher.Invoke(() => _mainWindow?.ShowAndActivate());
                    }
                };
                _notifyIcon.DoubleClick += (s, e) =>
                {
                    Dispatcher.Invoke(() => _mainWindow?.ShowAndActivate());
                };
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"d:\bank\crash.log", $"[TrayIcon Warning] {ex.Message}\n");
            }
        }

        private void UpdateTrayTexts()
        {
            if (_notifyIcon == null) return;
            var loc = LocalizationService.Instance;
            _notifyIcon.Text = loc.Get("TrayTooltip");

            if (_trayMenuQuick != null) _trayMenuQuick.Text = "🚀 " + loc.Get("TrayShow");
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
            File.AppendAllText(@"d:\bank\app.log", $"[{DateTime.Now}] OnExit called, code: {e.ApplicationExitCode}\n");
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
