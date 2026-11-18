using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace DiffuseWindows
{
    internal sealed class TrayIconHost : IDisposable
    {
        private const uint TrayCallbackMessage = Win32Interop.WM_USER + 101;
        private const uint TrayIconId = 1;
        private const uint CmdOpen = 1001;
        private const uint CmdReattach = 1002;
        private const uint CmdPause = 1003;
        private const uint CmdQuit = 1004;

        private readonly Action _onLeftClick;
        private readonly Action _onReattach;
        private readonly Action _onTogglePause;
        private readonly Func<bool> _isPaused;
        private readonly Action _onQuit;
        private readonly Action _onDisplayChanged;

        private readonly Win32Interop.WndProcDelegate _wndProc;
        private readonly uint _taskbarCreatedMsg;
        private IntPtr _hwnd;
        private IntPtr _hIcon;
        private bool _added;
        private bool _disposed;

        public TrayIconHost(
            Action onLeftClick,
            Action onReattach,
            Action onTogglePause,
            Func<bool> isPaused,
            Action onQuit,
            Action onDisplayChanged)
        {
            _onLeftClick = onLeftClick;
            _onReattach = onReattach;
            _onTogglePause = onTogglePause;
            _isPaused = isPaused;
            _onQuit = onQuit;
            _onDisplayChanged = onDisplayChanged;
            _wndProc = WindowProc;
            _taskbarCreatedMsg = Win32Interop.RegisterWindowMessage("TaskbarCreated");

            try { Win32Interop.SetPreferredAppMode(1); Win32Interop.FlushMenuThemes(); } catch { }

            CreateMessageWindow();
            AddTrayIcon();
        }

        public Rectangle? TryGetIconRect()
        {
            var id = new Win32Interop.NOTIFYICONIDENTIFIER
            {
                cbSize = (uint)Marshal.SizeOf(typeof(Win32Interop.NOTIFYICONIDENTIFIER)),
                hWnd = _hwnd,
                uID = TrayIconId
            };
            Win32Interop.RECT r;
            if (Win32Interop.Shell_NotifyIconGetRect(ref id, out r) == 0)
            {
                return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            }
            return null;
        }

        private void CreateMessageWindow()
        {
            IntPtr hInstance = Win32Interop.GetModuleHandle(null);
            string clsName = "DiffuseTrayWindow_" + Guid.NewGuid().ToString("N");
            var wc = new Win32Interop.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf(typeof(Win32Interop.WNDCLASSEX)),
                lpfnWndProc = _wndProc,
                hInstance = hInstance,
                lpszClassName = clsName
            };
            ushort atom = Win32Interop.RegisterClassEx(ref wc);
            int regErr = Marshal.GetLastWin32Error();

            _hwnd = Win32Interop.CreateWindowEx(
                0,
                clsName,
                "DiffuseTrayHost",
                0,
                0, 0, 0, 0,
                IntPtr.Zero,
                IntPtr.Zero,
                hInstance,
                IntPtr.Zero
            );
            int createErr = Marshal.GetLastWin32Error();
            AppLogger.Info(string.Format("Tray window atom={0} regErr={1} hwnd=0x{2:X} createErr={3}", atom, regErr, _hwnd.ToInt64(), createErr));
        }

        private void AddTrayIcon()
        {
            if (_hIcon == IntPtr.Zero)
            {
                _hIcon = CreateGradientIcon();
            }
            var nid = new Win32Interop.NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf(typeof(Win32Interop.NOTIFYICONDATA)),
                hWnd = _hwnd,
                uID = TrayIconId,
                uFlags = Win32Interop.NIF_MESSAGE | Win32Interop.NIF_ICON | Win32Interop.NIF_TIP,
                uCallbackMessage = TrayCallbackMessage,
                hIcon = _hIcon,
                szTip = "Diffuse for Windows"
            };

            _added = Win32Interop.Shell_NotifyIcon(Win32Interop.NIM_ADD, ref nid);
            int addErr = Marshal.GetLastWin32Error();
            AppLogger.Info(string.Format("Shell_NotifyIcon NIM_ADD={0} err={1} hIcon=0x{2:X}", _added, addErr, _hIcon.ToInt64()));
        }

        private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (_taskbarCreatedMsg != 0 && msg == _taskbarCreatedMsg)
            {
                AppLogger.Info("TaskbarCreated received; re-registering tray icon and desktop host");
                AddTrayIcon();
                _onReattach();
                return IntPtr.Zero;
            }
            if (msg == TrayCallbackMessage)
            {
                uint ev = (uint)(lParam.ToInt64() & 0xFFFF);
                if (ev != 0x200)
                {
                    AppLogger.Info(string.Format("TrayCallback ev=0x{0:X}", ev));
                }
                if (ev == Win32Interop.WM_LBUTTONUP || ev == Win32Interop.NIN_SELECT || ev == Win32Interop.NIN_KEYSELECT)
                {
                    Win32Interop.SetForegroundWindow(_hwnd);
                    _onLeftClick();
                }
                else if (ev == Win32Interop.WM_RBUTTONUP || ev == Win32Interop.WM_CONTEXTMENU)
                {
                    ShowContextMenu();
                }
                return IntPtr.Zero;
            }

            if (msg == Win32Interop.WM_DISPLAYCHANGE)
            {
                AppLogger.Info("WM_DISPLAYCHANGE received");
                _onDisplayChanged();
            }

            return Win32Interop.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private void ShowContextMenu()
        {
            IntPtr menu = Win32Interop.CreatePopupMenu();
            if (menu == IntPtr.Zero) return;

            try
            {
                Win32Interop.AppendMenu(menu, Win32Interop.MF_STRING, new UIntPtr(CmdOpen), "Open Controls");
                Win32Interop.AppendMenu(menu, Win32Interop.MF_STRING, new UIntPtr(CmdReattach), "Re-attach to Desktop");
                Win32Interop.AppendMenu(menu, Win32Interop.MF_STRING, new UIntPtr(CmdPause), _isPaused() ? "Resume Wallpaper" : "Pause Wallpaper");
                Win32Interop.AppendMenu(menu, Win32Interop.MF_SEPARATOR, UIntPtr.Zero, null);
                Win32Interop.AppendMenu(menu, Win32Interop.MF_STRING, new UIntPtr(CmdQuit), "Quit Diffuse");

                Win32Interop.POINT pt;
                Win32Interop.GetCursorPos(out pt);
                Win32Interop.SetForegroundWindow(_hwnd);

                uint cmd = Win32Interop.TrackPopupMenuEx(
                    menu,
                    Win32Interop.TPM_RETURNCMD | Win32Interop.TPM_NONOTIFY | Win32Interop.TPM_RIGHTALIGN | Win32Interop.TPM_BOTTOMALIGN,
                    pt.X,
                    pt.Y,
                    _hwnd,
                    IntPtr.Zero
                );

                if (cmd == CmdOpen) _onLeftClick();
                else if (cmd == CmdReattach) _onReattach();
                else if (cmd == CmdPause) _onTogglePause();
                else if (cmd == CmdQuit) _onQuit();
            }
            finally
            {
                Win32Interop.DestroyMenu(menu);
            }
        }

        private static IntPtr CreateGradientIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var brush = new LinearGradientBrush(
                    new Rectangle(0, 0, 32, 32),
                    Color.FromArgb(95, 155, 255),
                    Color.FromArgb(225, 95, 165),
                    45f))
                {
                    g.FillEllipse(brush, 2, 2, 28, 28);
                }
                return bmp.GetHicon();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_added)
            {
                var nid = new Win32Interop.NOTIFYICONDATA
                {
                    cbSize = Marshal.SizeOf(typeof(Win32Interop.NOTIFYICONDATA)),
                    hWnd = _hwnd,
                    uID = TrayIconId
                };
                Win32Interop.Shell_NotifyIcon(Win32Interop.NIM_DELETE, ref nid);
                _added = false;
            }

            if (_hIcon != IntPtr.Zero)
            {
                Win32Interop.DestroyIcon(_hIcon);
                _hIcon = IntPtr.Zero;
            }

            if (_hwnd != IntPtr.Zero)
            {
                Win32Interop.DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
        }
    }
}
