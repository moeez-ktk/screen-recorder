'use strict';
/**
 * The only surface the renderers get. Context isolation is on and Node is off
 * in every window, so this explicit allowlist is the whole API.
 */
const { contextBridge, ipcRenderer } = require('electron');

const listeners = new Map();

function on(channel, fn) {
  const wrapped = (_e, payload) => fn(payload);
  ipcRenderer.on(channel, wrapped);
  listeners.set(fn, { channel, wrapped });
  return () => off(channel, fn);
}

function off(channel, fn) {
  const rec = listeners.get(fn);
  if (rec) { ipcRenderer.off(rec.channel, rec.wrapped); listeners.delete(fn); }
}

contextBridge.exposeInMainWorld('api', {
  // ---- state ----
  getState: () => ipcRenderer.invoke('app:getState'),
  onState: fn => on('state', fn),
  onProgress: fn => on('progress', fn),
  onToast: fn => on('toast', fn),
  onSaved: fn => on('saved', fn),

  // ---- settings ----
  getSettings: () => ipcRenderer.invoke('settings:get'),
  setSettings: patch => ipcRenderer.invoke('settings:set', patch),
  resetSettings: () => ipcRenderer.invoke('settings:reset'),
  chooseFolder: () => ipcRenderer.invoke('settings:chooseFolder'),
  chooseFfmpeg: () => ipcRenderer.invoke('settings:chooseFfmpeg'),
  redetectEncoders: () => ipcRenderer.invoke('encoders:redetect'),

  // ---- sources ----
  listSources: () => ipcRenderer.invoke('source:list'),
  selectSource: src => ipcRenderer.invoke('source:select', src),
  defaultSource: () => ipcRenderer.invoke('source:default'),

  // ---- recording ----
  start: () => ipcRenderer.invoke('rec:start'),
  stop: () => ipcRenderer.invoke('rec:stop'),
  toggle: () => ipcRenderer.invoke('rec:toggle'),

  // ---- audio ----
  toggleMic: () => ipcRenderer.invoke('audio:toggleMic'),
  toggleSystemAudio: () => ipcRenderer.invoke('audio:toggleSystem'),
  listAudioDevices: () => ipcRenderer.invoke('audio:listDevices'),
  mixerList: () => ipcRenderer.invoke('mixer:list'),
  setAppMuted: (pid, muted) => ipcRenderer.invoke('mixer:setAppMuted', { pid, muted }),
  setTabMuted: (tabId, muted) => ipcRenderer.invoke('mixer:setTabMuted', { tabId, muted }),

  // ---- hotkey listener ----
  listenerStatus: () => ipcRenderer.invoke('listener:status'),
  setListenerAutostart: on => ipcRenderer.invoke('listener:setAutostart', on),
  startListener: () => ipcRenderer.invoke('listener:start'),

  // ---- windows / shell ----
  openPicker: () => ipcRenderer.invoke('ui:openPicker'),
  openSettings: () => ipcRenderer.invoke('ui:openSettings'),
  openMixer: () => ipcRenderer.invoke('ui:openMixer'),
  toggleCompact: () => ipcRenderer.invoke('ui:toggleCompact'),
  closeWindow: () => ipcRenderer.invoke('ui:close'),
  hideOverlay: () => ipcRenderer.invoke('ui:hideOverlay'),
  quit: () => ipcRenderer.invoke('ui:quit'),
  openFolder: () => ipcRenderer.invoke('shell:openFolder'),
  reveal: file => ipcRenderer.invoke('shell:reveal', file),
  openExternal: url => ipcRenderer.invoke('shell:openExternal', url),
  extensionPath: () => ipcRenderer.invoke('shell:extensionPath'),
  openExtensionFolder: () => ipcRenderer.invoke('shell:openExtensionFolder'),

  off
});

/**
 * Private channel for the hidden audio window only. Kept separate so the
 * visible UI has no way to push raw PCM at the recorder.
 */
contextBridge.exposeInMainWorld('audioBridge', {
  onStart: fn => ipcRenderer.on('audio:start', (_e, s) => fn(s)),
  onConfig: fn => ipcRenderer.on('audio:config', (_e, s) => fn(s)),
  onEnumerate: fn => ipcRenderer.on('audio:enumerate', () => fn()),
  ready: payload => ipcRenderer.send('audio:ready', payload),
  devices: list => ipcRenderer.send('audio:devices', list),
  error: msg => ipcRenderer.send('audio:error', msg),
  pcm: buf => ipcRenderer.send('audio:pcm', buf)
});
