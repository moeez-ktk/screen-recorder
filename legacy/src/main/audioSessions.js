'use strict';
/**
 * Per-application audio control (the desktop half of the mixer).
 *
 * Windows has no way to record one app's audio in isolation without a kernel
 * driver, so "mute app X" here means muting X's Core Audio session for the
 * duration of the recording - X is then absent from the loopback capture.
 * The side effect is that you also stop hearing X live; that is inherent, and
 * the UI says so. Everything we change is restored on stop.
 *
 * The PowerShell helper is started once and kept warm (compiling the COM
 * interop costs ~400ms, each command after that is ~10ms). It is spawned
 * lazily on first use and shut down when idle, so an idle recorder pays
 * nothing for this feature.
 */
const { spawn } = require('child_process');
const path = require('path');

const SCRIPT = path.join(__dirname, 'win', 'AudioSessions.ps1');
const IDLE_SHUTDOWN_MS = 60000;

class AudioSessions {
  constructor() {
    this.proc = null;
    this.ready = null;
    this.queue = [];          // pending resolvers, one per issued command
    this.buf = '';
    this.idleTimer = null;
    /** pid -> mute state before we touched it. */
    this.changedByUs = new Map();
  }

  _spawn() {
    if (this.ready) return this.ready;

    this.ready = new Promise((resolve, reject) => {
      let proc;
      try {
        proc = spawn('powershell.exe', [
          '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
          '-File', SCRIPT, '-Action', 'serve'
        ], { windowsHide: true });
      } catch (err) {
        return reject(err);
      }

      this.proc = proc;
      proc.stdout.setEncoding('utf8');
      proc.stdout.on('data', d => this._onData(d));
      proc.stderr.setEncoding('utf8');
      proc.stderr.on('data', d => console.error('[audioSessions]', String(d).trim()));

      proc.on('error', err => {
        this._reset();
        reject(err);
      });
      proc.on('close', () => {
        // Fail anything still in flight rather than leaving callers hanging.
        for (const r of this.queue) r({ ok: false, error: 'audio helper exited' });
        this.queue.length = 0;
        this._reset();
      });

      // The helper announces itself once the interop is compiled.
      this.queue.push(msg => (msg && msg.ready ? resolve(true) : reject(new Error('helper failed to start'))));
      setTimeout(() => reject(new Error('audio helper start timed out')), 15000);
    }).catch(err => {
      this.ready = null;
      throw err;
    });

    return this.ready;
  }

  _reset() {
    this.proc = null;
    this.ready = null;
    this.buf = '';
    clearTimeout(this.idleTimer);
    this.idleTimer = null;
  }

  _onData(text) {
    this.buf += text;
    let i;
    while ((i = this.buf.indexOf('\n')) >= 0) {
      const line = this.buf.slice(0, i).trim();
      this.buf = this.buf.slice(i + 1);
      if (!line) continue;
      const resolve = this.queue.shift();
      if (!resolve) continue;
      try { resolve(JSON.parse(line)); }
      catch (_) { resolve({ ok: false, error: 'bad reply from audio helper' }); }
    }
  }

  _touchIdle() {
    clearTimeout(this.idleTimer);
    // Nothing to keep warm if the user is not looking at the mixer.
    this.idleTimer = setTimeout(() => this.shutdown(), IDLE_SHUTDOWN_MS);
  }

  async _cmd(line) {
    try {
      await this._spawn();
    } catch (err) {
      return { ok: false, error: err.message };
    }
    this._touchIdle();
    return new Promise(resolve => {
      this.queue.push(resolve);
      try { this.proc.stdin.write(line + '\n'); }
      catch (err) { this.queue.pop(); resolve({ ok: false, error: err.message }); }
    });
  }

  /** Apps currently holding an audio session, most interesting first. */
  async list() {
    const r = await this._cmd('list');
    if (!r.ok) return { ok: false, error: r.error, sessions: [] };

    // Collapse the many sessions a browser or game opens into one row per app.
    const byPid = new Map();
    for (const s of r.sessions || []) {
      const prev = byPid.get(s.pid);
      if (!prev) byPid.set(s.pid, s);
      else if (s.active && !prev.active) byPid.set(s.pid, s);
    }

    const sessions = [...byPid.values()].sort((a, b) => {
      if (a.active !== b.active) return a.active ? -1 : 1;
      return (a.name || '').localeCompare(b.name || '');
    });

    return { ok: true, sessions };
  }

  async setMuted(pid, muted) {
    if (!this.changedByUs.has(pid)) {
      const cur = (await this.list()).sessions.find(s => s.pid === pid);
      this.changedByUs.set(pid, cur ? !!cur.muted : false);
    }
    return this._cmd(`${muted ? 'mute' : 'unmute'} ${pid}`);
  }

  /** Put every app we muted back the way the user had it. */
  async restoreAll() {
    for (const [pid, wasMuted] of this.changedByUs) {
      await this._cmd(`${wasMuted ? 'mute' : 'unmute'} ${pid}`);
    }
    this.changedByUs.clear();
  }

  shutdown() {
    clearTimeout(this.idleTimer);
    if (!this.proc) return;
    try { this.proc.stdin.write('quit\n'); } catch (_) {}
    const p = this.proc;
    setTimeout(() => { try { p.kill(); } catch (_) {} }, 1000);
    this._reset();
  }
}

module.exports = { AudioSessions };
