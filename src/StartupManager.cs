using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace DiffuseWindows
{
    internal static class StartupManager
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string ValueName = "DiffuseWindows";

        public static string GetExecutablePath()
        {
            string path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path))
            {
                try
                {
                    using (Process proc = Process.GetCurrentProcess())
                    {
                        if (proc.MainModule != null)
                        {
                            path = proc.MainModule.FileName;
                        }
                    }
                }
                catch { }
            }
            return string.IsNullOrEmpty(path) ? string.Empty : Path.GetFullPath(path);
        }

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false))
                {
                    if (runKey == null) return false;
                    string command = runKey.GetValue(ValueName) as string;
                    if (string.IsNullOrWhiteSpace(command)) return false;
                }

                // Honor toggles made in Windows Settings -> Apps -> Startup or Task Manager.
                using (RegistryKey approvedKey = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, writable: false))
                {
                    if (approvedKey != null)
                    {
                        byte[] state = approvedKey.GetValue(ValueName) as byte[];
                        if (state != null && state.Length > 0 && (state[0] & 1) == 1)
                        {
                            return false;
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("StartupManager.IsEnabled failed", ex);
                return false;
            }
        }

        public static bool SetEnabled(bool enabled)
        {
            try
            {
                if (enabled)
                {
                    string exePath = GetExecutablePath();
                    if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath) ||
                        !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        AppLogger.Error("StartupManager.SetEnabled refused invalid exe path: " + exePath);
                        return false;
                    }

                    using (RegistryKey runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true))
                    {
                        if (runKey == null) return false;
                        runKey.SetValue(ValueName, "\"" + exePath + "\"", RegistryValueKind.String);
                    }

                    ClearApprovedDisableFlag();
                    AppLogger.Info("Registered Windows startup entry: " + exePath);
                    return true;
                }
                else
                {
                    using (RegistryKey runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
                    {
                        if (runKey != null)
                        {
                            runKey.DeleteValue(ValueName, throwOnMissingValue: false);
                        }
                    }

                    ClearApprovedDisableFlag();
                    AppLogger.Info("Removed Windows startup entry");
                    return true;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("StartupManager.SetEnabled failed", ex);
                return false;
            }
        }

        public static void SyncExecutablePathIfEnabled()
        {
            try
            {
                if (!IsEnabled()) return;

                string exePath = GetExecutablePath();
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath) ||
                    !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                string desiredCommand = "\"" + exePath + "\"";
                using (RegistryKey runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
                {
                    if (runKey == null) return;
                    string current = runKey.GetValue(ValueName) as string;
                    if (!string.Equals(current, desiredCommand, StringComparison.OrdinalIgnoreCase))
                    {
                        runKey.SetValue(ValueName, desiredCommand, RegistryValueKind.String);
                        AppLogger.Info("Updated Windows startup path: " + exePath);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("StartupManager.SyncExecutablePathIfEnabled failed", ex);
            }
        }

        private static void ClearApprovedDisableFlag()
        {
            try
            {
                using (RegistryKey approvedKey = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, writable: true))
                {
                    if (approvedKey != null)
                    {
                        approvedKey.DeleteValue(ValueName, throwOnMissingValue: false);
                    }
                }
            }
            catch { }
        }
    }
}
