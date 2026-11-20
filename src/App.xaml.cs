using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DiffuseWindows
{
    public partial class App : Application
    {
        public static bool IsShuttingDown { get; private set; }

        private Mutex _instanceMutex;
        private AppSettings _settings;
        private readonly List<WallpaperHostWindow> _hosts = new List<WallpaperHostWindow>();
        private MainWindow _mainWindow;
        private InstallerWindow _installerWindow;
        private TrayIconHost _trayIcon;
        private MediaSessionWatcher _mediaWatcher;
        private WasapiAudioMonitor _audioMonitor;
        private readonly OnlineBpmService _onlineBpmService = new OnlineBpmService();

        private MediaStateSnapshot _lastMedia;
        private Bitmap _currentAlbumArt;
        private Bitmap _effectiveTexture;
        private bool _globallyPaused;
        private bool _showOnLaunch;
        private bool _isRebuildingHosts;
        private Timer _displayChangeDebounce;
        private string _lastMonitorSignature = "";

        private float _audioBpm;
        private float _audioBpmConf;
        private float _onlineBpm;
        private string _onlineBpmSource = "";
        private string _onlineTrackKey = "";
        private CancellationTokenSource _onlineLookupCts;
        private int _lastUiBpm;
        private string _lastUiBpmSource = "";

        public App()
        {
            InitializeComponent();
            UnhandledException += delegate(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
            {
                AppLogger.Error("WinUI UnhandledException: " + e.Message, e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, System.UnhandledExceptionEventArgs e)
            {
                AppLogger.Error("AppDomain UnhandledException", e.ExceptionObject as Exception);
            };
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            try
            {
                OnLaunchedCore(args);
            }
            catch (Exception ex)
            {
                AppLogger.Error("OnLaunched fatal", ex);
                throw;
            }
        }

        private void OnLaunchedCore(LaunchActivatedEventArgs args)
        {
            try
            {
                Win32Interop.SetProcessDpiAwarenessContext(Win32Interop.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            }
            catch { }

            string[] cmdArgs = Environment.GetCommandLineArgs();
            bool isInstallMode = false;
            bool isUninstallMode = false;
            bool isSilentMode = false;
            string sourceDir = AppContext.BaseDirectory.TrimEnd('\\');

            for (int i = 0; i < cmdArgs.Length; i++)
            {
                if (string.Equals(cmdArgs[i], "--install", StringComparison.OrdinalIgnoreCase))
                {
                    isInstallMode = true;
                }
                else if (string.Equals(cmdArgs[i], "--uninstall", StringComparison.OrdinalIgnoreCase))
                {
                    isUninstallMode = true;
                }
                else if (string.Equals(cmdArgs[i], "--silent", StringComparison.OrdinalIgnoreCase))
                {
                    isSilentMode = true;
                }
                else if (string.Equals(cmdArgs[i], "--source-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < cmdArgs.Length)
                {
                    sourceDir = cmdArgs[++i];
                }
            }

            if (isInstallMode || isUninstallMode)
            {
                if (isSilentMode)
                {
                    if (isUninstallMode)
                    {
                        string dir = InstallerEngine.GetInstalledDirectory() ?? AppContext.BaseDirectory.TrimEnd('\\');
                        InstallerEngine.UninstallAsync(dir, removeUserData: false, progress: null).GetAwaiter().GetResult();
                    }
                    else
                    {
                        string existingDir;
                        string targetDir = InstallerEngine.IsAlreadyInstalled(out existingDir)
                            ? existingDir
                            : InstallerEngine.DefaultInstallDirectory;
                        InstallerEngine.InstallAsync(sourceDir, targetDir, enableStartup: true, createDesktopShortcut: false, progress: null).GetAwaiter().GetResult();
                        InstallerEngine.LaunchInstalledApp(targetDir, showUi: false);
                    }
                    Exit();
                    return;
                }

                _installerWindow = new InstallerWindow(isUninstallMode, sourceDir);
                _installerWindow.Activate();
                return;
            }

            bool createdNew;
            _instanceMutex = new Mutex(true, "Global\\DiffuseWindowsSingleInstance", out createdNew);
            if (!createdNew)
            {
                Exit();
                return;
            }

            AppLogger.Reset();
            AppLogger.Info("DiffuseWindows (WinUI 3) starting");

            for (int i = 0; i < cmdArgs.Length; i++)
            {
                if (string.Equals(cmdArgs[i], "--preview", StringComparison.OrdinalIgnoreCase))
                {
                    var s = AppSettings.Load();
                    s.AttachToDesktop = false;
                    s.Save();
                }
                else if (string.Equals(cmdArgs[i], "--show-ui", StringComparison.OrdinalIgnoreCase))
                {
                    _showOnLaunch = true;
                }
            }

            _settings = AppSettings.Load();
            if (!_settings.StartupConfigured)
            {
                if (StartupManager.SetEnabled(true))
                {
                    _settings.StartupConfigured = true;
                }
            }
            else
            {
                StartupManager.SyncExecutablePathIfEnabled();
            }
            AppLogger.Info(string.Format("Windows startup enabled={0}", StartupManager.IsEnabled()));
            _settings.Save();

            _mainWindow = new MainWindow(
                _settings,
                OnSettingsChanged,
                ReattachAll,
                TogglePause,
                delegate { return _globallyPaused; },
                QuitApp,
                delegate { return _trayIcon != null ? _trayIcon.TryGetIconRect() : null; }
            );

            _trayIcon = new TrayIconHost(
                delegate { _mainWindow.ToggleFlyout(); },
                delegate { ReattachAll(); },
                delegate { TogglePause(); },
                delegate { return _globallyPaused; },
                delegate { QuitApp(); },
                ScheduleDisplayRebuild
            );

            RebuildHosts("startup");

            _mediaWatcher = new MediaSessionWatcher(DispatcherQueue.GetForCurrentThread(), OnMediaChanged);
            _mediaWatcher.Start();

            _audioMonitor = new WasapiAudioMonitor(OnAudioBands, OnBpmDetected);
            _audioMonitor.SetEnabled(_settings.EnableLiveBeats && _settings.BpmSource != "online");

            RecomputeEffectiveTexture();

            // Float cleanly into the system tray on startup (or pop open briefly if --show-ui passed).
            if (_showOnLaunch)
            {
                _mainWindow.ShowFlyout();
            }
        }

        private void ScheduleDisplayRebuild()
        {
            if (IsShuttingDown) return;
            if (_displayChangeDebounce == null)
            {
                _displayChangeDebounce = new Timer(delegate
                {
                    if (IsShuttingDown || _mainWindow == null) return;
                    _mainWindow.DispatcherQueue.TryEnqueue(delegate
                    {
                        if (!IsShuttingDown)
                        {
                            string sig = BuildMonitorSignature();
                            if (sig != _lastMonitorSignature)
                            {
                                RebuildHosts("display-change");
                            }
                        }
                    });
                }, null, 600, Timeout.Infinite);
            }
            else
            {
                _displayChangeDebounce.Change(600, Timeout.Infinite);
            }
        }

        private string BuildMonitorSignature()
        {
            List<DisplayMonitorInfo> monitors = (_settings.AttachToDesktop && _settings.SpanAllMonitors)
                ? DesktopWallpaperHost.GetMonitors()
                : new List<DisplayMonitorInfo> { DesktopWallpaperHost.GetPrimaryMonitor() };
            var parts = new List<string>(monitors.Count);
            for (int i = 0; i < monitors.Count; i++)
            {
                parts.Add(monitors[i].DeviceName + ":" + monitors[i].Bounds.ToString());
            }
            return string.Join("|", parts);
        }

        private void RebuildHosts(string reason)
        {
            if (_isRebuildingHosts || IsShuttingDown) return;
            _isRebuildingHosts = true;
            try
            {
                for (int i = 0; i < _hosts.Count; i++)
                {
                    try { _hosts[i].Dispose(); } catch { }
                }
                _hosts.Clear();

                List<DisplayMonitorInfo> monitors = (_settings.AttachToDesktop && _settings.SpanAllMonitors)
                    ? DesktopWallpaperHost.GetMonitors()
                    : new List<DisplayMonitorInfo> { DesktopWallpaperHost.GetPrimaryMonitor() };

                _lastMonitorSignature = BuildMonitorSignature();
                AppLogger.Info(string.Format("RebuildHosts({0}) monitors={1} sig={2}", reason, monitors.Count, _lastMonitorSignature));

                DispatcherQueue uiQueue = _mainWindow != null ? _mainWindow.DispatcherQueue : DispatcherQueue.GetForCurrentThread();
                for (int i = 0; i < monitors.Count; i++)
                {
                    try
                    {
                        var host = new WallpaperHostWindow(
                            monitors[i],
                            _settings,
                            uiQueue,
                            delegate
                            {
                                if (_hosts.Count > 0 && _mainWindow != null)
                                {
                                    _mainWindow.UpdateDesktopMode(_hosts[0].AttachmentModeDescription);
                                }
                            }
                        );
                        host.SetGloballyPaused(_globallyPaused);
                        _hosts.Add(host);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("Failed creating WallpaperHostWindow", ex);
                    }
                }

                if (_hosts.Count > 0)
                {
                    _mainWindow.UpdateDesktopMode(_hosts[0].AttachmentModeDescription);
                }

                if (_effectiveTexture != null)
                {
                    for (int i = 0; i < _hosts.Count; i++)
                    {
                        _hosts[i].QueueAlbumBitmap(_effectiveTexture);
                        _hosts[i].QueuePlaybackState(_lastMedia != null && _lastMedia.IsPlaying);
                    }
                }
            }
            finally
            {
                _isRebuildingHosts = false;
            }
        }

        private void ReattachAll()
        {
            for (int i = 0; i < _hosts.Count; i++)
            {
                _hosts[i].AttachOrConfigureWindow();
            }
            if (_hosts.Count > 0)
            {
                _mainWindow.UpdateDesktopMode(_hosts[0].AttachmentModeDescription);
            }
        }

        private void TogglePause()
        {
            _globallyPaused = !_globallyPaused;
            for (int i = 0; i < _hosts.Count; i++)
            {
                _hosts[i].SetGloballyPaused(_globallyPaused);
            }
            _mainWindow.UpdatePauseIcon();
        }

        private void OnSettingsChanged(AppSettings updated)
        {
            bool rebuildNeeded =
                _settings.SpanAllMonitors != updated.SpanAllMonitors ||
                _settings.AttachToDesktop != updated.AttachToDesktop;

            bool paletteChanged =
                _settings.DefaultPaletteId != updated.DefaultPaletteId ||
                _settings.CustomPalettePath != updated.CustomPalettePath ||
                _settings.PreserveAfterPause != updated.PreserveAfterPause ||
                _settings.BrightnessOffset != updated.BrightnessOffset;

            string prevSource = _settings.BpmSource;
            bool wasLiveBeats = _settings.EnableLiveBeats;
            _settings = updated;
            bool useWasapi = _settings.EnableLiveBeats && _settings.BpmSource != "online";
            _audioMonitor.SetEnabled(useWasapi);
            if (!useWasapi)
            {
                _audioBpm = 0f;
                _audioBpmConf = 0f;
                for (int i = 0; i < _hosts.Count; i++)
                {
                    _hosts[i].QueueAudioBands(new double[4]);
                }
            }
            if (!_settings.EnableLiveBeats)
            {
                CancelOnlineBpmLookup(clearValue: true);
            }
            else if (!wasLiveBeats || prevSource != _settings.BpmSource)
            {
                if (_settings.BpmSource == "audio")
                {
                    CancelOnlineBpmLookup(clearValue: true);
                }
                else
                {
                    TriggerOnlineBpmLookup(_lastMedia);
                }
            }
            ResolveAndPublishBpm();

            if (rebuildNeeded)
            {
                RebuildHosts("settings");
            }
            else
            {
                for (int i = 0; i < _hosts.Count; i++)
                {
                    _hosts[i].UpdateSettings(_settings);
                }
                if (_hosts.Count > 0)
                {
                    _mainWindow.UpdateDesktopMode(_hosts[0].AttachmentModeDescription);
                }
            }

            if (paletteChanged)
            {
                RecomputeEffectiveTexture();
            }
        }

        private void OnMediaChanged(MediaStateSnapshot snap)
        {
            if (IsShuttingDown) return;

            if (snap.AlbumArt != null)
            {
                if (_currentAlbumArt != null) _currentAlbumArt.Dispose();
                _currentAlbumArt = new Bitmap(snap.AlbumArt);
                snap.AlbumArt.Dispose();
                snap.AlbumArt = null;
            }
            else if (!snap.HasSession && !_settings.PreserveAfterPause)
            {
                if (_currentAlbumArt != null)
                {
                    _currentAlbumArt.Dispose();
                    _currentAlbumArt = null;
                }
            }

            _lastMedia = snap;

            for (int i = 0; i < _hosts.Count; i++)
            {
                _hosts[i].QueuePlaybackState(snap.IsPlaying);
            }

            if (_settings.EnableLiveBeats && _settings.BpmSource != "audio")
            {
                TriggerOnlineBpmLookup(snap);
            }

            RecomputeEffectiveTexture();
        }

        private void RecomputeEffectiveTexture()
        {
            bool playing = _lastMedia != null && _lastMedia.IsPlaying;
            bool useAlbum = _currentAlbumArt != null && (playing || _settings.PreserveAfterPause);

            Bitmap next = null;
            if (useAlbum)
            {
                next = new Bitmap(_currentAlbumArt);
            }
            else
            {
                string palettePath = AssetPaths.ResolvePaletteFile(_settings);
                if (!string.IsNullOrEmpty(palettePath) && File.Exists(palettePath))
                {
                    using (var raw = new Bitmap(palettePath))
                    {
                        next = new Bitmap(raw);
                    }
                }
                else
                {
                    next = CreateDefaultFallbackBitmap();
                }
            }

            if (_effectiveTexture != null) _effectiveTexture.Dispose();
            _effectiveTexture = next;

            for (int i = 0; i < _hosts.Count; i++)
            {
                _hosts[i].QueueAlbumBitmap(_effectiveTexture);
            }

            _mainWindow.UpdateMediaStatus(_lastMedia, _effectiveTexture);
        }

        private void OnAudioBands(double[] bands)
        {
            if (IsShuttingDown || !_settings.EnableLiveBeats || _settings.BpmSource == "online") return;
            for (int i = 0; i < _hosts.Count; i++)
            {
                _hosts[i].QueueAudioBands(bands);
            }
        }

        private void OnBpmDetected(float bpm, float confidence)
        {
            if (IsShuttingDown || !_settings.EnableLiveBeats) return;
            _audioBpm = bpm;
            _audioBpmConf = confidence;
            ResolveAndPublishBpm();
        }

        private void CancelOnlineBpmLookup(bool clearValue)
        {
            if (_onlineLookupCts != null)
            {
                try { _onlineLookupCts.Cancel(); _onlineLookupCts.Dispose(); } catch { }
                _onlineLookupCts = null;
            }
            if (clearValue)
            {
                _onlineTrackKey = "";
                _onlineBpm = 0f;
                _onlineBpmSource = "";
            }
        }

        private async void TriggerOnlineBpmLookup(MediaStateSnapshot snap)
        {
            if (IsShuttingDown || snap == null || !snap.HasSession || !snap.IsPlaying || string.IsNullOrWhiteSpace(snap.Title))
            {
                CancelOnlineBpmLookup(clearValue: true);
                ResolveAndPublishBpm();
                return;
            }

            string trackKey = (snap.Artist ?? "") + "|" + snap.Title + "|" + (snap.SourceApp ?? "");
            if (trackKey == _onlineTrackKey && _onlineBpm > 20f)
            {
                ResolveAndPublishBpm();
                return;
            }

            CancelOnlineBpmLookup(clearValue: true);
            _onlineTrackKey = trackKey;
            var cts = new CancellationTokenSource();
            _onlineLookupCts = cts;

            try
            {
                var res = await _onlineBpmService.LookupAsync(snap.Title, snap.Artist, snap.SourceApp, cts.Token);
                if (IsShuttingDown || cts.IsCancellationRequested || _onlineTrackKey != trackKey) return;

                if (res != null && res.Bpm > 20f)
                {
                    _onlineBpm = res.Bpm;
                    _onlineBpmSource = res.Source;
                }
                else
                {
                    _onlineBpm = 0f;
                    _onlineBpmSource = "";
                }
                ResolveAndPublishBpm();
            }
            catch { }
        }

        private void ResolveAndPublishBpm()
        {
            if (IsShuttingDown) return;

            float bpmToApply = 0f;
            float confToApply = 0f;
            string sourceLabel = "";

            if (_settings.EnableLiveBeats)
            {
                string mode = _settings.BpmSource ?? "auto";
                if (mode == "audio")
                {
                    bpmToApply = _audioBpm;
                    confToApply = _audioBpmConf;
                    sourceLabel = "Audio";
                }
                else if (mode == "online")
                {
                    if (_onlineBpm > 20f)
                    {
                        bpmToApply = _onlineBpm;
                        confToApply = 0.90f;
                        sourceLabel = _onlineBpmSource;
                    }
                }
                else
                {
                    if (_audioBpmConf >= 0.40f && _audioBpm > 20f)
                    {
                        bpmToApply = _audioBpm;
                        confToApply = _audioBpmConf;
                        sourceLabel = "Audio";
                    }
                    else if (_onlineBpm > 20f)
                    {
                        bpmToApply = _onlineBpm;
                        confToApply = 0.85f;
                        sourceLabel = _onlineBpmSource;
                    }
                    else
                    {
                        bpmToApply = _audioBpm;
                        confToApply = _audioBpmConf;
                        sourceLabel = "Audio";
                    }
                }
            }

            for (int i = 0; i < _hosts.Count; i++)
            {
                _hosts[i].QueueBpm(bpmToApply, confToApply);
            }

            int rounded = (bpmToApply > 20f && confToApply >= 0.25f) ? (int)Math.Round(bpmToApply) : 0;
            string shownSource = rounded > 0 ? sourceLabel : "";
            if (rounded != _lastUiBpm || shownSource != _lastUiBpmSource)
            {
                _lastUiBpm = rounded;
                _lastUiBpmSource = shownSource;
                _mainWindow?.UpdateLiveBeatsBpm(rounded, shownSource);
            }
        }

        private static Bitmap CreateDefaultFallbackBitmap()
        {
            var bmp = new Bitmap(64, 64);
            using (Graphics g = Graphics.FromImage(bmp))
            using (var brush = new LinearGradientBrush(
                new Rectangle(0, 0, 64, 64),
                Color.FromArgb(40, 65, 110),
                Color.FromArgb(145, 75, 120),
                45f))
            {
                g.FillRectangle(brush, 0, 0, 64, 64);
            }
            return bmp;
        }

        private void QuitApp()
        {
            if (IsShuttingDown) return;
            IsShuttingDown = true;

            try { _displayChangeDebounce?.Dispose(); } catch { }
            try { CancelOnlineBpmLookup(clearValue: false); _onlineBpmService.Dispose(); } catch { }
            try { _mediaWatcher?.Dispose(); } catch { }
            try { _audioMonitor?.Dispose(); } catch { }
            try { _trayIcon?.Dispose(); } catch { }

            for (int i = 0; i < _hosts.Count; i++)
            {
                try { _hosts[i].Dispose(); } catch { }
            }
            _hosts.Clear();

            if (_currentAlbumArt != null) _currentAlbumArt.Dispose();
            if (_effectiveTexture != null) _effectiveTexture.Dispose();
            if (_instanceMutex != null)
            {
                try { _instanceMutex.ReleaseMutex(); } catch { }
                _instanceMutex.Dispose();
            }

            try { _mainWindow?.Close(); } catch { }
            Exit();
        }
    }
}
