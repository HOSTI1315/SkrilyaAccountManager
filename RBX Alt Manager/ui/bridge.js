// Transport between the page and the manager. Nothing app-specific lives here — see app.js for that.
//
// Calls are promises:            const accounts = await RAM.call('accounts.list');
// Pushes are subscriptions:      RAM.on('launch.result', r => ...);
//
// A designer replacing index.html/styles.css never needs to touch this file.

const RAM = (() => {
  const pending = new Map();
  const listeners = new Map();
  let nextId = 1;

  const host = window.chrome && window.chrome.webview;

  if (!host) {
    // Opened in a plain browser (handy for styling work): calls resolve with nothing instead of throwing,
    // so the page still renders its empty state rather than a blank screen.
    console.warn('[RAM] no host bridge — running detached, calls will resolve empty');
  } else {
    host.addEventListener('message', e => {
      const message = e.data;

      if (!message) return;

      if (message.event) {
        (listeners.get(message.event) || []).forEach(fn => {
          try { fn(message.data); } catch (err) { console.error('[RAM] listener for ' + message.event + ' threw', err); }
        });
        return;
      }

      const waiter = pending.get(message.id);
      if (!waiter) return;

      pending.delete(message.id);
      message.ok ? waiter.resolve(message.result) : waiter.reject(new Error(message.error || 'call failed'));
    });
  }

  function call(method, params = {}, timeoutMs = 30000) {
    if (!host) return Promise.resolve(null);

    return new Promise((resolve, reject) => {
      const id = nextId++;
      pending.set(id, { resolve, reject });

      host.postMessage({ id, method, params });

      // Without this a lost reply would leave the UI spinning forever with no clue why.
      setTimeout(() => {
        if (!pending.has(id)) return;
        pending.delete(id);
        reject(new Error(method + ' timed out'));
      }, timeoutMs);
    });
  }

  function on(event, fn) {
    if (!listeners.has(event)) listeners.set(event, []);
    listeners.get(event).push(fn);
    return () => {
      const list = listeners.get(event) || [];
      const i = list.indexOf(fn);
      if (i >= 0) list.splice(i, 1);
    };
  }

  const log = (level, message) => call('app.log', { level, message }).catch(() => {});

  // Anything the page throws should end up in the app's log too, otherwise UI bugs are invisible.
  window.addEventListener('error', e => log('error', e.message + ' @ ' + e.filename + ':' + e.lineno));
  window.addEventListener('unhandledrejection', e => log('error', 'unhandled rejection: ' + (e.reason && e.reason.message || e.reason)));

  return { call, on, log, attached: !!host };
})();
