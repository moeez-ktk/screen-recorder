'use strict';
/**
 * Persisted user settings. Written to <userData>/settings.json.
 * Kept deliberately tiny and synchronous - it is read once at boot and
 * only rewritten when the user actually changes something.
 */
const { app } = require('electron');
const fs = require('fs');
const path = require('path');

/**
 * Bumped whenever a stored setting needs rewriting rather than just merging.
 * Stored values always win over defaults, so changing a default alone would
 * never reach anyone who has already run the app.
 */
const CURRENT_VERSION = 2;

const DEFAULTS = {
  settingsVersion: CURRENT_VERSION,

  // --- output ---
  outputDir: '',                 // resolved to Videos/Light Recorder on first run
  filenamePattern: 'Recording {date} {time}',
  container: 'mp4',
  remuxOnStop: false,            // rewrite fragmented mp4 into a seekable one

  // --- video ---
  fps: 60,
  resolution: 'source',          // 'source' | '2160' | '1440' | '1080' | '720' | '480'
  encoder: 'auto',               // 'auto' | h264_nvenc | h264_qsv | h264_amf | h264_mf | libx264
  qualityMode: 'quality',        // 'quality' (CQ) | 'bitrate'
  quality: 23,                   // CQ / CRF, lower = better
  bitrateMbps: 12,
  captureCursor: true,

  // --- audio ---
  systemAudioEnabled: true,
  systemAudioMuted: false,
  systemAudioGain: 1.0,
  micEnabled: true,
  micMuted: false,
  micGain: 1.0,
  micDeviceId: 'default',
  audioBitrateKbps: 160,

  // --- lifecycle ---
  // The recorder is launched on demand by the hotkey listener and quits when
  // it is no longer needed, so nothing heavy stays resident between takes.
  exitWhenHidden: true,
  autoHideAfterStopSeconds: 20,   // 0 keeps the overlay up until dismissed

  // --- behaviour ---
  hideOverlayWhileRecording: false,
  overlayCorner: 'top-right',
  overlayOffset: { x: 16, y: 16 },
  overlayCustomPos: null,        // {x,y} once the user drags it
  bridgePort: 8787,              // localhost port the browser extension talks to
  ffmpegPath: '',                // blank = auto-detect

  // --- hotkeys ---
  hotkeys: {
    toggleOverlay: 'Control+Shift+D',
    startStop: 'Control+Alt+S',
    stop: 'Control+Alt+X',
    toggleMic: 'Control+Alt+M',
    picker: 'Control+Alt+P'
  }
};

let file = null;
let cache = null;

function deepMerge(base, patch) {
  const out = Array.isArray(base) ? base.slice() : Object.assign({}, base);
  for (const k of Object.keys(patch || {})) {
    const v = patch[k];
    if (v && typeof v === 'object' && !Array.isArray(v) && base && typeof base[k] === 'object' && base[k] !== null && !Array.isArray(base[k])) {
      out[k] = deepMerge(base[k], v);
    } else if (v !== undefined) {
      out[k] = v;
    }
  }
  return out;
}

function init() {
  file = path.join(app.getPath('userData'), 'settings.json');
  let stored = {};
  try {
    stored = JSON.parse(fs.readFileSync(file, 'utf8'));
  } catch (_) { /* first run or corrupt - fall back to defaults */ }

  cache = deepMerge(DEFAULTS, stored);

  if (!cache.outputDir) {
    cache.outputDir = path.join(app.getPath('videos'), 'Light Recorder');
  }
  try {
    fs.mkdirSync(cache.outputDir, { recursive: true });
  } catch (_) { /* surfaced later when a recording is attempted */ }

  const storedVersion = Number(stored.settingsVersion) || 1;
  if (storedVersion < CURRENT_VERSION) migrate(storedVersion);

  return cache;
}

/**
 * Apply changes that stored settings would otherwise shadow forever.
 * Only rewrites values the user has demonstrably not customised.
 */
function migrate(from) {
  if (from < 2) {
    // The launch shortcut moved to Ctrl+Shift+D. Leave it alone if the user
    // had already picked something other than the old default.
    if (cache.hotkeys && cache.hotkeys.toggleOverlay === 'Control+Alt+R') {
      cache.hotkeys.toggleOverlay = 'Control+Shift+D';
    }
  }

  cache.settingsVersion = CURRENT_VERSION;
  try {
    fs.writeFileSync(file, JSON.stringify(cache, null, 2), 'utf8');
  } catch (err) {
    console.error('[settings] migration save failed:', err.message);
  }
}

function all() {
  return cache || init();
}

function get(key) {
  return all()[key];
}

/** Merge a patch and persist. Returns the full settings object. */
function set(patch) {
  cache = deepMerge(all(), patch);
  try {
    fs.writeFileSync(file, JSON.stringify(cache, null, 2), 'utf8');
  } catch (err) {
    console.error('[settings] save failed:', err.message);
  }
  return cache;
}

function reset() {
  const dir = cache && cache.outputDir;
  cache = deepMerge(DEFAULTS, { outputDir: dir });
  return set({});
}

module.exports = { init, all, get, set, reset, DEFAULTS };
