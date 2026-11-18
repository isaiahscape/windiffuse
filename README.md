# Diffuse for Windows

Native Windows 11 live wallpaper port of **Diffuse** featuring a **WinUI 3** floating system-tray flyout UI and an OpenGL desktop wallpaper engine that sits behind desktop icons on top of your static Windows wallpaper (the same `Progman` / `WorkerW` technique used by Lively Wallpaper).

## Features

- **WinUI 3 Floating Tray Flyout:**
  - Floats cleanly in the system tray (left-click tray icon to toggle the Desktop Acrylic flyout above the taskbar; auto-hides when clicking away unless pinned).
  - Right-click tray icon for quick actions (`Open Controls`, `Re-attach to Desktop`, `Pause / Resume Wallpaper`, `Quit Diffuse`).
- **Desktop Layering (`Lively`-style):**
  - Supports **Windows 11 24H2 / 25H2 / 26H2** raised-desktop `Progman` parenting (`WS_EX_LAYERED` child inserted directly behind `SHELLDLL_DefView`) and **Windows 10 / Windows 11 `<24H2`** classic `WorkerW` parenting.
  - Configurable **Wallpaper Opacity** (`10%–100%`), **Span all monitors**, and **Pause on fullscreen app**.
- **Exact Diffuse 4-Pass OpenGL Pipeline:**
  - Uses the same GLSL shaders (`color`, `kawaseBlur`, `fade`, `noise`, `flutedGlass`) and `rand.png` LUT as Diffuse for Android on a dedicated per-display GL thread with `GL_RGBA16F` + `GL_MIRRORED_REPEAT` and configurable **FPS limit (`10–240`) / VSync**.
- **Windows Now Playing (SMTC) & Live Beats (WASAPI):**
  - Reads album art and playback state from Cider, Spotify, Apple Music, YouTube Music, VLC, etc., plus optional 4-band WASAPI loopback FFT beat reactivity.

## Build & Run

```powershell
cd diffuse-windows
.\build.ps1
.\run.ps1
```

Options:
- `.\run.ps1 -ShowUi` — Open the floating WinUI 3 flyout immediately on launch.
- `.\run.ps1 -Preview` — Run in a preview window instead of attaching behind desktop icons.

Settings are stored in `%AppData%\DiffuseWindows\settings.json`.
