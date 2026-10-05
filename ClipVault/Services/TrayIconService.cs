using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace ClipVault.Services
{
    public class TrayIconService : IDisposable
    {
        private const int WM_USER = 0x0400;
        public const int WM_TRAYICON = WM_USER + 1024;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONUP = 0x0205;

        private const int NIM_ADD = 0x00000000;
        private const int NIM_MODIFY = 0x00000001;
        private const int NIM_DELETE = 0x00000002;

        private const int NIF_MESSAGE = 0x00000001;
        private const int NIF_ICON = 0x00000002;
        private const int NIF_TIP = 0x00000004;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public int uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private IntPtr _hWnd;
        private IntPtr _hIcon;
        private bool _isCreated;
        private ContextMenu? _contextMenu;

        public event Action? TrayClicked;

        public void Initialize(Window window, ContextMenu contextMenu)
        {
            _contextMenu = contextMenu;
            _contextMenu.Placement = PlacementMode.MousePoint;

            var wih = new WindowInteropHelper(window);
            _hWnd = wih.EnsureHandle();

            var source = HwndSource.FromHwnd(_hWnd);
            source?.AddHook(WndProc);

            CreateTrayIcon();
        }

        private void CreateTrayIcon()
        {
            try
            {
                _hIcon = LoadAppHIcon();

                var nid = new NOTIFYICONDATA
                {
                    cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                    hWnd = _hWnd,
                    uID = 1001,
                    uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                    uCallbackMessage = WM_TRAYICON,
                    hIcon = _hIcon,
                    szTip = "ClipVault - 剪贴板安全保管箱 (Alt+V)"
                };

                _isCreated = Shell_NotifyIcon(NIM_ADD, ref nid);
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"d:\bank\crash.log", $"[TrayIcon Create Error] {ex.Message}\n");
            }
        }

        private IntPtr LoadAppHIcon()
        {
            try
            {
                string icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "app.ico");
                if (File.Exists(icoPath))
                {
                    using var icon = new Icon(icoPath);
                    return icon.Handle;
                }
            }
            catch { }

            // 内存动态绘制现代高质感剪贴板矢量图标 (32x32)
            using var bmp = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.Clear(System.Drawing.Color.Transparent);

                // 外圈圆角蓝色背景 (#0078D4)
                using var path = new GraphicsPath();
                path.AddArc(1, 1, 10, 10, 180, 90);
                path.AddArc(21, 1, 10, 10, 270, 90);
                path.AddArc(21, 21, 10, 10, 0, 90);
                path.AddArc(1, 21, 10, 10, 90, 90);
                path.CloseFigure();
                using var bgBrush = new SolidBrush(System.Drawing.Color.FromArgb(0, 120, 212));
                g.FillPath(bgBrush, path);

                // 中间白色剪贴板纸张
                using var whiteBrush = new SolidBrush(System.Drawing.Color.White);
                g.FillRectangle(whiteBrush, 8, 7, 16, 18);

                // 顶部金黄色夹子 (#FFB900)
                using var clipBrush = new SolidBrush(System.Drawing.Color.FromArgb(255, 185, 0));
                g.FillRectangle(clipBrush, 12, 4, 8, 4);

                // 纸上的蓝色横线
                using var pen = new Pen(System.Drawing.Color.FromArgb(0, 120, 212), 1.5f);
                g.DrawLine(pen, 11, 12, 21, 12);
                g.DrawLine(pen, 11, 16, 21, 16);
                g.DrawLine(pen, 11, 20, 18, 20);
            }
            return bmp.GetHicon();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_TRAYICON)
            {
                int mouseMsg = lParam.ToInt32();
                if (mouseMsg == WM_LBUTTONUP || mouseMsg == WM_LBUTTONDBLCLK)
                {
                    TrayClicked?.Invoke();
                    handled = true;
                }
                else if (mouseMsg == WM_RBUTTONUP)
                {
                    if (_contextMenu != null)
                    {
                        SetForegroundWindow(_hWnd);
                        _contextMenu.IsOpen = true;
                    }
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_isCreated)
            {
                var nid = new NOTIFYICONDATA
                {
                    cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                    hWnd = _hWnd,
                    uID = 1001
                };
                Shell_NotifyIcon(NIM_DELETE, ref nid);
                _isCreated = false;
            }

            if (_hIcon != IntPtr.Zero)
            {
                try { DestroyIcon(_hIcon); } catch { }
                _hIcon = IntPtr.Zero;
            }
        }
    }
}
