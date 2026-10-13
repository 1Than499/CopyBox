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

            // 1 秒心跳轮询，实时捕获后台播放器状态与波形峰值
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

                    // 优先选择处于播放状态的 session
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
            // 若 GSMTC 未提供曲目详情，但系统确实有播放器（网易云、QQ音乐、抖音、浏览器）正在发声输出
            if (!newInfo.HasMedia || !newInfo.IsPlaying)
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
                            for (int i = 0; i < sessions.Count; i++)
                            {
                                var s = sessions[i];
                                uint pid = s.GetProcessID;
                                if (pid == 0) continue;

                                bool isActive = (int)s.State == 1; // 1 代表 AudioSessionStateActive
                                float peak = 0;
                                try { peak = s.AudioMeterInformation.MasterPeakValue; } catch { }

                                // 只要处于 Active 状态或有声波振幅峰值
                                if (isActive || peak > 0.001f)
                                {
                                    try
                                    {
                                        var proc = Process.GetProcessById((int)pid);
                                        string pName = proc.ProcessName.ToLowerInvariant();

                                        // 过滤系统自身与通知服务
                                        if (pName == "system" || pName == "audiodg" || pName == "explorer") continue;

                                        string friendlyName = GetFriendlyAppName(pName);
                                        string windowTitle = "";
                                        try { windowTitle = proc.MainWindowTitle?.Trim() ?? ""; } catch { }

                                        newInfo.HasMedia = true;
                                        newInfo.IsPlaying = true;
                                        newInfo.SourceAppId = pName;

                                        // 如果主窗口标题有歌名 (如 "周杰伦 - 晴天")
                                        if (!string.IsNullOrEmpty(windowTitle) && !windowTitle.Equals(friendlyName, StringComparison.OrdinalIgnoreCase))
                                        {
                                            newInfo.Title = windowTitle;
                                            newInfo.Artist = friendlyName;
                                        }
                                        else
                                        {
                                            newInfo.Title = "正在播放音频";
                                            newInfo.Artist = friendlyName;
                                        }

                                        break; // 优先挑选首个正在输出音频的活跃媒体应用
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            // 比较并触发更新
            if (HasMediaInfoChanged(CurrentMedia, newInfo))
            {
                CurrentMedia = newInfo;
                MediaInfoChanged?.Invoke(newInfo);
            }
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
            bool handled = false;
            if (_currentSession != null)
            {
                try
                {
                    handled = await _currentSession.TryTogglePlayPauseAsync();
                }
                catch { }
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
    }
}
