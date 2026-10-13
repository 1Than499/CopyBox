using System;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace ClipVault.Services
{
    /// <summary>
    /// 提供系统快捷控制支持：统一的系统屏幕真实亮度调度 (powrprof + WMI)、WiFi 可用网络弹窗、蓝牙、系统音量与全局媒体播控
    /// </summary>
    public static class SystemControlService
    {
        #region 屏幕真实亮度统一调度 (powrprof 电源背光方案 + WMI + 视觉平滑同步)

        [DllImport("powrprof.dll")]
        private static extern uint PowerGetActiveScheme(IntPtr UserRootPowerKey, out IntPtr ActivePolicyGuid);

        [DllImport("powrprof.dll")]
        private static extern uint PowerReadACValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroup, ref Guid Setting, out uint AcValueIndex);

        [DllImport("powrprof.dll")]
        private static extern uint PowerWriteACValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroup, ref Guid Setting, uint AcValueIndex);

        [DllImport("powrprof.dll")]
        private static extern uint PowerSetActiveScheme(IntPtr UserRootPowerKey, ref Guid SchemeGuid);

        private static readonly Guid SubVideo = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");
        private static readonly Guid VideoBrightness = new Guid("aded5e82-b909-4619-9949-f5d71dac0bcb");

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

        private static int _lastKnownBrightness = 100;
        private static bool _useWmi = true;

        /// <summary>
        /// 是否支持原生 WMI 硬件背光调节
        /// </summary>
        public static bool HasHardwareSupport => _useWmi;

        /// <summary>
        /// 实时读取当前 Windows 系统的真实物理屏幕亮度 (0 ~ 100)，无论用户在键盘 Fn 键还是系统设置中改变，均能即时获取
        /// </summary>
        public static int GetCurrentBrightness()
        {
            // 1. 优先通过 WMI 查询 Windows 硬件实时当前亮度 (与 Fn 键及任务栏亮度滑块 100% 毫秒级同步)
            if (_useWmi)
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        byte b = (byte)obj["CurrentBrightness"];
                        _lastKnownBrightness = (int)b;
                        return _lastKnownBrightness;
                    }
                }
                catch
                {
                    _useWmi = false;
                }
            }

            // 2. 其次通过 Windows 电源方案获取
            try
            {
                IntPtr pScheme;
                if (PowerGetActiveScheme(IntPtr.Zero, out pScheme) == 0 && pScheme != IntPtr.Zero)
                {
                    Guid scheme = Marshal.PtrToStructure<Guid>(pScheme);
                    var sub = SubVideo;
                    var setting = VideoBrightness;
                    uint val;
                    if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out val) == 0)
                    {
                        _lastKnownBrightness = (int)Math.Clamp(val, 0, 100);
                        return _lastKnownBrightness;
                    }
                }
            }
            catch { }

            return _lastKnownBrightness;
        }

        /// <summary>
        /// 设置屏幕亮度 (0 ~ 100)，优先调用硬件背光并同步电源方案
        /// </summary>
        public static void SetBrightness(int brightness)
        {
            brightness = Math.Clamp(brightness, 0, 100);
            _lastKnownBrightness = brightness;

            bool hardwareSuccess = false;

            // 1. 优先调用 WMI 硬件背光驱动设置真实的物理屏幕背光 (与系统完全一致)
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");
                foreach (ManagementObject obj in searcher.Get())
                {
                    obj.InvokeMethod("WmiSetBrightness", new object[] { 1, (byte)brightness });
                    hardwareSuccess = true;
                }
            }
            catch { }

            // 2. 写入 Windows 系统活动电源方案 (即时影响系统快速设置滑块和电源状态)
            try
            {
                IntPtr pScheme;
                if (PowerGetActiveScheme(IntPtr.Zero, out pScheme) == 0 && pScheme != IntPtr.Zero)
                {
                    Guid scheme = Marshal.PtrToStructure<Guid>(pScheme);
                    var sub = SubVideo;
                    var setting = VideoBrightness;
                    PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, (uint)brightness);
                    PowerSetActiveScheme(IntPtr.Zero, ref scheme);
                }
            }
            catch { }

            // 3. 硬件背光调节成功时强制复位 Gamma Ramp 为标准纯净色，绝不双重压暗导致色彩失真与系统脱节
            if (hardwareSuccess)
            {
                ResetGammaRamp();
            }
            else
            {
                // 仅在外接无硬件控制的显示器上才回退到 Gamma 软件调光
                ApplyVisualBrightness(brightness);
            }
        }

        /// <summary>
        /// 强制将显示色彩恢复为标准线性 Gamma，杜绝残留压暗色彩失真
        /// </summary>
        public static void ResetGammaRamp()
        {
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
                    for (int i = 0; i < 256; i++)
                    {
                        int value = (i * 65535) / 255;
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

        /// <summary>
        /// 实时应用视觉亮度色彩映射 (仅用于无硬件背光时的回退)
        /// </summary>
        public static void ApplyVisualBrightness(int brightness)
        {
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

                    // 映射 0 ~ 100 到适度合理的色彩倍率 (0.35 ~ 1.0)
                    double factor = 0.35 + (Math.Clamp(brightness, 0, 100) / 100.0) * 0.65;
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

        #region 系统多媒体播控与音量调节 (原生硬件虚拟键)

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const byte VK_VOLUME_MUTE = 0xAD;
        private const byte VK_VOLUME_DOWN = 0xAE;
        private const byte VK_VOLUME_UP = 0xAF;
        private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
        private const byte VK_MEDIA_PREV_TRACK = 0xB1;
        private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        public static void MediaPlayPause()
        {
            keybd_event(VK_MEDIA_PLAY_PAUSE, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MEDIA_PLAY_PAUSE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        public static void MediaNext()
        {
            keybd_event(VK_MEDIA_NEXT_TRACK, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MEDIA_NEXT_TRACK, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        public static void MediaPrev()
        {
            keybd_event(VK_MEDIA_PREV_TRACK, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MEDIA_PREV_TRACK, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

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

        #region Windows 原生面板联动 (WiFi / 蓝牙 / 系统设置)

        public static void OpenAvailableNetworks()
        {
            OpenUri("ms-availablenetworks:");
        }

        public static void OpenBluetoothSettings()
        {
            OpenUri("ms-settings:bluetooth");
        }

        public static void OpenSystemSettings()
        {
            OpenUri("ms-settings:");
        }

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

        #endregion
    }
}
