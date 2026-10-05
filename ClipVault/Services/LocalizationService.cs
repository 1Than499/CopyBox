using System;
using System.Collections.Generic;

namespace ClipVault.Services
{
    public class LocalizationService
    {
        public static LocalizationService Instance { get; } = new();

        public string CurrentLanguage { get; private set; } = "zh-CN";

        public event Action? LanguageChanged;

        private readonly Dictionary<string, Dictionary<string, string>> _strings = new()
        {
            ["zh-CN"] = new Dictionary<string, string>
            {
                // 主界面
                ["AppTitle"] = "CopyBox",
                ["SearchPlaceholder"] = "搜索剪贴板历史...",
                ["TabAll"] = "📋  全部",
                ["TabText"] = "≡  文本",
                ["TabImages"] = "🖼  图片",
                ["TabCode"] = "</>  代码",
                ["TabStarred"] = "⭐  置顶",
                ["BottomTips"] = "[Alt+1~9] 数字秒贴  •  [Enter] 贴入  •  [↑↓] 导航  •  [Del] 删除  •  [Esc] 隐藏",
                ["ToastCopied"] = "✓ 已复制到剪贴板",
                ["ToastPasted"] = "✓ 已贴入光标位置",

                // 顶栏按钮 ToolTip
                ["ToolTipThemeLight"] = "切换为白天明亮模式",
                ["ToolTipThemeDark"] = "切换为暗黑夜间模式",
                ["ToolTipSettings"] = "偏好设置与本地存储路径",
                ["ToolTipMinimize"] = "最小化窗口到任务栏 (缩小界面)",
                ["ToolTipCloseTray"] = "关闭窗口 (收起到后台系统托盘，Alt+V 随时唤起)",
                ["ToolTipCloseExit"] = "关闭并彻底退出 CopyBox",

                // 卡片三点菜单
                ["MenuPin"] = "⭐ 置顶此条",
                ["MenuUnpin"] = "⭐ 取消置顶",
                ["MenuCopy"] = "📋 复制到剪贴板",
                ["MenuDelete"] = "🗑 删除此条记录",
                ["MenuTooltip"] = "快捷菜单 (置顶/复制/删除)",

                // 偏好设置面板
                ["SettingsTitle"] = "⚙ 偏好设置",
                ["LangSectionTitle"] = "🌐 界面语言 / Language：",
                ["LangZh"] = "🇨🇳 简体中文",
                ["LangEn"] = "🇺🇸 English",
                ["StoragePathTitle"] = "本地数据存储路径：",
                ["BrowseButton"] = "浏览...",
                ["StatusInfoFormat"] = "ℹ 本地 SQLite 状态正常：已记录 {0} 条 (置顶 {1})，图片 {2} 张，数据 100% 物理留存于本地磁盘。",
                ["AppearanceTitle"] = "🎨 界面外观与背景透明度：",
                ["ThemeDark"] = "🌙 暗黑夜间模式",
                ["ThemeLight"] = "☀️ 白天明亮模式",
                ["OpacityLabel"] = "透明度:",
                ["CloseActionTitle"] = "点击窗口右上角 [✕ 关闭] 时的动作：",
                ["CloseActionTray"] = "最小化到系统托盘 (继续在后台常驻监听)",
                ["CloseActionExit"] = "直接彻底退出 CopyBox 程序",
                ["DoubleClickTitle"] = "🖱 双击卡片列表时的动作：",
                ["DoubleClickCopy"] = "仅复制到剪贴板 (保持窗口打开，绝不收起)",
                ["DoubleClickPaste"] = "直接贴入光标处并自动收起到托盘",
                ["DeactivateTitle"] = "点击软件区域外(失焦)时自动最小化到托盘",
                ["DeactivateTooltip"] = "默认不勾选：在外部操作其他软件时保持窗口可见，方便对照查看",
                ["BtnExitApp"] = "🛑 彻底退出程序",
                ["BtnDone"] = "完成",

                // 托盘菜单与提示
                ["TrayTooltip"] = "CopyBox - 剪贴板安全保管箱 (Alt+V)",
                ["TrayShow"] = "唤起 CopyBox 主界面 (Alt+V)",
                ["TrayPause"] = "暂停剪贴板监听",
                ["TrayResume"] = "恢复剪贴板监听",
                ["TrayClear"] = "清空剪贴板历史...",
                ["TraySettings"] = "偏好设置...",
                ["TrayExit"] = "彻底退出 CopyBox",

                // 对话框
                ["ConfirmExitTitle"] = "确认退出",
                ["ConfirmExitMsg"] = "确认要彻底退出 CopyBox 吗？退出后后台将停止监听剪贴板与热键。",
                ["ConfirmClearTitle"] = "确认清空",
                ["ConfirmClearMsg"] = "确定要清空全部剪贴板历史记录吗？(置顶条目将保留)",
                ["FolderDialogTitle"] = "选择 CopyBox 剪贴板本地数据保存目录",
                ["StorageMigratedTitle"] = "路径切换成功",
                ["StorageMigratedMsg"] = "存储路径已成功切换为：\n{0}\n\n现有历史数据与图片已平滑迁移！",
                ["AlreadyRunningTitle"] = "CopyBox 运行提示",
                ["AlreadyRunningMsg"] = "CopyBox 已经在后台运行中！\n请按下快捷键 [Alt + V] 唤醒，或在右下角系统托盘查看。",

                // 相对时间
                ["TimeJustNow"] = "刚刚",
                ["TimeMinsAgo"] = "{0} 分钟前",
                ["TimeHoursAgo"] = "{0} 小时前",
                ["TimeDaysAgo"] = "{0} 天前"
            },

            ["en-US"] = new Dictionary<string, string>
            {
                // Main Window
                ["AppTitle"] = "CopyBox",
                ["SearchPlaceholder"] = "Search history...",
                ["TabAll"] = "📋  All",
                ["TabText"] = "≡  Text",
                ["TabImages"] = "🖼  Images",
                ["TabCode"] = "</>  Code",
                ["TabStarred"] = "⭐  Starred",
                ["BottomTips"] = "[Alt+1~9] Quick Paste  •  [Enter] Paste  •  [↑↓] Navigate  •  [Del] Delete  •  [Esc] Hide",
                ["ToastCopied"] = "✓ Copied to clipboard",
                ["ToastPasted"] = "✓ Pasted to target cursor",

                // Top Bar Button ToolTips
                ["ToolTipThemeLight"] = "Switch to Light Mode",
                ["ToolTipThemeDark"] = "Switch to Dark Mode",
                ["ToolTipSettings"] = "Preferences & Storage Path",
                ["ToolTipMinimize"] = "Minimize to Taskbar",
                ["ToolTipCloseTray"] = "Close window (Hide to System Tray, wake up anytime with Alt+V)",
                ["ToolTipCloseExit"] = "Close and completely exit CopyBox",

                // Card Context Menu
                ["MenuPin"] = "⭐ Pin Item",
                ["MenuUnpin"] = "⭐ Unpin Item",
                ["MenuCopy"] = "📋 Copy to Clipboard",
                ["MenuDelete"] = "🗑 Delete This Item",
                ["MenuTooltip"] = "Quick Menu (Pin / Copy / Delete)",

                // Preferences Panel
                ["SettingsTitle"] = "⚙ Settings",
                ["LangSectionTitle"] = "🌐 Language / 语言：",
                ["LangZh"] = "🇨🇳 简体中文",
                ["LangEn"] = "🇺🇸 English",
                ["StoragePathTitle"] = "Local Storage Path:",
                ["BrowseButton"] = "Browse...",
                ["StatusInfoFormat"] = "ℹ Local SQLite status is active: {0} items ({1} pinned), {2} images. 100% stored offline on your local disk.",
                ["AppearanceTitle"] = "🎨 Appearance & Window Opacity:",
                ["ThemeDark"] = "🌙 Dark Theme",
                ["ThemeLight"] = "☀️ Light Theme",
                ["OpacityLabel"] = "Opacity:",
                ["CloseActionTitle"] = "Action on clicking [✕ Close]:",
                ["CloseActionTray"] = "Minimize to System Tray (Keep running in background)",
                ["CloseActionExit"] = "Completely Exit CopyBox Application",
                ["DoubleClickTitle"] = "🖱 Action on double-clicking card list:",
                ["DoubleClickCopy"] = "Copy to clipboard only (Keep window open, recommended)",
                ["DoubleClickPaste"] = "Paste to target cursor and auto-hide to tray",
                ["DeactivateTitle"] = "Auto-hide to tray when clicking outside window (lose focus)",
                ["DeactivateTooltip"] = "Default unchecked: keeps window visible while working in other apps for easy reference",
                ["BtnExitApp"] = "🛑 Exit CopyBox",
                ["BtnDone"] = "Done",

                // Tray Menu and ToolTip
                ["TrayTooltip"] = "CopyBox - Secure Local Clipboard Manager (Alt+V)",
                ["TrayShow"] = "Show CopyBox (Alt+V)",
                ["TrayPause"] = "Pause Clipboard Monitoring",
                ["TrayResume"] = "Resume Clipboard Monitoring",
                ["TrayClear"] = "Clear Clipboard History...",
                ["TraySettings"] = "Preferences...",
                ["TrayExit"] = "Exit CopyBox",

                // Dialogs
                ["ConfirmExitTitle"] = "Exit Confirmation",
                ["ConfirmExitMsg"] = "Are you sure you want to completely exit CopyBox? Background monitoring will stop.",
                ["ConfirmClearTitle"] = "Clear History",
                ["ConfirmClearMsg"] = "Are you sure you want to clear all clipboard history? (Pinned items will be preserved)",
                ["FolderDialogTitle"] = "Select Local Storage Directory for CopyBox",
                ["StorageMigratedTitle"] = "Storage Path Changed",
                ["StorageMigratedMsg"] = "Storage directory successfully switched to:\n{0}\n\nExisting history data and images migrated seamlessly!",
                ["AlreadyRunningTitle"] = "CopyBox Notice",
                ["AlreadyRunningMsg"] = "CopyBox is already running in background!\nPress [Alt + V] to bring it up, or check the system tray.",

                // Relative Time
                ["TimeJustNow"] = "Just now",
                ["TimeMinsAgo"] = "{0}m ago",
                ["TimeHoursAgo"] = "{0}h ago",
                ["TimeDaysAgo"] = "{0}d ago"
            }
        };

        public void SetLanguage(string lang)
        {
            if (lang != "en-US" && lang != "zh-CN") lang = "zh-CN";
            if (CurrentLanguage == lang) return;
            CurrentLanguage = lang;
            LanguageChanged?.Invoke();
        }

        public string Get(string key, params object[] args)
        {
            if (_strings.TryGetValue(CurrentLanguage, out var dict) && dict.TryGetValue(key, out var val))
            {
                return args.Length > 0 ? string.Format(val, args) : val;
            }
            if (_strings["en-US"].TryGetValue(key, out var fallback))
            {
                return args.Length > 0 ? string.Format(fallback, args) : fallback;
            }
            return key;
        }
    }
}
