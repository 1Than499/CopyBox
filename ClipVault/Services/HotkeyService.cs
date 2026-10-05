using System;
using System.Windows.Input;
using System.Windows.Interop;
using ClipVault.Interop;
using ClipVault.Models;

namespace ClipVault.Services
{
    public class HotkeyService : IDisposable
    {
        private HwndSource? _hwndSource;
        private const int HOTKEY_ID = 9001;
        private readonly SettingsService _settingsService;

        public event Action? HotkeyPressed;

        public HotkeyService(SettingsService settingsService)
        {
            _settingsService = settingsService;
        }

        public void Start()
        {
            if (_hwndSource != null) return;

            var parameters = new HwndSourceParameters("ClipVaultHotkeyHwnd")
            {
                WindowStyle = 0,
                Width = 0,
                Height = 0,
                PositionX = -2000,
                PositionY = -2000
            };

            _hwndSource = new HwndSource(parameters);
            _hwndSource.AddHook(HwndHook);

            RegisterDefaultHotkey();
        }

        private void RegisterDefaultHotkey()
        {
            if (_hwndSource == null) return;

            // 默认 Alt + V
            uint modifier = NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT;
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(Key.V);

            // 解析配置
            string modConfig = _settingsService.CurrentSettings.HotkeyModifiers;
            if (modConfig.Contains("Ctrl", StringComparison.OrdinalIgnoreCase)) modifier |= NativeMethods.MOD_CONTROL;
            if (modConfig.Contains("Shift", StringComparison.OrdinalIgnoreCase)) modifier |= NativeMethods.MOD_SHIFT;
            if (modConfig.Contains("Win", StringComparison.OrdinalIgnoreCase)) modifier |= NativeMethods.MOD_WIN;

            if (Enum.TryParse<Key>(_settingsService.CurrentSettings.HotkeyKey, true, out var parsedKey))
            {
                vk = (uint)KeyInterop.VirtualKeyFromKey(parsedKey);
            }

            bool success = NativeMethods.RegisterHotKey(_hwndSource.Handle, HOTKEY_ID, modifier, vk);
            if (!success)
            {
                // 若用户首选热键被第三方软件占用，自动尝试备用热键 Ctrl + Shift + V
                uint fallbackMod = NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT;
                uint fallbackVk = (uint)KeyInterop.VirtualKeyFromKey(Key.V);
                NativeMethods.RegisterHotKey(_hwndSource.Handle, HOTKEY_ID, fallbackMod, fallbackVk);
            }
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                handled = true;
                HotkeyPressed?.Invoke();
            }
            return IntPtr.Zero;
        }

        public void Stop()
        {
            if (_hwndSource != null)
            {
                NativeMethods.UnregisterHotKey(_hwndSource.Handle, HOTKEY_ID);
                _hwndSource.RemoveHook(HwndHook);
                _hwndSource.Dispose();
                _hwndSource = null;
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
