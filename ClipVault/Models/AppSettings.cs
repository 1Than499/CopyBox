using System;
using System.IO;

namespace ClipVault.Models
{
    public class AppSettings
    {
        // 核心亮点：用户自定义本地存储目录
        public string StorageDirectory { get; set; } = string.Empty;

        // 全局快捷键
        public string HotkeyModifiers { get; set; } = "Alt"; // Alt, Ctrl, Shift, Win
        public string HotkeyKey { get; set; } = "V";

        // 窗口关闭动作偏好: "MinimizeToTray" (最小化到托盘) 或 "ExitApp" (直接退出)
        public string CloseAction { get; set; } = "MinimizeToTray";

        // 双击卡片动作偏好: "CopyOnly" (仅复制且保持窗口打开，绝不自动隐藏) 或 "PasteAndHide" (贴入并收起)
        public string DoubleClickAction { get; set; } = "CopyOnly";

        // 点击软件外部(失去焦点)时是否自动隐藏: 默认 false (点击外部绝不自动隐藏/绝不最小化到托盘)
        public bool HideOnDeactivate { get; set; } = false;

        // 主题模式: "Dark" (暗黑模式) 或 "Light" (明亮白天模式)
        public string ThemeMode { get; set; } = "Dark";

        // 窗口透明度: 0.60 ~ 1.00 (默认 0.92)
        public double WindowOpacity { get; set; } = 0.92;

        // 界面语言偏好: "zh-CN" (简体中文) 或 "en-US" (English)
        public string Language { get; set; } = "zh-CN";

        // 隐私与过滤规则
        public bool IgnorePasswordManagers { get; set; } = true;
        public bool AutoConvertImageToWebP { get; set; } = false; // 是否自动压缩图片
        public int MaxHistoryCount { get; set; } = 1000; // 本地最大历史保留数
        public bool StartWithWindows { get; set; } = false; // 开机自启
        public bool PortableMode { get; set; } = false; // 便携模式

        public static string GetDefaultStoragePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CopyBox",
                "Data"
            );
        }
    }
}
