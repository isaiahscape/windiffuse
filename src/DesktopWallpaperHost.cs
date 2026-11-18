using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace DiffuseWindows
{
    internal sealed class DisplayMonitorInfo
    {
        public string DeviceName;
        public Rectangle Bounds;
        public Rectangle WorkingArea;
        public bool IsPrimary;
    }

    internal static class DesktopWallpaperHost
    {
        public sealed class AttachResult
        {
            public bool Attached;
            public IntPtr HostParent;
            public string ModeDescription;
        }

        public static List<DisplayMonitorInfo> GetMonitors()
        {
            var list = new List<DisplayMonitorInfo>();
            Win32Interop.EnumDisplayMonitors(
                IntPtr.Zero,
                IntPtr.Zero,
                delegate(IntPtr hMonitor, IntPtr hdcMonitor, ref Win32Interop.RECT lprcMonitor, IntPtr dwData)
                {
                    var mi = new Win32Interop.MONITORINFOEX();
                    mi.cbSize = Marshal.SizeOf(typeof(Win32Interop.MONITORINFOEX));
                    if (Win32Interop.GetMonitorInfo(hMonitor, ref mi))
                    {
                        list.Add(new DisplayMonitorInfo
                        {
                            DeviceName = mi.szDevice,
                            Bounds = Rectangle.FromLTRB(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom),
                            WorkingArea = Rectangle.FromLTRB(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom),
                            IsPrimary = (mi.dwFlags & 1) != 0
                        });
                    }
                    return true;
                },
                IntPtr.Zero
            );
            return list;
        }

        public static DisplayMonitorInfo GetPrimaryMonitor()
        {
            List<DisplayMonitorInfo> all = GetMonitors();
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].IsPrimary) return all[i];
            }
            if (all.Count > 0) return all[0];
            return new DisplayMonitorInfo
            {
                DeviceName = "DISPLAY1",
                Bounds = new Rectangle(0, 0, 1920, 1080),
                WorkingArea = new Rectangle(0, 0, 1920, 1040),
                IsPrimary = true
            };
        }

        public static AttachResult Attach(IntPtr hwnd, Rectangle screenBounds, byte opacity)
        {
            var result = new AttachResult();
            IntPtr progman = Win32Interop.FindWindow("Progman", null);
            if (progman == IntPtr.Zero)
            {
                AppLogger.Error("FindWindow(Progman) returned 0");
                ConfigureStandaloneOverlay(hwnd, screenBounds, opacity);
                result.Attached = false;
                result.HostParent = IntPtr.Zero;
                result.ModeDescription = "Fallback overlay (Progman unavailable)";
                return result;
            }

            long progmanEx = Win32Interop.GetWindowLongPtr(progman, Win32Interop.GWL_EXSTYLE).ToInt64();
            bool progmanNoRedir = (progmanEx & Win32Interop.WS_EX_NOREDIRECTIONBITMAP) != 0;
            AppLogger.Info(string.Format("Progman=0x{0:X} exStyle=0x{1:X} noRedir={2} screen={3}", progman.ToInt64(), progmanEx, progmanNoRedir, screenBounds));

            IntPtr msgResult;
            Win32Interop.SendMessageTimeout(
                progman,
                Win32Interop.WM_SPAWN_WORKER,
                new IntPtr(0xD),
                new IntPtr(0x1),
                Win32Interop.SMTO_NORMAL,
                1000,
                out msgResult
            );
            if (!progmanNoRedir)
            {
                Win32Interop.SendMessageTimeout(
                    progman,
                    Win32Interop.WM_SPAWN_WORKER,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    Win32Interop.SMTO_NORMAL,
                    1000,
                    out msgResult
                );
            }

            IntPtr shellViewInProgman = Win32Interop.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            IntPtr workerInProgman = Win32Interop.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
            AppLogger.Info(string.Format("In Progman: SHELLDLL_DefView=0x{0:X} WorkerW=0x{1:X}", shellViewInProgman.ToInt64(), workerInProgman.ToInt64()));

            if (shellViewInProgman != IntPtr.Zero)
            {
                AttachAsChild(hwnd, progman, shellViewInProgman, workerInProgman, screenBounds, opacity, forceLayered: true);
                result.Attached = true;
                result.HostParent = progman;
                result.ModeDescription = "Progman · Behind Icons";
                return result;
            }

            IntPtr workerW = FindClassicWorkerW();
            if (workerW != IntPtr.Zero)
            {
                AttachAsChild(hwnd, workerW, IntPtr.Zero, IntPtr.Zero, screenBounds, opacity, forceLayered: opacity < 255);
                result.Attached = true;
                result.HostParent = workerW;
                result.ModeDescription = "WorkerW · Behind Icons";
                return result;
            }

            ConfigureStandaloneOverlay(hwnd, screenBounds, opacity);
            result.Attached = false;
            result.HostParent = IntPtr.Zero;
            result.ModeDescription = "Bottommost fallback";
            return result;
        }

        public static void Detach(IntPtr hwnd, IntPtr previousParent)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                Win32Interop.SetParent(hwnd, IntPtr.Zero);
                if (previousParent != IntPtr.Zero)
                {
                    Win32Interop.SetWindowPos(
                        previousParent,
                        IntPtr.Zero,
                        0, 0, 0, 0,
                        Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOZORDER | Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_FRAMECHANGED
                    );
                }
            }
            catch { }
        }

        public static void ApplyOpacity(IntPtr hwnd, byte opacity, IntPtr hostParent)
        {
            if (hwnd == IntPtr.Zero) return;

            bool forceLayered = hostParent != IntPtr.Zero && Win32Interop.GetWindowClassName(hostParent) == "Progman";
            bool useLayered = forceLayered || opacity < 255;

            long exStyle = Win32Interop.GetWindowLongPtr(hwnd, Win32Interop.GWL_EXSTYLE).ToInt64();
            if (useLayered)
            {
                if ((exStyle & Win32Interop.WS_EX_LAYERED) == 0)
                {
                    Win32Interop.SetWindowLongPtr(hwnd, Win32Interop.GWL_EXSTYLE, new IntPtr(exStyle | Win32Interop.WS_EX_LAYERED));
                }
                Win32Interop.SetLayeredWindowAttributes(hwnd, 0, opacity, Win32Interop.LWA_ALPHA);
            }
            else if ((exStyle & Win32Interop.WS_EX_LAYERED) != 0)
            {
                Win32Interop.SetWindowLongPtr(hwnd, Win32Interop.GWL_EXSTYLE, new IntPtr(exStyle & ~((long)Win32Interop.WS_EX_LAYERED)));
            }
        }

        public static bool IsFullscreenCoveringBounds(Rectangle bounds, int currentProcessId)
        {
            IntPtr fg = Win32Interop.GetForegroundWindow();
            if (fg == IntPtr.Zero || !Win32Interop.IsWindowVisible(fg) || Win32Interop.IsIconic(fg))
            {
                return false;
            }

            uint pid;
            Win32Interop.GetWindowThreadProcessId(fg, out pid);
            if (pid == (uint)currentProcessId)
            {
                return false;
            }

            string cls = Win32Interop.GetWindowClassName(fg);
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd")
            {
                return false;
            }

            int cloaked = 0;
            if (Win32Interop.DwmGetWindowAttribute(fg, Win32Interop.DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0)
            {
                return false;
            }

            Win32Interop.RECT rect;
            if (!Win32Interop.GetWindowRect(fg, out rect))
            {
                return false;
            }

            return rect.Left <= bounds.Left &&
                   rect.Top <= bounds.Top &&
                   rect.Right >= bounds.Right &&
                   rect.Bottom >= bounds.Bottom;
        }

        private static IntPtr FindClassicWorkerW()
        {
            IntPtr foundWorkerW = IntPtr.Zero;
            Win32Interop.EnumWindows(delegate(IntPtr topHandle, IntPtr lParam)
            {
                IntPtr shellView = Win32Interop.FindWindowEx(topHandle, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (shellView != IntPtr.Zero)
                {
                    foundWorkerW = Win32Interop.FindWindowEx(IntPtr.Zero, topHandle, "WorkerW", null);
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            return foundWorkerW;
        }

        private static void AttachAsChild(IntPtr hwnd, IntPtr parent, IntPtr insertAfter, IntPtr workerInProgman, Rectangle screenBounds, byte opacity, bool forceLayered)
        {
            long style = Win32Interop.GetWindowLongPtr(hwnd, Win32Interop.GWL_STYLE).ToInt64();
            style = (style & ~((long)Win32Interop.WS_POPUP | Win32Interop.WS_CLIPCHILDREN)) | Win32Interop.WS_CHILD | Win32Interop.WS_CLIPSIBLINGS | Win32Interop.WS_VISIBLE;
            Win32Interop.SetWindowLongPtr(hwnd, Win32Interop.GWL_STYLE, new IntPtr(style));

            bool useLayered = forceLayered || opacity < 255;
            long exStyle = Win32Interop.GetWindowLongPtr(hwnd, Win32Interop.GWL_EXSTYLE).ToInt64();
            exStyle = (exStyle & ~((long)Win32Interop.WS_EX_TRANSPARENT)) | Win32Interop.WS_EX_TOOLWINDOW | Win32Interop.WS_EX_NOACTIVATE;
            if (useLayered) exStyle |= Win32Interop.WS_EX_LAYERED;
            else exStyle &= ~((long)Win32Interop.WS_EX_LAYERED);
            Win32Interop.SetWindowLongPtr(hwnd, Win32Interop.GWL_EXSTYLE, new IntPtr(exStyle));

            if (useLayered)
            {
                bool layeredOk = Win32Interop.SetLayeredWindowAttributes(hwnd, 0, opacity, Win32Interop.LWA_ALPHA);
                AppLogger.Info(string.Format("SetLayeredWindowAttributes(alpha={0}) -> {1}", opacity, layeredOk));
            }

            Win32Interop.SetParent(hwnd, parent);

            if (insertAfter != IntPtr.Zero)
            {
                Win32Interop.SetWindowPos(
                    hwnd,
                    insertAfter,
                    0, 0, 0, 0,
                    Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOACTIVATE
                );
            }

            if (workerInProgman != IntPtr.Zero)
            {
                IntPtr afterWorker = Win32Interop.GetWindow(workerInProgman, 2);
                if (afterWorker != IntPtr.Zero)
                {
                    Win32Interop.SetWindowPos(
                        workerInProgman,
                        Win32Interop.HWND_BOTTOM,
                        0, 0, 0, 0,
                        Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOACTIVATE
                    );
                }
            }

            var mapped = new Win32Interop.RECT
            {
                Left = screenBounds.Left,
                Top = screenBounds.Top,
                Right = screenBounds.Right,
                Bottom = screenBounds.Bottom
            };
            Win32Interop.MapWindowPoints(IntPtr.Zero, parent, ref mapped, 2);

            bool swpOk = Win32Interop.SetWindowPos(
                hwnd,
                IntPtr.Zero,
                mapped.Left,
                mapped.Top,
                mapped.Width,
                mapped.Height,
                Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_NOZORDER | Win32Interop.SWP_FRAMECHANGED | Win32Interop.SWP_SHOWWINDOW
            );
            int swpErr = Marshal.GetLastWin32Error();
            IntPtr actualParent = Win32Interop.GetParent(hwnd);
            IntPtr nextAfterInsert = insertAfter != IntPtr.Zero ? Win32Interop.GetWindow(insertAfter, 2) : IntPtr.Zero;
            IntPtr workerNext = workerInProgman != IntPtr.Zero ? Win32Interop.GetWindow(workerInProgman, 2) : IntPtr.Zero;
            AppLogger.Info(string.Format(
                "AttachAsChild hwnd=0x{0:X} parent=0x{1:X} actualParent=0x{2:X} nextAfterShell=0x{3:X} workerNext=0x{4:X} rect=({5},{6},{7}x{8}) swp={9} err={10}",
                hwnd.ToInt64(), parent.ToInt64(), actualParent.ToInt64(), nextAfterInsert.ToInt64(), workerNext.ToInt64(),
                mapped.Left, mapped.Top, mapped.Width, mapped.Height, swpOk, swpErr
            ));
            LogChildHierarchy(parent);
        }

        private static void LogChildHierarchy(IntPtr parent)
        {
            if (parent == IntPtr.Zero) return;
            IntPtr child = Win32Interop.GetWindow(parent, 5 /* GW_CHILD */);
            int idx = 0;
            while (child != IntPtr.Zero && idx < 16)
            {
                string cls = Win32Interop.GetWindowClassName(child);
                bool vis = Win32Interop.IsWindowVisible(child);
                Win32Interop.RECT r;
                Win32Interop.GetWindowRect(child, out r);
                long st = Win32Interop.GetWindowLongPtr(child, Win32Interop.GWL_STYLE).ToInt64();
                long ex = Win32Interop.GetWindowLongPtr(child, Win32Interop.GWL_EXSTYLE).ToInt64();
                AppLogger.Info(string.Format(
                    "  ParentChild[{0}] hwnd=0x{1:X} cls='{2}' vis={3} rect=({4},{5},{6}x{7}) style=0x{8:X} ex=0x{9:X}",
                    idx, child.ToInt64(), cls, vis, r.Left, r.Top, r.Width, r.Height, st, ex
                ));
                child = Win32Interop.GetWindow(child, 2 /* GW_HWNDNEXT */);
                idx++;
            }
        }

        private static void ConfigureStandaloneOverlay(IntPtr hwnd, Rectangle screenBounds, byte opacity)
        {
            long exStyle = Win32Interop.GetWindowLongPtr(hwnd, Win32Interop.GWL_EXSTYLE).ToInt64();
            exStyle |= Win32Interop.WS_EX_TOOLWINDOW | Win32Interop.WS_EX_NOACTIVATE | Win32Interop.WS_EX_TRANSPARENT;
            if (opacity < 255) exStyle |= Win32Interop.WS_EX_LAYERED;
            Win32Interop.SetWindowLongPtr(hwnd, Win32Interop.GWL_EXSTYLE, new IntPtr(exStyle));
            if (opacity < 255)
            {
                Win32Interop.SetLayeredWindowAttributes(hwnd, 0, opacity, Win32Interop.LWA_ALPHA);
            }
            Win32Interop.SetWindowPos(
                hwnd,
                Win32Interop.HWND_BOTTOM,
                screenBounds.Left,
                screenBounds.Top,
                screenBounds.Width,
                screenBounds.Height,
                Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_FRAMECHANGED | Win32Interop.SWP_SHOWWINDOW
            );
        }
    }
}
