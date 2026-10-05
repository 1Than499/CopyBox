# ClipVault 🚀

> **面向 Windows 10 / 11 的现代化、100% 纯本地离线、隐私优先剪贴板管理器。**
> A blazingly fast, privacy-first, 100% local clipboard manager for Windows 10/11 built with modern .NET 8 & Fluent Design.

<p align="center">
  <img src="assets/preview.jpg" alt="ClipVault Preview" width="850" />
</p>

---

## 🌟 为什么要做 ClipVault？

Windows 自带的 `Win+V` 经常在关键时刻呼出迟缓、图片加载卡顿、缺少快速搜索和常用内容置顶功能；而市面上许多第三方剪贴板工具要么体积庞大（基于 Electron 动辄占用 200MB+ 内存），要么强制捆绑云端同步，让密码、Token、聊天记录等高敏感数据面临泄露风险。

**ClipVault 的设计初衷：**
1. **绝对隐私，100% 离线**：不发任何网络请求、不收集任何遥测日志，所有数据物理留存于用户设备。
2. **用户自选保存路径**：你可以自由指定本地存储位置。将目录指定在你的 **OneDrive / Dropbox / 坚果云** 下，即可免配置享受安全的私有跨端同步！
3. **毫秒级弹出与全键盘流**：后台微型守护进程，按下 `Alt + V` 瞬间唤醒，数字键 `1~9` 直接秒贴。
4. **原生 Windows 11/10 现代美学**：基于 WPF-UI 实现亚克力毛玻璃 (Acrylic/Mica) 与平滑交互。

---

## ✨ 核心特性

- **🗂️ 双形态交互架构**：
  - **快捷悬浮窗 (Quick Paste)**：无边框半透明浮动面板，输入即搜索，支持 `1~9` 数字键一键粘贴。
  - **独立管理与设置中心 (Main Window)**：大图预览、代码高亮查看、历史批量导出、数据统计。
  - **系统通知栏托盘常驻 (Tray Icon)**：右键一键暂停记录、清空未置顶历史或退出。
- **🛡️ 智能密码黑名单拦截**：
  - 自动识别并忽略来自 1Password、Bitwarden、KeePass、LastPass、Dashlane 等密码管理器窗口的复制行为，杜绝明文凭证污染历史。
- **⚡ 多格式混合存储与解析**：
  - **纯文本**：智能过滤连续空格与换行。
  - **代码片段**：自动识别 JSON、SQL、C#、JS/TS 等常用语法结构。
  - **截图与图片**：自动提取位图并独立存储为高清图片文件，避免数据库文件膨胀。
  - **文件拖拽列表**：记录文件路径与数量。
- **💾 高性能本地引擎**：
  - 采用 **SQLite + WAL 并发日志模式**，高频复制写入的同时，悬浮窗读取零锁死、零卡顿。
  - 图片与数据库分治存储：数据库仅存元数据与相对路径，磁盘占用低且检索飞快。

---

## ⌨️ 快捷键速查

| 快捷键 | 作用 |
| :--- | :--- |
| `Alt + V` | 全局任意界面呼出/隐藏快捷剪贴板浮窗 |
| `1` ~ `9` | 直接秒贴前 1~9 条对应历史内容 |
| `↑` / `↓` | 在历史记录中上下移动光标 |
| `Enter` | 将当前选中的条目模拟回车自动贴入原活动窗口 |
| `Del` | 物理删除当前选中的历史条目 |
| `P` | 将当前条目切换为“常用置顶 (Pin)” |
| `Esc` 或 点击外部 | 瞬间隐藏浮动窗口 |

---

## 🛠️ 技术栈与依赖

- **运行环境**：[.NET 8.0 SDK](https://dotnet.microsoft.com/)
- **UI 框架**：WPF (.NET 8)
- **Fluent 视觉组件**：[WPF-UI (Lepoco)](https://github.com/lepoco/wpfui) (Windows 11 Mica / Fluent Design)
- **底层互操作**：Win32 API (`AddClipboardFormatListener`, `RegisterHotKey`, `SendInput`)
- **本地数据库**：`Microsoft.Data.Sqlite` (WAL 高性能并发模式)
- **系统托盘**：`H.NotifyIcon.Wpf`

---

## 🚀 快速上手与本地运行

### 1. 环境准备
确保你的电脑已安装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

### 2. 克隆项目并编译
```bash
# 克隆仓库
git clone https://github.com/your-username/ClipVault.git
cd ClipVault/ClipVault

# 恢复依赖并编译
dotnet build -c Release
```

### 3. 运行项目
```bash
dotnet run
```

### 4. 发布单文件便携版 (Portable Release)
如果你希望生成一个开箱即用的单文件 `.exe`：
```bash
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ../publish/
```
发布产物将在 `publish/` 目录下生成独立的 `ClipVault.exe`。

---

## 📁 本地数据目录结构说明

当你自定义保存路径（例如 `D:\MyClipboardData\`）后，ClipVault 会在本地维护以下清晰结构：

```text
D:\MyClipboardData\
├── clipboard.db          # SQLite 索引与元数据 (文本内容、来源应用、字符数、置顶标记等)
├── clipboard.db-shm      # SQLite WAL 共享内存索引
├── clipboard.db-wal      # SQLite 高性能预写日志
└── images/               # 截图与图片独立缓存目录 (sha256_hash.png)
```

---

## 🤝 参与贡献与开源许可

欢迎提交 Issue 与 Pull Request！无论是新特性建议、国际化多语言翻译还是体验优化，都十分感谢。

本项目基于 [MIT License](LICENSE) 开源。
