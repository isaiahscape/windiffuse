using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;

namespace DiffuseWindows
{
    internal sealed class WallpaperHostWindow : IDisposable
    {
        private static readonly object ClassLock = new object();
        private static string _registeredClassName;
        private static Win32Interop.WndProcDelegate _staticWndProc;

        private readonly DisplayMonitorInfo _monitor;
        private readonly DispatcherQueue _uiDispatcher;
        private readonly Action _onAttached;
        private AppSettings _settings;
        private IntPtr _hwnd;
        private IntPtr _hostParent;
        private bool _attachedToDesktop;

        private Bitmap _pendingAlbumBitmap;
        private bool _hasPendingBitmap;
        private bool _pendingPlaying;
        private double[] _pendingBands;
        private bool _hasPendingBands;
        private bool _pendingSettingsDirty = true;
        private volatile bool _globallyPaused;
        private readonly object _stateLock = new object();

        private Thread _renderThread;
        private volatile bool _stopRenderThread;
        private readonly int _currentProcessId;
        private bool _disposed;

        public string AttachmentModeDescription { get; private set; }

        public WallpaperHostWindow(DisplayMonitorInfo monitor, AppSettings settings, DispatcherQueue uiDispatcher, Action onAttached)
        {
            _monitor = monitor;
            _settings = settings.Clone();
            _uiDispatcher = uiDispatcher;
            _onAttached = onAttached;
            _currentProcessId = Process.GetCurrentProcess().Id;
            AttachmentModeDescription = "Initializing...";

            EnsureWindowClassRegistered();
            CreateHostHwnd();
            AttachOrConfigureWindow();
            StartRenderThread();
        }

        private static void EnsureWindowClassRegistered()
        {
            lock (ClassLock)
            {
                if (_registeredClassName != null) return;

                _staticWndProc = delegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
                {
                    if (msg == Win32Interop.WM_ERASEBKGND)
                    {
                        return new IntPtr(1);
                    }
                    if (msg == Win32Interop.WM_PAINT)
                    {
                        Win32Interop.ValidateRect(hWnd, IntPtr.Zero);
                        return IntPtr.Zero;
                    }
                    return Win32Interop.DefWindowProc(hWnd, msg, wParam, lParam);
                };

                string clsName = "DiffuseWallpaperHost_" + Guid.NewGuid().ToString("N");
                var wc = new Win32Interop.WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf(typeof(Win32Interop.WNDCLASSEX)),
                    style = 0,
                    lpfnWndProc = _staticWndProc,
                    hInstance = Win32Interop.GetModuleHandle(null),
                    lpszClassName = clsName
                };
                Win32Interop.RegisterClassEx(ref wc);
                _registeredClassName = clsName;
            }
        }

        private void CreateHostHwnd()
        {
            Rectangle bounds = _monitor.Bounds;
            uint style = Win32Interop.WS_POPUP | Win32Interop.WS_VISIBLE | Win32Interop.WS_CLIPSIBLINGS | Win32Interop.WS_CLIPCHILDREN;
            int exStyle = Win32Interop.WS_EX_TOOLWINDOW | Win32Interop.WS_EX_NOACTIVATE;

            _hwnd = Win32Interop.CreateWindowEx(
                exStyle,
                _registeredClassName,
                "Diffuse Wallpaper",
                style,
                bounds.Left,
                bounds.Top,
                Math.Max(1, bounds.Width),
                Math.Max(1, bounds.Height),
                IntPtr.Zero,
                IntPtr.Zero,
                Win32Interop.GetModuleHandle(null),
                IntPtr.Zero
            );

            if (_hwnd == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateWindowEx failed for WallpaperHostWindow.");
            }

            // Ensure DWM creates the window surface before SetParent moves it into Progman/WorkerW.
            Win32Interop.ShowWindow(_hwnd, 8);
        }

        public void AttachOrConfigureWindow()
        {
            if (_hwnd == IntPtr.Zero) return;

            byte opacity = (byte)_settings.WallpaperOpacity;
            if (_settings.AttachToDesktop)
            {
                DesktopWallpaperHost.AttachResult res = DesktopWallpaperHost.Attach(_hwnd, _monitor.Bounds, opacity);
                _attachedToDesktop = res.Attached;
                _hostParent = res.HostParent;
                AttachmentModeDescription = res.ModeDescription;
            }
            else
            {
                DesktopWallpaperHost.Detach(_hwnd, _hostParent);
                _attachedToDesktop = false;
                _hostParent = IntPtr.Zero;

                long style = Win32Interop.GetWindowLongPtr(_hwnd, Win32Interop.GWL_STYLE).ToInt64();
                style = (style & ~((long)Win32Interop.WS_CHILD)) | Win32Interop.WS_POPUP | Win32Interop.WS_VISIBLE;
                Win32Interop.SetWindowLongPtr(_hwnd, Win32Interop.GWL_STYLE, new IntPtr(style));

                long exStyle = Win32Interop.GetWindowLongPtr(_hwnd, Win32Interop.GWL_EXSTYLE).ToInt64();
                exStyle &= ~((long)Win32Interop.WS_EX_TRANSPARENT);
                Win32Interop.SetWindowLongPtr(_hwnd, Win32Interop.GWL_EXSTYLE, new IntPtr(exStyle));

                Rectangle work = _monitor.WorkingArea;
                int w = Math.Min(960, Math.Max(320, work.Width - 120));
                int h = Math.Min(600, Math.Max(240, work.Height - 100));
                int x = work.Left + (work.Width - w) / 2;
                int y = work.Top + (work.Height - h) / 2;

                Win32Interop.SetWindowPos(
                    _hwnd,
                    Win32Interop.HWND_BOTTOM,
                    x, y, w, h,
                    Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_FRAMECHANGED | Win32Interop.SWP_SHOWWINDOW
                );
                DesktopWallpaperHost.ApplyOpacity(_hwnd, opacity, IntPtr.Zero);
                AttachmentModeDescription = "Preview Window";
            }
        }

        public void SetGloballyPaused(bool paused)
        {
            _globallyPaused = paused;
        }

        public void UpdateSettings(AppSettings newSettings)
        {
            bool attachChanged;
            int oldOpacity;
            lock (_stateLock)
            {
                attachChanged = _settings.AttachToDesktop != newSettings.AttachToDesktop;
                oldOpacity = _settings.WallpaperOpacity;
                _settings = newSettings.Clone();
                _pendingSettingsDirty = true;
            }

            if (_hwnd != IntPtr.Zero)
            {
                if (attachChanged)
                {
                    AttachOrConfigureWindow();
                }
                else if (oldOpacity != newSettings.WallpaperOpacity)
                {
                    DesktopWallpaperHost.ApplyOpacity(_hwnd, (byte)newSettings.WallpaperOpacity, _hostParent);
                }
            }
        }

        public void QueueAlbumBitmap(Bitmap bitmap)
        {
            if (bitmap == null) return;
            Bitmap copy = new Bitmap(bitmap);
            lock (_stateLock)
            {
                if (_pendingAlbumBitmap != null) _pendingAlbumBitmap.Dispose();
                _pendingAlbumBitmap = copy;
                _hasPendingBitmap = true;
            }
        }

        public void QueuePlaybackState(bool playing)
        {
            lock (_stateLock)
            {
                _pendingPlaying = playing;
            }
        }

        public void QueueAudioBands(double[] bands)
        {
            if (bands == null) return;
            double[] copy = (double[])bands.Clone();
            lock (_stateLock)
            {
                _pendingBands = copy;
                _hasPendingBands = true;
            }
        }

        private void StartRenderThread()
        {
            if (_renderThread != null || _hwnd == IntPtr.Zero) return;
            IntPtr hwnd = _hwnd;
            _stopRenderThread = false;
            _renderThread = new Thread(delegate() { RenderLoop(hwnd); });
            _renderThread.IsBackground = true;
            _renderThread.Name = "DiffuseDX-" + _monitor.DeviceName;
            _renderThread.Start();
        }

        private void StopRenderThread()
        {
            _stopRenderThread = true;
            if (_renderThread != null && _renderThread.IsAlive)
            {
                _renderThread.Join(1500);
            }
            _renderThread = null;
        }

        private void RenderLoop(IntPtr hwnd)
        {
            D3D11DiffuseRenderer renderer = null;
            bool timerPeriodSet = false;

            try
            {
                Win32Interop.timeBeginPeriod(1);
                timerPeriodSet = true;

                Win32Interop.RECT rect;
                Win32Interop.GetClientRect(hwnd, out rect);
                int lastW = Math.Max(1, rect.Width);
                int lastH = Math.Max(1, rect.Height);

                renderer = new D3D11DiffuseRenderer();
                renderer.Initialize(hwnd, lastW, lastH);
                AppLogger.Info(string.Format("DX11 render thread initialized ({0}x{1}) for {2}", lastW, lastH, _monitor.DeviceName));

                if (_uiDispatcher != null)
                {
                    _uiDispatcher.TryEnqueue(delegate
                    {
                        if (!_disposed && _onAttached != null)
                        {
                            _onAttached();
                        }
                    });
                }

                var clock = Stopwatch.StartNew();
                long lastFrameTicks = clock.ElapsedTicks;
                bool loggedFirstRender = false;

                while (!_stopRenderThread)
                {
                    Win32Interop.RECT curRect;
                    if (!Win32Interop.GetClientRect(hwnd, out curRect))
                    {
                        Thread.Sleep(50);
                        continue;
                    }

                    int curW = curRect.Width;
                    int curH = curRect.Height;
                    if (curW <= 0 || curH <= 0)
                    {
                        Thread.Sleep(50);
                        continue;
                    }

                    if (curW != lastW || curH != lastH)
                    {
                        lastW = curW;
                        lastH = curH;
                        renderer.Resize(curW, curH);
                    }

                    Bitmap bitmapToApply = null;
                    double[] bandsToApply = null;
                    bool playingNow;
                    AppSettings settingsSnapshot = null;
                    int targetFps;
                    bool useVsync;
                    bool pauseFullscreen;
                    bool attached;

                    lock (_stateLock)
                    {
                        if (_hasPendingBitmap)
                        {
                            bitmapToApply = _pendingAlbumBitmap;
                            _pendingAlbumBitmap = null;
                            _hasPendingBitmap = false;
                        }
                        if (_hasPendingBands)
                        {
                            bandsToApply = _pendingBands;
                            _pendingBands = null;
                            _hasPendingBands = false;
                        }
                        if (_pendingSettingsDirty)
                        {
                            settingsSnapshot = _settings.Clone();
                            _pendingSettingsDirty = false;
                        }
                        playingNow = _pendingPlaying;
                        targetFps = _settings.TargetFps;
                        useVsync = _settings.UseVSync;
                        pauseFullscreen = _settings.PauseOnFullscreen;
                        attached = _attachedToDesktop;
                    }

                    if (settingsSnapshot != null)
                    {
                        renderer.ApplySettings(settingsSnapshot);
                        if (!settingsSnapshot.EnableLiveBeats)
                        {
                            renderer.ResetAudioBeat();
                        }
                    }

                    renderer.SetPlaybackState(playingNow);
                    if (bandsToApply != null)
                    {
                        renderer.SetAudioBands(bandsToApply);
                    }
                    if (bitmapToApply != null)
                    {
                        try
                        {
                            AppLogger.Info(string.Format("SetAlbumBitmap {0}x{1}", bitmapToApply.Width, bitmapToApply.Height));
                            renderer.SetAlbumBitmap(bitmapToApply);
                        }
                        finally
                        {
                            bitmapToApply.Dispose();
                        }
                    }

                    if (_globallyPaused || (attached && pauseFullscreen && DesktopWallpaperHost.IsFullscreenCoveringBounds(_monitor.Bounds, _currentProcessId)))
                    {
                        Thread.Sleep(100);
                        lastFrameTicks = clock.ElapsedTicks;
                        continue;
                    }

                    long frameStartTicks = clock.ElapsedTicks;
                    float delta = (float)(frameStartTicks - lastFrameTicks) / (float)Stopwatch.Frequency;
                    lastFrameTicks = frameStartTicks;
                    if (delta < 0f || delta > 0.25f) delta = 1f / Math.Max(1, targetFps);

                    renderer.Render(delta, useVsync);
                    if (!loggedFirstRender)
                    {
                        loggedFirstRender = true;
                        bool vis = Win32Interop.IsWindowVisible(hwnd);
                        Win32Interop.RECT wr;
                        Win32Interop.GetWindowRect(hwnd, out wr);
                        AppLogger.Info(string.Format(
                            "First DX11 frame rendered: visible={0} winRect=({1},{2},{3}x{4}) paused={5}",
                            vis, wr.Left, wr.Top, wr.Width, wr.Height, _globallyPaused
                        ));
                    }

                    if (!useVsync)
                    {
                        long targetFrameTicks = (long)(Stopwatch.Frequency / (double)Math.Max(10, targetFps));
                        long elapsedTicks = clock.ElapsedTicks - frameStartTicks;
                        long remainingTicks = targetFrameTicks - elapsedTicks;

                        if (remainingTicks > 0)
                        {
                            double remainingMs = remainingTicks * 1000.0 / Stopwatch.Frequency;
                            if (remainingMs > 2.5)
                            {
                                Thread.Sleep((int)(remainingMs - 1.5));
                            }
                            while ((clock.ElapsedTicks - frameStartTicks) < targetFrameTicks && !_stopRenderThread)
                            {
                                Thread.SpinWait(64);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("RenderLoop crashed", ex);
                AttachmentModeDescription = "DX11 Error: " + ex.Message;
            }
            finally
            {
                if (renderer != null)
                {
                    try { renderer.Dispose(); } catch { }
                }
                if (timerPeriodSet)
                {
                    Win32Interop.timeEndPeriod(1);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            StopRenderThread();

            lock (_stateLock)
            {
                if (_pendingAlbumBitmap != null)
                {
                    _pendingAlbumBitmap.Dispose();
                    _pendingAlbumBitmap = null;
                }
            }

            if (_attachedToDesktop && _hwnd != IntPtr.Zero)
            {
                DesktopWallpaperHost.Detach(_hwnd, _hostParent);
                _attachedToDesktop = false;
            }

            if (_hwnd != IntPtr.Zero)
            {
                Win32Interop.DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
        }
    }
}
