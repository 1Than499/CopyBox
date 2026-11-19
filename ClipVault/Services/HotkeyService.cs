using System;
using System.Collections.Generic;
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
        private const int SIDEBAR_HOTKEY_ID = 9002;
        private const int CUSTOM_ACTION_HOTKEY_BASE = 9100;
        private readonly SettingsService _settingsService;
        private readonly Dictionary<int, CustomActionItem> _customHotkeyMap = new();

        public event Action? HotkeyPressed;
        public event Action? SidebarHotkeyPressed;
        public event Action<CustomActionItem>? CustomActionHotkeyPressed;

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
            RegisterSidebarHotkey();
            RegisterCustomActionHotkeys(_settingsService.CurrentSettings.CustomActions);
        }

        public void RegisterCustomActionHotkeys(IReadOnlyList<CustomActionItem>? actions)
        {
            if (_hwndSource == null) return;

            foreach (int existingId in _customHotkeyMap.Keys)
            {
                NativeMethods.UnregisterHotKey(_hwndSource.Handle, existingId);
            }
            _customHotkeyMap.Clear();

            if (actions == null) return;

            int nextId = CUSTOM_ACTION_HOTKEY_BASE;
            foreach (var action in actions)
            {
                if (action == null || string.IsNullOrWhiteSpace(action.Hotkey)) continue;
                if (TryParseHotkeyGesture(action.Hotkey, out uint mod, out uint vk))
                {
                    if (NativeMethods.RegisterHotKey(_hwndSource.Handle, nextId, mod | NativeMethods.MOD_NOREPEAT, vk))
                    {
                        _customHotkeyMap[nextId] = action;
                        nextId++;
                    }
                }
            }
        }

        public static bool TryParseHotkeyGesture(string? gesture, out uint modifiers, out uint vk)
        {
            modifiers = 0;
            vk = 0;
            if (string.IsNullOrWhiteSpace(gesture)) return false;

            string[] parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) return false;

            string keyToken = parts[^1];
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string m = parts[i];
                if (m.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || m.Equals("Control", StringComparison.OrdinalIgnoreCase))
                    modifiers |= NativeMethods.MOD_CONTROL;
                else if (m.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                    modifiers |= NativeMethods.MOD_ALT;
                else if (m.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                    modifiers |= NativeMethods.MOD_SHIFT;
                else if (m.Equals("Win", StringComparison.OrdinalIgnoreCase) || m.Equals("Windows", StringComparison.OrdinalIgnoreCase))
                    modifiers |= NativeMethods.MOD_WIN;
            }

            if (modifiers == 0) return false; // 安全限制：全局热键必须带至少一个修饰键 (Ctrl/Alt/Shift/Win)

            if (keyToken.Length == 1 && char.IsDigit(keyToken[0]) && Enum.TryParse<Key>("D" + keyToken, out var digitKey))
            {
                vk = (uint)KeyInterop.VirtualKeyFromKey(digitKey);
                return vk != 0;
            }

            if (Enum.TryParse<Key>(keyToken, true, out var parsedKey))
            {
                vk = (uint)KeyInterop.VirtualKeyFromKey(parsedKey);
                return vk != 0;
            }
            return false;
        }

        public static string? FormatKeyGesture(KeyEventArgs e)
        {
            Key key = (e.Key == Key.System) ? e.SystemKey : e.Key;
            if (key == Key.LeftCtrl || key == Key.RightCtrl ||
                key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LeftShift || key == Key.RightShift ||
                key == Key.LWin || key == Key.RWin ||
                key == Key.None)
            {
                return null;
            }

            ModifierKeys mods = Keyboard.Modifiers;
            if (mods == ModifierKeys.None) return null;

            var parts = new List<string>();
            if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

            string keyName = (key >= Key.D0 && key <= Key.D9)
                ? ((char)('0' + (key - Key.D0))).ToString()
                : key.ToString();
            parts.Add(keyName);
            return string.Join("+", parts);
        }

        private void RegisterSidebarHotkey()
        {
            if (_hwndSource == null) return;

            // 侧边栏专属快捷键：Alt + Shift + V
            uint modifier = NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT;
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(Key.V);
            NativeMethods.RegisterHotKey(_hwndSource.Handle, SIDEBAR_HOTKEY_ID, modifier, vk);
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
            if (msg == NativeMethods.WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == HOTKEY_ID)
                {
                    handled = true;
                    HotkeyPressed?.Invoke();
                }
                else if (id == SIDEBAR_HOTKEY_ID)
                {
                    handled = true;
                    SidebarHotkeyPressed?.Invoke();
                }
                else if (_customHotkeyMap.TryGetValue(id, out var customAction))
                {
                    handled = true;
                    CustomActionHotkeyPressed?.Invoke(customAction);
                }
            }
            return IntPtr.Zero;
        }

        public void Stop()
        {
            if (_hwndSource != null)
            {
                NativeMethods.UnregisterHotKey(_hwndSource.Handle, HOTKEY_ID);
                NativeMethods.UnregisterHotKey(_hwndSource.Handle, SIDEBAR_HOTKEY_ID);
                foreach (int customId in _customHotkeyMap.Keys)
                {
                    NativeMethods.UnregisterHotKey(_hwndSource.Handle, customId);
                }
                _customHotkeyMap.Clear();
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
