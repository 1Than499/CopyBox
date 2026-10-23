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

        // 界面展示形态偏好: "Sidebar" (v2.0 贴边毛玻璃双模抽屉，默认) 或 "Centered" (v1.0 经典居中极速浮窗)
        public string WindowMode { get; set; } = "Sidebar";

        // 窗口关闭动作偏好: "MinimizeToTray" (最小化到托盘) 或 "ExitApp" (直接退出)
        public string CloseAction { get; set; } = "MinimizeToTray";

        // 双击卡片动作偏好: "CopyOnly" (仅复制且保持窗口打开，绝不自动隐藏) 或 "PasteAndHide" (贴入并收起)
        public string DoubleClickAction { get; set; } = "CopyOnly";

        // 点击软件外部(失去焦点)时是否自动隐藏: 默认 false (点击外部绝不自动隐藏/绝不最小化到托盘)
        public bool HideOnDeactivate { get; set; } = false;

        // 主题模式: "Dark" (暗黑模式), "Light" (明亮白天), "Aurora" (极光夜幕), "Sunset" (暮色紫霞), "Ocean" (深海湛蓝), "CustomImage" (自定义图片背景)
        public string ThemeMode { get; set; } = "Dark";

        // 自定义壁纸图片绝对路径 (当 ThemeMode 为 "CustomImage" 时使用)
        public string CustomBackgroundImagePath { get; set; } = string.Empty;

        // 自定义背景壁纸的暗色遮罩浓度: 0.30 ~ 0.95 (默认 0.75，确保前景文字绝对清晰)
        public double BackgroundOverlayOpacity { get; set; } = 0.75;

        // 窗口透明度: 0.60 ~ 1.00 (默认 0.92)
        public double WindowOpacity { get; set; } = 0.92;

        // 界面语言偏好: "zh-CN" (简体中文) 或 "en-US" (English)
        public string Language { get; set; } = "zh-CN";

        // 隐私与过滤规则
        public bool IgnorePasswordManagers { get; set; } = true;
        public bool AutoConvertImageToWebP { get; set; } = false; // 是否自动压缩图片
        public int MaxHistoryCount { get; set; } = 1000; // 本地最大历史保留数
        public bool StartWithWindows { get; set; } = false; // 开机自启
        public bool PortableMode { get; set; } = true; // 便携模式 (默认开启，数据与配置存储在程序根目录)

        // 顶部随手记临时便签内容
        public string ScratchpadContent { get; set; } = string.Empty;

        // 用户自定义快捷动作列表 (系统操作中心扩展磁贴)
        public System.Collections.Generic.List<CustomActionItem> CustomActions { get; set; } = new()
        {
            new CustomActionItem { Id = "screenclip", Name = "屏幕截图", Icon = "✂️", CommandOrPath = "ms-screenclip:" },
            new CustomActionItem { Id = "calc", Name = "计算器", Icon = "", CommandOrPath = "calc" },
            new CustomActionItem { Id = "taskmgr", Name = "任务管理", Icon = "", CommandOrPath = "taskmgr" }
        };

        /// <summary>
        /// 获取程序真实所在的根目录 (完美兼容单文件发布 SingleFile 与普通发布模式)
        /// </summary>
        public static string GetAppRootDirectory()
        {
            try
            {
                string? processPath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(processPath))
                {
                    string? dir = Path.GetDirectoryName(processPath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        return dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    }
                }
            }
            catch { }

            return AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        /// <summary>
        /// 默认本地数据存储路径：直接与程序根目录保持一致 (便携版即开即用，随目录走)
        /// </summary>
        public static string GetDefaultStoragePath()
        {
            string appRoot = GetAppRootDirectory();
            try
            {
                string testFile = Path.Combine(appRoot, ".write_test.tmp");
                File.WriteAllText(testFile, "ok");
                File.Delete(testFile);
                return appRoot;
            }
            catch
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CopyBox",
                    "Data"
                );
            }
        }
    }

    public class CustomActionItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;
        public string Icon { get; set; } = string.Empty;
        public string CommandOrPath { get; set; } = string.Empty;
    }
}
