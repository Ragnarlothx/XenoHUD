# XenoHUD

A system dashboard for Windows by Xeno: CPU, memory, GPU, disks, network and processes, shown as
a normal window, a heads-up overlay, a desktop layer, or a compact bar.

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
