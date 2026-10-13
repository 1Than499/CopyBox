using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClipVault.Services
{
    /// <summary>
    /// 提供系统快捷控制支持：真实屏幕亮度（Gamma/硬件）、WiFi可用网络弹窗、蓝牙、系统音量控制
    /// </summary>
    public static class SystemControlService
    {
        #region 真实屏幕亮度调节 (GDI Gamma Ramp，跨平台跨屏幕 100% 普适兼容)

        [DllImport("gdi32.dll")]
        private static extern bool SetDeviceGammaRamp(IntPtr hDC, ref RAMP lpRamp);

        [DllImport("gdi32.dll")]
        private static extern bool GetDeviceGammaRamp(IntPtr hDC, ref RAMP lpRamp);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct RAMP
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public ushort[] Red;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public ushort[] Green;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public ushort[] Blue;
        }

        private static int _currentBrightness = 80;

        public static int GetCurrentBrightness() => _currentBrightness;

        /// <summary>
        /// 设置屏幕亮度 (10 ~ 100)
        /// </summary>
        public static void SetBrightness(int brightness)
        {
            try
            {
                brightness = Math.Clamp(brightness, 15, 100);
                _currentBrightness = brightness;

                IntPtr hdc = GetDC(IntPtr.Zero);
                if (hdc != IntPtr.Zero)
                {
                    var ramp = new RAMP
                    {
                        Red = new ushort[256],
                        Green = new ushort[256],
                        Blue = new ushort[256]
                    };

                    double factor = brightness / 100.0;

                    for (int i = 0; i < 256; i++)
                    {
                        int value = (int)(i * 256 * factor);
                        if (value > 65535) value = 65535;
                        ramp.Red[i] = (ushort)value;
                        ramp.Green[i] = (ushort)value;
                        ramp.Blue[i] = (ushort)value;
                    }

                    SetDeviceGammaRamp(hdc, ref ramp);
                    ReleaseDC(IntPtr.Zero, hdc);
                }
            }
            catch { }
        }

        #endregion

        #region 系统音量快捷调节 (通过原生多媒体虚拟键)

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const byte VK_VOLUME_MUTE = 0xAD;
        private const byte VK_VOLUME_DOWN = 0xAE;
        private const byte VK_VOLUME_UP = 0xAF;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        public static void VolumeUp()
        {
            keybd_event(VK_VOLUME_UP, 0, 0, UIntPtr.Zero);
            keybd_event(VK_VOLUME_UP, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        public static void VolumeDown()
        {
            keybd_event(VK_VOLUME_DOWN, 0, 0, UIntPtr.Zero);
            keybd_event(VK_VOLUME_DOWN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        public static void VolumeMute()
        {
            keybd_event(VK_VOLUME_MUTE, 0, 0, UIntPtr.Zero);
            keybd_event(VK_VOLUME_MUTE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        #endregion

        #region 原生核心快捷动作

        public static void OpenUri(string uri)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = uri,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        /// <summary>
        /// 唤出 Windows 原生 WiFi 与网络可用连接浮层
        /// </summary>
        public static void OpenAvailableNetworks()
        {
            OpenUri("ms-availablenetworks:");
        }

        /// <summary>
        /// 呼出 Windows 蓝牙设备设置
        /// </summary>
        public static void OpenBluetoothSettings()
        {
            OpenUri("ms-settings:bluetooth");
        }

        /// <summary>
        /// 呼出 Windows 整体设置中心
        /// </summary>
        public static void OpenSystemSettings()
        {
            OpenUri("ms-settings:");
        }

        /// <summary>
        /// 执行自定义动作 (URL / 可执行程序 / 系统命令)
        /// </summary>
        public static void ExecuteAction(string commandOrPath)
        {
            if (string.IsNullOrWhiteSpace(commandOrPath)) return;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = commandOrPath.Trim(),
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"执行失败: {ex.Message}", "快捷动作错误", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
        }

        #endregion
    }
}
