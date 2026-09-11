/**
 * Light Recorder Bridge - service worker.
 *
 * Talks to the desktop app over a token-authenticated WebSocket on loopback.
 * Its job is the part Windows cannot do: enumerate tabs, mute them one by one,
 * and capture a single tab (video *and* only that tab's audio).
 *
 * MV3 evicts idle service workers, so the app pings every 20s and an alarm
 * re-arms the connection if it is ever torn down anyway.
 */

const DEFAULTS = { port: 8787, token: '' };

let socket = null;
let connected = false;
let retryDelay = 1000;
let retryTimer = null;
let capturingTabId = null;

// --------------------------------------------------------------- connection

async function config() {
  const stored = await chrome.storage.local.get(DEFAULTS);
  return { port: Number(stored.port) || DEFAULTS.port, token: stored.token || '' };
}

async function connect() {
  clearTimeout(retryTimer);
  if (socket && (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING)) return;

  const { port, token } = await config();
  if (!token) { scheduleRetry(5000); return; }

  try {
    socket = new WebSocket(`ws://127.0.0.1:${port}`);
  } catch (_) {
    scheduleRetry();
    return;
  }
  socket.binaryType = 'arraybuffer';

  socket.onopen = () => {
    socket.send(JSON.stringify({
      type: 'hello',
      token,
      browser: navigator.userAgent.includes('Edg/') ? 'Edge' : 'Chrome'
    }));
  };

  socket.onmessage = e => {
    if (typeof e.data !== 'string') return;
    let msg;
    try { msg = JSON.parse(e.data); } catch (_) { return; }
    handle(msg);
  };

  socket.onclose = () => {
    connected = false;
    socket = null;
    setBadge(false);
    scheduleRetry();
  };

  socket.onerror = () => { /* onclose follows and handles the retry */ };
}

function scheduleRetry(ms) {
  clearTimeout(retryTimer);
  const delay = ms || retryDelay;
  retryTimer = setTimeout(connect, delay);
  // Back off to at most 15s so a closed app does not mean a busy loop.
  retryDelay = Math.min(retryDelay * 1.6, 15000);
}

function send(obj) {
  if (socket && socket.readyState === WebSocket.OPEN) {
    socket.send(JSON.stringify(obj));
    return true;
  }
  return false;
}

function setBadge(ok) {
  chrome.action.setBadgeText({ text: ok ? '' : '!' });
  chrome.action.setBadgeBackgroundColor({ color: ok ? '#34c759' : '#ff4d4f' });
}

// ------------------------------------------------------------------ handlers

async function handle(msg) {
  switch (msg.type) {
    case 'welcome':
      connected = true;
      retryDelay = 1000;
      setBadge(true);
      break;

    case 'ping':
      send({ type: 'pong' });
      break;

    case 'getTabs':
      send({ type: 'tabs', tabs: await listTabs() });
      break;

    case 'setMuted':
      try { await chrome.tabs.update(msg.tabId, { muted: !!msg.muted }); } catch (_) {}
      send({ type: 'tabs', tabs: await listTabs() });
      break;

    case 'startTabCapture':
      startCapture(msg.tabId, msg.opts || {});
      break;

    case 'stopTabCapture':
      stopCapture();
      break;

    case 'pauseTabCapture':
    case 'resumeTabCapture':
      if (capturingTabId != null) {
        chrome.runtime.sendMessage({
          target: 'offscreen',
          type: msg.type === 'pauseTabCapture' ? 'pause' : 'resume'
        });
      }
      break;
  }
}

async function listTabs() {
  const tabs = await chrome.tabs.query({});
  return tabs
    // Browser-internal pages cannot be captured, so do not offer them.
    .filter(t => t.url && !/^(chrome|edge|about|devtools|chrome-extension):/i.test(t.url))
    .map(t => ({
      id: t.id,
      title: t.title || t.url,
      url: t.url,
      favIconUrl: t.favIconUrl || null,
      audible: !!t.audible,
      muted: !!(t.mutedInfo && t.mutedInfo.muted),
      active: !!t.active,
      windowId: t.windowId
    }));
}

function pushTabs() {
  if (connected) listTabs().then(tabs => send({ type: 'tabs', tabs }));
}

chrome.tabs.onUpdated.addListener((_id, info) => {
  // Only the fields we actually surface are worth a round trip.
  if ('audible' in info || 'mutedInfo' in info || 'title' in info || 'favIconUrl' in info || 'status' in info) pushTabs();
});
chrome.tabs.onRemoved.addListener(pushTabs);
chrome.tabs.onCreated.addListener(pushTabs);

// ------------------------------------------------------------ tab capturing

async function ensureOffscreen() {
  const existing = await chrome.offscreen.hasDocument();
  if (existing) return;
  await chrome.offscreen.createDocument({
    url: 'offscreen.html',
    reasons: ['USER_MEDIA', 'DISPLAY_MEDIA'],
    justification: 'Encode the captured tab and stream it to the desktop recorder.'
  });
}

/**
 * @param {number} tabId
 * @param {object} opts
 * @param {boolean} [fromGesture] true when a popup click drove this, which is
 *        the only case Chrome reliably allows getMediaStreamId.
 */
async function startCapture(tabId, opts, fromGesture) {
  if (capturingTabId != null) return;
  try {
    const streamId = await chrome.tabCapture.getMediaStreamId({ targetTabId: tabId });
    await ensureOffscreen();
    capturingTabId = tabId;
    // The offscreen document opens its own socket to the app, so encoded
    // chunks go straight out instead of being copied through this worker.
    const { port, token } = await config();
    chrome.runtime.sendMessage({ target: 'offscreen', type: 'start', streamId, opts, port, token });
  } catch (err) {
    capturingTabId = null;
    const hint = /invoked/i.test(err.message)
      ? 'Chrome needs the extension invoked on that tab first — open the Light Recorder extension popup on the tab and press "Record this tab".'
      : err.message;
    send({ type: 'tabCaptureError', error: hint });
  }
}

function stopCapture() {
  if (capturingTabId == null) return;
  chrome.runtime.sendMessage({ target: 'offscreen', type: 'stop' });
  capturingTabId = null;
}

// Messages coming back from the offscreen document.
chrome.runtime.onMessage.addListener((msg, _sender, reply) => {
  if (msg && msg.target === 'background') {
    switch (msg.type) {
      case 'started':
        send({ type: 'tabCaptureStarted', mime: msg.mime, tabId: capturingTabId });
        break;
      case 'stopped':
        send({ type: 'tabCaptureStopped' });
        capturingTabId = null;
        chrome.offscreen.closeDocument().catch(() => {});
        break;
      case 'error':
        send({ type: 'tabCaptureError', error: msg.error });
        capturingTabId = null;
        chrome.offscreen.closeDocument().catch(() => {});
        break;
    }
    return;
  }

  // Messages from the popup.
  if (msg && msg.target === 'sw') {
    (async () => {
      switch (msg.type) {
        case 'status':
          reply({ connected, capturingTabId, ...(await config()) });
          break;
        case 'save':
          await chrome.storage.local.set({ port: Number(msg.port) || 8787, token: msg.token || '' });
          if (socket) { try { socket.close(); } catch (_) {} }
          retryDelay = 500;
          connect();
          reply({ ok: true });
          break;
        case 'recordTab':
          // Driven by a real click in the popup, which satisfies Chrome's
          // "extension was invoked" requirement for tab capture.
          send({ type: 'tabRecordingStarting', tabId: msg.tabId });
          await startCapture(msg.tabId, msg.opts || {}, true);
          reply({ ok: capturingTabId != null });
          break;
        case 'stopTab':
          stopCapture();
          reply({ ok: true });
          break;
      }
    })();
    return true;   // async reply
  }
});

// ----------------------------------------------------------------- lifecycle

chrome.runtime.onStartup.addListener(connect);
chrome.runtime.onInstalled.addListener(connect);

// Belt and braces: if the worker was evicted while the app is running, this
// brings the socket back within a minute.
chrome.alarms.create('keepalive', { periodInMinutes: 0.5 });
chrome.alarms.onAlarm.addListener(() => { if (!connected) connect(); });

connect();
