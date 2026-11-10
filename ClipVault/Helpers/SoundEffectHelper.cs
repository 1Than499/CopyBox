using System;
using System.IO;
using System.Media;
using System.Threading.Tasks;
using ClipVault.Interop;
using ClipVault.Models;

namespace ClipVault.Helpers
{
    /// <summary>
    /// v3.0 纯内存合成轻量微触感音效引擎
    /// 无需任何外部音频文件 (0KB 体积开销)，在内存中预生成柔和正弦波泛音，
    /// 支持复制捕获、贴边弹出、秒贴确认三种微触感反馈，并在全屏游戏免打扰模式下自动静默
    /// </summary>
    public static class SoundEffectHelper
    {
        private static readonly byte[] CopyWavBytes = SynthesizeDualToneWav(587.33, 880.0, 42, 0.08);   // D5 -> A5 柔和气泡音
        private static readonly byte[] PasteWavBytes = SynthesizeDualToneWav(523.25, 1046.50, 48, 0.09); // C5 -> C6 清脆贴入确认
        private static readonly byte[] PopWavBytes = SynthesizeDualToneWav(440.0, 659.25, 36, 0.06);    // A4 -> E5 轻盈滑出微音

        public static void PlayCopyCue(AppSettings? settings) => PlayIfAllowed(settings, CopyWavBytes);
        public static void PlayPasteCue(AppSettings? settings) => PlayIfAllowed(settings, PasteWavBytes);
        public static void PlayPopCue(AppSettings? settings) => PlayIfAllowed(settings, PopWavBytes);

        private static void PlayIfAllowed(AppSettings? settings, byte[] wavData)
        {
            if (settings == null || !settings.EnableSoundEffects) return;
            if (settings.IsManualDndMode) return;

            _ = Task.Run(() =>
            {
                try
                {
                    if (settings.EnableFullscreenDnd && NativeMethods.IsForegroundFullscreenOrGame())
                    {
                        return;
                    }
                    using var ms = new MemoryStream(wavData, writable: false);
                    using var player = new SoundPlayer(ms);
                    player.PlaySync();
                }
                catch { }
            });
        }

        /// <summary>
        /// 在内存中生成带指数衰减包络 (Exponential Decay Envelope) 的 16-bit 44.1kHz 单声道 WAV 字节流
        /// </summary>
        public static byte[] SynthesizeDualToneWav(double freq1, double freq2, int durationMs, double amplitude)
        {
            const int sampleRate = 44100;
            int totalSamples = (sampleRate * durationMs) / 1000;
            int dataBytesLen = totalSamples * 2;

            using var ms = new MemoryStream(44 + dataBytesLen);
            using var bw = new BinaryWriter(ms);

            // RIFF Header
            bw.Write(new[] { 'R', 'I', 'F', 'F' });
            bw.Write(36 + dataBytesLen);
            bw.Write(new[] { 'W', 'A', 'V', 'E' });
            bw.Write(new[] { 'f', 'm', 't', ' ' });
            bw.Write(16);
            bw.Write((short)1); // PCM
            bw.Write((short)1); // Mono
            bw.Write(sampleRate);
            bw.Write(sampleRate * 2);
            bw.Write((short)2);
            bw.Write((short)16);
            bw.Write(new[] { 'd', 'a', 't', 'a' });
            bw.Write(dataBytesLen);

            int halfSamples = totalSamples / 2;
            for (int i = 0; i < totalSamples; i++)
            {
                double t = (double)i / sampleRate;
                double freq = (i < halfSamples) ? freq1 : freq2;
                double progress = (double)i / totalSamples;
                // 柔和淡入 + 指数衰减包络，彻底消除爆音 (Click-free)
                double attack = Math.Min(1.0, i / 120.0);
                double decay = Math.Exp(-4.2 * progress);
                double sample = Math.Sin(2.0 * Math.PI * freq * t) * amplitude * attack * decay;
                short pcm = (short)Math.Clamp(sample * short.MaxValue, short.MinValue, short.MaxValue);
                bw.Write(pcm);
            }

            return ms.ToArray();
        }
    }
}
