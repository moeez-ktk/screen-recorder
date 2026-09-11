'use strict';
/**
 * Local bridge to the browser extension.
 *
 * Bound to 127.0.0.1 only, and every connection must present the shared token
 * written to <userData>/bridge-token.txt, which the user pastes into the
 * extension once. Without that, any web page could open a socket to us.
 *
 * Purpose: Windows cannot separate one browser tab's audio from another's -
 * they all share the browser's audio process. Only the browser itself can do
 * that, so tab enumeration, per-tab muting, and tab-scoped capture live here.
 */
const { EventEmitter } = require('events');
const { WebSocketServer } = require('ws');
const crypto = require('crypto');
const fs = require('fs');
const path = require('path');

class Bridge extends EventEmitter {
  constructor(userDataDir) {
    super();
    this.wss = null;
    this.client = null;          // the service worker: control messages
    this.dataClients = new Set(); // offscreen documents: video chunks only
    this.tabs = [];
    this.port = 0;
    this.tokenFile = path.join(userDataDir, 'bridge-token.txt');
    this.token = this._loadOrCreateToken();
    this._heartbeat = null;
    /** tabId -> the tab's mute state before we touched it, so we can restore. */
    this.mutedByUs = new Map();
  }

  _loadOrCreateToken() {
    try {
      const t = fs.readFileSync(this.tokenFile, 'utf8').trim();
      if (t.length >= 16) return t;
    } catch (_) { /* first run */ }
    const t = crypto.randomBytes(16).toString('hex');
    try { fs.writeFileSync(this.tokenFile, t, 'utf8'); } catch (_) {}
    return t;
  }

  get connected() {
    return !!(this.client && this.client.readyState === 1);
  }

  start(port) {
    this.stop();
    this.port = port;

    try {
      this.wss = new WebSocketServer({ host: '127.0.0.1', port });
    } catch (err) {
      this.emit('error', `Bridge could not listen on ${port}: ${err.message}`);
      return;
    }

    this.wss.on('error', err => {
      this.emit('error', err.code === 'EADDRINUSE'
        ? `Port ${port} is already in use. Pick another in Settings.`
        : err.message);
    });

    this.wss.on('connection', (ws, req) => {
      // Reject anything not from this machine, and anything without the token.
      const addr = req.socket.remoteAddress || '';
      if (!/^(::1|::ffff:127\.0\.0\.1|127\.0\.0\.1)$/.test(addr)) return ws.close(1008, 'local only');

      let authed = false;
      let role = 'control';
      const authTimer = setTimeout(() => { if (!authed) ws.close(1008, 'auth timeout'); }, 5000);

      ws.on('message', (data, isBinary) => {
        if (isBinary) {
          // Tab-capture chunks. The offscreen document streams these on its
          // own socket so they never pass through the service worker.
          if (authed) this.emit('binary', data);
          return;
        }
        let msg;
        try { msg = JSON.parse(data.toString()); } catch (_) { return; }

        if (!authed) {
          const supplied = typeof msg.token === 'string' ? msg.token : '';
          const ok = msg.type === 'hello' &&
            supplied.length === this.token.length &&
            crypto.timingSafeEqual(Buffer.from(supplied), Buffer.from(this.token));

          if (!ok) { ws.close(1008, 'bad token'); return; }

          authed = true;
          role = msg.role === 'data' ? 'data' : 'control';
          clearTimeout(authTimer);

          if (role === 'data') {
            this.dataClients.add(ws);
            try { ws.send(JSON.stringify({ type: 'welcome', role: 'data' })); } catch (_) {}
            return;
          }

          // Only one control client at a time - a reloaded extension replaces
          // the stale worker rather than piling up.
          if (this.client && this.client !== ws) { try { this.client.close(); } catch (_) {} }
          this.client = ws;
          this.send({ type: 'welcome', app: 'Light Recorder' });
          this.send({ type: 'getTabs' });
          this.emit('connected', { browser: msg.browser || 'browser' });
          return;
        }

        if (role === 'control') this._handle(msg);
      });

      ws.on('close', () => {
        clearTimeout(authTimer);
        this.dataClients.delete(ws);
        if (this.client === ws) {
          this.client = null;
          this.tabs = [];
          this.emit('disconnected');
        }
      });

      ws.on('error', () => { /* close handler does the cleanup */ });
    });

    // The extension's MV3 service worker is evicted when idle; a periodic
    // message over the socket keeps it alive for as long as we are running.
    this._heartbeat = setInterval(() => {
      if (this.connected) this.send({ type: 'ping', t: Date.now() });
    }, 20000);

    this.emit('listening', port);
  }

  _handle(msg) {
    switch (msg.type) {
      case 'tabs':
        this.tabs = Array.isArray(msg.tabs) ? msg.tabs : [];
        this.emit('tabs', this.tabs);
        break;
      case 'tabCaptureStarted':
        this.emit('tabCaptureStarted', msg);
        break;
      case 'tabCaptureStopped':
        this.emit('tabCaptureStopped', msg);
        break;
      case 'tabCaptureError':
        this.emit('tabCaptureError', msg.error || 'Tab capture failed');
        break;
      case 'tabRecordingStarting':
        // The user pressed "Record this tab" inside the extension popup, so
        // the app has to open the output file rather than initiate capture.
        this.emit('tabRecordingStarting', msg);
        break;
      case 'pong':
        break;
      default:
        this.emit('message', msg);
    }
  }

  send(obj) {
    if (!this.connected) return false;
    try { this.client.send(JSON.stringify(obj)); return true; } catch (_) { return false; }
  }

  refreshTabs() { return this.send({ type: 'getTabs' }); }

  /**
   * Mute/unmute one tab. Remembers the tab's original state so stopping a
   * recording puts the user's browser back exactly how they left it.
   */
  setTabMuted(tabId, muted) {
    const tab = this.tabs.find(t => t.id === tabId);
    if (tab && !this.mutedByUs.has(tabId)) this.mutedByUs.set(tabId, !!tab.muted);
    if (tab) tab.muted = !!muted;
    return this.send({ type: 'setMuted', tabId, muted: !!muted });
  }

  /** Mute every tab except `keepTabId` - used when recording a single tab. */
  soloTab(keepTabId) {
    for (const t of this.tabs) {
      if (t.id !== keepTabId && t.audible) this.setTabMuted(t.id, true);
    }
  }

  restoreTabMutes() {
    for (const [tabId, wasMuted] of this.mutedByUs) {
      this.send({ type: 'setMuted', tabId, muted: wasMuted });
      const tab = this.tabs.find(t => t.id === tabId);
      if (tab) tab.muted = wasMuted;
    }
    this.mutedByUs.clear();
  }

  startTabCapture(tabId, opts) {
    return this.send({ type: 'startTabCapture', tabId, opts });
  }

  stopTabCapture() {
    return this.send({ type: 'stopTabCapture' });
  }

  stop() {
    clearInterval(this._heartbeat);
    this._heartbeat = null;
    try { if (this.client) this.client.close(); } catch (_) {}
    for (const ws of this.dataClients) { try { ws.close(); } catch (_) {} }
    try { if (this.wss) this.wss.close(); } catch (_) {}
    this.client = null;
    this.dataClients.clear();
    this.wss = null;
    this.tabs = [];
  }
}

module.exports = { Bridge };
