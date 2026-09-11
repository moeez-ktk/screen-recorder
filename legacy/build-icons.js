'use strict';
/**
 * Generates the app and tray icons.
 *
 * Written by hand rather than committed as binaries so the icons stay
 * reviewable and tweakable: run `npm run icons` after changing anything here.
 *
 * Produces:
 *   tray-idle.png / tray-rec.png   transparent, for the system tray
 *   icon.ico                       app / window / installer icon
 */
const fs = require('fs');
const path = require('path');
const zlib = require('zlib');

const OUT = __dirname;

// ---------------------------------------------------------------- rasteriser

/** RGBA canvas with 4x supersampled coverage, which is all the AA we need. */
function canvas(size) {
  return { size, px: new Uint8ClampedArray(size * size * 4) };
}

function blend(c, x, y, [r, g, b], alpha) {
  if (alpha <= 0 || x < 0 || y < 0 || x >= c.size || y >= c.size) return;
  const i = (y * c.size + x) * 4;
  const dstA = c.px[i + 3] / 255;
  const outA = alpha + dstA * (1 - alpha);
  if (outA <= 0) return;
  c.px[i] = (r * alpha + c.px[i] * dstA * (1 - alpha)) / outA;
  c.px[i + 1] = (g * alpha + c.px[i + 1] * dstA * (1 - alpha)) / outA;
  c.px[i + 2] = (b * alpha + c.px[i + 2] * dstA * (1 - alpha)) / outA;
  c.px[i + 3] = outA * 255;
}

const SS = 4;   // supersample factor

/** Fill every pixel whose supersampled centre satisfies `inside(x, y)`. */
function fill(c, colour, inside, opacity = 1) {
  for (let y = 0; y < c.size; y++) {
    for (let x = 0; x < c.size; x++) {
      let hits = 0;
      for (let sy = 0; sy < SS; sy++) {
        for (let sx = 0; sx < SS; sx++) {
          if (inside(x + (sx + 0.5) / SS, y + (sy + 0.5) / SS)) hits++;
        }
      }
      if (hits) blend(c, x, y, colour, (hits / (SS * SS)) * opacity);
    }
  }
}

const disc = (cx, cy, r) => (x, y) => (x - cx) ** 2 + (y - cy) ** 2 <= r * r;
const ring = (cx, cy, r, w) => (x, y) => {
  const d2 = (x - cx) ** 2 + (y - cy) ** 2;
  return d2 <= r * r && d2 >= (r - w) ** 2;
};
const roundRect = (x0, y0, x1, y1, r) => (x, y) => {
  if (x < x0 || x > x1 || y < y0 || y > y1) return false;
  const dx = Math.max(x0 + r - x, 0, x - (x1 - r));
  const dy = Math.max(y0 + r - y, 0, y - (y1 - r));
  return dx * dx + dy * dy <= r * r;
};

// -------------------------------------------------------------------- encode

function crc32(buf) {
  let c, crc = 0xffffffff;
  for (let n = 0; n < buf.length; n++) {
    c = (crc ^ buf[n]) & 0xff;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    crc = c ^ (crc >>> 8);
  }
  return (crc ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body));
  return Buffer.concat([len, body, crc]);
}

function toPng(c) {
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(c.size, 0);
  ihdr.writeUInt32BE(c.size, 4);
  ihdr[8] = 8;    // bit depth
  ihdr[9] = 6;    // RGBA
  // Each scanline is prefixed with filter type 0 (none).
  const raw = Buffer.alloc(c.size * (c.size * 4 + 1));
  for (let y = 0; y < c.size; y++) {
    raw[y * (c.size * 4 + 1)] = 0;
    Buffer.from(c.px.buffer, y * c.size * 4, c.size * 4)
      .copy(raw, y * (c.size * 4 + 1) + 1);
  }
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', zlib.deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0))
  ]);
}

/**
 * ICO entry as a classic DIB. Small sizes are written as BMP rather than PNG
 * because a few Windows shell surfaces still render PNG-in-ICO inconsistently
 * below 256px.
 */
function toDib(c) {
  const n = c.size;
  const header = Buffer.alloc(40);
  header.writeUInt32LE(40, 0);
  header.writeInt32LE(n, 4);
  header.writeInt32LE(n * 2, 8);   // height counts XOR + AND masks
  header.writeUInt16LE(1, 12);
  header.writeUInt16LE(32, 14);
  header.writeUInt32LE(n * n * 4, 20);

  const xor = Buffer.alloc(n * n * 4);
  for (let y = 0; y < n; y++) {
    for (let x = 0; x < n; x++) {
      const s = ((n - 1 - y) * n + x) * 4;   // DIBs are bottom-up
      const d = (y * n + x) * 4;
      xor[d] = c.px[s + 2];
      xor[d + 1] = c.px[s + 1];
      xor[d + 2] = c.px[s];
      xor[d + 3] = c.px[s + 3];
    }
  }
  // Fully transparent AND mask; the 32-bit alpha above does the real work.
  const rowBytes = Math.ceil(n / 32) * 4;
  return Buffer.concat([header, xor, Buffer.alloc(rowBytes * n)]);
}

function toIco(canvases) {
  const entries = canvases.map(c => ({
    size: c.size,
    data: c.size >= 256 ? toPng(c) : toDib(c)
  }));

  const dir = Buffer.alloc(6 + entries.length * 16);
  dir.writeUInt16LE(0, 0);
  dir.writeUInt16LE(1, 2);
  dir.writeUInt16LE(entries.length, 4);

  let offset = dir.length;
  entries.forEach((e, i) => {
    const p = 6 + i * 16;
    dir[p] = e.size >= 256 ? 0 : e.size;      // 0 means 256
    dir[p + 1] = e.size >= 256 ? 0 : e.size;
    dir[p + 2] = 0;
    dir[p + 3] = 0;
    dir.writeUInt16LE(1, p + 4);
    dir.writeUInt16LE(32, p + 6);
    dir.writeUInt32LE(e.data.length, p + 8);
    dir.writeUInt32LE(offset, p + 12);
    offset += e.data.length;
  });

  return Buffer.concat([dir, ...entries.map(e => e.data)]);
}

// -------------------------------------------------------------------- design

const RED = [255, 59, 48];
const LIGHT = [232, 234, 237];
const DARK = [26, 29, 35];

/** Tray: a bare record dot. Ring when idle, solid red while recording. */
function trayIcon(size, recording) {
  const c = canvas(size);
  const mid = size / 2;
  const r = size * 0.34;
  if (recording) {
    fill(c, RED, disc(mid, mid, r));
  } else {
    fill(c, LIGHT, ring(mid, mid, r, Math.max(1.4, size * 0.11)));
  }
  return c;
}

/** App icon: dark rounded tile with the same record dot, so they read as a set. */
function appIcon(size) {
  const c = canvas(size);
  const pad = size * 0.06;
  fill(c, DARK, roundRect(pad, pad, size - pad, size - pad, size * 0.22));
  fill(c, [255, 255, 255], roundRect(pad, pad, size - pad, size - pad, size * 0.22), 0.06);
  fill(c, RED, disc(size / 2, size / 2, size * 0.26));
  return c;
}

// ---------------------------------------------------------------------- main

const written = [];
function write(name, buf) {
  fs.writeFileSync(path.join(OUT, name), buf);
  written.push(`${name} (${buf.length} bytes)`);
}

// Electron picks the right one for the display's scale factor.
write('tray-idle.png', toPng(trayIcon(32, false)));
write('tray-idle@2x.png', toPng(trayIcon(64, false)));
write('tray-rec.png', toPng(trayIcon(32, true)));
write('tray-rec@2x.png', toPng(trayIcon(64, true)));
write('icon.png', toPng(appIcon(256)));
write('icon.ico', toIco([16, 24, 32, 48, 64, 128, 256].map(appIcon)));

console.log('wrote:\n  ' + written.join('\n  '));
