// Fake host for working on the UI in a normal browser. Inside the app this file does nothing.
// Snapshot shapes match docs/CONTRACT.md.
window.XenoMock = (() => {
  if (window.chrome && window.chrome.webview) return null;

  const threads = 16;
  let seq = 0, mode = 'window', started = false;
  let settings = {
    host: { startMode: 'window', sampleMs: 1000, procSampleMs: 2000, cpuSensors: false, desktopMonitor: 'primary',
            topmost: false, frameless: true, overlay:{corner:'tr',monitor:'primary',margin:16,width:340,height:260,opacity:.92,clickThrough:false}, hotkeys: { overlay:'Ctrl+Alt+O',clickThrough:'Ctrl+Alt+C',hud: 'Ctrl+Alt+H', desktop: 'Ctrl+Alt+D', window: 'Ctrl+Alt+W' } },
    ui: {},
  };
  const names = ['chrome', 'Code', 'XenoStudio', 'node', 'dotnet', 'Godot', 'Discord', 'Spotify', 'explorer', 'svchost', 'dwm', 'codex', 'claude'];
  let procs = names.map((n, i) => ({ pid: 1000 + i * 37, name: n, cpu: 0, memMB: 50 + Math.random() * 900, threads: 8 + i * 3, title: null }));
  const wobble = (v, amt, lo, hi) => Math.min(hi, Math.max(lo, v + (Math.random() - 0.5) * amt));
  let cpu = 20, gpu = 30, rx = 2e5, tx = 4e4, rxTotal = 0, txTotal = 0;
  let cores = Array.from({ length: threads }, () => Math.random() * 40);

  function snapshot() {
    cpu = wobble(cpu, 12, 2, 98);
    gpu = wobble(gpu, 15, 0, 100);
    rx = wobble(rx, 4e5, 0, 5e6); tx = wobble(tx, 1e5, 0, 1e6);
    rxTotal += rx; txTotal += tx;
    cores = cores.map(c => wobble(c, 25, 0, 100));
    const withProcs = seq % 2 === 0;
    if (withProcs) procs = procs.map(p => ({ ...p, cpu: +wobble(p.cpu, 6, 0, 40).toFixed(1), memMB: +wobble(p.memMB, 20, 10, 4000).toFixed(1) }));
    return {
      seq: ++seq, t: Date.now(), intervalMs: settings.host.sampleMs,
      cpu: { name: 'Mock CPU 9000X', threads, total: +cpu.toFixed(1), perCore: cores.map(c => +c.toFixed(1)),
             freqMHz: Math.round(3600 + cpu * 15), temp: null, powerW: null },
      mem: { totalMB: 32768, usedMB: 14000 + Math.round(cpu * 40), pct: 45, commitUsedMB: 20000, commitLimitMB: 40000 },
      gpus: [{ name: 'Mock GPU (preview data)', vendor: 'Nvidia', load: +gpu.toFixed(1), memCtrlLoad: 20, videoEngineLoad: 0,
               temp: Math.round(40 + gpu * 0.35), hotspot: Math.round(50 + gpu * 0.4), memUsedMB: 3200, memTotalMB: 10240,
               powerW: Math.round(40 + gpu * 2.8), coreMHz: 1700, memMHz: 9500, fanRpm: 1200, fanPct: 40 }],
      disks: [{ name: 'C:', label: 'System', format: 'NTFS', type: 'Fixed', totalGB: 931, freeGB: 220, readBps: rx * 3, writeBps: tx * 5, busy: +(cpu / 3).toFixed(1) },
              { name: 'F:', label: 'Work', format: 'NTFS', type: 'Fixed', totalGB: 1863, freeGB: 900, readBps: 0, writeBps: 0, busy: 1 }],
      net: { rxBps: Math.round(rx), txBps: Math.round(tx), adapters: [{ name: 'Ethernet', description: 'Mock NIC', type: 'Ethernet', speedMbps: 1000,
             ipv4: ['192.0.2.42'], rxBps: Math.round(rx), txBps: Math.round(tx), rxTotal, txTotal }] },
      procs: withProcs ? { count: procs.length, threads: procs.reduce((a, p) => a + p.threads, 0), list: procs } : null,
      sys: { uptimeSec: Math.round(performance.now() / 1000) + 86400, isAdmin: false, battery: null },
      sensors: [{ hw: 'Mock GPU', hwType: 'GpuNvidia', type: 'Temperature', name: 'GPU Core', value: Math.round(40 + gpu * 0.35) }],
    };
  }

  function handle(msg, reply) {
    const ok = data => setTimeout(() => reply({ type: 'result', id: msg.id, ok: true, data }), 10);
    const fail = error => setTimeout(() => reply({ type: 'result', id: msg.id, ok: false, error }), 10);
    const push = (type, data) => setTimeout(() => reply({ type, data }), 15);
    switch (msg.type) {
      case 'ready':
        if (!started) { started = true; const tick = () => { push('snapshot', snapshot()); setTimeout(tick, settings.host.sampleMs); }; tick(); }
        return ok({ hello: { app: 'XenoHUD', version: 'mock', mode, dataDir: '(mock)', backupsDir: '(mock)', hotkeyErrors: [], cpuSensorsActive: false,
                             info: { machine: 'XENO-PC', user: 'xeno', os: 'Windows 11 (mock)', osBuild: '10.0.26100', cpuName: 'Mock CPU 9000X',
                                     logicalCores: threads, cpuBaseMHz: 3600, isAdmin: false } }, settings });
      case 'getSettings': return ok(settings);
      case 'saveSettings': settings = msg.data; push('settings', settings); return ok(settings);
      case 'setMode': mode = msg.mode; push('mode', { mode, interactive: mode !== 'desktop' && !(mode==='overlay'&&settings.host.overlay?.clickThrough) }); return ok(mode);
      case 'fitOverlay': window.__overlayFit={width:msg.width,height:msg.height};return ok(window.__overlayFit);
      case 'setClickThrough': push('mode',{mode,interactive:!msg.on,clickThrough:msg.on});return ok({on:msg.on});
      case 'killProcess':
        if (!confirm(`(mock) Kill PID ${msg.pid}?`)) return ok({ killed: false });
        procs = procs.filter(p => p.pid !== msg.pid); return ok({ killed: true, pid: msg.pid });
      case 'processDetails': { const p = procs.find(p => p.pid === msg.pid); return p ? ok({ ...p, path: `C:\\Mock\\${p.name}.exe`, started: new Date().toISOString(), priority: 'Normal', handles: 400, privateMB: p.memMB }) : fail('no such process'); }
      case 'netConnections': return ok({ tcp: [{ local: '192.0.2.42:51234', remote: '198.51.100.3:443', state: 'Established' }], listeners: ['0.0.0.0:135', '0.0.0.0:445'] });
      case 'listBackups': return ok(['settings-20261101-230000-000.json']);
      default: return ok(null);
    }
  }

  return { handle };
})();
