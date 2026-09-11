'use strict';
/* Small helpers shared by every window. */

function fmtDuration(ms) {
  const total = Math.max(0, Math.floor(ms / 1000));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  const p = n => String(n).padStart(2, '0');
  return h > 0 ? `${h}:${p(m)}:${p(s)}` : `${p(m)}:${p(s)}`;
}

function fmtBytes(bytes) {
  if (!bytes) return '0 MB';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let i = 0, n = bytes;
  while (n >= 1024 && i < units.length - 1) { n /= 1024; i++; }
  return `${n < 10 && i > 1 ? n.toFixed(1) : Math.round(n)} ${units[i]}`;
}

/** Human label for whatever the current source is. */
function sourceLabel(src) {
  if (!src) return 'Entire Screen';
  if (src.kind === 'tab') return src.name || 'Browser tab';
  if (src.kind === 'window') return src.name || 'Window';
  return src.name || 'Entire Screen';
}

/** Toast host, created on demand so pages do not need markup for it. */
function initToasts() {
  let host = document.getElementById('toasts');
  if (!host) {
    host = document.createElement('div');
    host.id = 'toasts';
    document.body.appendChild(host);
  }
  window.api.onToast(({ message, kind }) => {
    const el = document.createElement('div');
    el.className = `toast ${kind || 'info'}`;
    el.textContent = message;
    host.appendChild(el);
    // Errors linger; routine confirmations get out of the way quickly.
    const life = kind === 'error' ? 7000 : kind === 'warn' ? 5500 : 3200;
    setTimeout(() => {
      el.style.transition = 'opacity 200ms';
      el.style.opacity = '0';
      setTimeout(() => el.remove(), 220);
    }, life);
  });
}

function escapeHtml(s) {
  return String(s == null ? '' : s).replace(/[&<>"']/g, c =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}
