using System;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace ClipVault.Services
{
    /// <summary>
    /// 提供系统快捷控制支持：统一的系统屏幕真实亮度调度、WiFi 可用网络弹窗、蓝牙、系统音量控制
    /// </summary>
    public static class SystemControlService
    {
        #region 屏幕真实亮度统一调度 (WMI 系统硬件背光 + GDI Gamma 平滑回退)

        private static bool _useWmi = true;
        private static int _cachedBrightness = -1;

        [DllImport("gdi32.dll")]
        private static extern bool SetDeviceGammaRamp(IntPtr hDC, ref RAMP lpRamp);

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

        /// <summary>
        /// 读取当前系统真实的屏幕亮度 (0 ~ 100)，与 Windows 系统设置保持完全一致
        /// </summary>
        public static int GetCurrentBrightness()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
                foreach (ManagementObject obj in searcher.Get())
                {
                    byte b = (byte)obj["CurrentBrightness"];
                    _cachedBrightness = (int)b;
                    _useWmi = true;
                    return _cachedBrightness;
                }
            }
            catch
            {
                _useWmi = false;
            }

            // 若不支持 WMI (如外接显示器台式机)，优先返回上次设定的缓存值或 100% 正常亮度
            if (_cachedBrightness >= 0) return _cachedBrightness;
            _cachedBrightness = 100;
            return _cachedBrightness;
        }

        /// <summary>
        /// 设置屏幕亮度 (10 ~ 100)，优先通过 Windows WMI 驱动与系统保持完全一致调度
        /// </summary>
        public static void SetBrightness(int brightness)
        {
            brightness = Math.Clamp(brightness, 10, 100);
            _cachedBrightness = brightness;

            if (_useWmi)
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");
                    bool success = false;
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        obj.InvokeMethod("WmiSetBrightness", new object[] { 1, (byte)brightness });
                        success = true;
                    }
                    if (success) return;
                }
                catch
                {
                    _useWmi = false;
                }
            }

            // WMI 不可用时的无缝平滑回退：调节色彩亮度
            try
            {
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

        public static void OpenAvailableNetworks() => OpenUri("ms-availablenetworks:");

        public static void OpenBluetoothSettings() => OpenUri("ms-settings:bluetooth");

        public static void OpenSystemSettings() => OpenUri("ms-settings:");

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
