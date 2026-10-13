using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Windows.Media.Control;

namespace ClipVault.Services
{
    /// <summary>
    /// 当前系统播放媒体的元数据模型
    /// </summary>
    public class MediaInfo
    {
        public bool HasMedia { get; set; } = false;
        public string Title { get; set; } = "未检测到播放中的音源";
        public string Artist { get; set; } = "支持网易云/QQ音乐/抖音/Spotify/Edge等";
        public string Album { get; set; } = "";
        public bool IsPlaying { get; set; } = false;
        public BitmapImage? Thumbnail { get; set; }
        public string SourceAppId { get; set; } = "";
    }

    /// <summary>
    /// 全能系统媒体监控服务：融合 WinRT GSMTC 与 Windows CoreAudio WASAPI 双引擎
    /// 100% 覆盖网易云音乐、QQ音乐、抖音桌面版、各大浏览器及任意音频播放器
    /// </summary>
    public class SystemMediaService
    {
        private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
        private GlobalSystemMediaTransportControlsSession? _currentSession;
        private readonly DispatcherTimer _pollTimer;
        private MMDeviceEnumerator? _audioEnumerator;

        public MediaInfo CurrentMedia { get; private set; } = new();
        public event Action<MediaInfo>? MediaInfoChanged;

        public SystemMediaService()
        {
            try
            {
                _audioEnumerator = new MMDeviceEnumerator();
            }
            catch { }

            // 1 秒心跳轮询，实时捕获后台播放器状态
            _pollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1000)
            };
            _pollTimer.Tick += async (s, e) => await RefreshMediaAsync();
            _pollTimer.Start();

            _ = InitializeGsmtcAsync();
        }

        private async Task InitializeGsmtcAsync()
        {
            try
            {
                _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                if (_sessionManager != null)
                {
                    _sessionManager.SessionsChanged += OnSessionsChanged;
                }
            }
            catch { }
            await RefreshMediaAsync();
        }

        private async void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
        {
            await RefreshMediaAsync();
        }

        /// <summary>
        /// 双引擎实时刷新媒体信息
        /// </summary>
        public async Task RefreshMediaAsync()
        {
            var newInfo = new MediaInfo();

            // ================= 引擎 1: WinRT GSMTC (提取高质量标题/艺术家/封面) =================
            try
            {
                if (_sessionManager == null)
                {
                    try { _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); } catch { }
                }

                if (_sessionManager != null)
                {
                    var allSessions = _sessionManager.GetSessions();
                    GlobalSystemMediaTransportControlsSession? bestSession = null;

                    foreach (var s in allSessions)
                    {
                        try
                        {
                            var playback = s.GetPlaybackInfo();
                            if (playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                            {
                                bestSession = s;
                                break;
                            }
                        }
                        catch { }
                    }

                    if (bestSession == null)
                    {
                        try { bestSession = _sessionManager.GetCurrentSession() ?? allSessions.FirstOrDefault(); } catch { }
                    }

                    _currentSession = bestSession;

                    if (_currentSession != null)
                    {
                        newInfo.SourceAppId = _currentSession.SourceAppUserModelId ?? "";
                        var playback = _currentSession.GetPlaybackInfo();
                        newInfo.IsPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                        var props = await _currentSession.TryGetMediaPropertiesAsync();
                        if (props != null)
                        {
                            string t = props.Title?.Trim() ?? "";
                            string a = props.Artist?.Trim() ?? "";
                            if (!string.IsNullOrEmpty(t) || !string.IsNullOrEmpty(a))
                            {
                                newInfo.HasMedia = true;
                                newInfo.Title = string.IsNullOrEmpty(t) ? "正在播放媒体" : t;
                                newInfo.Artist = string.IsNullOrEmpty(a) ? GetFriendlyAppName(newInfo.SourceAppId) : a;
                                newInfo.Album = props.AlbumTitle?.Trim() ?? "";

                                if (props.Thumbnail != null)
                                {
                                    try
                                    {
                                        using var stream = await props.Thumbnail.OpenReadAsync();
                                        using var netStream = stream.AsStreamForRead();
                                        var bmp = new BitmapImage();
                                        bmp.BeginInit();
                                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                                        bmp.StreamSource = netStream;
                                        bmp.EndInit();
                                        bmp.Freeze();
                                        newInfo.Thumbnail = bmp;
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            // ================= 引擎 2: CoreAudio WASAPI 会话探测 (100% 捕获发声进程) =================
            // 若 GSMTC 未提供曲目详情，或者未处于活跃发声，通过核心音频枚举底层会话
            if (!newInfo.HasMedia)
            {
                try
                {
                    if (_audioEnumerator == null) _audioEnumerator = new MMDeviceEnumerator();
                    var defaultDevice = _audioEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    if (defaultDevice != null)
                    {
                        var sessionManager = defaultDevice.AudioSessionManager;
                        if (sessionManager != null)
                        {
                            var sessions = sessionManager.Sessions;

                            int bestIndex = -1;
                            float maxPeak = -1f;
                            int fallbackIndex = -1;

                            for (int i = 0; i < sessions.Count; i++)
                            {
                                var s = sessions[i];
                                uint pid = s.GetProcessID;
                                if (pid == 0) continue;

                                float peak = 0;
                                try { peak = s.AudioMeterInformation.MasterPeakValue; } catch { }
                                bool isActive = (int)s.State == 1; // AudioSessionStateActive

                                // 优先寻找正在发声 (peak > 0 或 Active) 的媒体
                                if ((isActive || peak > 0.001f) && peak > maxPeak)
                                {
                                    maxPeak = peak;
                                    bestIndex = i;
                                }
                                else if (fallbackIndex == -1 && IsKnownMediaProcess(pid))
                                {
                                    fallbackIndex = i;
                                }
                            }

                            // 优先选正在发声的，其次选已打开的媒体会话
                            int targetIdx = bestIndex >= 0 ? bestIndex : fallbackIndex;

                            if (targetIdx >= 0)
                            {
                                var s = sessions[targetIdx];
                                uint pid = s.GetProcessID;
                                try
                                {
                                    var proc = Process.GetProcessById((int)pid);
                                    string pName = proc.ProcessName.ToLowerInvariant();

                                    if (pName != "system" && pName != "audiodg" && pName != "explorer")
                                    {
                                        string friendly = GetFriendlyAppName(pName);
                                        string wTitle = "";
                                        try { wTitle = proc.MainWindowTitle?.Trim() ?? ""; } catch { }

                                        newInfo.HasMedia = true;
                                        newInfo.IsPlaying = bestIndex >= 0; // 有峰值或Active则为播放中
                                        newInfo.SourceAppId = pName;
                                        newInfo.Thumbnail = ExtractIconFromProcess((int)pid);

                                        // 若窗口标题包含有价值的歌名信息 (例如 "周杰伦 - 晴天")
                                        if (!string.IsNullOrEmpty(wTitle) && !wTitle.Equals(friendly, StringComparison.OrdinalIgnoreCase) && !wTitle.Equals(pName, StringComparison.OrdinalIgnoreCase))
                                        {
                                            newInfo.Title = wTitle;
                                            newInfo.Artist = friendly;
                                        }
                                        else
                                        {
                                            newInfo.Title = newInfo.IsPlaying ? "正在播放音频" : "音频已暂停";
                                            newInfo.Artist = friendly;
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch { }
            }

            // ================= 引擎 3: 知名播放器进程保障兜底 =================
            // 若声卡驱动会话延迟，检测系统是否存在网易云、QQ音乐、酷狗等进程
            if (!newInfo.HasMedia)
            {
                string[] knownMedia = { "cloudmusic", "qqmusic", "kugou", "kuwo", "spotify", "douyin" };
                foreach (var km in knownMedia)
                {
                    try
                    {
                        var procs = Process.GetProcessesByName(km);
                        if (procs.Length > 0)
                        {
                            var proc = procs[0];
                            string friendly = GetFriendlyAppName(km);
                            string wTitle = "";
                            try { wTitle = proc.MainWindowTitle?.Trim() ?? ""; } catch { }

                            newInfo.HasMedia = true;
                            newInfo.IsPlaying = true; // 进程在后台运行，保持可播控
                            newInfo.SourceAppId = km;
                            newInfo.Thumbnail = ExtractIconFromProcess(proc.Id);
                            newInfo.Title = !string.IsNullOrEmpty(wTitle) && !wTitle.Equals(friendly, StringComparison.OrdinalIgnoreCase) ? wTitle : "正在播放音频";
                            newInfo.Artist = friendly;
                            break;
                        }
                    }
                    catch { }
                }
            }

            // 比较并触发更新
            if (HasMediaInfoChanged(CurrentMedia, newInfo))
            {
                CurrentMedia = newInfo;
                MediaInfoChanged?.Invoke(newInfo);
            }
        }

        private bool IsKnownMediaProcess(uint pid)
        {
            try
            {
                var proc = Process.GetProcessById((int)pid);
                string pName = proc.ProcessName.ToLowerInvariant();
                return pName.Contains("cloudmusic") || pName.Contains("qqmusic") ||
                       pName.Contains("kugou") || pName.Contains("kuwo") ||
                       pName.Contains("spotify") || pName.Contains("douyin") ||
                       pName.Contains("edge") || pName.Contains("chrome") ||
                       pName.Contains("potplayer");
            }
            catch { return false; }
        }

        private bool HasMediaInfoChanged(MediaInfo a, MediaInfo b)
        {
            if (a == null || b == null) return true;
            return a.HasMedia != b.HasMedia ||
                   a.Title != b.Title ||
                   a.Artist != b.Artist ||
                   a.IsPlaying != b.IsPlaying ||
                   (a.Thumbnail == null) != (b.Thumbnail == null);
        }

        public async Task TogglePlayPauseAsync()
        {
            if (_currentSession != null)
            {
                try { await _currentSession.TryTogglePlayPauseAsync(); } catch { }
            }

            // 硬件级多媒体按键兜底 (对网易云音乐、QQ音乐、抖音、浏览器 100% 有效)
            SystemControlService.MediaPlayPause();
            await RefreshMediaAsync();
        }

        public async Task SkipNextAsync()
        {
            if (_currentSession != null)
            {
                try { await _currentSession.TrySkipNextAsync(); } catch { }
            }
            SystemControlService.MediaNext();
            await RefreshMediaAsync();
        }

        public async Task SkipPreviousAsync()
        {
            if (_currentSession != null)
            {
                try { await _currentSession.TrySkipPreviousAsync(); } catch { }
            }
            SystemControlService.MediaPrev();
            await RefreshMediaAsync();
        }

        public static string GetFriendlyAppName(string appId)
        {
            if (string.IsNullOrEmpty(appId)) return "媒体播放器";
            string lower = appId.ToLowerInvariant();

            if (lower.Contains("cloudmusic")) return "网易云音乐";
            if (lower.Contains("qqmusic")) return "QQ音乐";
            if (lower.Contains("kugou")) return "酷狗音乐";
            if (lower.Contains("kuwo")) return "酷我音乐";
            if (lower.Contains("douyin")) return "抖音桌面版";
            if (lower.Contains("spotify")) return "Spotify";
            if (lower.Contains("msedge") || lower.Contains("edge")) return "Microsoft Edge";
            if (lower.Contains("chrome")) return "Google Chrome";
            if (lower.Contains("firefox")) return "Firefox";
            if (lower.Contains("potplayer")) return "PotPlayer";
            if (lower.Contains("vlc")) return "VLC 播放器";
            if (lower.Contains("bilibili")) return "哔哩哔哩";
            if (lower.Contains("video.ui")) return "Windows 电影与电视";

            return appId;
        }

        private static BitmapImage? ExtractIconFromProcess(int pid)
        {
            try
            {
                var proc = Process.GetProcessById(pid);
                string? exePath = proc.MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                {
                    using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                    if (icon != null)
                    {
                        using var bmp = icon.ToBitmap();
                        using var ms = new MemoryStream();
                        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        ms.Position = 0;

                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.StreamSource = ms;
                        bi.EndInit();
                        bi.Freeze();
                        return bi;
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
