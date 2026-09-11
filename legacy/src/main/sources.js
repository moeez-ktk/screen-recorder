'use strict';
/**
 * Enumerates what can be recorded: monitors, application windows, and (when
 * the browser extension is connected) individual browser tabs.
 *
 * Thumbnails are only generated when the picker is actually open - they are
 * the single most expensive thing this app does, so nothing here runs while
 * a recording is in progress.
 */
const { desktopCapturer, screen } = require('electron');

/** Windows that are technically visible but are never what a user means. */
const NOISE = [
  /^Program Manager$/i,
  /^Windows Input Experience$/i,
  /^Windows Shell Experience Host$/i,
  /^Microsoft Text Input Application$/i,
  /^Task Switching$/i,
  /^Light Recorder$/i,
  /^Search$/i,
  /^Start$/i
];

/**
 * Map an Electron display to the DXGI output index ddagrab expects.
 * Electron's display order follows the same enumeration Windows uses, so the
 * array position is the right index in practice. Sorting by position first
 * makes it deterministic when displays are added or removed.
 */
function displayOutputIndex(displayId) {
  const displays = screen.getAllDisplays()
    .slice()
    .sort((a, b) => (a.bounds.x - b.bounds.x) || (a.bounds.y - b.bounds.y));
  const primary = screen.getPrimaryDisplay();
  const idx = displays.findIndex(d => String(d.id) === String(displayId));
  if (idx >= 0) return idx;
  return Math.max(0, displays.findIndex(d => d.id === primary.id));
}

async function list({ thumbnails = true, thumbSize = { width: 320, height: 180 } } = {}) {
  const sources = await desktopCapturer.getSources({
    types: ['screen', 'window'],
    thumbnailSize: thumbnails ? thumbSize : { width: 0, height: 0 },
    fetchWindowIcons: thumbnails
  });

  const screens = [];
  const windows = [];

  for (const s of sources) {
    const isScreen = s.id.startsWith('screen:');
    if (!isScreen && NOISE.some(rx => rx.test(s.name))) continue;
    if (!isScreen && !s.name.trim()) continue;

    const thumb = thumbnails && s.thumbnail && !s.thumbnail.isEmpty()
      ? s.thumbnail.toDataURL()
      : null;

    if (isScreen) {
      const display = screen.getAllDisplays().find(d => String(d.id) === String(s.display_id));
      screens.push({
        kind: 'screen',
        id: s.id,
        name: display && display.id === screen.getPrimaryDisplay().id
          ? `${s.name} (Primary)` : s.name,
        outputIdx: displayOutputIndex(s.display_id),
        width: display ? display.size.width : 0,
        height: display ? display.size.height : 0,
        scaleFactor: display ? display.scaleFactor : 1,
        thumbnail: thumb
      });
    } else {
      windows.push({
        kind: 'window',
        id: s.id,
        name: s.name,
        title: s.name,               // gdigrab matches on the window title
        appIcon: s.appIcon && !s.appIcon.isEmpty() ? s.appIcon.toDataURL() : null,
        thumbnail: thumb
      });
    }
  }

  screens.sort((a, b) => (b.name.includes('Primary') ? 1 : 0) - (a.name.includes('Primary') ? 1 : 0));
  windows.sort((a, b) => a.name.localeCompare(b.name));

  return { screens, windows };
}

/** The zero-config default: whole primary monitor. */
function defaultSource() {
  const primary = screen.getPrimaryDisplay();
  return {
    kind: 'screen',
    id: `screen:${primary.id}`,
    name: 'Entire Screen (Primary)',
    outputIdx: displayOutputIndex(primary.id),
    width: primary.size.width,
    height: primary.size.height
  };
}

module.exports = { list, defaultSource, displayOutputIndex };
