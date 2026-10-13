using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
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
    }

    /// <summary>
    /// 利用 Windows 10/11 原生 GSMTC (全局系统媒体传输控制) 监听与控制音源
    /// </summary>
    public class SystemMediaService
    {
        private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
        private GlobalSystemMediaTransportControlsSession? _currentSession;

        public event Action<MediaInfo>? MediaInfoChanged;

        public MediaInfo CurrentMedia { get; private set; } = new();

        public async Task InitializeAsync()
        {
            try
            {
                _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                if (_sessionManager != null)
                {
                    _sessionManager.CurrentSessionChanged += OnCurrentSessionChanged;
                    HookCurrentSession(_sessionManager.GetCurrentSession());
                }
            }
            catch
            {
                // 系统不支持或无权限时静默降级
            }
        }

        private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        {
            HookCurrentSession(sender.GetCurrentSession());
        }

        private void HookCurrentSession(GlobalSystemMediaTransportControlsSession? session)
        {
            if (_currentSession != null)
            {
                try
                {
                    _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                    _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                }
                catch { }
            }

            _currentSession = session;

            if (_currentSession != null)
            {
                _currentSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
                _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
            }

            _ = RefreshMediaInfoAsync();
        }

        private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            _ = RefreshMediaInfoAsync();
        }

        private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        {
            _ = RefreshMediaInfoAsync();
        }

        public async Task RefreshMediaInfoAsync()
        {
            var info = new MediaInfo();

            if (_currentSession != null)
            {
                try
                {
                    var playback = _currentSession.GetPlaybackInfo();
                    info.IsPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                    var props = await _currentSession.TryGetMediaPropertiesAsync();
                    if (props != null && (!string.IsNullOrEmpty(props.Title) || !string.IsNullOrEmpty(props.Artist)))
                    {
                        info.HasMedia = true;
                        info.Title = string.IsNullOrWhiteSpace(props.Title) ? "未知曲目" : props.Title;
                        info.Artist = string.IsNullOrWhiteSpace(props.Artist) ? "未知艺术家" : props.Artist;
                        info.Album = props.AlbumTitle ?? "";

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
                                info.Thumbnail = bmp;
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }

            CurrentMedia = info;
            MediaInfoChanged?.Invoke(info);
        }

        public async Task TogglePlayPauseAsync()
        {
            if (_currentSession != null)
            {
                try
                {
                    await _currentSession.TryTogglePlayPauseAsync();
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
                }
                catch { }
            }
        }
    }
}
