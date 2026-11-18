using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DiffuseSetup
{
    internal static class Program
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        [STAThread]
        private static int Main(string[] args)
        {
            string tempDir = Path.Combine(
                Path.GetTempPath(),
                "DiffuseSetup_" + Guid.NewGuid().ToString("N").Substring(0, 8)
            );

            try
            {
                using (Stream payloadStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
                {
                    if (payloadStream == null)
                    {
                        MessageBoxW(IntPtr.Zero, "Embedded installer payload was not found.", "Diffuse Setup", 0x10);
                        return 1;
                    }

                    Directory.CreateDirectory(tempDir);
                    using (var archive = new ZipArchive(payloadStream, ZipArchiveMode.Read, leaveOpen: false))
                    {
                        archive.ExtractToDirectory(tempDir, overwriteFiles: true);
                    }
                }

                string uiExe = Path.Combine(tempDir, "DiffuseWindows.exe");
                if (!File.Exists(uiExe))
                {
                    MessageBoxW(IntPtr.Zero, "Extracted installer executable was not found.", "Diffuse Setup", 0x10);
                    return 1;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = uiExe,
                    WorkingDirectory = tempDir,
                    UseShellExecute = false
                };
                psi.ArgumentList.Add("--install");
                psi.ArgumentList.Add("--source-dir");
                psi.ArgumentList.Add(tempDir);
                for (int i = 0; i < args.Length; i++)
                {
                    psi.ArgumentList.Add(args[i]);
                }

                using (Process proc = Process.Start(psi))
                {
                    if (proc == null)
                    {
                        MessageBoxW(IntPtr.Zero, "Failed to launch the WinUI 3 installer.", "Diffuse Setup", 0x10);
                        return 1;
                    }
                    proc.WaitForExit();
                    return proc.ExitCode;
                }
            }
            catch (Exception ex)
            {
                MessageBoxW(IntPtr.Zero, "Setup failed to initialize:\n\n" + ex.Message, "Diffuse Setup", 0x10);
                return 1;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, recursive: true);
                    }
                }
                catch { }
            }
        }
    }
}
