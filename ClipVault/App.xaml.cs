using System;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using ClipVault.Services;
using ClipVault.Views;
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

        private QuickPasteWindow? _quickPasteWindow;
        private MainWindow? _mainWindow;
        private TaskbarIcon? _trayIcon;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            try
            {
                // 1. 初始化核心后台服务
                _settingsService = new SettingsService();
                _dbService = new DatabaseService(_settingsService.CurrentSettings.StorageDirectory);
                _monitorService = new ClipboardMonitorService(_dbService, _settingsService);
                _pasteSimulator = new PasteSimulator(_monitorService);
                _hotkeyService = new HotkeyService(_settingsService);

                // 2. 启动 Win32 消息泵监听与快捷键
                _monitorService.Start();
                _hotkeyService.Start();

                // 3. 初始化双窗口（快捷悬浮窗 + 独立管理主窗口）
                _quickPasteWindow = new QuickPasteWindow(_dbService, _pasteSimulator);
                _mainWindow = new MainWindow(_dbService, _settingsService, _monitorService);

                // 4. 绑定全局热键 Alt + V
                _hotkeyService.HotkeyPressed += () =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (_quickPasteWindow != null)
                        {
                            if (_quickPasteWindow.IsVisible)
                            {
                                _quickPasteWindow.Hide();
                            }
                            else
                            {
                                _quickPasteWindow.ShowAndRefresh();
                            }
                        }
                    });
                };

                // 5. 初始化 Windows 通知栏托盘图标
                InitTrayIcon();

                // 6. 首次启动打开独立主界面，让用户直观看到效果和自选存储路径
                _mainWindow.Show();
                _mainWindow.Activate();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"ClipVault 启动失败: {ex.Message}\n{ex.StackTrace}", "启动异常", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }

        private void InitTrayIcon()
        {
            _trayIcon = new TaskbarIcon
            {
                Icon = SystemIcons.Application,
                ToolTipText = "ClipVault - 剪贴板安全保管箱 (Alt+V)"
            };

            // 创建托盘右键上下文菜单
            var contextMenu = new ContextMenu();

            var menuQuick = new MenuItem { Header = "🚀 呼出快捷剪贴板 (Alt + V)" };
            menuQuick.Click += (s, e) => _quickPasteWindow?.ShowAndRefresh();

            var menuMain = new MenuItem { Header = "⚙️ 独立管理与本地存储设置" };
            menuMain.Click += (s, e) =>
            {
                if (_mainWindow != null)
                {
                    _mainWindow.Show();
                    _mainWindow.Activate();
                }
            };

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
                _mainWindow?.RefreshHistoryList();
            };

            var menuExit = new MenuItem { Header = "❌ 退出 ClipVault" };
            menuExit.Click += (s, e) => ExitApplication();

            contextMenu.Items.Add(menuQuick);
            contextMenu.Items.Add(menuMain);
            contextMenu.Items.Add(new Separator());
            contextMenu.Items.Add(menuPause);
            contextMenu.Items.Add(menuClear);
            contextMenu.Items.Add(new Separator());
            contextMenu.Items.Add(menuExit);

            _trayIcon.ContextMenu = contextMenu;

            // 左键单机托盘：快速呼出悬浮窗
            _trayIcon.TrayLeftMouseDown += (s, e) => _quickPasteWindow?.ShowAndRefresh();

            // 双击托盘：打开主管理设置窗口
            _trayIcon.TrayMouseDoubleClick += (s, e) =>
            {
                if (_mainWindow != null)
                {
                    _mainWindow.Show();
                    _mainWindow.Activate();
                }
            };
        }

        private void ExitApplication()
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
