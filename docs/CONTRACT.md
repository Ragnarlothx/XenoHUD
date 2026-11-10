# XenoHUD host ⇄ UI contract

Backend (Claude): `src\XenoHUD` — .NET 10 WinForms + WebView2. UI (Codex): `web\` — plain HTML/CSS/JS, served at `https://xenohud.local/index.html`.
Dev loop: edit `web\`, press F5 in the app (a Debug build reads the repo's `web\` directly). Open `web\index.html` in a normal browser to work against `js\mock.js` fake data. F12 = DevTools.

Use `js/host.js` (`XenoHost`) — don't call `chrome.webview` directly.

```js
const { hello, settings } = await XenoHost.ready(); // must be called once; starts the stream
XenoHost.on('snapshot', s => ...);
await XenoHost.send('setMode', { mode: 'hud' });   // resolves with data, rejects with Error(message)
```

## Pushed by host

| type | data |
|---|---|
| `snapshot` | every `host.sampleMs` (default 1 s), see below |
| `settings` | full settings object after any save/restore |
| `alert` | `{ key, title, text, severity: warning\|critical, t }` — threshold held for `sustainSamples` in a row, then cooldown. Also shown as a tray balloon when `host.alerts.tray` |
| `mode` | `{ mode: 'window'\|'hud'\|'desktop'\|'overlay', interactive, clickThrough }` — `interactive:false` in desktop mode (icons sit on top, no clicks reach us) and in click-through overlay; hide buttons then |

### Overlay mode
Small borderless always-on-top window (default 340×260 CSS px, top-right, 92% opacity). Render a dense **single-view** layout: every enabled metric visible at once, no scrolling, no sidebar.
Call `fitOverlay {width, height}` (CSS px) after layout so the window hugs the content. `startDrag` works here (when not click-through); dropping it saves `corner:'custom'` + x/y.
Hotkeys: `Ctrl+Alt+O` overlay on/off, `Ctrl+Alt+C` click-through on/off (click-through also available from the tray).

### snapshot
```
seq, t (unix ms), intervalMs
cpu:   { name, threads, total %, perCore[%], freqMHz|null, temp|null, powerW|null }
mem:   { totalMB, usedMB, pct, commitUsedMB, commitLimitMB }
gpus:  [{ name, vendor, load, memCtrlLoad, videoEngineLoad, temp, hotspot, memUsedMB, memTotalMB, powerW, coreMHz, memMHz, fanRpm, fanPct }]  any field may be null
disks: [{ name:'C:', label, format, type, totalGB, freeGB, readBps, writeBps, busy % }]
net:   { rxBps, txBps (physical adapters only), adapters:[{ name, description, type, isVirtual, speedMbps, ipv4[], rxBps, txBps, rxTotal, txTotal }] }
procs: { count, threads, list:[{ pid, name, cpu %|null, gpu %|null (busiest engine, like Task Manager), memMB, threads, title|null }] }   NULL on ticks between refreshes (host.procSampleMs) — keep the last one
sys:   { uptimeSec, isAdmin, battery:null|{ pct, charging, secondsLeft } }
sensors: [{ hw, hwType, type, name, value }]   raw LibreHardwareMonitor feed, for an "all sensors" panel
```
CPU temp/power are null unless the user opted in (`host.cpuSensors`), runs as admin, AND already has PawnIO installed. We never install drivers. Show "—", don't fake it.

## Requests (UI → host)

| type | payload | returns |
|---|---|---|
| `ready` | | `{ hello, settings }` — hello: `{ app, version, mode, info:{machine,user,os,osBuild,cpuName,logicalCores,cpuBaseMHz,isAdmin}, dataDir, backupsDir, hotkeyErrors[], cpuSensorsActive }` |
| `getSettings` / `getDefaults` | | settings |
| `saveSettings` | `{ data }` full object | settings (previous file auto-backed-up) |
| `resetSettings` | | defaults (current backed up first) |
| `listBackups` / `restoreBackup {name}` / `openBackups` | | |
| `setMode` | `{ mode }` | mode |
| `startResize` | `{ edge }` left/right/top/bottom/topleft/topright/bottomleft/bottomright | frameless window resize; call on mousedown of an edge grip |
| `getHistory` | `{ seconds = 300, maxPoints = 300 }` | `{ seconds, bucketSamples, points:[{ t, cpu, cpuTemp, mem, gpuLoad[], gpuTemp[], netRx, netTx, diskBusy }] }` from a 3600-sample in-memory ring (1 h at 1 s). Use on load so graphs start full, and for 5m/15m/1h zoom |
| `testAlert` | | fires a sample `alert` (and tray balloon) |
| `fitOverlay` | `{ width, height }` CSS px | resizes overlay (clamped 120–1600 × 60–1200) |
| `setClickThrough` | `{ on }` | runtime only; persistent default is `host.overlay.clickThrough` |
| `getMode`, `minimize`, `toggleMaximize`, `setTopmost {on}`, `startDrag`, `exit` | | window-mode only where it makes sense. `startDrag` on mousedown lets a frameless window be dragged |
| `killProcess` | `{ pid }` | `{ killed }` — host shows its own native Yes/No; core Windows processes are refused |
| `processDetails` | `{ pid }` | `{ pid, name, path, started, priority, threads, handles, memMB, privateMB, title }` |
| `netConnections` | | `{ tcp:[{local,remote,state}], listeners[] }` |
| `openPath` | `{ path }` | opens folder / selects file in Explorer |
| `launch` | `{ path, args? }` | runs an existing file (for user-configured launcher buttons) |
| `openUrl` | `{ url }` | http/https only, opens default browser |
| `relaunchAdmin` | | UAC prompt; restarts elevated in the same mode |
| `notify` | `{ text, title? }` | tray balloon |

## Settings

`settings.json` in `%LOCALAPPDATA%\XenoHUD`, shape `{ host:{...}, ui:{...} }`.
`host` is read by the backend (keys below). **`ui` is entirely the UI's** — themes, widget layout, colors, effects, launcher buttons, whatever. Host merges saved values over defaults, so add keys freely.

```
host.startMode       window|hud|desktop
host.sampleMs        250..10000
host.procSampleMs    500..30000
host.cpuSensors      bool (applies immediately, restarts sensor thread)
host.desktopMonitor  primary|all
host.topmost         bool (window mode)
host.frameless       bool (window mode; use startDrag for a custom title bar)
host.hotkeys         { hud, desktop, window, overlay, clickThrough }  e.g. "Ctrl+Alt+H"; failures listed in hello.hotkeyErrors
host.alerts          { enabled, tray, cpuTempC 90, gpuTempC 85, ramPct 92, diskFreeGB 10, sustainSamples 5, cooldownMin 10 }
host.overlay         { corner: tl|tr|bl|br|custom, monitor: primary|cursor, margin, width, height, opacity 0.2..1, clickThrough, x, y }
                     saving settings while in overlay re-places it live
```

## Safety rules (keep these)
- Nothing touches Windows settings, wallpaper files or the registry (we only *read* the CPU name from it).
- Desktop mode is a runtime window re-parent; exit / tray "Restore normal desktop" undoes it.
- Every settings write backs up the old file to `backups\` (last 30 kept).
