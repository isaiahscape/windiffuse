using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;

namespace DiffuseWindows
{
    public sealed partial class MainWindow : Window
    {
        private const int FlyoutWidthDips = 420;
        private const int FlyoutHeightDips = 650;

        private readonly AppSettings _settings;
        private readonly Action<AppSettings> _onSettingsChanged;
        private readonly Action _onReattachDesktop;
        private readonly Action _onTogglePause;
        private readonly Func<bool> _isPaused;
        private readonly Action _onQuit;
        private readonly Func<Rectangle?> _getTrayRect;

        private AppWindow _appWindow;
        private IntPtr _hwnd;
        private bool _suppressEvents;
        private bool _isPinned;
        private bool _isPickerOpen;
        private DateTime _lastShownUtc = DateTime.MinValue;

        private RectInt32 _targetFlyoutRect;
        private int _slideDir = 1;
        private int _slideOffsetPx = 22;
        private bool _isHiding;
        private int _animGeneration;
        private bool _renderingHooked;
        private Stopwatch _animClock;
        private double _animDurationMs;
        private int _animStartY;
        private int _animEndY;
        private Action _animOnComplete;

        public MainWindow(
            AppSettings initialSettings,
            Action<AppSettings> onSettingsChanged,
            Action onReattachDesktop,
            Action onTogglePause,
            Func<bool> isPaused,
            Action onQuit,
            Func<Rectangle?> getTrayRect)
        {
            _settings = initialSettings.Clone();
            _onSettingsChanged = onSettingsChanged;
            _onReattachDesktop = onReattachDesktop;
            _onTogglePause = onTogglePause;
            _isPaused = isPaused;
            _onQuit = onQuit;
            _getTrayRect = getTrayRect;

            InitializeComponent();
            ConfigureFlyoutWindow();
            PopulatePalettes();
            LoadSettingsIntoUi();
            WireControlEvents();

            Activated += OnWindowActivated;
            Closed += OnWindowClosed;
        }

        private void WireControlEvents()
        {
            EffectCombo.SelectionChanged += OnSettingControlChanged;
            PaletteCombo.SelectionChanged += OnSettingControlChanged;
            HdrModeCombo.SelectionChanged += OnSettingControlChanged;

            ScaleSlider.ValueChanged += OnSliderValueChanged;
            SpeedMinSlider.ValueChanged += OnSliderValueChanged;
            SpeedMaxSlider.ValueChanged += OnSliderValueChanged;
            BrightnessSlider.ValueChanged += OnSliderValueChanged;
            SaturationSlider.ValueChanged += OnSliderValueChanged;
            HdrIntensitySlider.ValueChanged += OnSliderValueChanged;
            OpacitySlider.ValueChanged += OnSliderValueChanged;
            BeatStrengthSlider.ValueChanged += OnSliderValueChanged;

            LiveBeatsToggle.Toggled += OnSettingControlChanged;
            PreservePauseToggle.Toggled += OnSettingControlChanged;
            VSyncToggle.Toggled += OnSettingControlChanged;
            PerformanceToggle.Toggled += OnSettingControlChanged;
            PauseFullscreenToggle.Toggled += OnSettingControlChanged;
            SpanMonitorsToggle.Toggled += OnSettingControlChanged;
            AttachDesktopToggle.Toggled += OnSettingControlChanged;
            StartWithWindowsToggle.Toggled += OnStartWithWindowsToggled;

            FpsNumberBox.ValueChanged += OnFpsValueChanged;
        }

        private void ConfigureFlyoutWindow()
        {
            _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            SystemBackdrop = new DesktopAcrylicBackdrop();

            if (_appWindow != null)
            {
                var presenter = _appWindow.Presenter as OverlappedPresenter;
                if (presenter != null)
                {
                    presenter.IsResizable = false;
                    presenter.IsMaximizable = false;
                    presenter.IsMinimizable = false;
                    presenter.IsAlwaysOnTop = true;
                    presenter.SetBorderAndTitleBar(true, false);
                }
            }

            long exStyle = Win32Interop.GetWindowLongPtr(_hwnd, Win32Interop.GWL_EXSTYLE).ToInt64();
            Win32Interop.SetWindowLongPtr(_hwnd, Win32Interop.GWL_EXSTYLE, new IntPtr(exStyle | Win32Interop.WS_EX_TOOLWINDOW));

            try
            {
                int roundPref = Win32Interop.DWMWCP_ROUND;
                Win32Interop.DwmSetWindowAttribute(_hwnd, Win32Interop.DWMWA_WINDOW_CORNER_PREFERENCE, ref roundPref, sizeof(int));
                int disableDwmTransitions = 1;
                Win32Interop.DwmSetWindowAttribute(_hwnd, Win32Interop.DWMWA_TRANSITIONS_FORCEDISABLED, ref disableDwmTransitions, sizeof(int));
            }
            catch { }

            ElementCompositionPreview.SetIsTranslationEnabled(RootGrid, true);
        }

        public void ToggleFlyout()
        {
            if (_appWindow != null && _appWindow.IsVisible && !_isHiding)
            {
                HideFlyout();
            }
            else
            {
                ShowFlyout();
            }
        }

        public void ShowFlyout()
        {
            _lastShownUtc = DateTime.UtcNow;
            _isHiding = false;
            int gen = ++_animGeneration;

            ComputeFlyoutPlacement();
            int startY = _targetFlyoutRect.Y + (_slideDir * _slideOffsetPx);

            if (_appWindow != null)
            {
                _appWindow.MoveAndResize(new RectInt32(_targetFlyoutRect.X, startY, _targetFlyoutRect.Width, _targetFlyoutRect.Height));
                _appWindow.Show();
            }
            else
            {
                Activate();
            }

            Win32Interop.ShowWindow(_hwnd, 5);
            Win32Interop.SetForegroundWindow(_hwnd);
            Activate();
            UpdatePauseIcon();
            RefreshStartupToggle();

            PlayRootCompositionOpen(gen);
            StartWindowSlide(_targetFlyoutRect.X, startY, _targetFlyoutRect.Y, 200.0, gen, null);
            AppLogger.Info(string.Format("ShowFlyout invoked visible={0}", _appWindow != null && _appWindow.IsVisible));
        }

        public void HideFlyout()
        {
            if (_appWindow == null || !_appWindow.IsVisible) return;
            if (App.IsShuttingDown)
            {
                StopWindowSlide();
                _isHiding = false;
                _appWindow.Hide();
                return;
            }
            if (_isHiding) return;

            _isHiding = true;
            int gen = ++_animGeneration;
            int currentY = _targetFlyoutRect.Y;
            Win32Interop.RECT wr;
            if (Win32Interop.GetWindowRect(_hwnd, out wr))
            {
                currentY = wr.Top;
            }
            int endY = _targetFlyoutRect.Y + (_slideDir * (_slideOffsetPx - 4));

            PlayRootCompositionClose();
            StartWindowSlide(_targetFlyoutRect.X, currentY, endY, 140.0, gen, delegate
            {
                if (gen != _animGeneration) return;
                _isHiding = false;
                if (_appWindow != null)
                {
                    _appWindow.Hide();
                }
                ResetRootCompositionVisual();
            });
        }

        private void ComputeFlyoutPlacement()
        {
            if (_appWindow == null) return;

            DisplayMonitorInfo mon = DesktopWallpaperHost.GetPrimaryMonitor();
            uint dpi = Win32Interop.GetDpiForWindow(_hwnd);
            if (dpi == 0) dpi = 96;
            double scale = dpi / 96.0;

            int widthPx = (int)Math.Round(FlyoutWidthDips * scale);
            int maxHeightPx = Math.Max(360, mon.WorkingArea.Height - (int)Math.Round(24 * scale));
            int heightPx = Math.Min((int)Math.Round(FlyoutHeightDips * scale), maxHeightPx);
            int margin = (int)Math.Round(12 * scale);

            int x = mon.WorkingArea.Right - widthPx - margin;
            int y = mon.WorkingArea.Bottom - heightPx - margin;
            _slideDir = 1;
            _slideOffsetPx = Math.Max(12, (int)Math.Round(22 * scale));

            Rectangle? trayRect = _getTrayRect != null ? _getTrayRect() : null;
            if (trayRect.HasValue && trayRect.Value.Width > 0)
            {
                int centeredX = trayRect.Value.Left + (trayRect.Value.Width / 2) - (widthPx / 2);
                x = Math.Max(mon.WorkingArea.Left + margin, Math.Min(centeredX, mon.WorkingArea.Right - widthPx - margin));

                if (trayRect.Value.Top < mon.Bounds.Top + (mon.Bounds.Height / 2))
                {
                    y = mon.WorkingArea.Top + margin;
                    _slideDir = -1;
                }
            }

            x = Math.Max(mon.WorkingArea.Left + margin, Math.Min(x, mon.WorkingArea.Right - widthPx - margin));
            y = Math.Max(mon.WorkingArea.Top + margin, Math.Min(y, mon.WorkingArea.Bottom - heightPx - margin));
            _targetFlyoutRect = new RectInt32(x, y, widthPx, heightPx);
        }

        private void PlayRootCompositionOpen(int gen)
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(RootGrid);
            Compositor compositor = visual.Compositor;

            CubicBezierEasingFunction ease = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.1f, 0.9f),
                new Vector2(0.2f, 1.0f)
            );

            visual.StopAnimation("Opacity");
            visual.StopAnimation("Translation");

            ScalarKeyFrameAnimation opacityAnim = compositor.CreateScalarKeyFrameAnimation();
            opacityAnim.InsertKeyFrame(0.0f, 0.0f);
            opacityAnim.InsertKeyFrame(1.0f, 1.0f, ease);
            opacityAnim.Duration = TimeSpan.FromMilliseconds(180);

            Vector3KeyFrameAnimation transAnim = compositor.CreateVector3KeyFrameAnimation();
            transAnim.InsertKeyFrame(0.0f, new Vector3(0f, _slideDir * 14f, 0f));
            transAnim.InsertKeyFrame(1.0f, Vector3.Zero, ease);
            transAnim.Duration = TimeSpan.FromMilliseconds(220);

            visual.StartAnimation("Opacity", opacityAnim);
            visual.StartAnimation("Translation", transAnim);
        }

        private void PlayRootCompositionClose()
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(RootGrid);
            Compositor compositor = visual.Compositor;

            CubicBezierEasingFunction easeIn = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.7f, 0.0f),
                new Vector2(1.0f, 0.5f)
            );

            visual.StopAnimation("Opacity");
            visual.StopAnimation("Translation");

            ScalarKeyFrameAnimation opacityAnim = compositor.CreateScalarKeyFrameAnimation();
            opacityAnim.InsertKeyFrame(1.0f, 0.0f, easeIn);
            opacityAnim.Duration = TimeSpan.FromMilliseconds(120);

            Vector3KeyFrameAnimation transAnim = compositor.CreateVector3KeyFrameAnimation();
            transAnim.InsertKeyFrame(1.0f, new Vector3(0f, _slideDir * 10f, 0f), easeIn);
            transAnim.Duration = TimeSpan.FromMilliseconds(140);

            visual.StartAnimation("Opacity", opacityAnim);
            visual.StartAnimation("Translation", transAnim);
        }

        private void ResetRootCompositionVisual()
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(RootGrid);
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Translation");
            visual.Opacity = 1.0f;
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
        }

        private void StartWindowSlide(int x, int startY, int endY, double durationMs, int gen, Action onComplete)
        {
            StopWindowSlide();
            _animStartY = startY;
            _animEndY = endY;
            _animDurationMs = durationMs;
            _animOnComplete = onComplete;
            _animClock = Stopwatch.StartNew();

            if (!_renderingHooked)
            {
                _renderingHooked = true;
                CompositionTarget.Rendering += OnCompositionRendering;
            }
        }

        private void StopWindowSlide()
        {
            if (_renderingHooked)
            {
                _renderingHooked = false;
                CompositionTarget.Rendering -= OnCompositionRendering;
            }
            _animOnComplete = null;
        }

        private void OnCompositionRendering(object sender, object e)
        {
            if (_animClock == null || _hwnd == IntPtr.Zero)
            {
                StopWindowSlide();
                return;
            }

            double t = Math.Min(1.0, _animClock.Elapsed.TotalMilliseconds / Math.Max(1.0, _animDurationMs));
            double eased = _isHiding ? (t * t) : (1.0 - Math.Pow(1.0 - t, 3.0));
            int y = (int)Math.Round(_animStartY + (_animEndY - _animStartY) * eased);

            Win32Interop.SetWindowPos(
                _hwnd,
                IntPtr.Zero,
                _targetFlyoutRect.X,
                y,
                0,
                0,
                Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOZORDER | Win32Interop.SWP_NOACTIVATE
            );

            if (t >= 1.0)
            {
                Action complete = _animOnComplete;
                StopWindowSlide();
                if (complete != null) complete();
            }
        }

        private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Escape && !_isPinned)
            {
                e.Handled = true;
                HideFlyout();
            }
        }

        private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
            {
                if ((DateTime.UtcNow - _lastShownUtc).TotalMilliseconds < 400)
                {
                    return;
                }
                if (!_isPinned && !_isPickerOpen)
                {
                    HideFlyout();
                }
            }
        }

        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            if (!App.IsShuttingDown)
            {
                args.Handled = true;
                HideFlyout();
            }
        }

        private void PopulatePalettes()
        {
            PaletteCombo.Items.Clear();
            List<PaletteEntry> palettes = AssetPaths.GetBuiltInPalettes();
            for (int i = 0; i < palettes.Count; i++)
            {
                PaletteCombo.Items.Add(new ComboBoxItem
                {
                    Content = palettes[i].DisplayName,
                    Tag = palettes[i].Id
                });
            }
        }

        private void LoadSettingsIntoUi()
        {
            _suppressEvents = true;
            try
            {
                SelectComboByTag(EffectCombo, _settings.PostProcessingMode, 0);
                ScaleSlider.Value = _settings.NoiseScale;
                SpeedMinSlider.Value = _settings.SpeedMin;
                SpeedMaxSlider.Value = _settings.SpeedMax;
                BrightnessSlider.Value = _settings.BrightnessOffset;
                SaturationSlider.Value = _settings.Saturation;
                OpacitySlider.Value = _settings.WallpaperOpacity;
                SelectComboByTag(HdrModeCombo, _settings.HdrMode, 0);
                HdrIntensitySlider.Value = _settings.HdrIntensity;
                bool hdrCustomizable = _settings.HdrMode != "off";
                HdrIntensityPanel.Opacity = hdrCustomizable ? 1.0 : 0.45;
                HdrIntensitySlider.IsEnabled = hdrCustomizable;

                LiveBeatsToggle.IsOn = _settings.EnableLiveBeats;
                BeatStrengthSlider.Value = _settings.BeatStrength;
                BeatStrengthPanel.Opacity = _settings.EnableLiveBeats ? 1.0 : 0.45;
                BeatStrengthSlider.IsEnabled = _settings.EnableLiveBeats;

                SelectComboByTag(PaletteCombo, _settings.DefaultPaletteId, 0);
                PreservePauseToggle.IsOn = _settings.PreserveAfterPause;

                FpsNumberBox.Value = _settings.TargetFps;
                VSyncToggle.IsOn = _settings.UseVSync;
                FpsNumberBox.IsEnabled = !_settings.UseVSync;

                PerformanceToggle.IsOn = _settings.PerformanceMode;
                PauseFullscreenToggle.IsOn = _settings.PauseOnFullscreen;
                SpanMonitorsToggle.IsOn = _settings.SpanAllMonitors;
                AttachDesktopToggle.IsOn = _settings.AttachToDesktop;
                StartWithWindowsToggle.IsOn = StartupManager.IsEnabled();

                UpdateSliderReadouts();
                UpdatePauseIcon();
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        private void UpdateSliderReadouts()
        {
            ScaleValueText.Text = string.Format("{0:0.00}", ScaleSlider.Value);
            SpeedMinValueText.Text = string.Format("{0:0.00}", SpeedMinSlider.Value);
            SpeedMaxValueText.Text = string.Format("{0:0.00}", SpeedMaxSlider.Value);
            BrightnessValueText.Text = string.Format("{0:+0.00;-0.00;0.00}", BrightnessSlider.Value);
            SaturationValueText.Text = string.Format("{0:0.00}x", SaturationSlider.Value);
            OpacityValueText.Text = string.Format("{0}%", (int)Math.Round(OpacitySlider.Value * 100.0 / 255.0));
            HdrIntensityValueText.Text = string.Format("{0:0.00}x", HdrIntensitySlider.Value);
            BeatStrengthValueText.Text = string.Format("{0:0.00}", BeatStrengthSlider.Value);
        }

        private void EmitSettings()
        {
            if (_suppressEvents) return;

            var effectItem = EffectCombo.SelectedItem as ComboBoxItem;
            _settings.PostProcessingMode = effectItem != null && effectItem.Tag != null ? effectItem.Tag.ToString() : "blur";
            _settings.NoiseScale = (float)ScaleSlider.Value;
            _settings.SpeedMin = (float)SpeedMinSlider.Value;
            _settings.SpeedMax = Math.Max(_settings.SpeedMin, (float)SpeedMaxSlider.Value);
            _settings.BrightnessOffset = (float)BrightnessSlider.Value;
            _settings.Saturation = (float)SaturationSlider.Value;
            _settings.WallpaperOpacity = (int)Math.Round(OpacitySlider.Value);

            var hdrItem = HdrModeCombo.SelectedItem as ComboBoxItem;
            _settings.HdrMode = hdrItem != null && hdrItem.Tag != null ? hdrItem.Tag.ToString() : "auto";
            _settings.HdrIntensity = (float)HdrIntensitySlider.Value;
            bool hdrCustomizable = _settings.HdrMode != "off";
            HdrIntensityPanel.Opacity = hdrCustomizable ? 1.0 : 0.45;
            HdrIntensitySlider.IsEnabled = hdrCustomizable;

            _settings.EnableLiveBeats = LiveBeatsToggle.IsOn;
            _settings.BeatStrength = (float)BeatStrengthSlider.Value;
            BeatStrengthPanel.Opacity = _settings.EnableLiveBeats ? 1.0 : 0.45;
            BeatStrengthSlider.IsEnabled = _settings.EnableLiveBeats;

            var palItem = PaletteCombo.SelectedItem as ComboBoxItem;
            if (palItem != null && palItem.Tag != null)
            {
                _settings.DefaultPaletteId = palItem.Tag.ToString();
            }

            _settings.PreserveAfterPause = PreservePauseToggle.IsOn;
            _settings.UseVSync = VSyncToggle.IsOn;
            FpsNumberBox.IsEnabled = !_settings.UseVSync;

            if (!double.IsNaN(FpsNumberBox.Value))
            {
                _settings.TargetFps = (int)Math.Round(FpsNumberBox.Value);
            }

            _settings.PerformanceMode = PerformanceToggle.IsOn;
            _settings.PauseOnFullscreen = PauseFullscreenToggle.IsOn;
            _settings.SpanAllMonitors = SpanMonitorsToggle.IsOn;
            _settings.AttachToDesktop = AttachDesktopToggle.IsOn;

            UpdateSliderReadouts();
            _settings.Save();
            _onSettingsChanged(_settings.Clone());
        }

        private void OnSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            EmitSettings();
        }

        private void OnSettingControlChanged(object sender, RoutedEventArgs e)
        {
            EmitSettings();
        }

        private void OnSettingControlChanged(object sender, SelectionChangedEventArgs e)
        {
            EmitSettings();
        }

        private void OnFpsValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (!_suppressEvents && !double.IsNaN(args.NewValue))
            {
                EmitSettings();
            }
        }

        private void OnStartWithWindowsToggled(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool desired = StartWithWindowsToggle.IsOn;
            if (!StartupManager.SetEnabled(desired))
            {
                RefreshStartupToggle();
            }
        }

        private void RefreshStartupToggle()
        {
            bool enabled = StartupManager.IsEnabled();
            if (StartWithWindowsToggle.IsOn != enabled)
            {
                bool prev = _suppressEvents;
                _suppressEvents = true;
                try
                {
                    StartWithWindowsToggle.IsOn = enabled;
                }
                finally
                {
                    _suppressEvents = prev;
                }
            }
        }

        private async void OnBrowsePaletteClick(object sender, RoutedEventArgs e)
        {
            _isPickerOpen = true;
            try
            {
                var picker = new FileOpenPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".bmp");
                picker.FileTypeFilter.Add(".webp");

                var file = await picker.PickSingleFileAsync();
                if (file != null && File.Exists(file.Path))
                {
                    _settings.CustomPalettePath = file.Path;
                    _settings.Save();
                    _onSettingsChanged(_settings.Clone());
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Browse palette failed", ex);
            }
            finally
            {
                _isPickerOpen = false;
                ShowFlyout();
            }
        }

        private void OnResetPaletteClick(object sender, RoutedEventArgs e)
        {
            _settings.CustomPalettePath = "";
            _settings.Save();
            _onSettingsChanged(_settings.Clone());
        }

        private void OnPinClick(object sender, RoutedEventArgs e)
        {
            _isPinned = PinButton.IsChecked == true;
        }

        private void OnReattachClick(object sender, RoutedEventArgs e)
        {
            _onReattachDesktop();
        }

        private void OnPauseClick(object sender, RoutedEventArgs e)
        {
            _onTogglePause();
            UpdatePauseIcon();
        }

        private void OnQuitClick(object sender, RoutedEventArgs e)
        {
            _onQuit();
        }

        public void UpdatePauseIcon()
        {
            bool paused = _isPaused != null && _isPaused();
            PauseIcon.Glyph = paused ? "\uE768" : "\uE769";
        }

        public void UpdateDesktopMode(string mode)
        {
            DesktopStatusText.Text = string.IsNullOrEmpty(mode) ? "Desktop" : mode;
        }

        public async void UpdateMediaStatus(MediaStateSnapshot snap, Bitmap activeDisplayBitmap)
        {
            if (snap != null && snap.HasSession)
            {
                TrackTitleText.Text = string.IsNullOrEmpty(snap.Title) ? "Playing Media" : snap.Title;
                TrackArtistText.Text = string.IsNullOrEmpty(snap.Artist) ? (snap.IsPlaying ? "Playing" : "Paused") : snap.Artist;
                TrackSourceText.Text = string.Format(
                    "{0} · {1}",
                    snap.IsPlaying ? "Playing" : "Paused",
                    string.IsNullOrEmpty(snap.SourceApp) ? "SMTC" : snap.SourceApp
                );
            }
            else
            {
                TrackTitleText.Text = "No Music Playing";
                TrackArtistText.Text = "Using fallback palette";
                TrackSourceText.Text = "SMTC · Waiting for Cider, Spotify, etc.";
            }

            if (activeDisplayBitmap != null)
            {
                try
                {
                    using (var copy = new Bitmap(activeDisplayBitmap))
                    using (var ms = new MemoryStream())
                    {
                        copy.Save(ms, ImageFormat.Png);
                        byte[] bytes = ms.ToArray();
                        using (var ras = new InMemoryRandomAccessStream())
                        {
                            await ras.WriteAsync(bytes.AsBuffer());
                            ras.Seek(0);
                            var bmpImage = new BitmapImage();
                            await bmpImage.SetSourceAsync(ras);
                            AlbumArtImage.Source = bmpImage;
                        }
                    }
                }
                catch { }
            }
        }

        private static void SelectComboByTag(ComboBox combo, string tag, int fallbackIndex)
        {
            for (int i = 0; i < combo.Items.Count; i++)
            {
                var item = combo.Items[i] as ComboBoxItem;
                if (item != null && item.Tag != null &&
                    string.Equals(item.Tag.ToString(), tag, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
            if (combo.Items.Count > fallbackIndex && fallbackIndex >= 0)
            {
                combo.SelectedIndex = fallbackIndex;
            }
        }
    }
}
