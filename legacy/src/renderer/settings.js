'use strict';
/** Settings window. Every change saves immediately - there is no Apply button. */

const $ = id => document.getElementById(id);

let s = {};
let state = {};

const HOTKEYS = [
  ['toggleOverlay', 'Show / hide the overlay'],
  ['startStop', 'Start or stop recording'],
  ['stop', 'Stop recording'],
  ['toggleMic', 'Mute / unmute microphone'],
  ['picker', 'Choose what to record']
];

function save(patch) {
  Object.assign(s, patch);
  return window.api.setSettings(patch);
}

// ------------------------------------------------------------------ binding

/** Two-way bind a control to a settings key. */
function bind(id, key, { event = 'change', get, set, after } = {}) {
  const el = $(id);
  if (!el) return;
  const read = get || (() => (el.type === 'checkbox' ? el.checked : el.value));
  const write = set || (v => { if (el.type === 'checkbox') el.checked = !!v; else el.value = v; });
  write(s[key]);
  el.addEventListener(event, async () => {
    await save({ [key]: read() });
    if (after) after();
  });
  return { el, write };
}

function renderQualityMode() {
  const byBitrate = s.qualityMode === 'bitrate';
  $('qualityWrap').hidden = byBitrate;
  $('bitrateWrap').hidden = !byBitrate;
}

function renderEncoders() {
  const sel = $('encoder');
  const list = (state.encoders && state.encoders.available) || [];
  sel.replaceChildren();

  const auto = document.createElement('option');
  auto.value = 'auto';
  const bestLabel = list.find(e => e.id === state.encoders.best);
  auto.textContent = bestLabel ? `Automatic (${bestLabel.label})` : 'Automatic';
  sel.appendChild(auto);

  for (const e of list) {
    const o = document.createElement('option');
    o.value = e.id;
    o.textContent = e.label;
    sel.appendChild(o);
  }
  sel.value = list.some(e => e.id === s.encoder) ? s.encoder : 'auto';

  const hw = list.filter(e => e.hw).length;
  $('encoderHint').textContent = list.length
    ? (hw
      ? 'Hardware encoding keeps the CPU free, which is what makes long game recordings cheap.'
      : 'Only software encoding is available, so expect noticeable CPU use.')
    : 'No encoders detected — check the ffmpeg path below.';
}

function renderFfmpeg() {
  const enc = state.encoders || {};
  $('ffmpegPath').value = enc.ffmpeg || '';
  const hint = $('ffmpegHint');
  if (!enc.ffmpeg) {
    hint.innerHTML = '<span class="bad">Not found.</span> Screen and window recording need it.';
  } else {
    hint.innerHTML = enc.hasDdagrab
      ? '<span class="ok">Desktop Duplication available</span> — GPU screen capture, ideal for games.'
      : '<span class="bad">No ddagrab filter</span> — falling back to GDI capture, which uses more CPU.';
  }
}

function renderBridge() {
  const b = state.bridge || {};
  $('bridgeToken').value = b.token || '';
  $('bridgeStatus').innerHTML = b.connected
    ? '<span class="ok">Extension connected</span>'
    : 'Waiting for the browser extension…';
}

async function renderMics() {
  const sel = $('micDeviceId');
  const devices = await window.api.listAudioDevices();
  sel.replaceChildren();
  const def = document.createElement('option');
  def.value = 'default';
  def.textContent = 'System default';
  sel.appendChild(def);
  for (const d of devices) {
    if (d.deviceId === 'default' || !d.deviceId) continue;
    const o = document.createElement('option');
    o.value = d.deviceId;
    o.textContent = d.label;
    sel.appendChild(o);
  }
  sel.value = devices.some(d => d.deviceId === s.micDeviceId) ? s.micDeviceId : 'default';
}

function renderAutoHide() {
  const n = Number(s.autoHideAfterStopSeconds) || 0;
  $('autoHideVal').textContent = n === 0 ? 'never' : `${n}s`;
}

/**
 * The listener is what makes the hotkeys work while the recorder itself is
 * closed, so its state is worth showing plainly rather than hiding in a log.
 */
async function renderListener() {
  const st = await window.api.listenerStatus();
  const dot = $('listenerDot');
  const state = $('listenerState');
  const hint = $('listenerHint');
  const startBtn = $('btnListenerStart');
  const auto = $('listenerAutostart');

  dot.className = 'dot-live' + (st.running ? ' on' : st.built ? ' off' : '');
  auto.checked = !!st.autostart;
  auto.disabled = !st.built;
  startBtn.disabled = !st.built || st.running;

  if (!st.built) {
    state.textContent = 'Hotkey listener not built';
    hint.innerHTML = 'Run <code>npm run listener:build</code> once. Until then, shortcuts only work while this window is open.';
    return;
  }
  if (st.running) {
    state.textContent = 'Hotkey listener running';
    hint.textContent = st.autostart
      ? 'Shortcuts work everywhere, including after a reboot.'
      : 'Shortcuts work now. Turn on “Start with Windows” to keep them after a reboot.';
  } else {
    state.textContent = 'Hotkey listener stopped';
    hint.textContent = 'Shortcuts will not work until it is running.';
  }
}

// ------------------------------------------------------------------ hotkeys

/** Translate a keydown into an Electron accelerator string. */
function accelFrom(e) {
  const parts = [];
  if (e.ctrlKey) parts.push('Control');
  if (e.altKey) parts.push('Alt');
  if (e.shiftKey) parts.push('Shift');
  if (e.metaKey) parts.push('Super');

  const k = e.key;
  if (['Control', 'Alt', 'Shift', 'Meta'].includes(k)) return null;  // modifier alone

  let key;
  if (k === ' ') key = 'Space';
  else if (/^F\d{1,2}$/.test(k)) key = k;
  else if (k.length === 1) key = k.toUpperCase();
  else key = k;

  // A bare letter would swallow that key system-wide; require a modifier.
  if (!parts.length && !/^F\d{1,2}$/.test(key)) return null;
  parts.push(key);
  return parts.join('+');
}

function renderHotkeys() {
  const host = $('hotkeys');
  host.replaceChildren();

  for (const [key, label] of HOTKEYS) {
    const rowEl = document.createElement('div');
    rowEl.className = 'hk-row';
    rowEl.innerHTML = `<span>${escapeHtml(label)}</span>`;

    const input = document.createElement('div');
    input.className = 'hk-input';
    input.tabIndex = 0;
    input.textContent = (s.hotkeys && s.hotkeys[key]) || 'Not set';

    const stop = () => {
      input.classList.remove('capturing');
      input.textContent = (s.hotkeys && s.hotkeys[key]) || 'Not set';
    };

    input.addEventListener('click', () => {
      input.classList.add('capturing');
      input.textContent = 'Press keys…';
      input.focus();
    });
    input.addEventListener('blur', stop);
    input.addEventListener('keydown', async e => {
      if (!input.classList.contains('capturing')) return;
      e.preventDefault();
      if (e.key === 'Escape') { stop(); input.blur(); return; }
      if (e.key === 'Backspace' || e.key === 'Delete') {
        await save({ hotkeys: Object.assign({}, s.hotkeys, { [key]: '' }) });
        stop(); input.blur(); return;
      }
      const accel = accelFrom(e);
      if (!accel) return;
      await save({ hotkeys: Object.assign({}, s.hotkeys, { [key]: accel }) });
      stop();
      input.blur();
    });

    rowEl.appendChild(input);
    host.appendChild(rowEl);
  }
}

// -------------------------------------------------------------------- setup

async function init() {
  state = await window.api.getState();
  s = state.settings;

  bind('outputDir', 'outputDir');
  bind('filenamePattern', 'filenamePattern', { event: 'input' });
  bind('remuxOnStop', 'remuxOnStop');

  bind('fps', 'fps', { get: () => Number($('fps').value) });
  bind('resolution', 'resolution');
  bind('encoder', 'encoder');
  bind('captureCursor', 'captureCursor');
  bind('qualityMode', 'qualityMode', { after: renderQualityMode });

  bind('quality', 'quality', {
    event: 'input',
    get: () => Number($('quality').value),
    after: () => { $('qualityVal').textContent = `CQ ${s.quality}`; }
  });
  bind('bitrateMbps', 'bitrateMbps', {
    event: 'input',
    get: () => Number($('bitrateMbps').value),
    after: () => { $('bitrateVal').textContent = `${s.bitrateMbps} Mbps`; }
  });

  bind('systemAudioEnabled', 'systemAudioEnabled');
  bind('micEnabled', 'micEnabled');
  bind('micDeviceId', 'micDeviceId');
  bind('systemAudioGain', 'systemAudioGain', {
    event: 'input',
    get: () => Number($('systemAudioGain').value),
    after: () => { $('sysGainVal').textContent = `${Math.round(s.systemAudioGain * 100)}%`; }
  });
  bind('micGain', 'micGain', {
    event: 'input',
    get: () => Number($('micGain').value),
    after: () => { $('micGainVal').textContent = `${Math.round(s.micGain * 100)}%`; }
  });
  bind('bridgePort', 'bridgePort', { get: () => Number($('bridgePort').value) });

  bind('exitWhenHidden', 'exitWhenHidden');
  bind('autoHideAfterStopSeconds', 'autoHideAfterStopSeconds', {
    event: 'input',
    get: () => Number($('autoHideAfterStopSeconds').value),
    after: renderAutoHide
  });
  renderAutoHide();
  renderListener();

  $('qualityVal').textContent = `CQ ${s.quality}`;
  $('bitrateVal').textContent = `${s.bitrateMbps} Mbps`;
  $('sysGainVal').textContent = `${Math.round(s.systemAudioGain * 100)}%`;
  $('micGainVal').textContent = `${Math.round(s.micGain * 100)}%`;

  renderQualityMode();
  renderEncoders();
  renderFfmpeg();
  renderBridge();
  renderHotkeys();
  renderMics();
}

// ------------------------------------------------------------------- wiring

$('btnFolder').addEventListener('click', async () => {
  const dir = await window.api.chooseFolder();
  if (dir) { s.outputDir = dir; $('outputDir').value = dir; }
});
$('btnOpenFolder').addEventListener('click', () => window.api.openFolder());

$('btnFfmpeg').addEventListener('click', async () => {
  const p = await window.api.chooseFfmpeg();
  if (p) { state = await window.api.getState(); renderFfmpeg(); renderEncoders(); }
});
$('btnRedetect').addEventListener('click', async () => {
  await window.api.redetectEncoders();
  state = await window.api.getState();
  renderFfmpeg();
  renderEncoders();
});

$('btnListenerStart').addEventListener('click', async () => {
  $('btnListenerStart').disabled = true;
  const r = await window.api.startListener();
  if (r && r.ok === false) $('listenerHint').textContent = r.error;
  setTimeout(renderListener, 600);
});

$('listenerAutostart').addEventListener('change', async e => {
  const r = await window.api.setListenerAutostart(e.target.checked);
  if (r && r.ok === false) $('listenerHint').textContent = r.error;
  renderListener();
});

$('btnCopyToken').addEventListener('click', () => {
  const input = $('bridgeToken');
  input.select();
  navigator.clipboard.writeText(input.value);
});
$('btnExtFolder').addEventListener('click', () => window.api.openExtensionFolder());

$('btnReset').addEventListener('click', async () => {
  await window.api.resetSettings();
  state = await window.api.getState();
  s = state.settings;
  location.reload();
});

$('btnDone').addEventListener('click', () => window.api.closeWindow());
$('btnClose').addEventListener('click', () => window.api.closeWindow());
document.addEventListener('keydown', e => {
  // Escape must not close the window while a hotkey field has focus.
  if (e.key === 'Escape' && !document.querySelector('.hk-input.capturing')) window.api.closeWindow();
});

window.api.onState(next => {
  state = next;
  s = next.settings;
  renderBridge();
});

initToasts();
init();
