'use strict';
/**
 * Source picker. Thumbnails are fetched only while this window is open, and
 * the window is destroyed on close, so none of this costs anything during a
 * recording.
 */

const $ = id => document.getElementById(id);

let data = { screens: [], windows: [], tabs: [], bridgeConnected: false };
let active = 'screens';
let chosen = null;

// ---------------------------------------------------------------- rendering

function card(sel, { thumb, icon, title, meta, badge }) {
  const el = document.createElement('button');
  el.className = 'card' + (chosen && chosen.id === sel.id ? ' selected' : '');
  el.innerHTML = `
    <div class="thumb">${thumb ? `<img src="${thumb}" alt="">` : 'No preview'}</div>
    <div class="card-body">
      ${icon ? `<img class="favicon" src="${icon}" alt="">` : ''}
      <div class="grow" style="min-width:0">
        <div class="card-title truncate">${escapeHtml(title)}</div>
        ${meta ? `<div class="card-meta truncate">${escapeHtml(meta)}</div>` : ''}
      </div>
      ${badge ? `<span class="badge ${badge.cls || ''}">${escapeHtml(badge.text)}</span>` : ''}
    </div>`;
  el.addEventListener('click', () => select(sel));
  el.addEventListener('dblclick', () => { select(sel); use(true); });
  return el;
}

function renderScreens() {
  const grid = document.createElement('div');
  grid.className = 'grid';
  for (const s of data.screens) {
    grid.appendChild(card(s, {
      thumb: s.thumbnail,
      title: s.name,
      meta: s.width ? `${s.width} × ${s.height}` : ''
    }));
  }
  return data.screens.length ? grid : emptyMsg('No displays were detected.');
}

function renderWindows() {
  const grid = document.createElement('div');
  grid.className = 'grid';
  for (const w of data.windows) {
    grid.appendChild(card(w, { thumb: w.thumbnail, icon: w.appIcon, title: w.name }));
  }
  return data.windows.length ? grid : emptyMsg('No open windows were found.');
}

function renderBrowser() {
  if (!data.bridgeConnected) {
    return emptyMsg(
      'The browser extension is not connected.',
      `Recording a single tab — and muting tabs individually — has to happen inside the
       browser, because Windows cannot separate one tab's audio from another's.
       <br><br>Load the extension from <code id="extPath">the extension folder</code> via
       <code>chrome://extensions</code> → Developer mode → Load unpacked.`,
      'Open extension folder',
      () => window.api.openExtensionFolder()
    );
  }
  if (!data.tabs.length) return emptyMsg('Connected, but no recordable tabs were reported.');

  const grid = document.createElement('div');
  grid.className = 'grid';
  for (const t of data.tabs) {
    grid.appendChild(card(
      { kind: 'tab', id: `tab:${t.id}`, tabId: t.id, name: t.title },
      {
        thumb: null,
        icon: t.favIconUrl,
        title: t.title || t.url,
        meta: t.url,
        badge: t.audible ? { text: 'audio', cls: 'audible' } : null
      }
    ));
  }
  return grid;
}

function emptyMsg(headline, detail, actionLabel, onAction) {
  const el = document.createElement('div');
  el.className = 'empty';
  el.innerHTML = `<strong>${escapeHtml(headline)}</strong>${detail ? `<div>${detail}</div>` : ''}`;
  if (actionLabel) {
    const b = document.createElement('button');
    b.className = 'btn';
    b.textContent = actionLabel;
    b.addEventListener('click', onAction);
    el.appendChild(b);
  }
  return el;
}

function render() {
  const panel = $('panel');
  panel.replaceChildren(
    active === 'screens' ? renderScreens() :
    active === 'windows' ? renderWindows() : renderBrowser()
  );

  for (const t of document.querySelectorAll('.tab')) {
    t.classList.toggle('active', t.dataset.tab === active);
    const counts = { screens: data.screens.length, windows: data.windows.length, tabs: data.tabs.length };
    const n = t.dataset.tab === 'browser' ? counts.tabs : counts[t.dataset.tab];
    let badge = t.querySelector('.count');
    if (n) {
      if (!badge) { badge = document.createElement('span'); badge.className = 'count'; t.appendChild(badge); }
      badge.textContent = n;
    } else if (badge) badge.remove();
  }
}

function select(src) {
  chosen = src;
  $('selected').textContent = sourceLabel(src);
  $('btnUse').disabled = false;
  $('btnRecord').disabled = false;
  render();
}

// ------------------------------------------------------------------ actions

async function load() {
  $('panel').replaceChildren(emptyMsg('Looking for sources…'));
  data = await window.api.listSources();
  render();
}

async function use(closeAfter) {
  if (!chosen) return;
  await window.api.selectSource(chosen);
  if (closeAfter) window.api.closeWindow();
}

async function recordNow() {
  if (!chosen) return;
  await window.api.selectSource(chosen);
  const r = await window.api.start();
  if (r && r.ok) window.api.closeWindow();
}

// ------------------------------------------------------------------- wiring

for (const t of document.querySelectorAll('.tab')) {
  t.addEventListener('click', () => { active = t.dataset.tab; render(); });
}
$('btnRefresh').addEventListener('click', load);
$('btnClose').addEventListener('click', () => window.api.closeWindow());
$('btnCancel').addEventListener('click', () => window.api.closeWindow());
$('btnUse').addEventListener('click', () => use(true));
$('btnRecord').addEventListener('click', recordNow);

document.addEventListener('keydown', e => {
  if (e.key === 'Escape') window.api.closeWindow();
  if (e.key === 'Enter' && chosen) recordNow();
});

// Reflect the source already in use so the picker opens on the current choice.
window.api.getState().then(s => {
  if (s.source) {
    chosen = s.source;
    $('selected').textContent = sourceLabel(s.source);
    $('btnUse').disabled = false;
    $('btnRecord').disabled = false;
    if (s.source.kind === 'window') active = 'windows';
    else if (s.source.kind === 'tab') active = 'browser';
  }
  load();
});

initToasts();
