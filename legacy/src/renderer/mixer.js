'use strict';
/**
 * Per-source audio control.
 *
 * Two different mechanisms sit side by side here, because Windows and the
 * browser expose different things:
 *   - Applications: Core Audio session mute. Silences the app in the
 *     recording and on your speakers, and is restored when you stop.
 *   - Browser tabs: the extension's tab mute, same trade-off.
 * Recording a single tab needs neither - only that tab is captured.
 */

const $ = id => document.getElementById(id);

let settings = {};
let refreshTimer = null;

const SPEAKER_ICON = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M4 9v6h4l5 4V5L8 9H4z"/><path d="M17 8.5a5 5 0 0 1 0 7"/></svg>`;
const MIC_ICON = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="2" width="6" height="12" rx="3"/><path d="M5 11a7 7 0 0 0 14 0M12 18v4"/></svg>`;
const APP_ICON = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="18" height="18" rx="3"/><path d="M9 9h6v6H9z"/></svg>`;

function row({ iconHtml, iconUrl, label, meta, live, checked, onToggle, disabled }) {
  const el = document.createElement('div');
  el.className = 'item' + (checked ? '' : ' off');

  const icon = iconUrl
    ? `<img class="icon" src="${iconUrl}" alt="">`
    : `<span class="icon">${iconHtml || APP_ICON}</span>`;

  el.innerHTML = `
    ${live === undefined ? '' : `<span class="dot-live ${live ? '' : 'idle'}"></span>`}
    ${icon}
    <div class="grow" style="min-width:0">
      <div class="label truncate">${escapeHtml(label)}</div>
      ${meta ? `<div class="meta truncate">${escapeHtml(meta)}</div>` : ''}
    </div>
    <label class="switch">
      <input type="checkbox" ${checked ? 'checked' : ''} ${disabled ? 'disabled' : ''}>
      <span></span>
    </label>`;

  const input = el.querySelector('input');
  input.addEventListener('change', () => onToggle(input.checked));
  return el;
}

/** The two capture sources that always exist. */
function renderCapture() {
  const host = $('capture');
  host.replaceChildren(
    row({
      iconHtml: SPEAKER_ICON,
      label: 'System audio',
      meta: settings.systemAudioEnabled ? 'Everything you hear' : 'Not captured',
      checked: settings.systemAudioEnabled && !settings.systemAudioMuted,
      onToggle: async () => { await window.api.toggleSystemAudio(); }
    }),
    row({
      iconHtml: MIC_ICON,
      label: 'Microphone',
      meta: settings.micEnabled ? 'Your voice' : 'Not captured',
      checked: settings.micEnabled && !settings.micMuted,
      onToggle: async () => { await window.api.toggleMic(); }
    })
  );
}

function renderApps(apps, error) {
  const host = $('apps');
  $('appsNote').textContent = error ? '· unavailable' : '';

  if (error) {
    host.replaceChildren(Object.assign(document.createElement('div'), {
      className: 'none',
      textContent: `Could not read audio sessions: ${error}`
    }));
    return;
  }
  if (!apps.length) {
    host.replaceChildren(Object.assign(document.createElement('div'), {
      className: 'none',
      textContent: 'No application is playing audio right now.'
    }));
    return;
  }

  host.replaceChildren(...apps.map(a => row({
    label: a.displayName || a.name,
    meta: a.active ? `pid ${a.pid} · playing` : `pid ${a.pid} · idle`,
    live: a.active,
    checked: !a.muted,
    onToggle: async on => {
      const r = await window.api.setAppMuted(a.pid, !on);
      if (r && r.ok === false) refresh();
    }
  })));
}

function renderTabs(tabs, connected) {
  const host = $('tabs');
  if (!connected) {
    const el = document.createElement('div');
    el.className = 'none';
    el.innerHTML = `Browser extension not connected. Load it from the
      <code>extension</code> folder to mute tabs individually.`;
    host.replaceChildren(el);
    return;
  }
  if (!tabs.length) {
    host.replaceChildren(Object.assign(document.createElement('div'), {
      className: 'none', textContent: 'No tabs reported.'
    }));
    return;
  }

  // Tabs making noise are the ones worth acting on, so float them up.
  const sorted = tabs.slice().sort((a, b) => (b.audible ? 1 : 0) - (a.audible ? 1 : 0));
  host.replaceChildren(...sorted.map(t => row({
    iconUrl: t.favIconUrl,
    label: t.title || t.url,
    meta: t.audible ? 'playing audio' : 'silent',
    live: t.audible,
    checked: !t.muted,
    onToggle: async on => {
      const r = await window.api.setTabMuted(t.id, !on);
      if (r && r.ok === false) refresh();
    }
  })));
}

async function refresh() {
  const [state, mix] = await Promise.all([window.api.getState(), window.api.mixerList()]);
  settings = state.settings || {};
  renderCapture();
  renderApps(mix.apps || [], mix.appsError);
  renderTabs(mix.tabs || [], mix.bridgeConnected);

  $('foot').textContent = state.source && state.source.kind === 'tab'
    ? 'Recording a single tab: only that tab’s audio is captured, so other tabs are already excluded.'
    : 'Muting here silences the app or tab in the recording and on your speakers. Everything is restored when you stop.';
}

// ------------------------------------------------------------------- wiring

$('btnRefresh').addEventListener('click', refresh);
$('btnClose').addEventListener('click', () => window.api.closeWindow());
document.addEventListener('keydown', e => { if (e.key === 'Escape') window.api.closeWindow(); });

window.api.onState(s => { settings = s.settings || {}; renderCapture(); });

// Sessions and tabs come and go while the panel is open; a slow poll keeps it
// honest without being a drain. The window is destroyed when closed.
refreshTimer = setInterval(refresh, 3000);
window.addEventListener('beforeunload', () => clearInterval(refreshTimer));

initToasts();
refresh();
