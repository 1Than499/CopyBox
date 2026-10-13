using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace ClipVault.Services
{
    public class MediaInfo
    {
        public bool HasMedia { get; set; }
        public string Title { get; set; } = "未检测到正在播放的音源";
        public string Artist { get; set; } = "播放器待机中";
        public string Album { get; set; } = "";
        public bool IsPlaying { get; set; }
        public BitmapImage? Thumbnail { get; set; }
        public string SourceAppId { get; set; } = "";
    }

    /// <summary>
    /// 利用 Windows 10/11 原生 GSMTC (全局系统媒体传输控制) 全量监听与控制系统音源
    /// </summary>
    public class SystemMediaService
    {
        private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
        private GlobalSystemMediaTransportControlsSession? _currentSession;
        private DispatcherTimer? _pollTimer;

        public event Action<MediaInfo>? MediaInfoChanged;

        public MediaInfo CurrentMedia { get; private set; } = new();

        public async Task InitializeAsync()
        {
            try
            {
                _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                if (_sessionManager != null)
                {
                    _sessionManager.CurrentSessionChanged += (s, e) => _ = RefreshMediaAsync();
                    _sessionManager.SessionsChanged += (s, e) => _ = RefreshMediaAsync();
                }
            }
            catch { }

            // 启动定时轮询心跳 (每 1.2 秒检测一次，确保后台播放器/切歌毫秒级响应)
            _pollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1200)
            };
            _pollTimer.Tick += async (s, e) => await RefreshMediaAsync();
            _pollTimer.Start();

            await RefreshMediaAsync();
        }

        public async Task RefreshMediaAsync()
        {
            if (_sessionManager == null)
            {
                try
                {
                    _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                }
                catch { return; }
            }

            if (_sessionManager == null) return;

            GlobalSystemMediaTransportControlsSession? bestSession = null;

            try
            {
                var allSessions = _sessionManager.GetSessions();
                if (allSessions != null && allSessions.Count > 0)
                {
                    // 1. 优先挑选正在播放 (Playing) 的 session
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

                    // 2. 其次选择系统的 CurrentSession
                    if (bestSession == null)
                    {
                        try
                        {
                            bestSession = _sessionManager.GetCurrentSession();
                        }
                        catch { }
                    }

                    // 3. 再次回退到任意具有媒体信息的 session
                    if (bestSession == null)
                    {
                        bestSession = allSessions.FirstOrDefault();
                    }
                }
            }
            catch { }

            _currentSession = bestSession;

            var newInfo = new MediaInfo();

            if (_currentSession != null)
            {
                try
                {
                    newInfo.SourceAppId = _currentSession.SourceAppUserModelId ?? "";
                    var playback = _currentSession.GetPlaybackInfo();
                    newInfo.IsPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                    var props = await _currentSession.TryGetMediaPropertiesAsync();
                    if (props != null)
                    {
                        string title = props.Title?.Trim() ?? "";
                        string artist = props.Artist?.Trim() ?? "";
                        string album = props.AlbumTitle?.Trim() ?? "";

                        // 如果抓取到了曲目名称或艺术家
                        if (!string.IsNullOrEmpty(title) || !string.IsNullOrEmpty(artist))
                        {
                            newInfo.HasMedia = true;
                            newInfo.Title = string.IsNullOrEmpty(title) ? "正在播放媒体" : title;
                            newInfo.Artist = string.IsNullOrEmpty(artist) ? GetFriendlyAppName(newInfo.SourceAppId) : artist;
                            newInfo.Album = album;

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

                    // 即使没有读取到具体元数据，只要处于播放状态，也提示播放器在线
                    if (!newInfo.HasMedia && newInfo.IsPlaying)
                    {
                        newInfo.HasMedia = true;
                        newInfo.Title = "音频播放中";
                        newInfo.Artist = GetFriendlyAppName(newInfo.SourceAppId);
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
            if (a.HasMedia != b.HasMedia) return true;
            if (a.IsPlaying != b.IsPlaying) return true;
            if (a.Title != b.Title) return true;
            if (a.Artist != b.Artist) return true;
            if ((a.Thumbnail == null) != (b.Thumbnail == null)) return true;
            return false;
        }

        private string GetFriendlyAppName(string appId)
        {
            if (string.IsNullOrEmpty(appId)) return "未知播放源";
            string lower = appId.ToLowerInvariant();
            if (lower.Contains("cloudmusic")) return "网易云音乐";
            if (lower.Contains("qqmusic")) return "QQ音乐";
            if (lower.Contains("kugou")) return "酷狗音乐";
            if (lower.Contains("spotify")) return "Spotify";
            if (lower.Contains("chrome")) return "Google Chrome 音频";
            if (lower.Contains("edge") || lower.Contains("msedge")) return "Microsoft Edge 音频";
            if (lower.Contains("firefox")) return "Firefox 音频";
            if (lower.Contains("foobar2000")) return "Foobar2000";
            if (lower.Contains("potplayer")) return "PotPlayer";
            return Path.GetFileNameWithoutExtension(appId);
        }

        public async Task TogglePlayPauseAsync()
        {
            if (_currentSession != null)
            {
                try
                {
                    await _currentSession.TryTogglePlayPauseAsync();
                    await RefreshMediaAsync();
                }
                catch { }
            }
        }

        public async Task SkipPreviousAsync()
        {
            if (_currentSession != null)
            {
                try
                {
                    await _currentSession.TrySkipPreviousAsync();
                    await RefreshMediaAsync();
                }
                catch { }
            }
        }

        public async Task SkipNextAsync()
        {
            if (_currentSession != null)
            {
                try
                {
                    await _currentSession.TrySkipNextAsync();
                    await RefreshMediaAsync();
                }
                catch { }
            }
        }
    }
}
