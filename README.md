<div align="center">

# CopyBox 📦 v2.2.1

**面向 Windows 10 / 11 的现代化贴边毛玻璃效率抽屉 & 100% 纯本地离线隐私剪贴板管理器。**  
*A blazingly fast, privacy-first, 100% local clipboard manager & productivity drawer for Windows 10/11 built with .NET 8 & modern Fluent Design.*

[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D4?logo=windows&logoColor=white)](https://microsoft.com/windows)
[![Release](https://img.shields.io/badge/Release-v2.2.1-success?logo=github&logoColor=white)](https://github.com/1Than499/CopyBox/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Offline First](https://img.shields.io/badge/Privacy-100%25%20Offline-success.svg)](#)

[ **🆕 v2.2.1 更新内容** ](#-v221-版本更新说明-whats-new-in-v221) | [ **📜 v2.1 更新内容** ](#-v21-版本更新说明-whats-new-in-v21) | [ **🇨🇳 简体中文** ](#-简体中文) | [ **🇺🇸 English** ](#-english)

</div>

---

## 🆕 v2.2.1 版本更新说明 (What's New in v2.2.1)

### 🛡️ 三大核心新特性进化
1. **🎮 全屏防误触与游戏“免打扰模式” (Fullscreen Anti-Mistouch & Gaming DND Mode)**：
   - **全屏应用/游戏自动感知**：底层结合 `SHQueryUserNotificationState`（Direct3D 独占全屏游戏识别）与物理显示器全屏窗口边界比对，当检测到全屏游戏、全屏视频播放或 PPT 演示时，自动屏蔽鼠标移至屏幕边缘的贴边唤出，彻底杜绝打游戏甩鼠标或看电影时边缘误触弹窗（`Alt + V` 热键仍可随时主动唤出）。
   - **手动免打扰开关**：支持在「偏好设置」或「系统托盘右键菜单」一键开启/关闭 **🔕 强制免打扰模式**，自由掌控贴边唤醒。
2. **🖥️ 多显示器支持与贴边位置自定义 (Multi-Monitor Support & Custom Dock Edge)**：
   - **左右贴边自由切换**：侧边栏不仅支持默认的 **➡️ 屏幕右侧贴边**，还可在设置中一键切换为 **⬅️ 屏幕左侧贴边**，滑入/滑出动画、边缘热区感应与毛玻璃圆角自动镜像适配。
   - **智能多屏唤醒策略**：支持 **🖱️ 跟随鼠标所在显示器唤醒（智能多屏）** 与 **🖥️ 始终固定在主显示器唤醒** 两种策略，并内置 Per-Monitor DPI（`GetDpiForMonitor`）精准缩放换算，跨不同分辨率与缩放比例的多屏环境依然严丝合缝（v2.0 侧边栏与 v1.0 居中浮窗双形态同步生效）。
3. **🔒 敏感信息自动遮罩 (Automatic Sensitive Data Masking)**：
   - **智能识别 6 类高敏隐私内容**：自动检测剪贴板中的 **API 密钥 / Token / JWT**（如 `sk-...`、`ghp_...`、`Bearer ...`）、**密码与连接串凭据**、**私钥证书块**、**18 位身份证号**、**16~19 位银行卡号** 以及 **11 位手机号码**。
   - **UI 安全打码 + 粘贴原样输出**：卡片界面默认对敏感字段智能打码（如 `sk-proj****9aB2`、`138****5678`）并标注醒目的金色隐私盾牌角标（`🛡️ 密钥/Token`、`🛡️ 银行卡号` 等），防止投屏、录屏或旁人窥屏泄露隐私；点击卡片右上角 **`👁` / `🙈` 小眼睛按钮**（或右键菜单）可随时单条切换明文预览，且**双击/回车/快捷键粘贴或复制时始终输出 100% 完整原始明文**。

---

<p align="center">
  <img src="assets/v2_clipboard.png" alt="CopyBox v2.2.1 剪贴板中心" width="46%" style="border-radius: 12px; margin-right: 2%; box-shadow: 0 8px 30px rgba(0,0,0,0.18);" />
  <img src="assets/v2_dashboard.png" alt="CopyBox v2.2.1 偏好设置与新特性" width="46%" style="border-radius: 12px; box-shadow: 0 8px 30px rgba(0,0,0,0.18);" />
</p>
<p align="center">
  <em>左图：v2.2.1 剪贴板中心（历史卡片、随手记便签、分类药丸、快速秒贴） &nbsp;|&nbsp; 右图：v2.2.1 偏好设置（多显示器唤醒策略、左右贴边自定义、全屏游戏免打扰、敏感隐私自动遮罩）</em>
</p>

<div align="center">

### 📥 绿色便携免安装版下载 (小白与普通用户推荐)
无需安装 Git，也不需要复杂的开发环境，解压即用，数据默认随程序根目录便携存储，不写系统注册表：

[![Download Portable Zip](https://img.shields.io/badge/下载便携压缩包-CopyBox--v2.2.1--Portable.zip-0078D4?style=for-the-badge&logo=windows&logoColor=white)](https://github.com/1Than499/CopyBox/releases/download/v2.2.1/CopyBox-v2.2.1-Windows-x64-Portable.zip)  
[![Direct EXE](https://img.shields.io/badge/直接下载单文件-CopyBox.exe-10B981?style=for-the-badge&logo=powershell&logoColor=white)](https://github.com/1Than499/CopyBox/raw/main/publish/CopyBox.exe)

*(历史版本可通过顶部 **Branches** 切换查看对应分支，或查阅右侧 [📦 GitHub Releases 发行版列表](https://github.com/1Than499/CopyBox/releases))*

</div>

---

## 📜 v2.1 版本更新说明 (What's New in v2.1)

### 🛠️ 核心修复与体验进化
1. **🖱️ 贴边收起后屏幕右边缘悬停自动唤出**：
   - 新增毫秒级屏幕右边缘热区感应引擎。当侧边栏贴边隐藏后，只需将鼠标移至屏幕最右侧边缘即可丝滑滑出界面；未锁定（`🔓`）状态下鼠标移出面板区域后自动平滑收起。
2. **⚡ 彻底修复首次点击「设置」崩溃问题**：
   - 移除单文件打包环境下存在字体流寻址隐患的第三方 UI 依赖，重构为 100% 原生 WPF 矢量 Fluent 控件样式，并加固初始化生命周期保护锁，首次及任意时刻打开设置均零崩溃、秒级响应。
3. **📁 便携版本地数据存储路径默认与程序根目录一致**：
   - 绿色便携版默认将配置文件（`settings.json`）、本地 SQLite 数据库（`clipvault.db`）及图片/壁纸缓存直接保存在程序所在根目录，真正实现「整个文件夹拷入 U 盘或移动硬盘即插即用，数据随身走」。
4. **🔄 v1.0 居中浮窗与 v2.0 侧边栏双形态设置 100% 同源互通**：
   - 彻底统一两套界面形态下的全部设置项（6 大主题、自定义壁纸、背景透明度、本地存储路径与 SQLite 状态统计、关闭动作、双击卡片行为等），修改任意一项设置或迁移数据目录均在双形态间实时双向同步。
5. **🎨 「背景透明度」逻辑修正与 120FPS 满帧丝滑拖动**：
   - 修正透明度滑块语义：`0%` 代表完全不透明，向右滑动（`0% ~ 85%`）逐步增加背景通透度，且前景文字与图标始终保持 100% 锐利清晰；采用底层 DirectX 画刷 Alpha 通道原位更新与磁盘写入防抖技术，滑动调节全程零卡顿。
6. **🧩 快捷看板添加程序时默认自动提取程序原生图标**：
   - 在快捷看板添加应用程序（`.exe`）动作时，默认自动提取并高清渲染该程序的原生系统图标；同时保留自定义 Emoji 图标能力（填写 Emoji 则优先展示自定义 Emoji，留空则展示程序原生图标）。

---

# 🇨🇳 简体中文

## 🌟 为什么选择 CopyBox 2.1？

在日常使用 Windows 时，自带的剪贴板历史经常呼出迟缓、图片加载卡顿、缺少快速搜索和常用内容置顶功能；而市面上大多数第三方剪贴板工具不仅体积臃肿（基于 Electron 动辄占用几百兆内存），甚至强制捆绑云端同步，让密码、API Token、私信记录等极度敏感的数据面临泄露风险。

**CopyBox 2.1 全面进化**，它不仅是一个极速轻量的离线剪贴板，更是 Windows 桌面右侧优雅的 **多合一生产力抽屉**：
1. **绝对隐私，100% 纯本地离线**：不发起任何网络请求、不收集任何用户遥测，所有历史数据物理留存在用户本地磁盘；
2. **开箱即用真便携，数据默认存放在程序根目录**：默认将配置与数据库保存在程序所在根目录，拷入移动硬盘随插随用；同时也支持自由绑定到 **OneDrive / Dropbox / 坚果云** 目录实现零成本私有云同步；
3. **贴边悬停自动唤出 & 双模形态 100% 同源互通**：屏幕右侧边缘亚克力智能抽屉（支持鼠标移至屏幕右边缘自动滑出、移出或失焦即收、图钉常驻），也可随时切换至经典居中卡片面板，两套形态所有设置实时双向同步；
4. **双重视图架构**：集成 `[ 📋 剪贴板中心 ]` 与 `[ ⚡ 快捷看板 ]` 药丸切换，办公提效与系统控制触手可及；
5. **原生 120FPS 极速性能与刀锋级画质**：100% 纯原生 .NET 8 + WPF 矢量渲染引擎，无第三方重型 UI 库累赘，冷启动飞快，常驻内存极低，毫秒级响应。

---

## ✨ 核心特性一览

### 1. 🗂️ 剪贴板中心 (Clipboard Center)
- **智能内容格式化呈现**：
  - **代码片段**：自动识别 JSON、各类编程语言，采用代码高亮卡片排版；
  - **图片与截图**：自动解析图片分辨率与尺寸（如 `444 × 90 px`），提供圆角自适应缩略图；
  - **多行文本**：优雅段落排版，智能标注复制来源应用（如 Weixin、Word、Chrome 等）；
- **数字键极限秒贴 (`1~9` / `Alt+1~9`)**：无需移动鼠标，按下对应序号即可瞬间将内容粘入目标光标处；
- **智能分类药丸**：提供 `全部`、`文本`、`图片`、`代码`、`置顶` 快速过滤筛选；
- **即时交互体系**：
  - **右键上下文菜单**：卡片上单击右键立即呼出上下文菜单；
  - **右上角 `···` 快捷按钮**：平滑展开置顶、连续粘贴、复制与删除；
  - **钉住置顶**：置顶项目始终固定在最前部；

### 2. ⚡ 快捷看板 (Quick Dashboard)
- **正在播放流媒体控制 (Media Controls)**：
  - 深度集成 Windows SMTC 协议，实时同步当前播放器（网易云音乐、QQ音乐、Spotify 等）的歌曲名、歌手与播放状态，支持上一曲、播放/暂停、下一曲即时操控；
- **核心系统快捷磁贴**：
  - WLAN / 网络设置、蓝牙设备面板、Windows 设置、原生屏幕截图一键触达；
- **自定义快捷动作网格 (Custom Actions)**：
  - 用户可自由添加常用软件、网址、批处理脚本或终端命令；
  - **智能原生图标提取 + 自定义 Emoji**：当目标为应用程序（`.exe`）时，默认自动提取并渲染该程序的原生系统图标；同时支持手动填写自定义 Emoji 图标覆盖；
  - 支持右键点击动作卡片瞬间删除，随心整理工作流；
- **真实系统屏幕亮度双向滑动调节**：
  - 范围 0%~100%，与 Windows 真实亮度保持双向同步；采用独立微胶囊底座与高明度暖金黄太阳矢量图标（`#F59E0B`），在暗夜中清晰醒目；
- **真实系统主音量双向滑动调节与静音控制**：
  - 采用 Windows 原生 `Segoe MDL2 Assets` 矢量字体，支持静音（`\uE74F`）、低音量（`\uE993`）、中音量（`\uE994`）、高音量（`\uE767`）图标动态状态切换，高亮天蓝着色。

---

## 🏆 4 项杀手级生产力功能

| 杀手级特性 | 亮点说明 |
| :--- | :--- |
| 🖱️ **全局拖拽直接粘贴** | 直接按住侧边栏中任意卡片，拖动到聊天框、Word、浏览器、代码编辑器甚至 Windows 资源管理器目录，**松手即自动粘贴或复制文件**，跨窗口交互丝滑流畅。 |
| 🪟 **贴边悬停唤醒与失焦秒收** | 采用毫秒级屏幕右边缘热区检测与全局鼠标挂钩。贴边收起后**鼠标移至屏幕右边缘自动唤出**；未锁定状态下移出或点击外部瞬间智能收起；开启顶栏 📌 图钉后则稳定常驻。 |
| 📑 **顺序连续粘贴流** | 点击卡片上的“连续粘贴”图标即可将多张卡片加入流水线队列。切换到目标文档后，**只需连续按下 `Ctrl + V`**，便会按加入顺序依次粘贴出每一条内容，批量填表与整理文档的终极利器！ |
| 📝 **快捷看板随手记便签** | 侧边栏常驻轻量随手记草稿板。随手记录临时密码、备忘电话、零碎灵感，**软件关闭或电脑重启后自动持久化恢复**，无需频繁新建记事本。 |

---

## 🎨 6 大高级预设主题、自定义壁纸与真背景透明度调节

CopyBox 2.1 拥有精心打磨的 Fluent 视觉系统，在双形态设置中心均提供完整一致的外观定制：

1. 🌙 **深色模式 (Dark)**：经典极夜蓝黑，深沉护眼，高对比度；
2. ☀️ **明亮模式 (Light)**：象牙冷白，纯净素雅，浅灰底盘彻底告别黑块；
3. 🌌 **极光夜空 (Aurora)**：孔雀墨蓝底色与荧光极光绿（`#10B981`）强调色；
4. 🌆 **暮色霞光 (Sunset)**：落日深紫底色与绯橙（`#F97316`）霞光强调色；
5. 🌊 **深海之渊 (Ocean)**：深邃渊蓝底色与天青碧海（`#06B6D4`）强调色；
6. 🖼️ **壁纸模式 (CustomImage)**：
   - **自主上传本地图片**：支持用户选择本地图片作为背景壁纸，自动安全复制至本地持久化目录；
   - **磨砂遮罩浓度自由调节 (30% ~ 95%)**：可根据壁纸明暗随意调节遮罩浓度，确保前景纯白文字（`#FFFFFF`）刀锋级清晰度；
   - **内置暗夜流光壁纸**：免配置内置生成高质感渐变星辉壁纸，开箱即美；
7. 💧 **真实背景透明度丝滑调节 (0% ~ 85%)**：
   - `0%` 代表背景完全不透明，向右滑动（最高 `85%`）逐步增加背景通透感，而前景文字与图标始终保持 100% 不透明清晰锐利；底层原位更新 Alpha 通道，120FPS 拖动零卡顿。

---

## ⌨️ 快捷键速查

| 快捷键 | 作用说明 |
| :--- | :--- |
| `Alt + V` | 全局任意界面快速唤醒 / 隐藏 CopyBox |
| `屏幕最右边缘悬停` | 侧边栏贴边收起后，鼠标移至屏幕右边缘自动滑出唤起 |
| `1` ~ `9` 或 `Alt + 1~9` | 直接秒贴前 1~9 条对应历史内容 |
| `↑` / `↓` | 在卡片列表中上下快速导航移动 |
| `Enter` | 选定当前卡片并自动贴入目标窗口光标处 |
| `Del` | 从本地数据库物理删除当前选中的历史条目 |
| `P` | 将当前条目切换置顶状态 (Pin / Unpin) |
| `鼠标右键` 或 `Apps` | 呼出卡片功能上下文菜单 |
| `Esc` | 瞬间收起并隐藏窗口 |

---

## 🛠️ 技术栈与底层架构

- **运行平台**：[.NET 8.0 SDK](https://dotnet.microsoft.com/) (Windows Desktop)
- **UI 框架**：原生 WPF (.NET 8 Windows 10/11，零重型第三方 UI 库依赖)
- **底层互操作**：
  - `AddClipboardFormatListener`：系统剪贴板高频异步监听
  - `RegisterHotKey`：全局 `Alt + V` 低延迟唤醒
  - `WS_EX_NOACTIVATE`：侧边栏无侵入展示（点击卡片不夺取工作窗口光标焦点）
  - `GetCursorPos` + `SetWindowsHookEx` (WH_MOUSE_LL)：屏幕右边缘悬停唤醒与外部物理边界失焦识别
  - `ExtractAssociatedIcon`：可执行程序原生高清图标提取与内存缓存
  - `Windows.Media.Control` (SMTC)：原生流媒体全局播放会话控制
- **本地存储**：`Microsoft.Data.Sqlite`（开启 **WAL 并发预写日志模式**）
- **打包模式**：单文件独立免安装运行文件 (Single-File Binary)

---

## 🚀 快速上手与本地编译

### 1. 环境准备
确保您的计算机已安装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

### 2. 克隆项目并编译
```bash
git clone https://github.com/1Than499/CopyBox.git
cd CopyBox/ClipVault
dotnet build -c Release
```

### 3. 发布单文件便携免安装版
```bash
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ../publish/
```
生成的单文件可执行程序位于 `publish/CopyBox.exe`（直接双击即可运行，绿色免安装）。

---

## 📁 本地数据目录规范

CopyBox v2.1 默认采用便携式根目录存储（与 `CopyBox.exe` 同目录），当你使用默认路径或自定义保存路径后，会在本地维护清晰的数据拓扑：

```text
CopyBox/
├── CopyBox.exe           # 主程序
├── settings.json         # 用户偏好设置 (双形态 100% 实时互通)
├── clipvault.db          # SQLite 索引与元数据 (文本内容、来源应用、字符数、置顶标记等)
├── clipvault.db-shm      # SQLite WAL 共享内存索引
├── clipvault.db-wal      # SQLite 高性能并发预写日志
├── images/               # 截图与图片独立缓存目录 (sha256_hash.png)
└── Wallpapers/           # 自定义背景壁纸持久化目录
```

---

<br/>

# 🇺🇸 English

## 🌟 Why CopyBox 2.1?

Windows' built-in clipboard history frequently suffers from sluggish popups, slow thumbnail loading, and a lack of quick search or pinning capabilities. Most third-party alternatives are bloated Electron apps that consume hundreds of megabytes of RAM while forcing mandatory cloud synchronization—exposing sensitive credentials, private notes, and source code tokens to potential data leaks.

**CopyBox 2.1 evolves further**: it is not just a lightweight, 100% offline clipboard manager, but an elegant, **docked productivity drawer** on your Windows desktop.

1. **100% Offline & Private**: Zero network connections, zero telemetry, zero analytics. All your history stays securely on your physical disk;
2. **True Portable Root Storage & Private Cloud Sync**: Stores configuration (`settings.json`), database (`clipvault.db`), and image caches directly in the app root directory by default—or point it to your **OneDrive / Dropbox / Google Drive** directory for zero-config multi-device sync;
3. **Edge-Hover Auto-Wake & Unified Dual Window Modes**: Hover at the right edge of your screen to smoothly slide out the **Sidebar Acrylic Drawer**, or switch to the classic **Centered Palette**—with 100% synchronized settings across both modes;
4. **Dual-View Architecture**: Toggle effortlessly between `[ 📋 Clipboard Center ]` and `[ ⚡ Quick Dashboard ]`;
5. **120FPS Blazing Native Performance**: Pure native .NET 8 and WPF vector styles with in-place DirectX brush alpha updates and razor-sharp ClearType rendering.

---

## ✨ Key Capabilities

### 1. 🗂️ Clipboard Center
- **Smart Formatting**: Automatic syntax-highlighted code cards (JSON/C#/Python/JS/etc.), image thumbnails with auto-calculated resolution tags (e.g. `444 × 90 px`), and multi-line text with source app badges;
- **Instant Number-Key Pasting (`1~9` / `Alt+1~9`)**: Paste the top items without ever touching your mouse;
- **Category Filter Pills**: Fast filter tabs for `All`, `Text`, `Images`, `Code`, and `Pinned`;
- **Interactive Rich Context**: Right-click context menus, expandable `···` action bars, warm golden pin badges, and one-click copying/deletion.

### 2. ⚡ Quick Dashboard
- **Now Playing Media Controls**: Deep integration with Windows SMTC (System Media Transport Controls) to view track titles and control Play/Pause/Skip for Spotify, Netease Cloud Music, QQ Music, etc.;
- **Core System Quick Tiles**: Instant access to WLAN, Bluetooth devices, Windows Settings, and Screen Snipping;
- **Custom Quick Action Grid with Native Program Icons**: Add your own applications, websites, scripts, or terminal commands—automatically extracts native `.exe` icons by default while still supporting custom Emoji overrides; right-click to delete anytime;
- **System Display Brightness Slider**: Smooth bidirectional sync with your hardware display, featuring a pill-badge base and a warm-yellow sun icon (`#F59E0B`) for night-time visibility;
- **System Master Volume Slider & Mute Toggle**: Windows native `Segoe MDL2 Assets` vector icons with dynamic volume level states and accent blue styling.

---

## 🏆 4 Killer Productivity Features

1. 🖱️ **Global Drag & Drop Direct Paste**: Drag any clipboard card directly into WeChat, Word, browser textareas, VS Code, or Windows Explorer folders—**release to paste or copy the file instantly**!
2. 🪟 **Right-Edge Hover Wake & Smart Auto-Dismiss**: When collapsed to the edge, simply move your cursor to the right edge of the screen to wake the drawer; when unpinned, moving outside or clicking outside smoothly hides it; pin it (📌) to keep it permanently on top.
3. 📑 **Sequential Continuous Paste Queue**: Queue multiple items by clicking the "Continuous Paste" button. Switch to your target document and **simply press `Ctrl + V` repeatedly** to paste each queued item in sequence.
4. 📝 **Persistent Scratchpad**: Built-in notepad right inside the drawer for temporary passwords, phone numbers, or code snippets—automatically preserved across app restarts.

---

## 🎨 6 High-End Themes, Acrylic Wallpaper & True Background Transparency

- 🌙 **Dark Mode**: Midnight blue-black palette, clean and glare-free;
- ☀️ **Light Mode**: Ivory cool white with delicate slate borders;
- 🌌 **Aurora**: Deep peacock dark green with vibrant emerald accents (`#10B981`);
- 🌆 **Sunset**: Twilight purple with warm sunset orange accents (`#F97316`);
- 🌊 **Ocean**: Abyssal ocean blue with cyan accents (`#06B6D4`);
- 🖼️ **Custom Wallpaper Mode**:
  - Upload your own wallpaper image with adjustable acrylic overlay opacity (30% to 95%) and built-in starry night generator;
- 💧 **True Background Transparency (0% ~ 85%)**:
  - `0%` means completely opaque background, sliding right increases background transparency up to `85%` while keeping foreground text 100% crisp at 120FPS.

---

## ⌨️ Keyboard Shortcuts Reference

| Shortcut | Description |
| :--- | :--- |
| `Alt + V` | Globally toggle / dismiss CopyBox from anywhere |
| `Hover Right Screen Edge` | Automatically slide out the sidebar when collapsed |
| `1` ~ `9` or `Alt + 1~9` | Instantly paste items #1 through #9 |
| `↑` / `↓` | Navigate smoothly through cards |
| `Enter` | Select and paste item directly into active cursor position |
| `Del` | Permanently delete the selected item from database |
| `P` | Toggle item pin status (Pin / Unpin) |
| `Right Click` or `Apps` | Open context menu |
| `Esc` | Instantly dismiss and hide the drawer |

---

## 🚀 Build from Source

```bash
git clone https://github.com/1Than499/CopyBox.git
cd CopyBox/ClipVault
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ../publish/
```

Single-file portable executable will be generated at `publish/CopyBox.exe`.

---

## 🤝 License

This project is open-sourced under the [MIT License](LICENSE).
