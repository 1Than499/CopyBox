using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
        private TrayIconService? _trayService;

        private MainWindow? _mainWindow;
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

                // 5. 初始化 Windows 原生系统托盘服务
                _trayService = new TrayIconService();
                var trayMenu = CreateTrayContextMenu();
                _trayService.Initialize(_mainWindow, trayMenu);
                _trayService.TrayClicked += () =>
                {
                    Dispatcher.Invoke(() => _mainWindow.ShowAndActivate());
                };

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

        private ContextMenu CreateTrayContextMenu()
        {
            var menu = new ContextMenu();

            var menuQuick = new MenuItem { Header = "🚀 呼出 ClipVault (Alt + V)", FontWeight = FontWeights.Bold };
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

            var menuSettings = new MenuItem { Header = "⚙️ 偏好设置与本地路径" };
            menuSettings.Click += (s, e) =>
            {
                _mainWindow?.ShowAndActivate();
                _mainWindow?.OpenSettings();
            };

            var menuExit = new MenuItem { Header = "🛑 彻底退出 ClipVault" };
            menuExit.Click += (s, e) => ExitApplication();

            menu.Items.Add(menuQuick);
            menu.Items.Add(new Separator());
            menu.Items.Add(menuPause);
            menu.Items.Add(menuClear);
            menu.Items.Add(menuSettings);
            menu.Items.Add(new Separator());
            menu.Items.Add(menuExit);

            return menu;
        }

        public void ExitApplication()
        {
            _monitorService?.Stop();
            _hotkeyService?.Stop();
            _trayService?.Dispose();
            _dbService?.Dispose();
            Current.Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _monitorService?.Stop();
            _hotkeyService?.Stop();
            _trayService?.Dispose();
            _dbService?.Dispose();
            base.OnExit(e);
        }
    }
}
