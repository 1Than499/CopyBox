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

        #region Windows 原生 CoreAudio 主音量高精度调度 (IAudioEndpointVolume)

        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject { }

        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig]
            int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);

            [PreserveSig]
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
        }

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig]
            int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        }

        [ComImport]

        [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr pNotify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr pNotify);
            [PreserveSig] int GetChannelCount(out uint pnChannelCount);
            [PreserveSig] int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
            [PreserveSig] int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
            [PreserveSig] int GetMasterVolumeLevel(out float pfLevelDB);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float pfLevel);
            [PreserveSig] int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
            [PreserveSig] int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
            [PreserveSig] int GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);
            [PreserveSig] int VolumeStepUp(ref Guid pguidEventContext);
            [PreserveSig] int VolumeStepDown(ref Guid pguidEventContext);
            [PreserveSig] int QueryHardwareSupport(out uint pdwHardwareSupportMask);
            [PreserveSig] int GetVolumeRange(out float pflVolumeMindB, out float pflVolumeMaxdB, out float pflVolumeIncrementdB);
        }

        private static IAudioEndpointVolume? GetAudioEndpointVolume()
        {
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                IMMDevice? dev = null;

                // 优先尝试 eConsole (0: 任务栏与系统主音量默认输出端点)，次选 eMultimedia (1)，兜底 eCommunications (2)
                int[] roles = { 0, 1, 2 };
                foreach (int role in roles)
                {
                    if (enumerator.GetDefaultAudioEndpoint(0 /* eRender */, role, out IMMDevice endpoint) == 0 && endpoint != null)
                    {
                        dev = endpoint;
                        break;
                    }
                }

                if (dev != null)
                {
                    Guid iid = typeof(IAudioEndpointVolume).GUID;
                    // CLSCTX_ALL = 23
                    if (dev.Activate(ref iid, 23, IntPtr.Zero, out object epvObj) == 0 && epvObj is IAudioEndpointVolume epv)
                    {
                        Marshal.ReleaseComObject(dev);
                        return epv;
                    }
                    Marshal.ReleaseComObject(dev);
                }
            }
            catch { }
            return null;
        }

        private static int _lastKnownMasterVolume = -1;

        /// <summary>
        /// 实时获取 Windows 系统主音量 (0 ~ 100)
        /// </summary>
        public static int GetMasterVolume()
        {
            try
            {
                var epv = GetAudioEndpointVolume();
                if (epv != null)
                {
                    try
                    {
                        if (epv.GetMasterVolumeLevelScalar(out float level) == 0)
                        {
                            int vol = (int)Math.Clamp(Math.Round(level * 100f), 0, 100);
                            _lastKnownMasterVolume = vol;
                            return vol;
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(epv);
                    }
                }
            }
            catch { }

            return _lastKnownMasterVolume >= 0 ? _lastKnownMasterVolume : 50;
        }

        /// <summary>
        /// 毫秒级调节 Windows 系统真实主音量 (0 ~ 100)
        /// </summary>
        public static void SetMasterVolume(int volume)
        {
            int clamped = Math.Clamp(volume, 0, 100);
            _lastKnownMasterVolume = clamped;

            try
            {
                var epv = GetAudioEndpointVolume();
                if (epv != null)
                {
                    try
                    {
                        float level = clamped / 100.0f;
                        Guid empty = Guid.Empty;
                        epv.SetMasterVolumeLevelScalar(level, ref empty);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(epv);
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 获取当前主音量静音状态
        /// </summary>
        public static bool GetIsMuted()
        {
            try
            {
                var epv = GetAudioEndpointVolume();
                if (epv != null)
                {
                    try
                    {
                        if (epv.GetMute(out bool isMuted) == 0)
                        {
                            return isMuted;
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(epv);
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 切换系统静音状态
        /// </summary>
        public static bool ToggleMute()
        {
            try
            {
                var epv = GetAudioEndpointVolume();
                if (epv != null)
                {
                    try
                    {
                        if (epv.GetMute(out bool isMuted) == 0)
                        {
                            Guid empty = Guid.Empty;
                            epv.SetMute(!isMuted, ref empty);
                            return !isMuted;
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(epv);
                    }
                }
            }
            catch { }
            VolumeMute();
            return false;
        }

        #endregion

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
