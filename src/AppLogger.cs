using System;
using System.IO;
using System.Text;

namespace DiffuseWindows
{
    internal static class AppLogger
    {
        private static readonly object LockObj = new object();

        public static string LogFilePath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "DiffuseWindows",
                    "diffuse.log"
                );
            }
        }

        public static void Reset()
        {
            try
            {
                string dir = Path.GetDirectoryName(LogFilePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(LogFilePath, string.Empty, Encoding.UTF8);
            }
            catch { }
        }

        public static void Info(string message)
        {
            Write("INFO", message);
        }

        public static void Error(string message, Exception ex = null)
        {
            string full = ex == null ? message : message + " | " + ex.ToString();
            Write("ERROR", full);
        }

        private static void Write(string level, string message)
        {
            try
            {
                lock (LockObj)
                {
                    string dir = Path.GetDirectoryName(LogFilePath);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    string line = string.Format(
                        "{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}{3}",
                        DateTime.Now,
                        level,
                        message,
                        Environment.NewLine
                    );
                    File.AppendAllText(LogFilePath, line, Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
