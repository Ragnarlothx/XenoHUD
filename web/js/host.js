// XenoHUD host bridge. Same API inside the app (WebView2) and in a plain browser (mock data).
//   XenoHost.on('snapshot', snap => ...)      // also: 'settings', 'mode', '*'
//   await XenoHost.send('setMode', { mode: 'hud' })
//   const { hello, settings } = await XenoHost.ready()
// Full contract: docs/CONTRACT.md
window.XenoHost = (() => {
  const inApp = !!(window.chrome && window.chrome.webview);
  const listeners = {};
  const pending = new Map();
  let nextId = 1;

  function on(type, fn) {
    (listeners[type] ||= []).push(fn);
    return () => (listeners[type] = listeners[type].filter(f => f !== fn));
  }

  function emit(msg) {
    for (const fn of listeners[msg.type] || []) fn(msg.data, msg);
    for (const fn of listeners['*'] || []) fn(msg);
  }

  function receive(msg) {
    if (msg.type === 'result') {
      const p = pending.get(msg.id);
      if (!p) return;
      pending.delete(msg.id);
      msg.ok ? p.resolve(msg.data) : p.reject(new Error(msg.error || 'host error'));
      return;
    }
    emit(msg);
  }

  function send(type, payload = {}) {
    const id = nextId++;
    const msg = { type, id, ...payload };
    return new Promise((resolve, reject) => {
      pending.set(id, { resolve, reject });
      if (inApp) window.chrome.webview.postMessage(msg);
      else if (window.XenoMock) window.XenoMock.handle(msg, receive);
      else reject(new Error('no host and no mock loaded'));
    });
  }

  if (inApp) window.chrome.webview.addEventListener('message', e => receive(e.data));

  return { inApp, on, send, ready: () => send('ready') };
})();
