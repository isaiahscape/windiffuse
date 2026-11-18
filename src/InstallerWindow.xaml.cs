using System;
using System.IO;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage.Pickers;

namespace DiffuseWindows
{
    public sealed partial class InstallerWindow : Window
    {
        private const int WindowWidthDips = 560;
        private const int WindowHeightDips = 460;

        private readonly bool _isUninstall;
        private readonly string _sourceDir;
        private readonly string _initialInstallDir;
        private IntPtr _hwnd;
        private AppWindow _appWindow;
        private bool _isBusy;

        public InstallerWindow(bool isUninstall, string sourceDir)
        {
            _isUninstall = isUninstall;
            _sourceDir = sourceDir;

            InitializeComponent();
            ConfigureWindow();

            VersionPillText.Text = InstallerEngine.GetVersionLabel();

            if (_isUninstall)
            {
                _initialInstallDir = InstallerEngine.GetInstalledDirectory() ?? AppContext.BaseDirectory.TrimEnd('\\');
                TitleBarLabel.Text = "Diffuse Uninstall";
                UninstallPathText.Text = _initialInstallDir;
                UninstallOptionsPanel.Visibility = Visibility.Visible;
            }
            else
            {
                string existingDir;
                bool isUpdate = InstallerEngine.IsAlreadyInstalled(out existingDir);
                _initialInstallDir = isUpdate ? existingDir : InstallerEngine.DefaultInstallDirectory;

                InstallPathBox.Text = _initialInstallDir;
                if (isUpdate)
                {
                    InstallHeaderText.Text = "Update Diffuse for Windows";
                    InstallActionButton.Content = "Update";
                    StartupToggle.IsOn = StartupManager.IsEnabled();
                }
                else
                {
                    StartupToggle.IsOn = true;
                }
                LaunchAfterToggle.IsOn = true;
                InstallOptionsPanel.Visibility = Visibility.Visible;
            }

            Closed += OnWindowClosed;
        }

        private void ConfigureWindow()
        {
            _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            try
            {
                SystemBackdrop = new MicaBackdrop();
            }
            catch
            {
                try { SystemBackdrop = new DesktopAcrylicBackdrop(); } catch { }
            }

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleBarGrid);

            if (_appWindow != null)
            {
                var presenter = _appWindow.Presenter as OverlappedPresenter;
                if (presenter != null)
                {
                    presenter.IsResizable = false;
                    presenter.IsMaximizable = false;
                }

                uint dpi = Win32Interop.GetDpiForWindow(_hwnd);
                if (dpi == 0) dpi = 96;
                double scale = dpi / 96.0;

                int w = (int)Math.Round(WindowWidthDips * scale);
                int h = (int)Math.Round(WindowHeightDips * scale);
                DisplayMonitorInfo mon = DesktopWallpaperHost.GetPrimaryMonitor();
                int x = mon.WorkingArea.Left + Math.Max(0, (mon.WorkingArea.Width - w) / 2);
                int y = mon.WorkingArea.Top + Math.Max(0, (mon.WorkingArea.Height - h) / 2);
                _appWindow.MoveAndResize(new RectInt32(x, y, w, h));
            }

            try
            {
                int roundPref = Win32Interop.DWMWCP_ROUND;
                Win32Interop.DwmSetWindowAttribute(_hwnd, Win32Interop.DWMWA_WINDOW_CORNER_PREFERENCE, ref roundPref, sizeof(int));
            }
            catch { }
        }

        private async void OnBrowseClick(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            try
            {
                var picker = new FolderPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
                picker.FileTypeFilter.Add("*");
                var folder = await picker.PickSingleFolderAsync();
                if (folder != null && !string.IsNullOrWhiteSpace(folder.Path))
                {
                    string selected = folder.Path;
                    if (!selected.EndsWith("Diffuse", StringComparison.OrdinalIgnoreCase))
                    {
                        selected = Path.Combine(selected, "Diffuse");
                    }
                    InstallPathBox.Text = selected;
                }
            }
            catch { }
        }

        private async void OnInstallClick(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            string targetDir = string.IsNullOrWhiteSpace(InstallPathBox.Text)
                ? InstallerEngine.DefaultInstallDirectory
                : InstallPathBox.Text.Trim();
            bool enableStartup = StartupToggle.IsOn;
            bool launchAfter = LaunchAfterToggle.IsOn;
            bool desktopShortcut = DesktopShortcutToggle.IsOn;

            _isBusy = true;
            InstallOptionsPanel.Visibility = Visibility.Collapsed;
            ProgressHeadingText.Text = "Installing Diffuse...";
            ProgressStatusText.Text = "Preparing...";
            SetupProgressBar.Value = 0;
            ProgressPanel.Visibility = Visibility.Visible;

            var progress = new Progress<InstallProgress>(delegate(InstallProgress p)
            {
                SetupProgressBar.Value = p.Percent;
                if (!string.IsNullOrEmpty(p.Status))
                {
                    ProgressStatusText.Text = p.Status;
                }
            });

            try
            {
                await InstallerEngine.InstallAsync(_sourceDir, targetDir, enableStartup, desktopShortcut, progress);

                if (launchAfter)
                {
                    InstallerEngine.LaunchInstalledApp(targetDir, showUi: true);
                }

                ProgressPanel.Visibility = Visibility.Collapsed;
                CompleteTitleText.Text = "Diffuse is ready";
                CompleteSubtitleText.Text = launchAfter
                    ? "Installed to " + targetDir + "\nDiffuse is now running in your system tray."
                    : "Installed to " + targetDir + "\nLaunch Diffuse anytime from the Start Menu.";
                CompletePanel.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                ShowFailure(ex.Message);
            }
            finally
            {
                _isBusy = false;
            }
        }

        private async void OnUninstallClick(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            bool removeData = RemoveDataCheckBox.IsChecked == true;

            _isBusy = true;
            UninstallOptionsPanel.Visibility = Visibility.Collapsed;
            ProgressHeadingText.Text = "Uninstalling Diffuse...";
            ProgressStatusText.Text = "Preparing...";
            SetupProgressBar.Value = 0;
            ProgressPanel.Visibility = Visibility.Visible;

            var progress = new Progress<InstallProgress>(delegate(InstallProgress p)
            {
                SetupProgressBar.Value = p.Percent;
                if (!string.IsNullOrEmpty(p.Status))
                {
                    ProgressStatusText.Text = p.Status;
                }
            });

            try
            {
                await InstallerEngine.UninstallAsync(_initialInstallDir, removeData, progress);
                ProgressPanel.Visibility = Visibility.Collapsed;
                CompleteTitleText.Text = "Diffuse uninstalled";
                CompleteSubtitleText.Text = "All shortcuts and startup entries have been removed.";
                CompletePanel.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                ShowFailure(ex.Message);
            }
            finally
            {
                _isBusy = false;
            }
        }

        private void ShowFailure(string message)
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            CompleteIcon.Glyph = "\uE783";
            CompleteTitleText.Text = "Installation error";
            CompleteSubtitleText.Text = "";
            ErrorBanner.Message = string.IsNullOrWhiteSpace(message) ? "An unexpected error occurred." : message;
            ErrorBanner.IsOpen = true;
            CompletePanel.Visibility = Visibility.Visible;
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            Close();
        }

        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            if (_isBusy)
            {
                args.Handled = true;
                return;
            }
            Application.Current.Exit();
        }
    }
}
