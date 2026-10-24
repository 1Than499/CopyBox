# CopyBox 官方宣传与介绍视频制作规范 (Video Production Standard)

本规范确立自 2026 年 10 月，适用于 CopyBox 后续所有版本发布、特性更新及功能展示宣传视频的制作与交付。

---

## 1. 核心技术规格 (Core Video Specifications)

- **视频分辨率 (Resolution)**: `1080P` (`1920 × 1080`)，标准 16:9 横屏。
- **视频帧率 (Framerate)**: **`60 FPS`**（全面升级为 60 帧丝滑原生渲染，所有 UI 滑动、缓动动效及鼠标位移均保持 60 帧无撕裂）。
- **编码格式 (Codec)**: 
  - 视频轨：`H.264 / AVC (High Profile)`，像素格式 `yuv420p`，CRF = `18`，Preset = `faster`。
  - 音频轨：`AAC-LC`，立体声/双声道，采样率 `44.1 kHz`，码率 `192 kbps`。
- **容器格式**: `.mp4`（开启 faststart，兼顾 Web 直播流与离线播放）。

---

## 2. AI 配音与音频工程规范 (Voiceover & Audio Standards)

- **AI 配音音色**: 严格固定为 **1.0 经典男声**：
  - 引擎：`edge-tts`
  - 角色：**`zh-CN-YunyangNeural`**
  - 语速：推荐固定为 **`+15%`**（保证节奏紧凑清晰、声线自信沉稳）。
- **混音与母带工程**:
  - **背景音乐 (BGM)**：现代科技电子微节奏（Techno / Ambient Synth），带低频律动。
  - **人声避让 (Sidechain Ducking)**：在配音起音段自动衰减 BGM 音量（衰减至 15%~20%），无配音间隙自动平滑回升。
  - **拟真音效 (SFX)**：在关键交互点注入专属音效（如：贴边呼出 Whoosh、图钉锁定 Click、敏感信息防窥 Shield Pulse 等）。

---

## 3. 发布物料交付套件 (Publishing Kit)

每次视频制作必须完整打包输出以下文件至对应的 `promo_vX.X/` 目录下：

1. **`CopyBox_vX.X_Promo_1080P_60FPS.mp4`**：1080P 60FPS 最终成片。
2. **`CopyBox_vX.X_Bilibili_Cover.jpg`**：Bilibili 专属 1920×1080 高清封面图（主视觉强调核心升级亮点、科技感暗色背景与界面拟真展示）。
3. **`promo_video_vX.X.html`**：完整分镜动画驱动源码（基于纯前端 CSS / Canvas / JS 实现数学连续性插值动画）。
4. **`promo_audio_master.wav`**：独立高保真混音工程音频文件。
5. **发布文案与时间戳指南**：包含 3 套高点击率推荐标题、完整时间戳章节（Timestamps）及推荐搜索 Tags。
