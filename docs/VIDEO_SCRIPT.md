# CopyBox 📦 官方产品宣传视频脚本与分镜设计方案

> **视频定位**：高燃、极简、现代感十足的桌面生产力工具宣传短片  
> **推荐时长**：30秒（短视频快节奏版） / 60秒（精讲版）  
> **适用平台**：B站 (Bilibili)、抖音、视频号、小红书、YouTube Shorts、X (Twitter)  
> **配套工具**：已内置交互式全自动放映演示器 [`docs/promo_video.html`](./promo_video.html)  
> **成品视频**：已直接离线渲染生成 1080P 高清成片 [`docs/CopyBox_Promo_1080P.mp4`](./CopyBox_Promo_1080P.mp4)（包含完整 6 幕 1:1 画面、AI 中文旁白配音、科技感 BGM 与立体交互音效）

---

## 🎬 30秒短视频分镜脚本 (Storyboard & Narration)

| 时间 | 画面分镜 (Visuals) | 操作动效与特效 (Action & SFX) | 旁白配音台词 (Voiceover - 中文) | 配音字幕 (Voiceover - English) |
| :---: | :--- | :--- | :--- | :--- |
| **00:00 - 00:05**<br/>(第1幕) | **痛点引入与瞬间唤起**<br/>Windows 桌面背景，按下快捷键光效爆发，CopyBox 1:1 现代双列卡片从屏幕中央破空而出。 | **SFX**: 清脆机械键盘按键声 + 空间音效<br/>**动作**: 模拟按快捷键 `Alt + V`，窗口 0.1ms 瞬时弹出，数字键 `1` 秒贴。 | “还在忍受 Windows 自带剪贴板的卡顿与隐私泄露？按下 Alt+V，唤醒 CopyBox！” | "Tired of laggy clipboards and cloud risks? Press Alt+V to wake up CopyBox instantly!" |
| **00:05 - 00:10**<br/>(第2幕) | **1:1 现代双列卡片排版**<br/>镜头推近，展示代码高亮卡片（JSON 语法金黄着色）、高清桌面截图预览、纯文本优雅多行排版。 | **SFX**: 柔和平滑滑动声<br/>**动作**: 鼠标滑过代码卡片与图片卡片，微光发光外框跟随，层级分明。 | “代码片段自动语法高亮，图片高清缩略预览，纯文本优雅呈现，所见即所得。” | "Auto syntax highlighting for code, crisp image previews, and elegant typography." |
| **00:10 - 00:16**<br/>(第3幕) | **右键专属菜单与三点操作**<br/>鼠标在卡片上右键单击，卡片亮起发光，光标处弹出带有饱满立体微阴影的现代悬浮菜单。 | **SFX**: 鼠标微点击声 + 柔和提示音<br/>**动作**: 演示暖金星号 `★ 置顶`、经典天蓝 `❐ 复制`、警示红 `✕ 删除`。 | “卡片上单击右键，暖金星号秒置顶，一键防循环复制，操作行云流水。” | "Right-click any card to pin with golden star, copy, or delete in a single flow." |
| **00:16 - 00:22**<br/>(第4幕) | **窗口内独立拖拽设置面板**<br/>齿轮按钮展开设置面板，鼠标按住标题栏抓手，在窗口内部自由顺畅拖拽，严密贴合边缘。 | **SFX**: 丝滑阻尼感滑行声<br/>**动作**: 展示三层式固定架构，完成按钮永远可见；展示拖拽时严格不超出主窗口边界。 | “想要移动设置？按住标题栏自由拖拽，智能边界保护，绝不移出屏幕。” | "Drag the settings panel freely inside the client area with zero boundary overflowing." |
| **00:22 - 00:26**<br/>(第5幕) | **双语即时切换与超清文字**<br/>演示中英文单选切换，文字笔画纤毫毕现，ClearType 亚像素超清特写。 | **SFX**: 切换确认音效<br/>**动作**: 点击 English 选项，界面瞬间双语重载；特写展示解耦阴影后的刀锋级锐利文字。 | “原生中英双语无缝切换，独家双层解耦架构，字迹如刀锋般锐利清晰。” | "Instant bilingual switching with decoupled shadow layer for razor-sharp ClearType text." |
| **00:26 - 00:30**<br/>(第6幕) | **100% 物理离线与开源致谢**<br/>数据留存本地 SQLite WAL，支持自定义目录与私有云盘同步，定格于 GitHub 仓库主页。 | **SFX**: 宏大明朗的收尾音效<br/>**动作**: 画面浮现“100% Offline • SQLite WAL • 0 Cloud Risks • MIT Licensed”及 GitHub 链接。 | “100% 物理离线，零云端追踪，你的数据完全属于你。CopyBox，现已在 GitHub 开源！” | "100% local, zero telemetry, your privacy guaranteed. CopyBox is now open source on GitHub!" |

---

## 🎥 一键录制与生成视频指南

您无需安装任何专业 3D 或视频剪辑软件，本项目已内置**可交互放映机**：

1. **直接双击打开放映机**：
   - 用任何现代浏览器（Edge、Chrome、Firefox 等）直接打开项目中的：
     `d:\bank\docs\promo_video.html`
2. **全屏沉浸式录制**：
   - 按键盘 `F11` 进入浏览器全屏；
   - 按 Windows 系统自带的录屏快捷键：<kbd>Win</kbd> + <kbd>Alt</kbd> + <kbd>R</kbd>（或使用 OBS Studio）；
   - 点击播放器中央的 **“▶ 播放”**；
   - 放映机将以 60 FPS 电影级平滑帧率自动完成 30 秒全流程高保真动态演示；
   - 再次按 <kbd>Win</kbd> + <kbd>Alt</kbd> + <kbd>R</kbd> 停止录制，录制好的高清 MP4 视频将自动保存在系统的 `Videos\Captures` (捕获) 目录中！
3. **内置科技背景音乐与交互音效 (Built-in Web Audio BGM & SFX)**：
   - **零依赖原生合成引擎**：放映机内置了纯前端 Web Audio 实时合成器，无需联网或下载任何音频文件，点击播放即自动奏响；
   - **现代科技律动编曲**：
     - **BPM**：125 节拍，节奏轻快、活力充沛且富有现代生产力工具质感；
     - **和弦进行**：温暖高级的 `Cmaj7 → Am7 → Fmaj7 → Gsus4` 循环；
     - **声部织体**：低通 Sine 环境音垫 (Ambient Pad) + 弹性 Sub-Bass + 晶莹水滴琶音 (Pluck Arp) + 细腻轻微闭镲 (Hi-hat) 脉冲；
   - **三大立体交互音效**：
     - `Alt + V` 窗口呼出破空音 (`playWhoosh`)；
     - 鼠标点击与卡片选中微滴答声 (`playClick`)；
     - Toast 提示与星标收藏晶莹和弦铃音 (`playChime`)；
   - **自带录音混流**：使用 <kbd>Win</kbd> + <kbd>Alt</kbd> + <kbd>R</kbd> 录制时，系统声卡会自动将此高质量 Web Audio 音乐与音效混流录入 MP4，无需后期手动贴音轨！

4. **音频控制与个性化配乐选项**：
   - **一键静音/开启**：播放控制栏提供 `🎵 科技律动 BGM: 开启 / 🔇 已静音` 开关；
   - **音量滑块**：支持 0% ~ 100% 实时平滑音量调节（推荐保持 30%~40%，方便后期叠加人声解说）；
   - **自选本地音频**：点击控制栏的 `📁 自选音乐` 按钮，可直接选取您本地喜爱的 `.mp3` 或 `.wav` 音频文件，放映机会自动切换为该自选音频循环播放。

5. **外部免版权商业商用音乐推荐渠道 (Royalty-Free Tracks)**：
   - **Pixabay Music** (`pixabay.com/music`)：搜索关键字 `Technology`、`Future Bass`、`Minimal Lo-Fi`、`Modern Corporate`；
   - **YouTube Audio Library**：筛选流派 `Dance & Electronic` / `Ambient`，情绪选择 `Bright` / `Calm`；
   - **推荐配比**：若后期添加真人/AI 语音解说，建议解说旁白音量设为 100%，BGM 响度设为 15%~25%（LUFS -18 至 -22 左右），确保讲解清晰洪亮。
