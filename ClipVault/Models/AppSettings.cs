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
                "ClipVault",
                "Data"
            );
        }
    }
}
