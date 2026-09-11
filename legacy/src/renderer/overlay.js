'use strict';
/**
 * Overlay logic. The only per-second work while recording is one text update
 * driven by a local interval - progress events from ffmpeg arrive about once
 * a second and are used for the file size, not the clock, so the timer stays
 * smooth even if the encoder is busy.
 */

const $ = id => document.getElementById(id);

const el = {
  body: document.body,
  dot: $('dot'), time: $('time'), sub: $('sub'),
  start: $('btnStart'), stop: $('btnStop'), source: $('btnSource'),
  mic: $('btnMic'), sys: $('btnSys'), mixer: $('btnMixer'),
  settings: $('btnSettings'), hide: $('btnHide'), collapse: $('btnCollapse')
};

let state = null;
let startedAt = 0;
let bytes = 0;
let ticker = null;

function tick() {
  if (!state || !state.recording) return;
  const elapsed = startedAt ? Date.now() - startedAt : 0;
  el.time.textContent = fmtDuration(elapsed);
  // The sub-line is hidden in compact mode, so skip the string work entirely.
  if (state.compact) return;
  el.sub.textContent = bytes
    ? `${fmtBytes(bytes)} · ${sourceLabel(state.source)}`
    : sourceLabel(state.source);
}

function startTicker() {
  stopTicker();
  tick();
  ticker = setInterval(tick, 500);
}

function stopTicker() {
  if (ticker) { clearInterval(ticker); ticker = null; }
}

function render(s) {
  state = s;
  const rec = !!s.recording;
  const starting = s.state === 'starting';

  el.body.classList.toggle('recording', rec && !starting);
  el.body.classList.toggle('starting', starting);

  // Collapsing only makes sense while there is a recording to watch; when
  // idle the full pill is always shown.
  const compact = rec && !!s.compact;
  el.body.classList.toggle('compact', compact);
  el.collapse.hidden = !rec;
  el.collapse.title = compact ? 'Expand' : 'Collapse';
  el.collapse.setAttribute('aria-label', compact ? 'Expand recorder' : 'Collapse recorder');
  el.collapse.setAttribute('aria-expanded', compact ? 'false' : 'true');

  // The core enable/disable rule: exactly one of the two is ever usable.
  el.start.disabled = rec;
  el.stop.disabled = !rec;

  const st = s.settings || {};
  el.mic.classList.toggle('muted', !st.micEnabled || st.micMuted);
  const micKey = (st.hotkeys && st.hotkeys.toggleMic) || 'Ctrl+Alt+M';
  el.mic.title = (!st.micEnabled || st.micMuted)
    ? `Microphone muted — click to unmute (${micKey})`
    : `Microphone live — click to mute (${micKey})`;

  el.sys.classList.toggle('muted', !st.systemAudioEnabled || st.systemAudioMuted);
  el.sys.title = (!st.systemAudioEnabled || st.systemAudioMuted)
    ? 'System audio muted — click to unmute'
    : 'System audio recording — click to mute';

  if (rec) {
    startedAt = s.startedAt || (Date.now() - (s.elapsedMs || 0));
    if (s.progress && s.progress.bytes) bytes = s.progress.bytes;
    startTicker();
  } else {
    stopTicker();
    startedAt = 0;
    bytes = 0;
    el.time.textContent = 'Ready';
    el.sub.textContent = sourceLabel(s.source);
  }
}

// ---- wiring ---------------------------------------------------------------

el.start.addEventListener('click', () => { if (!el.start.disabled) window.api.start(); });
el.stop.addEventListener('click', () => { if (!el.stop.disabled) window.api.stop(); });
el.source.addEventListener('click', () => window.api.openPicker());
el.mic.addEventListener('click', () => window.api.toggleMic());
el.sys.addEventListener('click', () => window.api.toggleSystemAudio());
el.mixer.addEventListener('click', () => window.api.openMixer());
el.settings.addEventListener('click', () => window.api.openSettings());
el.hide.addEventListener('click', () => window.api.hideOverlay());
el.collapse.addEventListener('click', () => window.api.toggleCompact());

window.api.onState(render);
window.api.onProgress(p => {
  if (p && p.bytes) { bytes = p.bytes; tick(); }
});
window.api.onSaved(({ file }) => {
  el.time.textContent = 'Saved';
  el.sub.textContent = file.split(/[\\/]/).pop();
});

initToasts();
window.api.getState().then(render);
