# CopyBox 📦

> **面向 Windows 10 / 11 的现代化、100% 纯本地离线、隐私优先剪贴板管理器。**  
> A blazingly fast, privacy-first, 100% local clipboard manager for Windows 10/11 built with modern .NET 8 & Fluent Design.

<p align="center">
  <img src="assets/preview.jpg" alt="CopyBox Preview" width="850" />
</p>

---

## 🌟 为什么选择 CopyBox？

Windows 自带的剪贴板历史经常在关键时刻呼出迟缓、图片加载卡顿、缺少快速搜索和常用内容置顶功能；而市面上许多第三方工具要么体积庞大（基于 Electron 动辄占用 200MB+ 内存），要么强制捆绑云端同步，让密码、Token、代码凭证等高敏感数据面临泄露风险。

**CopyBox 的设计初衷：**
1. **绝对隐私，100% 本地离线**：不发起任何网络请求、不收集任何遥测日志，所有数据物理留存于用户本地磁盘。
2. **用户自选保存路径**：自由指定本地存储位置。将路径设置为你的 **OneDrive / Dropbox / 坚果云** 目录，即可免配置享受安全的私有跨端同步！
3. **原生 Windows 11/10 现代美学**：1:1 还原 Fluent 现代卡片设计，双列卡片布局、自适应明亮与暗黑主题、精致微阴影。
4. **毫秒级弹出与全键盘流**：微型低占用常驻进程，按下 `Alt + V` 瞬间唤醒，支持数字键 `1~9` 直接秒贴。
5. **完整双语国际化支持**：原生内置简体中文与 English，界面、设置与提示无缝即时切换。

---

## ✨ 核心特性

- **🗂️ 1:1 现代双列卡片排版**：
  - **代码片段**：内置 Cascadia Code / Consolas 语法高亮卡片质感，自动识别 JSON、代码语法。
  - **图片预览**：圆角缩略图自适应显示，截图高清无缝预览。
  - **纯文本**：优雅段落排版与来源程序识别。
- **🖱️ 丝滑便捷的交互体验**：
  - **右键上下文菜单**：在任意卡片上右键单击即可立即高亮选中，并在光标处呼出功能菜单。
  - **右上角快捷操作**：与卡片右上角“···”按钮功能 100% 同步，向左内收展开，完美贴合卡片。
  - **核心操作三合一**：
    - `★` **置顶 / 取消置顶**：暖金星号高亮，置顶卡片始终优先沉淀在顶部。
    - `❐` **复制到剪贴板**：一键写入系统剪贴板，带有防死循环自循环智能过滤与 Toast 提示。
    - `✕` **删除此条记录**：从本地 SQLite 数据库中物理删除。
- **⚙️ 规整且可独立拖拽的设置面板**：
  - **窗口内自由拖拽**：按住设置面板标题栏抓手，即可在软件窗口内部任意移动，内置智能边界算法，绝不超出窗口边缘。
  - **三层式固定架构**：固顶拖拽栏 + 中间紧凑滚动区 + 固底操作栏，“完成”与“彻底退出”按钮永远可见，绝不被截断。
  - **刀锋级高清文本渲染**：分离阴影层与内容层，彻底消除 WPF Shader 造成的文字发虚发糊，文字享受原生 ClearType 亚像素级超清显示。
- **🌐 深度国际化 (i18n)**：
  - 完美适配 `简体中文` 与 `English`，支持在设置面板中实时一键切换。
- **💾 高性能本地引擎**：
  - 采用 **SQLite + WAL 并发日志模式**，高频复制写入时界面零卡顿、零锁死。
  - 图片与数据库分治存储：数据库仅存元数据与哈希索引，图片独立存放，轻盈且检索飞快。

---

## ⌨️ 快捷键速查

| 快捷键 | 作用 |
| :--- | :--- |
| `Alt + V` | 全局任意界面呼出 / 隐藏 CopyBox |
| `1` ~ `9` / `Alt+1~9` | 直接秒贴前 1~9 条对应历史内容 |
| `↑` / `↓` | 在卡片列表中上下快速导航移动 |
| `Enter` | 选定当前卡片并自动贴入目标窗口光标处 |
| `Del` | 物理删除当前选中的历史条目 |
| `P` | 将当前条目切换置顶状态 (Pin / Unpin) |
| `Apps` 或 `Shift+F10` | 呼出当前选中卡片的功能菜单 |
| `Esc` | 瞬间收起并隐藏窗口 |

---

## 🛠️ 技术栈

- **开发平台**：[.NET 8.0 SDK](https://dotnet.microsoft.com/)
- **UI 框架**：WPF (.NET 8 Windows Desktop)
- **底层互操作**：Win32 API (`AddClipboardFormatListener`, `RegisterHotKey`, `SendInput`, `SetForegroundWindow`)
- **本地数据库**：`Microsoft.Data.Sqlite` (WAL 并发模式)
- **系统托盘**：原生 Windows Forms `NotifyIcon` 托盘集成

---

## 🚀 快速上手与本地运行

### 1. 环境准备
确保你的电脑已安装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

### 2. 克隆项目并编译
```bash
# 克隆仓库
git clone https://github.com/<your-username>/CopyBox.git
cd CopyBox/ClipVault

# 恢复依赖并编译
dotnet build -c Release
```

### 3. 运行项目
```bash
dotnet run
```

### 4. 发布单文件免安装便携版 (Single-File Release)
```bash
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ../publish/
```
生成的单文件可执行程序将输出到 `publish/CopyBox.exe`。

---

## 📁 本地数据目录结构

当你自定义保存路径（例如 `D:\CopyBoxData\`）后，CopyBox 会在本地维护以下清晰结构：

```text
D:\CopyBoxData\
├── clipboard.db          # SQLite 索引与元数据 (文本内容、来源应用、字符数、置顶标记等)
├── clipboard.db-shm      # SQLite WAL 共享内存索引
├── clipboard.db-wal      # SQLite 高性能预写日志
└── images/               # 截图与图片独立缓存目录 (sha256_hash.png)
```

---

## 🤝 开源许可

本项目基于 [MIT License](LICENSE) 许可协议开源。
