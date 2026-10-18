// Bridge exposed to the game page as window.inkwaveNative (desktop app only; the web build never sees it).
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('inkwaveNative', {
  platform: process.platform,
  isFullScreen: () => ipcRenderer.sendSync('fs:get'),
  setFullScreen: (on) => ipcRenderer.send('fs:set', !!on),
  onFullScreenChange: (cb) => { ipcRenderer.on('fs:changed', (_e, on) => cb(!!on)); },
});

// Steamworks (desktop app only). Every call is a no-op when the Steamworks SDK is not installed — the page can
// always call these without checking first. `info()` is the only one worth awaiting before using the rest.
contextBridge.exposeInMainWorld('inkwaveSteam', {
  info: () => ipcRenderer.invoke('steam:info'),
  unlock: (name) => ipcRenderer.invoke('steam:unlock', name),
  unlocked: (name) => ipcRenderer.invoke('steam:unlocked', name),
  all: () => ipcRenderer.invoke('steam:all'),
  presence: (key, value) => ipcRenderer.invoke('steam:presence', key, value),
  clearPresence: () => ipcRenderer.invoke('steam:clearPresence'),
  overlay: (dialog) => ipcRenderer.invoke('steam:overlay', dialog),
  cloudRead: () => ipcRenderer.invoke('steam:cloudRead'),
  cloudWrite: (data) => ipcRenderer.invoke('steam:cloudWrite', data),
  stat: (key, value) => ipcRenderer.invoke('steam:stat', key, value),
});
