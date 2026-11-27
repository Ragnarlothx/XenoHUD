// Live Wallpaper options (Settings → Live Wallpaper). Everything here is look-and-feel for the desktop layer:
// it sets CSS variables and body classes; wallpaper.css does the rest. app.js calls the hooks marked "hook".
// Options live in settings.ui.wp; host-side rates live in settings.host.wallpaper.
'use strict';
window.XenoWallpaper = (() => {
  const defaults = {
    preview: false,
    // layout
    scale: 1, columns: 'auto', boxMinWidth: 300, boxHeight: 0, gap: 12,
    marginTop: 16, marginRight: 16, marginBottom: 56, marginLeft: 16, maxWidth: 0, hAlign: 'center', vAlign: 'stretch',
    // boxes
    boxOpacity: 0.85, boxColor: '#1e1f22', border: true, borderOpacity: 0.6, radius: 6, blur: 0, shadow: false, titles: true, padding: 12,
    // text
    textScale: 1, metricScale: 1, textColorMode: 'theme', textColor: '#e6e8ec', textOpacity: 1, textShadow: 0.6, font: 'mono', weight: 500,
    // graphs
    graphs: true, graphHeight: 60, graphWidth: 2, graphFill: false, fillOpacity: 0.18, graphColorMode: 'theme', graphColor: '#4f9cf9', graphGlow: 0.3,
    // header
    header: true, headerTitle: true, machine: true, bigClock: true, clockScale: 1, menubar: true, footer: false,
    // background
    bgMode: 'theme', bgColor: '#0b0c0e', bgImage: '', bgFit: 'cover', bgDim: 0.35, bgBlur: 0, grid: true, gridOpacity: 0.13,
    // effects
    rain: true, rainOpacity: 0.42, rainFps: 15, rainDensity: 1, rainSize: 12, rainColorMode: 'theme', rainColor: '#4f9cf9', scanlines: false, vignette: false,
    // which boxes show on the wallpaper (independent of the window layout)
    show: { cpu: true, memory: true, gpu: true, network: true, disks: true, processes: true, feed: true, sensors: true },
  };
  const BOXES = { cpu: 'Processor', memory: 'Memory', gpu: 'Graphics', network: 'Network', disks: 'Storage', processes: 'Processes', feed: 'Journal', sensors: 'Sensors' };

  let monitors = [];
  const labels = {
    'host.desktopMonitor': { primary: 'Primary monitor', all: 'Span all monitors' },
    'ui.wp.columns': { auto: 'Automatic (fit)' },
    'ui.wp.bgMode': { theme: 'Theme background', color: 'Solid color', image: 'Picture', none: 'Black' },
    'ui.wp.textColorMode': { theme: 'Theme colors', custom: 'Custom color' },
    'ui.wp.graphColorMode': { theme: 'Theme accent', custom: 'Custom color' },
    'ui.wp.rainColorMode': { theme: 'Theme accent', custom: 'Custom color' },
    'ui.wp.font': { mono: 'Monospace', ui: 'Segoe UI', condensed: 'Bahnschrift (condensed)' },
  };
  const monitorChoices = () => ['primary', 'all', ...monitors.map(m => m.id)].join(',');

  // [path, label, type, min/options, max, step] — same format as app.js's schema
  const fields = () => [
    ['ui.wp.preview', 'Preview these looks in this window too', 'checkbox'],
    ['§', 'Monitor & speed'],
    ['host.desktopMonitor', 'Show on', 'select', monitorChoices()],
    ['host.wallpaper.sampleMs', 'Update every (ms)', 'number', 250, 10000, 250],
    ['host.wallpaper.procSampleMs', 'Process list every (ms)', 'number', 500, 30000, 500],
    ['host.wallpaper.pauseWhenCovered', 'Slow down while a maximized or full-screen app covers it', 'checkbox'],
    ['host.wallpaper.coveredSampleMs', 'Update every (ms) while covered', 'number', 1000, 60000, 500],
    ['§', 'Layout'],
    ['ui.wp.scale', 'Overall size', 'range', 0.5, 2.5, 0.05],
    ['ui.wp.columns', 'Columns', 'select', 'auto,1,2,3,4,5,6'],
    ['ui.wp.boxMinWidth', 'Box width when automatic (px)', 'number', 120, 900, 10],
    ['ui.wp.boxHeight', 'Box height (px, 0 = fill the screen)', 'number', 0, 1200, 10],
    ['ui.wp.gap', 'Space between boxes (px)', 'number', 0, 120, 1],
    ['ui.wp.marginTop', 'Margin top (px)', 'number', 0, 600, 2],
    ['ui.wp.marginRight', 'Margin right (px)', 'number', 0, 1200, 2],
    ['ui.wp.marginBottom', 'Margin bottom (px, room for the taskbar)', 'number', 0, 600, 2],
    ['ui.wp.marginLeft', 'Margin left (px, room for desktop icons)', 'number', 0, 1200, 2],
    ['ui.wp.maxWidth', 'Maximum width (px, 0 = full)', 'number', 0, 8000, 10],
    ['ui.wp.hAlign', 'Horizontal position', 'select', 'left,center,right'],
    ['ui.wp.vAlign', 'Vertical position', 'select', 'stretch,top,center,bottom'],
    ['§', 'Boxes'],
    ['ui.wp.boxOpacity', 'Box background (0 = see-through, only text and graphs)', 'range', 0, 1, 0.01],
    ['ui.wp.boxColor', 'Box color', 'color'],
    ['ui.wp.border', 'Border', 'checkbox'],
    ['ui.wp.borderOpacity', 'Border strength', 'range', 0, 1, 0.05],
    ['ui.wp.radius', 'Corner rounding (px)', 'number', 0, 40, 1],
    ['ui.wp.padding', 'Inner padding (px)', 'number', 0, 48, 1],
    ['ui.wp.blur', 'Frosted glass blur (px, uses the GPU)', 'number', 0, 30, 1],
    ['ui.wp.shadow', 'Drop shadow', 'checkbox'],
    ['ui.wp.titles', 'Box titles', 'checkbox'],
    ...Object.entries(BOXES).map(([k, v]) => [`ui.wp.show.${k}`, `Show ${v}`, 'checkbox']),
    ['§', 'Text'],
    ['ui.wp.textScale', 'Text size', 'range', 0.5, 3, 0.05],
    ['ui.wp.metricScale', 'Big numbers size', 'range', 0.4, 4, 0.05],
    ['ui.wp.font', 'Font', 'select', 'mono,ui,condensed'],
    ['ui.wp.weight', 'Weight', 'range', 300, 800, 100],
    ['ui.wp.textColorMode', 'Text color', 'select', 'theme,custom'],
    ['ui.wp.textColor', 'Custom text color', 'color'],
    ['ui.wp.textOpacity', 'Text opacity', 'range', 0.2, 1, 0.05],
    ['ui.wp.textShadow', 'Text shadow (readability on pictures)', 'range', 0, 1, 0.05],
    ['§', 'Graphs'],
    ['ui.wp.graphs', 'Show graphs', 'checkbox'],
    ['ui.wp.graphHeight', 'Graph height (px)', 'number', 16, 500, 2],
    ['ui.wp.graphWidth', 'Line thickness', 'range', 1, 8, 0.5],
    ['ui.wp.graphFill', 'Fill under the line', 'checkbox'],
    ['ui.wp.fillOpacity', 'Fill strength', 'range', 0.02, 0.8, 0.02],
    ['ui.wp.graphColorMode', 'Graph color', 'select', 'theme,custom'],
    ['ui.wp.graphColor', 'Custom graph color', 'color'],
    ['ui.wp.graphGlow', 'Glow', 'range', 0, 1, 0.05],
    ['§', 'Header'],
    ['ui.wp.header', 'Show header', 'checkbox'],
    ['ui.wp.headerTitle', 'Title ("System overview")', 'checkbox'],
    ['ui.wp.machine', 'Machine details', 'checkbox'],
    ['ui.wp.bigClock', 'Big clock', 'checkbox'],
    ['ui.wp.clockScale', 'Clock size', 'range', 0.5, 4, 0.05],
    ['ui.wp.menubar', 'Top bar', 'checkbox'],
    ['ui.wp.footer', 'Status line at the bottom', 'checkbox'],
    ['§', 'Background'],
    ['ui.wp.bgMode', 'Background', 'select', 'theme,color,image,none'],
    ['ui.wp.bgColor', 'Background color', 'color'],
    ['ui.wp.bgImage', 'Picture file', 'text'],
    ['ui.wp.bgFit', 'Picture fit', 'select', 'cover,contain,center,stretch,tile'],
    ['ui.wp.bgDim', 'Darken picture', 'range', 0, 0.95, 0.01],
    ['ui.wp.bgBlur', 'Blur picture (px)', 'number', 0, 40, 1],
    ['ui.wp.grid', 'Grid lines', 'checkbox'],
    ['ui.wp.gridOpacity', 'Grid strength', 'range', 0, 0.6, 0.01],
    ['§', 'Matrix rain & effects'],
    ['ui.wp.rain', 'Matrix rain', 'checkbox'],
    ['ui.wp.rainOpacity', 'Rain opacity', 'range', 0, 1, 0.01],
    ['ui.wp.rainFps', 'Rain speed (frames per second)', 'number', 2, 60, 1],
    ['ui.wp.rainDensity', 'Rain density', 'range', 0.25, 3, 0.05],
    ['ui.wp.rainSize', 'Rain character size (px)', 'number', 8, 40, 1],
    ['ui.wp.rainColorMode', 'Rain color', 'select', 'theme,custom'],
    ['ui.wp.rainColor', 'Custom rain color', 'color'],
    ['ui.wp.scanlines', 'CRT scanlines', 'checkbox'],
    ['ui.wp.vignette', 'Edge vignette', 'checkbox'],
  ];

  let ctx = null;          // { ui(), mode(), send(), apply(), settingsPage() } from app.js
  let covered = false;
  let imageKey = '', imageUrl = '';
  const root = document.documentElement.style, body = document.body;

  /** hook: app.js apply() / setMode(). Merges defaults into ui.wp and pushes everything into CSS. */
  function apply(ui, mode) {
    ui.wp = Object.assign({}, defaults, ui.wp || {});
    ui.wp.show = Object.assign({}, defaults.show, ui.wp.show || {});
    const w = ui.wp;
    const on = mode === 'desktop' || (w.preview && mode !== 'overlay');
    body.classList.toggle('wp', on);
    body.classList.toggle('wp-layout', mode === 'desktop');
    if (!on) return;
    const px = v => (Number(v) || 0) + 'px';
    const vars = {
      '--wp-scale': w.scale, '--wp-gap': px(w.gap), '--wp-min': px(w.boxMinWidth), '--wp-h': w.boxHeight > 0 ? px(w.boxHeight) : 'minmax(0,1fr)',
      '--wp-mt': px(w.marginTop), '--wp-mr': px(w.marginRight), '--wp-mb': px(w.marginBottom), '--wp-ml': px(w.marginLeft),
      '--wp-maxw': w.maxWidth > 0 ? px(w.maxWidth) : 'none',
      '--wp-box': w.boxColor, '--wp-box-a': Math.round(w.boxOpacity * 100) + '%', '--wp-border-a': w.border ? Math.round(w.borderOpacity * 100) + '%' : '0%',
      '--wp-radius': px(w.radius), '--wp-pad': px(w.padding), '--wp-blur': px(w.blur),
      '--wp-text': w.textScale, '--wp-metric': w.metricScale, '--wp-weight': w.weight, '--wp-text-a': w.textOpacity,
      '--wp-text-color': w.textColorMode === 'custom' ? w.textColor : 'var(--fg, #e6e8ec)',
      '--wp-shadow-a': w.textShadow,
      '--wp-graph-h': px(w.graphHeight), '--wp-graph-w': w.graphWidth, '--wp-fill-a': w.fillOpacity, '--wp-glow': px(w.graphGlow * 8),
      '--wp-graph': w.graphColorMode === 'custom' ? w.graphColor : 'var(--accent)',
      '--wp-clock': w.clockScale, '--wp-bg': w.bgMode === 'color' ? w.bgColor : w.bgMode === 'none' ? '#000' : 'var(--bg)',
      '--wp-dim': w.bgDim, '--wp-bg-blur': px(w.bgBlur), '--wp-grid-a': w.gridOpacity, '--wp-rain-a': w.rainOpacity,
    };
    for (const [k, v] of Object.entries(vars)) root.setProperty(k, String(v));
    root.setProperty('--wp-cols', w.columns === 'auto' ? 'repeat(auto-fit, minmax(min(100%, var(--wp-min)), 1fr))' : `repeat(${w.columns}, minmax(0, 1fr))`);
    const flags = {
      'wp-no-titles': !w.titles, 'wp-no-graphs': !w.graphs, 'wp-fill': w.graphFill, 'wp-shadow': w.shadow, 'wp-no-header': !w.header,
      'wp-no-title': !w.headerTitle, 'wp-no-machine': !w.machine, 'wp-no-clock': !w.bigClock, 'wp-no-menubar': !w.menubar, 'wp-footer': w.footer,
      'wp-no-grid': !w.grid, 'wp-vignette': w.vignette, 'wp-blur': w.blur > 0, 'wp-image': w.bgMode === 'image' && !!w.bgImage,
      'wp-font-ui': w.font === 'ui', 'wp-font-cond': w.font === 'condensed', 'wp-fixed-h': w.boxHeight > 0,
    };
    for (const [k, v] of Object.entries(flags)) body.classList.toggle(k, !!v);
    for (const k of Object.keys(BOXES)) body.classList.toggle('wp-hide-' + k, !w.show[k]);
    body.dataset.wpH = w.hAlign; body.dataset.wpV = w.vAlign; body.dataset.wpFit = w.bgFit;
    loadImage(w);
  }

  /** The picture is read by the host once per file (the page can't open local files). */
  async function loadImage(w) {
    const layer = document.getElementById('wpBackdrop');
    if (!layer) return;
    if (w.bgMode !== 'image' || !w.bgImage) { layer.style.backgroundImage = ''; return; }
    if (imageKey === w.bgImage) { layer.style.backgroundImage = imageUrl ? `url("${imageUrl}")` : ''; return; }
    imageKey = w.bgImage; imageUrl = '';
    try { imageUrl = (await ctx.send('readImage', { path: w.bgImage })).url; layer.style.backgroundImage = `url("${imageUrl}")`; }
    catch (e) { layer.style.backgroundImage = ''; ctx.toast?.('Picture: ' + e.message); }
  }

  /** hook: drawRain() — size, column spacing, frame interval and color for the matrix rain. */
  function rain(ui, mode, accent) {
    const w = ui.wp || defaults;
    if (mode !== 'desktop') return { size: 12, spacing: 22, interval: Math.max(33, ui.rainSpeed), color: accent };
    const size = Math.max(8, Math.min(40, w.rainSize));
    return { size, spacing: Math.max(6, size * 1.8 / Math.max(0.25, w.rainDensity)), interval: 1000 / Math.max(2, Math.min(60, w.rainFps)), color: w.rainColorMode === 'custom' ? w.rainColor : accent };
  }
  /** hook: effectEnabled() — the wallpaper has its own rain / scanline switches; nothing animates while covered. */
  function effect(ui, mode, key) {
    if (mode !== 'desktop') return null;
    const w = ui.wp || defaults;
    if (covered) return false;
    if (key === 'rain') return !!w.rain;
    if (key === 'scanlines') return !!w.scanlines;
    return null;
  }

  /** hook: settingsPage() for the Live Wallpaper tab. */
  function page(field) {
    let html = '<p>These options change only the Live Wallpaper. Turn on <b>Preview</b> to see the look in this window while you adjust (layout options apply on the desktop).</p>';
    for (const f of fields()) html += f[0] === '§' ? `<h3 class="wp-sec">${f[1]}</h3>` : field(f);
    html += '<div class="wp-actions"><button type="button" id="wpPickImage">Choose picture…</button><button type="button" id="wpReset">Reset wallpaper options</button><button type="button" data-mode="desktop">Show Live Wallpaper</button></div>';
    return html;
  }
  const optionLabel = (path, value) => (path === 'host.desktopMonitor' && value.startsWith('monitor:'))
    ? (() => { const m = monitors.find(x => x.id === value); return m ? `Monitor ${m.index + 1}: ${m.width}×${m.height} at ${m.scale}%${m.primary ? ' (primary)' : ''}` : value; })()
    : (labels[path]?.[value] ?? value);

  function bind(c) {
    ctx = c;
    const setCovered = v => { v = !!v; if (v === covered) return; covered = v; body.classList.toggle('wp-covered', covered); c.covered(covered); };
    // the host reports changes, includes the state with every mode change, and is asked once at start (an early report can arrive before the page loads)
    c.on('wallpaperState', s => setCovered(s.covered));
    c.on('mode', d => setCovered(d.covered));
    c.send('wallpaperState').then(s => setCovered(s && s.covered)).catch(() => { });
    c.send('listMonitors').then(list => { monitors = list || []; }).catch(() => { });
    document.addEventListener('click', async e => {
      const b = e.target.closest('button'); if (!b) return;
      if (b.id === 'wpPickImage') {
        const p = await c.send('pickImage').catch(() => null);
        if (!p) return;
        const u = c.ui(); u.wp = Object.assign({}, defaults, u.wp, { bgImage: p, bgMode: 'image' });
        c.commit(); c.settingsPage();
      }
      if (b.id === 'wpReset') {
        const u = c.ui(); u.wp = JSON.parse(JSON.stringify(defaults)); imageKey = '';
        c.commit(); c.settingsPage();
      }
    });
  }

  return { defaults, apply, rain, effect, page, bind, optionLabel, get covered() { return covered; } };
})();
