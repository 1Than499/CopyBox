using System;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ClipVault.Services;
using H.NotifyIcon;

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
        private TaskbarIcon? _trayIcon;

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
                            if (_mainWindow.IsVisible)
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

                // 5. 初始化 Windows 通知栏托盘图标
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

        private void InitTrayIcon()
        {
            try
            {
                Icon? myIcon = null;
                string icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "app.ico");
                if (File.Exists(icoPath))
                {
                    try { myIcon = new Icon(icoPath); } catch { }
                }
                if (myIcon == null)
                {
                    myIcon = SystemIcons.Application;
                }

                _trayIcon = new TaskbarIcon
                {
                    Icon = myIcon,
                    ToolTipText = "ClipVault - 剪贴板安全保管箱 (Alt+V)",
                    Visibility = Visibility.Visible
                };

                // 创建托盘右键上下文菜单
                var contextMenu = new ContextMenu();

                var menuQuick = new MenuItem { Header = "🚀 呼出 ClipVault (Alt + V)" };
                menuQuick.Click += (s, e) => _mainWindow?.ShowAndActivate();

                var menuPause = new MenuItem { Header = "⏸️ 暂停记录剪贴板", IsCheckable = true };
                menuPause.Click += (s, e) =>
                {
                    if (_monitorService != null)
                    {
                        _monitorService.IsPaused = menuPause.IsChecked;
                        menuPause.Header = _monitorService.IsPaused ? "▶️ 恢复记录剪贴板" : "⏸️ 暂停记录剪贴板";
                    }
                };

                var menuClear = new MenuItem { Header = "🗑️ 清空所有非置顶记录" };
                menuClear.Click += (s, e) =>
                {
                    _dbService?.ClearAll();
                    _mainWindow?.RefreshCards();
                };

                var menuExit = new MenuItem { Header = "🛑 彻底退出 ClipVault" };
                menuExit.Click += (s, e) => ExitApplication();

                contextMenu.Items.Add(menuQuick);
                contextMenu.Items.Add(new Separator());
                contextMenu.Items.Add(menuPause);
                contextMenu.Items.Add(menuClear);
                contextMenu.Items.Add(new Separator());
                contextMenu.Items.Add(menuExit);

                _trayIcon.ContextMenu = contextMenu;

                // 单击或双击托盘图标：快速唤醒主窗口
                _trayIcon.TrayLeftMouseDown += (s, e) => _mainWindow?.ShowAndActivate();
                _trayIcon.TrayMouseDoubleClick += (s, e) => _mainWindow?.ShowAndActivate();
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
            _trayIcon?.Dispose();
            _dbService?.Dispose();
            Current.Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _monitorService?.Stop();
            _hotkeyService?.Stop();
            _trayIcon?.Dispose();
            _dbService?.Dispose();
            base.OnExit(e);
        }
    }
}
