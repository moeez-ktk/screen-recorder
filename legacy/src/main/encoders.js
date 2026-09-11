'use strict';
/**
 * Locates ffmpeg and works out which hardware encoder this machine can
 * actually use. "Listed in -encoders" is not the same as "works", so each
 * candidate is proven with a real one-frame encode before we trust it.
 * The result is cached on disk so this only ever costs anything once.
 */
const { app } = require('electron');
const { execFile, execFileSync } = require('child_process');
const fs = require('fs');
const path = require('path');
const os = require('os');

// Best first. Hardware encoders keep the CPU free for the game/app being recorded.
const CANDIDATES = [
  { id: 'h264_nvenc', label: 'NVIDIA NVENC (H.264)', hw: true },
  { id: 'h264_qsv', label: 'Intel Quick Sync (H.264)', hw: true },
  { id: 'h264_amf', label: 'AMD AMF (H.264)', hw: true },
  { id: 'h264_mf', label: 'Media Foundation (H.264)', hw: true },
  { id: 'libx264', label: 'libx264 (CPU)', hw: false }
];

const COMMON_FFMPEG_DIRS = [
  'C:\\ffmpeg\\bin',
  'C:\\Program Files\\ffmpeg\\bin',
  path.join(os.homedir(), 'scoop', 'shims')
];

let cached = null;

function cacheFile() {
  return path.join(app.getPath('userData'), 'encoder-cache.json');
}

/** Find ffmpeg.exe: explicit setting -> PATH -> a few well-known install spots. */
function resolveFfmpeg(explicit) {
  if (explicit && fs.existsSync(explicit)) return explicit;

  try {
    const out = execFileSync('where', ['ffmpeg'], { encoding: 'utf8', windowsHide: true });
    const first = out.split(/\r?\n/).map(s => s.trim()).filter(Boolean)[0];
    if (first && fs.existsSync(first)) return first;
  } catch (_) { /* not on PATH */ }

  for (const dir of COMMON_FFMPEG_DIRS) {
    const p = path.join(dir, 'ffmpeg.exe');
    if (fs.existsSync(p)) return p;
  }

  // Glob the versioned build layout ffmpeg.org ships (C:\ffmpeg\ffmpeg-<date>-full_build\bin)
  try {
    for (const entry of fs.readdirSync('C:\\ffmpeg')) {
      const p = path.join('C:\\ffmpeg', entry, 'bin', 'ffmpeg.exe');
      if (fs.existsSync(p)) return p;
    }
  } catch (_) { /* no C:\ffmpeg */ }

  return null;
}

function run(bin, args, timeout = 20000) {
  return new Promise(resolve => {
    execFile(bin, args, { timeout, windowsHide: true, maxBuffer: 8 << 20 }, (err, stdout, stderr) => {
      resolve({ ok: !err, stdout: stdout || '', stderr: stderr || '' });
    });
  });
}

/**
 * Encode two frames of colour bars to NUL. Cheap, and it fails the same way a
 * real capture would if the encoder is missing/busy/unlicensed.
 */
async function probeEncoder(bin, id) {
  const args = [
    '-hide_banner', '-loglevel', 'error',
    '-f', 'lavfi', '-i', 'testsrc=size=640x360:rate=30',
    '-frames:v', '2', '-c:v', id, '-pix_fmt', 'yuv420p',
    '-f', 'null', '-'
  ];
  const { ok } = await run(bin, args, 25000);
  return ok;
}

async function listed(bin) {
  const { stdout } = await run(bin, ['-hide_banner', '-encoders']);
  const set = new Set();
  for (const line of stdout.split(/\r?\n/)) {
    const m = line.match(/^\s*[VAS][\w.]*\s+(\S+)/);
    if (m) set.add(m[1]);
  }
  return set;
}

/**
 * @returns {{ffmpeg:string|null, available:Array, best:string|null, hasDdagrab:boolean}}
 */
async function detect(explicitPath, { force = false } = {}) {
  if (cached && !force) return cached;

  const ffmpeg = resolveFfmpeg(explicitPath);
  if (!ffmpeg) {
    cached = { ffmpeg: null, available: [], best: null, hasDdagrab: false };
    return cached;
  }

  if (!force) {
    try {
      const disk = JSON.parse(fs.readFileSync(cacheFile(), 'utf8'));
      if (disk.ffmpeg === ffmpeg && Array.isArray(disk.available)) {
        cached = disk;
        return cached;
      }
    } catch (_) { /* no cache yet */ }
  }

  const declared = await listed(ffmpeg);
  const filters = await run(ffmpeg, ['-hide_banner', '-filters']);
  const hasDdagrab = /\bddagrab\b/.test(filters.stdout);

  const available = [];
  for (const cand of CANDIDATES) {
    if (!declared.has(cand.id)) continue;
    // libx264 is software and always works if it is compiled in; skip the probe cost.
    const works = cand.id === 'libx264' ? true : await probeEncoder(ffmpeg, cand.id);
    if (works) available.push(cand);
  }

  cached = {
    ffmpeg,
    available,
    best: available.length ? available[0].id : null,
    hasDdagrab
  };

  try {
    fs.writeFileSync(cacheFile(), JSON.stringify(cached), 'utf8');
  } catch (_) { /* cache is an optimisation, not a requirement */ }

  return cached;
}

/** Resolve the 'auto' setting into a concrete encoder id. */
function pick(setting, info) {
  if (setting && setting !== 'auto') {
    if (info.available.some(e => e.id === setting)) return setting;
  }
  return info.best;
}

module.exports = { detect, pick, resolveFfmpeg, CANDIDATES };
