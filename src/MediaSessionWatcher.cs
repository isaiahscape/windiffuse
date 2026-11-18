using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DiffuseWindows
{
    public sealed class MediaStateSnapshot
    {
        public bool HasSession;
        public bool IsPlaying;
        public string Title = "";
        public string Artist = "";
        public string SourceApp = "";
        public Bitmap AlbumArt;
    }

    internal sealed class MediaSessionWatcher : IDisposable
    {
        private readonly DispatcherQueue _dispatcher;
        private readonly Action<MediaStateSnapshot> _onChanged;
        private readonly Timer _pollTimer;

        private GlobalSystemMediaTransportControlsSessionManager _manager;
        private GlobalSystemMediaTransportControlsSession _currentSession;
        private string _lastTrackKey = "";
        private int _refreshInFlight;
        private bool _disposed;

        public MediaSessionWatcher(DispatcherQueue dispatcher, Action<MediaStateSnapshot> onChanged)
        {
            _dispatcher = dispatcher;
            _onChanged = onChanged;
            _pollTimer = new Timer(delegate { TriggerRefresh(); }, null, Timeout.Infinite, Timeout.Infinite);
        }

        public async void Start()
        {
            try
            {
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask();
                if (_disposed || _manager == null) return;

                _manager.CurrentSessionChanged += OnCurrentSessionChanged;
                HookSession(_manager.GetCurrentSession());
                _pollTimer.Change(1200, 1200);
                TriggerRefresh();
            }
            catch (Exception ex)
            {
                AppLogger.Error("SMTC Start failed", ex);
            }
        }

        private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        {
            if (_disposed) return;
            HookSession(sender.GetCurrentSession());
            TriggerRefresh();
        }

        private void HookSession(GlobalSystemMediaTransportControlsSession session)
        {
            if (_currentSession == session) return;

            if (_currentSession != null)
            {
                try
                {
                    _currentSession.MediaPropertiesChanged -= OnSessionChanged;
                    _currentSession.PlaybackInfoChanged -= OnSessionChanged;
                }
                catch { }
            }

            _currentSession = session;
            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged += OnSessionChanged;
                _currentSession.PlaybackInfoChanged += OnSessionChanged;
            }
        }

        private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args)
        {
            if (!_disposed) TriggerRefresh();
        }

        private void TriggerRefresh()
        {
            if (_disposed || _manager == null) return;
            if (Interlocked.Exchange(ref _refreshInFlight, 1) == 1) return;

            Task.Run(async delegate
            {
                try
                {
                    GlobalSystemMediaTransportControlsSession session = _manager.GetCurrentSession();
                    HookSession(session);

                    if (session == null)
                    {
                        EmitIfChanged(new MediaStateSnapshot { HasSession = false, IsPlaying = false }, "none");
                        return;
                    }

                    var playbackInfo = session.GetPlaybackInfo();
                    bool playing = playbackInfo != null &&
                                   playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                    var props = await session.TryGetMediaPropertiesAsync().AsTask();
                    string title = props != null && props.Title != null ? props.Title : "";
                    string artist = props != null && props.Artist != null ? props.Artist : "";
                    string sourceApp = session.SourceAppUserModelId ?? "";
                    bool hasThumb = props != null && props.Thumbnail != null;

                    string key = sourceApp + "|" + playing + "|" + title + "|" + artist + "|" + hasThumb;
                    if (key == _lastTrackKey)
                    {
                        return;
                    }

                    Bitmap art = null;
                    if (hasThumb)
                    {
                        art = await LoadThumbnailAsync(props.Thumbnail);
                    }

                    var snap = new MediaStateSnapshot
                    {
                        HasSession = true,
                        IsPlaying = playing,
                        Title = title,
                        Artist = artist,
                        SourceApp = sourceApp,
                        AlbumArt = art
                    };

                    if (hasThumb && art == null)
                    {
                        key = sourceApp + "|" + playing + "|" + title + "|" + artist + "|pending";
                    }

                    EmitIfChanged(snap, key);
                }
                catch
                {
                }
                finally
                {
                    Interlocked.Exchange(ref _refreshInFlight, 0);
                }
            });
        }

        private void EmitIfChanged(MediaStateSnapshot snap, string newKey)
        {
            if (_disposed || newKey == _lastTrackKey)
            {
                if (snap.AlbumArt != null) snap.AlbumArt.Dispose();
                return;
            }
            _lastTrackKey = newKey;

            if (_dispatcher != null)
            {
                _dispatcher.TryEnqueue(delegate
                {
                    if (!_disposed)
                    {
                        _onChanged(snap);
                    }
                    else if (snap.AlbumArt != null)
                    {
                        snap.AlbumArt.Dispose();
                    }
                });
            }
            else if (!_disposed)
            {
                _onChanged(snap);
            }
        }

        private static async Task<Bitmap> LoadThumbnailAsync(IRandomAccessStreamReference thumbRef)
        {
            try
            {
                using (IRandomAccessStreamWithContentType winrtStream = await thumbRef.OpenReadAsync().AsTask())
                using (Stream netStream = winrtStream.AsStreamForRead())
                using (var mem = new MemoryStream())
                {
                    await netStream.CopyToAsync(mem);
                    mem.Position = 0;
                    using (var raw = new Bitmap(mem))
                    {
                        return new Bitmap(raw);
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _pollTimer.Dispose();
            if (_manager != null)
            {
                try { _manager.CurrentSessionChanged -= OnCurrentSessionChanged; } catch { }
            }
            if (_currentSession != null)
            {
                try
                {
                    _currentSession.MediaPropertiesChanged -= OnSessionChanged;
                    _currentSession.PlaybackInfoChanged -= OnSessionChanged;
                }
                catch { }
            }
        }
    }
}
