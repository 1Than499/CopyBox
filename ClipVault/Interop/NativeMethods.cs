using System;
using System.Runtime.InteropServices;

namespace ClipVault.Interop
{
    public static class NativeMethods
    {
        public const int WM_CLIPBOARDUPDATE = 0x031D;
        public const int WM_HOTKEY = 0x0312;

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        // SendInput 模拟按键结构
        public const int INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const ushort VK_CONTROL = 0x11;
        public const ushort VK_V = 0x56;

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)]
            public MOUSEINPUT mi;
            [FieldOffset(0)]
            public KEYBDINPUT ki;
            [FieldOffset(0)]
            public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        /// <summary>
        /// 模拟按下并释放 Ctrl + V
        /// </summary>
        public static void SimulateCtrlV()
        {
            var inputs = new INPUT[4];

            // 1. Ctrl Down
            inputs[0] = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = 0 }
                }
            };

            // 2. V Down
            inputs[1] = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT { wVk = VK_V, dwFlags = 0 }
                }
            };

            // 3. V Up
            inputs[2] = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT { wVk = VK_V, dwFlags = KEYEVENTF_KEYUP }
                }
            };

            // 4. Ctrl Up
            inputs[3] = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP }
                }
            };

            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern IntPtr GetWindowLongPtr32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        {
            return IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLongPtr32(hWnd, nIndex);
        }

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            return IntPtr.Size == 8 
                ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) 
                : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
        }

        /// <summary>
        /// 为指定窗口赋予 WS_EX_NOACTIVATE 与 WS_EX_TOPMOST 扩展样式，避免点击时夺取用户主软件的光标焦点
        /// </summary>
        public static void SetNoActivate(IntPtr hWnd)
        {
            var exStyle = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
            exStyle |= WS_EX_NOACTIVATE;
            SetWindowLongPtr(hWnd, GWL_EXSTYLE, new IntPtr(exStyle));
        }

        // ==================== v2.2.1 多显示器与全屏防误触免打扰原生 API ====================

        public const uint MONITOR_DEFAULTTONULL = 0x00000000;
        public const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;
        public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        public enum QUERY_USER_NOTIFICATION_STATE
        {
            QUNS_NOT_PRESENT = 1,
            QUNS_BUSY = 2,
            QUNS_RUNNING_D3D_FULL_SCREEN = 3,
            QUNS_PRESENTATION_MODE = 4,
            QUNS_ACCEPTS_NOTIFICATIONS = 5,
            QUNS_QUIET_TIME = 6,
            QUNS_APP = 7
        }

        [DllImport("shell32.dll")]
        public static extern int SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE pquns);

        /// <summary>
        /// 获取目标显示器物理像素区域与工作区（支持 FollowCursor 跟随鼠标所在屏幕 或 Primary 主屏幕）
        /// </summary>
        public static (RECT monitorRect, RECT workRect, double dpiScaleX, double dpiScaleY) GetTargetMonitorBounds(string monitorStrategy)
        {
            IntPtr hMonitor;
            if (string.Equals(monitorStrategy, "Primary", StringComparison.OrdinalIgnoreCase))
            {
                hMonitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
            }
            else
            {
                if (GetCursorPos(out POINT pt))
                {
                    hMonitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
                }
                else
                {
                    hMonitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
                }
            }

            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (hMonitor != IntPtr.Zero && GetMonitorInfo(hMonitor, ref mi))
            {
                double scaleX = 1.0;
                double scaleY = 1.0;
                try
                {
                    if (GetDpiForMonitor(hMonitor, 0, out uint dpiX, out uint dpiY) == 0 && dpiX > 0 && dpiY > 0)
                    {
                        scaleX = dpiX / 96.0;
                        scaleY = dpiY / 96.0;
                    }
                }
                catch { }
                return (mi.rcMonitor, mi.rcWork, scaleX, scaleY);
            }

            // 兜底返回主屏幕区域
            var fallbackWork = System.Windows.SystemParameters.WorkArea;
            var fallbackRect = new RECT
            {
                Left = (int)fallbackWork.Left,
                Top = (int)fallbackWork.Top,
                Right = (int)fallbackWork.Right,
                Bottom = (int)fallbackWork.Bottom
            };
            return (fallbackRect, fallbackRect, 1.0, 1.0);
        }

        /// <summary>
        /// 智能检测当前前台是否处于全屏游戏、全屏视频或演示模式（用于全屏防误触免打扰）
        /// </summary>
        public static bool IsForegroundFullscreenOrGame()
        {
            try
            {
                // 1. 检测 Direct3D 独占全屏游戏或演示模式
                if (SHQueryUserNotificationState(out var state) == 0)
                {
                    if (state == QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN ||
                        state == QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE)
                    {
                        return true;
                    }
                }

                // 2. 检测前台窗口是否为无边框全屏游戏 / 全屏浏览器视频 / 全屏播放器
                IntPtr fgHwnd = GetForegroundWindow();
                if (fgHwnd == IntPtr.Zero) return false;

                // 排除自身进程窗口
                GetWindowThreadProcessId(fgHwnd, out uint pid);
                if (pid == (uint)Environment.ProcessId) return false;

                // 排除 Windows 桌面与任务栏壳窗口 (Progman, WorkerW, Shell_TrayWnd)
                var sb = new System.Text.StringBuilder(64);
                if (GetClassName(fgHwnd, sb, sb.Capacity) > 0)
                {
                    string cls = sb.ToString();
                    if (cls.Equals("Progman", StringComparison.OrdinalIgnoreCase) ||
                        cls.Equals("WorkerW", StringComparison.OrdinalIgnoreCase) ||
                        cls.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
                        cls.Equals("Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                if (!GetWindowRect(fgHwnd, out RECT winRect)) return false;
                if (winRect.Width <= 0 || winRect.Height <= 0) return false;

                IntPtr hMon = MonitorFromWindow(fgHwnd, MONITOR_DEFAULTTONEAREST);
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (hMon != IntPtr.Zero && GetMonitorInfo(hMon, ref mi))
                {
                    var mon = mi.rcMonitor;
                    // 当窗口完全覆盖所在显示器的整个物理屏幕（连任务栏也遮盖）时，判定为全屏状态
                    if (winRect.Left <= mon.Left &&
                        winRect.Top <= mon.Top &&
                        winRect.Right >= mon.Right &&
                        winRect.Bottom >= mon.Bottom)
                    {
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }
    }
}
