using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClipVault.Helpers
{
    /// <summary>
    /// 全局单一真相来源 (Single Source of Truth) 主题配色引擎：
    /// Linear × Apple Notes 极简克制中性色阶，统一驱动 v2.0 侧边栏与 v1.0 居中浮窗全部 6 套主题与 Win11 材质。
    /// </summary>
    public static class ThemePalette
    {
        private readonly record struct PaletteSpec(
            bool IsLight,
            Color Canvas,
            Color Header,
            Color Surface,
            Color SurfaceHover,
            Color Border,
            Color TextPrimary,
            Color TextSecondary,
            Color Accent,
            Color CodeBg,
            Color CodeText,
            Color InputBg);

        private static readonly PaletteSpec DarkSpec = new(
            IsLight: false,
            Canvas: Color.FromRgb(0x11, 0x11, 0x14),
            Header: Color.FromRgb(0x14, 0x14, 0x18),
            Surface: Color.FromRgb(0x18, 0x18, 0x1C),
            SurfaceHover: Color.FromRgb(0x23, 0x23, 0x29),
            Border: Color.FromRgb(0x27, 0x27, 0x2E),
            TextPrimary: Color.FromRgb(0xF4, 0xF4, 0xF5),
            TextSecondary: Color.FromRgb(0x8E, 0x8E, 0x96),
            Accent: Color.FromRgb(0x3B, 0x82, 0xF6),
            CodeBg: Color.FromRgb(0x12, 0x12, 0x16),
            CodeText: Color.FromRgb(0xA1, 0xE3, 0xCB),
            InputBg: Color.FromRgb(0x15, 0x15, 0x19));

        private static readonly PaletteSpec LightSpec = new(
            IsLight: true,
            Canvas: Color.FromRgb(0xF5, 0xF5, 0xF7),
            Header: Color.FromRgb(0xFA, 0xFA, 0xFC),
            Surface: Color.FromRgb(0xFF, 0xFF, 0xFF),
            SurfaceHover: Color.FromRgb(0xEE, 0xEE, 0xF1),
            Border: Color.FromRgb(0xE2, 0xE2, 0xE7),
            TextPrimary: Color.FromRgb(0x1D, 0x1D, 0x1F),
            TextSecondary: Color.FromRgb(0x6E, 0x6E, 0x73),
            Accent: Color.FromRgb(0x00, 0x7A, 0xFF),
            CodeBg: Color.FromRgb(0xF0, 0xF0, 0xF4),
            CodeText: Color.FromRgb(0x0F, 0x76, 0x6E),
            InputBg: Color.FromRgb(0xEC, 0xEC, 0xF0));

        private static readonly PaletteSpec AuroraSpec = new(
            IsLight: false,
            Canvas: Color.FromRgb(0x10, 0x16, 0x15),
            Header: Color.FromRgb(0x12, 0x1A, 0x19),
            Surface: Color.FromRgb(0x16, 0x1F, 0x1D),
            SurfaceHover: Color.FromRgb(0x1F, 0x2C, 0x29),
            Border: Color.FromRgb(0x25, 0x35, 0x32),
            TextPrimary: Color.FromRgb(0xEE, 0xF7, 0xF4),
            TextSecondary: Color.FromRgb(0x88, 0xA8, 0x9E),
            Accent: Color.FromRgb(0x10, 0xB9, 0x81),
            CodeBg: Color.FromRgb(0x0E, 0x14, 0x13),
            CodeText: Color.FromRgb(0x6E, 0xE7, 0xB7),
            InputBg: Color.FromRgb(0x12, 0x1A, 0x19));

        private static readonly PaletteSpec SunsetSpec = new(
            IsLight: false,
            Canvas: Color.FromRgb(0x17, 0x12, 0x16),
            Header: Color.FromRgb(0x1B, 0x15, 0x1A),
            Surface: Color.FromRgb(0x20, 0x18, 0x1F),
            SurfaceHover: Color.FromRgb(0x2B, 0x20, 0x2A),
            Border: Color.FromRgb(0x36, 0x28, 0x34),
            TextPrimary: Color.FromRgb(0xFA, 0xF2, 0xF4),
            TextSecondary: Color.FromRgb(0xAD, 0x92, 0x9E),
            Accent: Color.FromRgb(0xF9, 0x73, 0x16),
            CodeBg: Color.FromRgb(0x14, 0x10, 0x14),
            CodeText: Color.FromRgb(0xFD, 0xBA, 0x74),
            InputBg: Color.FromRgb(0x1B, 0x15, 0x1A));

        private static readonly PaletteSpec OceanSpec = new(
            IsLight: false,
            Canvas: Color.FromRgb(0x0F, 0x14, 0x1B),
            Header: Color.FromRgb(0x12, 0x18, 0x21),
            Surface: Color.FromRgb(0x16, 0x1E, 0x29),
            SurfaceHover: Color.FromRgb(0x1E, 0x29, 0x38),
            Border: Color.FromRgb(0x26, 0x33, 0x45),
            TextPrimary: Color.FromRgb(0xF0, 0xF6, 0xFC),
            TextSecondary: Color.FromRgb(0x8B, 0x9E, 0xB4),
            Accent: Color.FromRgb(0x0E, 0xA5, 0xE9),
            CodeBg: Color.FromRgb(0x0C, 0x11, 0x17),
            CodeText: Color.FromRgb(0x7D, 0xD3, 0xFC),
            InputBg: Color.FromRgb(0x12, 0x18, 0x21));

        private static PaletteSpec GetSpec(string? themeMode)
        {
            if (string.Equals(themeMode, "Light", StringComparison.OrdinalIgnoreCase)) return LightSpec;
            if (string.Equals(themeMode, "Aurora", StringComparison.OrdinalIgnoreCase)) return AuroraSpec;
            if (string.Equals(themeMode, "Sunset", StringComparison.OrdinalIgnoreCase)) return SunsetSpec;
            if (string.Equals(themeMode, "Ocean", StringComparison.OrdinalIgnoreCase)) return OceanSpec;
            return DarkSpec;
        }

        public static bool IsLightTheme(string? themeMode)
        {
            return string.Equals(themeMode, "Light", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 统一透明度归一化（0% 透明度 = 1.0 完全不透明；防止异常 0 值被误夹紧为 0.15 -> 85%）
        /// </summary>
        public static double NormalizeWindowOpacity(double rawOpacity)
        {
            if (double.IsNaN(rawOpacity) || double.IsInfinity(rawOpacity) || rawOpacity <= 0.001)
            {
                return 1.0;
            }
            return Math.Clamp(rawOpacity, 0.15, 1.0);
        }

        /// <summary>
        /// 单一真相来源 Alpha 计算：实时拖动滑块与 ApplyTheme 共用同一套公式，0% 透明度严格保持完全不透明 (255)
        /// </summary>
        public static (byte BgAlpha, byte HeaderAlpha, byte CardAlpha) ComputeAlphas(double windowOpacity, string? backdropMode, bool isCustom)
        {
            double clamped = NormalizeWindowOpacity(windowOpacity);
            double effectiveBg = clamped;
            if (clamped < 0.995)
            {
                if (string.Equals(backdropMode, "Mica", StringComparison.OrdinalIgnoreCase))
                {
                    effectiveBg = Math.Min(clamped, 0.84);
                }
                else if (string.Equals(backdropMode, "Acrylic", StringComparison.OrdinalIgnoreCase))
                {
                    effectiveBg = Math.Min(clamped, 0.72);
                }
            }

            byte bgAlpha = (byte)Math.Clamp((int)Math.Round(effectiveBg * 255), 38, 255);
            byte headerAlpha = (byte)Math.Clamp((int)Math.Round(Math.Min(1.0, effectiveBg + 0.04) * 255), 48, 255);
            byte cardAlpha = isCustom
                ? (byte)0x88
                : (byte)Math.Clamp((int)Math.Round((0.68 + 0.32 * clamped) * 255), 165, 255);
            return (bgAlpha, headerAlpha, cardAlpha);
        }

        /// <summary>
        /// 将统一克制的主题色板写入目标窗口资源字典（同时兼容 SidebarWindow 与 MainWindow 所有画刷键）
        /// </summary>
        public static Brush Apply(Window window, string? themeMode, double windowOpacity, string? backdropMode)
        {
            bool isCustom = string.Equals(themeMode, "CustomImage", StringComparison.OrdinalIgnoreCase);
            var spec = GetSpec(themeMode);
            var (bgAlpha, headerAlpha, cardAlpha) = ComputeAlphas(windowOpacity, backdropMode, isCustom);

            Brush canvasBrush = isCustom
                ? Brushes.Transparent
                : new SolidColorBrush(Color.FromArgb(bgAlpha, spec.Canvas.R, spec.Canvas.G, spec.Canvas.B));

            var res = window.Resources;
            res["SidebarBgBrush"] = canvasBrush;
            res["SidebarBorderBrush"] = new SolidColorBrush(isCustom ? Color.FromArgb(0x45, 0xFF, 0xFF, 0xFF) : spec.Border);
            res["HeaderBgBrush"] = new SolidColorBrush(Color.FromArgb(isCustom ? (byte)0x78 : headerAlpha, spec.Header.R, spec.Header.G, spec.Header.B));
            res["CardBgBrush"] = new SolidColorBrush(Color.FromArgb(cardAlpha, spec.Surface.R, spec.Surface.G, spec.Surface.B));
            res["CardHoverBgBrush"] = new SolidColorBrush(isCustom ? Color.FromArgb(0xB8, spec.SurfaceHover.R, spec.SurfaceHover.G, spec.SurfaceHover.B) : spec.SurfaceHover);
            res["CardBorderBrush"] = new SolidColorBrush(isCustom ? Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF) : spec.Border);
            res["TextPrimaryBrush"] = new SolidColorBrush(spec.TextPrimary);
            res["TextSecondaryBrush"] = new SolidColorBrush(spec.TextSecondary);
            res["AccentBlueBrush"] = new SolidColorBrush(spec.Accent);
            res["AccentBlueHoverBrush"] = new SolidColorBrush(spec.Accent);
            res["TileBgBrush"] = new SolidColorBrush(Color.FromArgb(cardAlpha, spec.Surface.R, spec.Surface.G, spec.Surface.B));
            res["TileHoverBgBrush"] = new SolidColorBrush(spec.SurfaceHover);
            res["TabContainerBgBrush"] = new SolidColorBrush(Color.FromArgb(isCustom ? (byte)0x88 : cardAlpha, spec.InputBg.R, spec.InputBg.G, spec.InputBg.B));
            res["SegmentedActiveBgBrush"] = new SolidColorBrush(spec.IsLight ? Color.FromRgb(0xFF, 0xFF, 0xFF) : (isCustom ? Color.FromArgb(0xC0, 0x26, 0x26, 0x2E) : Color.FromRgb(0x26, 0x26, 0x2E)));
            res["SegmentedActiveBorderBrush"] = new SolidColorBrush(spec.IsLight ? Color.FromRgb(0xDC, 0xDC, 0xE2) : (isCustom ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromRgb(0x36, 0x36, 0x40)));
            res["CodeBoxBgBrush"] = new SolidColorBrush(spec.CodeBg);
            res["CodeTextBrush"] = new SolidColorBrush(spec.CodeText);
            res["CodeKeyBrush"] = new SolidColorBrush(spec.IsLight ? Color.FromRgb(0x03, 0x69, 0xA1) : Color.FromRgb(0x38, 0xBD, 0xF8));
            res["CodeStrBrush"] = new SolidColorBrush(spec.IsLight ? Color.FromRgb(0xC2, 0x41, 0x0C) : Color.FromRgb(0xFB, 0x92, 0x3C));
            res["CodeBraceBrush"] = new SolidColorBrush(spec.IsLight ? Color.FromRgb(0xB4, 0x53, 0x09) : Color.FromRgb(0xFB, 0xBF, 0x24));
            res["CodeNumBrush"] = new SolidColorBrush(spec.IsLight ? Color.FromRgb(0x6D, 0x28, 0xD9) : Color.FromRgb(0xA7, 0x8B, 0xFA));
            res["SliderTrackBgBrush"] = new SolidColorBrush(spec.IsLight ? Color.FromRgb(0xD4, 0xD4, 0xDA) : Color.FromRgb(0x32, 0x32, 0x3A));
            res["MenuBgBrush"] = new SolidColorBrush(Color.FromArgb(0xFA, spec.Surface.R, spec.Surface.G, spec.Surface.B));
            res["MenuBorderBrush"] = new SolidColorBrush(spec.Border);
            res["MenuTextBrush"] = new SolidColorBrush(spec.TextPrimary);
            res["SelectedBorderBrush"] = new SolidColorBrush(spec.IsLight ? Color.FromRgb(0xC2, 0xC2, 0xCC) : Color.FromRgb(0x3F, 0x3F, 0x48));
            res["ScratchpadInputBgBrush"] = new SolidColorBrush(spec.CodeBg);
            res["ScratchpadInputBorderBrush"] = new SolidColorBrush(spec.Border);
            res["QueueBarBgBrush"] = new SolidColorBrush(spec.SurfaceHover);

            // 统一映射 MainWindow 既有语义别名，确保双形态 100% 同源
            res["TitleTextBrush"] = res["TextPrimaryBrush"];
            res["CardTextBrush"] = res["TextPrimaryBrush"];
            res["SubTextBrush"] = res["TextSecondaryBrush"];
            res["CodeBgBrush"] = res["CodeBoxBgBrush"];
            res["WindowBorderBrush"] = res["SidebarBorderBrush"];
            res["SearchBgBrush"] = res["TabContainerBgBrush"];
            res["SearchBorderBrush"] = res["CardBorderBrush"];
            res["HoverBgBrush"] = new SolidColorBrush(spec.IsLight ? Color.FromArgb(0x14, 0x00, 0x00, 0x00) : Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
            res["PressedBgBrush"] = new SolidColorBrush(spec.IsLight ? Color.FromArgb(0x22, 0x00, 0x00, 0x00) : Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
            res["TopPillBgBrush"] = Brushes.Transparent;
            res["TopPillBorderBrush"] = Brushes.Transparent;
            res["TopButtonFgBrush"] = res["TextSecondaryBrush"];
            res["TabPillBgBrush"] = res["CardBgBrush"];
            res["TabPillBorderBrush"] = res["CardBorderBrush"];
            res["TabPillFgBrush"] = res["TextSecondaryBrush"];
            res["InfoBgBrush"] = res["TabContainerBgBrush"];
            res["InfoBorderBrush"] = res["CardBorderBrush"];
            res["InfoTextBrush"] = res["TextSecondaryBrush"];

            return isCustom
                ? new SolidColorBrush(Color.FromArgb(0x28, 0x0E, 0x0E, 0x12))
                : canvasBrush;
        }

        /// <summary>
        /// 生成统一的 12px 单色 Segoe MDL2 菜单线性图标，消除所有彩色 Emoji 杂音
        /// </summary>
        public static TextBlock CreateMenuGlyph(string glyph, Brush? foreground = null)
        {
            return new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Foreground = foreground ?? new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA2)),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
        }
    }
}
