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

            // 进程防多开检测
            _singleInstanceMutex = new System.Threading.Mutex(true, "ClipVault_SingleInstance_Mutex_98765", out bool isNewInstance);
            if (!isNewInstance)
            {
                MessageBox.Show("ClipVault 已经在后台运行中！\n请按下快捷键 [Alt + V] 唤醒，或在右下角系统托盘查看。", "ClipVault 运行提示", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                File.AppendAllText(@"d:\bank\crash.log", $"[AppDomain Crash] {args.ExceptionObject}\n");
            };
            this.DispatcherUnhandledException += (s, args) =>
            {
                File.AppendAllText(@"d:\bank\crash.log", $"[Dispatcher Crash] {args.Exception}\n");
            };

            try
            {
                // 1. 初始化核心后台服务
                _settingsService = new SettingsService();
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
                MessageBox.Show($"ClipVault 启动失败: {ex.Message}\n{ex.StackTrace}", "启动异常", MessageBoxButton.OK, MessageBoxImage.Error);
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

        private void InitTrayIcon()
        {
            try
            {
                _appIcon = GetAppIcon();

                _notifyIcon = new System.Windows.Forms.NotifyIcon
                {
                    Icon = _appIcon,
                    Text = "ClipVault - 剪贴板安全保管箱 (Alt+V)",
                    Visible = true
                };

                // 创建原生上下文右键菜单 (工业级稳定)
                var contextMenu = new System.Windows.Forms.ContextMenuStrip();

                var menuQuick = new System.Windows.Forms.ToolStripMenuItem("🚀 呼出 ClipVault (Alt + V)");
                menuQuick.Font = new Font(menuQuick.Font, System.Drawing.FontStyle.Bold);
                menuQuick.Click += (s, e) => Dispatcher.Invoke(() => _mainWindow?.ShowAndActivate());

                var menuPause = new System.Windows.Forms.ToolStripMenuItem("⏸️ 暂停记录剪贴板");
                menuPause.Click += (s, e) =>
                {
                    if (_monitorService != null)
                    {
                        _monitorService.IsPaused = !_monitorService.IsPaused;
                        menuPause.Checked = _monitorService.IsPaused;
                        menuPause.Text = _monitorService.IsPaused ? "▶️ 恢复记录剪贴板" : "⏸️ 暂停记录剪贴板";
                    }
                };

                var menuClear = new System.Windows.Forms.ToolStripMenuItem("🗑️ 清空所有非置顶记录");
                menuClear.Click += (s, e) =>
                {
                    _dbService?.ClearAll();
                    Dispatcher.Invoke(() => _mainWindow?.RefreshCards());
                };

                var menuSettings = new System.Windows.Forms.ToolStripMenuItem("⚙️ 偏好设置与本地路径");
                menuSettings.Click += (s, e) => Dispatcher.Invoke(() =>
                {
                    _mainWindow?.ShowAndActivate();
                    _mainWindow?.OpenSettings();
                });

                var menuExit = new System.Windows.Forms.ToolStripMenuItem("🛑 彻底退出 ClipVault");
                menuExit.Click += (s, e) => Dispatcher.Invoke(() => ExitApplication());

                contextMenu.Items.Add(menuQuick);
                contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                contextMenu.Items.Add(menuPause);
                contextMenu.Items.Add(menuClear);
                contextMenu.Items.Add(menuSettings);
                contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                contextMenu.Items.Add(menuExit);

                _notifyIcon.ContextMenuStrip = contextMenu;

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
