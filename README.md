# XenoHUD

A system dashboard for Windows by Xeno: CPU, memory, GPU, disks, network and processes, shown as
a normal window, a heads-up overlay, a live wallpaper behind the desktop icons, or a compact bar.

## Download

Get the latest `XenoHUD-*-win-x64.zip` from [Releases](https://github.com/Ragnarlothx/XenoHUD/releases/latest),
extract it, and run `XenoHUD.exe`. .NET is included; nothing needs to be installed first.

## Display modes

| Mode | Hotkey | |
|---|---|---|
| Window | `Ctrl+Alt+W` | Normal resizable window with movable, resizable panels |
| Full HUD | `Ctrl+Alt+H` | Full screen, always on top |
| Live Wallpaper | `Ctrl+Alt+D` | Behind the desktop icons, look-only |
| Compact overlay | `Ctrl+Alt+O` | Small always-on-top bar; `Ctrl+Alt+C` toggles click-through |

## Live Wallpaper options

**Settings → Live Wallpaper** has its own options, separate from the window:

- **Monitor & speed:** primary monitor, all monitors or one specific monitor; update intervals; slows to a
  5-second update and stops all animation while a maximized or full-screen app hides the wallpaper.
- **Layout:** overall size, columns, box width and height, spacing, margins on each side (room for desktop
  icons and the taskbar), maximum width, horizontal and vertical position.
- **Boxes:** background opacity (0 = only the text and graphs show), color, border, corner rounding, padding,
  frosted-glass blur, shadow, titles, and which boxes appear.
- **Text:** size, big-number size, font, weight, color, opacity, and a shadow for readability over pictures.
- **Graphs:** show or hide, height, line thickness, fill under the line, color, glow.
- **Header:** title, machine details, big clock and its size, top bar, status line.
- **Background:** theme, solid color, or your own picture (fit, darken, blur); grid lines.
- **Effects:** matrix rain (opacity, speed, density, character size, color), scanlines, vignette.

The wallpaper can't be clicked, so adjust it from the window. **Preview** shows the look there while you change
options; layout options apply on the desktop.

## Build

```
build.cmd
```

Produces `dist\XenoHUD\XenoHUD.exe` (self-contained, no .NET install needed). Requires the
.NET 10 SDK to build and the WebView2 runtime (built into Windows 11) to run.

Sensors come from [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor);
CPU sensors are read only if the PawnIO driver is already installed (XenoHUD never installs drivers).
Settings live in `%LOCALAPPDATA%\XenoHUD`. The UI contract between host and web UI is in `docs/CONTRACT.md`.

## Privacy

The dashboard reads the running computer's username, machine name, hardware, local IP addresses,
and process details for local display. Diagnostic snapshots and exports can contain these values.
Review them before sharing. Preview mode uses synthetic data and documentation IP addresses.

Screenshots, local settings, diagnostic snapshots, backups, and build output are excluded from
this source repository. Screen captures have been removed from the project copy prepared for publishing.
