using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace DiffuseWindows
{
    internal static class InstallerEngine
    {
        private const string AppKeyPath = @"Software\DiffuseWindows";
        private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DiffuseWindows";
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string StartupValueName = "DiffuseWindows";

        public static string DefaultInstallDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs",
                    "Diffuse"
                );
            }
        }

        public static string StartMenuFolderPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                    "Diffuse"
                );
            }
        }

        public static string DesktopShortcutPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    "Diffuse.lnk"
                );
            }
        }

        public static string GetVersionLabel()
        {
            var attr = typeof(InstallerEngine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            string ver = attr != null ? attr.InformationalVersion : null;
            if (string.IsNullOrWhiteSpace(ver))
            {
                Version v = typeof(InstallerEngine).Assembly.GetName().Version;
                return v != null ? v.ToString(3) : "dev";
            }
            int plusIdx = ver.IndexOf('+');
            if (plusIdx > 0) ver = ver.Substring(0, plusIdx);
            return ver.Trim();
        }

        public static string GetInstalledDirectory()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(AppKeyPath, writable: false))
                {
                    if (key != null)
                    {
                        string dir = key.GetValue("InstallDir") as string;
                        if (!string.IsNullOrWhiteSpace(dir))
                        {
                            return dir.Trim();
                        }
                    }
                }
            }
            catch { }

            string fallback = DefaultInstallDirectory;
            if (File.Exists(Path.Combine(fallback, "DiffuseWindows.exe")))
            {
                return fallback;
            }
            return null;
        }

        public static bool IsAlreadyInstalled(out string installDir)
        {
            installDir = GetInstalledDirectory();
            return !string.IsNullOrEmpty(installDir) &&
                   File.Exists(Path.Combine(installDir, "DiffuseWindows.exe"));
        }

        public static Task InstallAsync(
            string sourceDir,
            string targetDir,
            bool enableStartup,
            bool createDesktopShortcut,
            IProgress<InstallProgress> progress)
        {
            return Task.Run(delegate
            {
                if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
                {
                    throw new DirectoryNotFoundException("Installer payload directory not found: " + sourceDir);
                }

                string fullTarget = Path.GetFullPath(targetDir.Trim());
                string fullSource = Path.GetFullPath(sourceDir.Trim());
                string installedExe = Path.Combine(fullTarget, "DiffuseWindows.exe");

                Report(progress, 5, "Checking running Diffuse instances...");
                StopRunningInstances();

                Report(progress, 12, "Preparing installation directory...");
                Directory.CreateDirectory(fullTarget);

                if (!string.Equals(fullSource, fullTarget, StringComparison.OrdinalIgnoreCase))
                {
                    string[] files = Directory.GetFiles(fullSource, "*", SearchOption.AllDirectories);
                    int total = Math.Max(1, files.Length);
                    long totalBytes = 0;

                    for (int i = 0; i < files.Length; i++)
                    {
                        string srcFile = files[i];
                        string rel = Path.GetRelativePath(fullSource, srcFile);
                        if (rel.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;

                        string dstFile = Path.Combine(fullTarget, rel);
                        string dstParent = Path.GetDirectoryName(dstFile);
                        if (!string.IsNullOrEmpty(dstParent))
                        {
                            Directory.CreateDirectory(dstParent);
                        }

                        CopyFileWithRetry(srcFile, dstFile);
                        totalBytes += new FileInfo(dstFile).Length;

                        if (i % 10 == 0 || i == files.Length - 1)
                        {
                            double pct = 15.0 + (70.0 * (i + 1) / total);
                            Report(progress, pct, "Installing application files...");
                        }
                    }

                    RegisterAppMetadata(fullTarget, installedExe, totalBytes);
                }
                else
                {
                    long totalBytes = ComputeDirectoryBytes(fullTarget);
                    RegisterAppMetadata(fullTarget, installedExe, totalBytes);
                }

                Report(progress, 90, "Creating Start Menu shortcuts...");
                string startMenuDir = StartMenuFolderPath;
                Directory.CreateDirectory(startMenuDir);

                CreateShortcut(
                    Path.Combine(startMenuDir, "Diffuse.lnk"),
                    installedExe,
                    "",
                    fullTarget,
                    "Diffuse Live Wallpaper"
                );
                CreateShortcut(
                    Path.Combine(startMenuDir, "Diffuse Settings.lnk"),
                    installedExe,
                    "--show-ui",
                    fullTarget,
                    "Open Diffuse Controls"
                );

                if (createDesktopShortcut)
                {
                    CreateShortcut(
                        DesktopShortcutPath,
                        installedExe,
                        "--show-ui",
                        fullTarget,
                        "Diffuse Live Wallpaper"
                    );
                }

                Report(progress, 95, "Configuring Windows startup preference...");
                ApplyStartupSetting(installedExe, enableStartup);

                Report(progress, 100, "Diffuse for Windows is ready.");
            });
        }

        public static void LaunchInstalledApp(string targetDir, bool showUi)
        {
            string exePath = Path.Combine(targetDir, "DiffuseWindows.exe");
            if (!File.Exists(exePath)) return;

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = showUi ? "--show-ui" : "",
                WorkingDirectory = targetDir,
                UseShellExecute = true
            };
            Process.Start(psi);
        }

        public static Task UninstallAsync(
            string installDir,
            bool removeUserData,
            IProgress<InstallProgress> progress)
        {
            return Task.Run(delegate
            {
                Report(progress, 10, "Stopping running Diffuse instances...");
                StopRunningInstances();

                Report(progress, 30, "Removing Windows startup registration...");
                RemoveStartupEntry();

                Report(progress, 50, "Removing shortcuts...");
                try
                {
                    if (Directory.Exists(StartMenuFolderPath))
                    {
                        Directory.Delete(StartMenuFolderPath, recursive: true);
                    }
                }
                catch { }

                try
                {
                    if (File.Exists(DesktopShortcutPath))
                    {
                        File.Delete(DesktopShortcutPath);
                    }
                }
                catch { }

                Report(progress, 70, "Removing registry entries...");
                try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false); } catch { }
                try { Registry.CurrentUser.DeleteSubKeyTree(AppKeyPath, throwOnMissingSubKey: false); } catch { }

                if (removeUserData)
                {
                    Report(progress, 82, "Removing saved preferences and logs...");
                    try
                    {
                        string appDataDir = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "DiffuseWindows"
                        );
                        if (Directory.Exists(appDataDir))
                        {
                            Directory.Delete(appDataDir, recursive: true);
                        }
                    }
                    catch { }
                }

                Report(progress, 92, "Cleaning up installed files...");
                if (!string.IsNullOrWhiteSpace(installDir) && Directory.Exists(installDir))
                {
                    ScheduleDirectoryCleanup(installDir);
                }

                Report(progress, 100, "Diffuse has been uninstalled.");
            });
        }

        private static void RegisterAppMetadata(string installDir, string installedExe, long totalBytes)
        {
            using (RegistryKey appKey = Registry.CurrentUser.CreateSubKey(AppKeyPath, writable: true))
            {
                if (appKey != null)
                {
                    appKey.SetValue("InstallDir", installDir, RegistryValueKind.String);
                    appKey.SetValue("Version", GetVersionLabel(), RegistryValueKind.String);
                }
            }

            int estimatedKb = (int)Math.Max(1, totalBytes / 1024);
            using (RegistryKey uninstKey = Registry.CurrentUser.CreateSubKey(UninstallKeyPath, writable: true))
            {
                if (uninstKey != null)
                {
                    uninstKey.SetValue("DisplayName", "Diffuse for Windows", RegistryValueKind.String);
                    uninstKey.SetValue("DisplayVersion", GetVersionLabel(), RegistryValueKind.String);
                    uninstKey.SetValue("Publisher", "Diffuse", RegistryValueKind.String);
                    uninstKey.SetValue("DisplayIcon", installedExe + ",0", RegistryValueKind.String);
                    uninstKey.SetValue("InstallLocation", installDir, RegistryValueKind.String);
                    uninstKey.SetValue("UninstallString", "\"" + installedExe + "\" --uninstall", RegistryValueKind.String);
                    uninstKey.SetValue("QuietUninstallString", "\"" + installedExe + "\" --uninstall --silent", RegistryValueKind.String);
                    uninstKey.SetValue("EstimatedSize", estimatedKb, RegistryValueKind.DWord);
                    uninstKey.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    uninstKey.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                }
            }
        }

        private static void ApplyStartupSetting(string installedExe, bool enableStartup)
        {
            if (enableStartup)
            {
                using (RegistryKey runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true))
                {
                    if (runKey != null)
                    {
                        runKey.SetValue(StartupValueName, "\"" + installedExe + "\"", RegistryValueKind.String);
                    }
                }
                using (RegistryKey approvedKey = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, writable: true))
                {
                    if (approvedKey != null)
                    {
                        approvedKey.DeleteValue(StartupValueName, throwOnMissingValue: false);
                    }
                }
            }
            else
            {
                RemoveStartupEntry();
            }

            try
            {
                AppSettings s = AppSettings.Load();
                s.StartupConfigured = true;
                s.Save();
            }
            catch { }
        }

        private static void RemoveStartupEntry()
        {
            try
            {
                using (RegistryKey runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
                {
                    if (runKey != null)
                    {
                        runKey.DeleteValue(StartupValueName, throwOnMissingValue: false);
                    }
                }
                using (RegistryKey approvedKey = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, writable: true))
                {
                    if (approvedKey != null)
                    {
                        approvedKey.DeleteValue(StartupValueName, throwOnMissingValue: false);
                    }
                }
            }
            catch { }
        }

        private static void StopRunningInstances()
        {
            int selfPid = Environment.ProcessId;
            Process[] procs = Process.GetProcessesByName("DiffuseWindows");
            foreach (Process p in procs)
            {
                try
                {
                    if (p.Id == selfPid) continue;
                    try { p.CloseMainWindow(); } catch { }
                    if (!p.WaitForExit(1500))
                    {
                        p.Kill(entireProcessTree: true);
                        p.WaitForExit(3000);
                    }
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }
            Thread.Sleep(250);
        }

        private static void CopyFileWithRetry(string src, string dst)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    File.Copy(src, dst, overwrite: true);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(200);
                }
                catch (UnauthorizedAccessException) when (attempt < 4)
                {
                    Thread.Sleep(200);
                }
            }
        }

        private static long ComputeDirectoryBytes(string dir)
        {
            long sum = 0;
            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                sum += new FileInfo(f).Length;
            }
            return sum;
        }

        private static void ScheduleDirectoryCleanup(string installDir)
        {
            try
            {
                string escaped = installDir.TrimEnd('\\');
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c timeout /t 2 /nobreak >nul & rmdir /s /q \"" + escaped + "\"",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                Process.Start(psi);
            }
            catch { }
        }

        private static void Report(IProgress<InstallProgress> progress, double percent, string status)
        {
            if (progress != null)
            {
                progress.Report(new InstallProgress { Percent = percent, Status = status });
            }
        }

        private static void CreateShortcut(string shortcutPath, string targetPath, string arguments, string workingDir, string description)
        {
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(targetPath);
            if (!string.IsNullOrEmpty(arguments))
            {
                link.SetArguments(arguments);
            }
            link.SetWorkingDirectory(workingDir);
            link.SetDescription(description);
            link.SetIconLocation(targetPath, 0);

            var file = (IPersistFile)link;
            file.Save(shortcutPath, true);
        }

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink { }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cchMaxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cchMaxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cchMaxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cchIconPath, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("0000010b-0000-0000-C000-000000000046")]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }
    }

    internal sealed class InstallProgress
    {
        public double Percent;
        public string Status;
    }
}
