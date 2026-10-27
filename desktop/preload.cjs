'use strict';

const { contextBridge, ipcRenderer, webUtils } = require('electron');
const listeners = new Set();
const emit = message => { for (const listener of listeners) listener({ data: message }); };
const failure = (request, error) => emit({ type: 'reply', id: request?.id, ok: false, error: error.message });
ipcRenderer.on('termlume:event', (_event, message) => emit(message));
const notifyFocus = () => ipcRenderer.send('termlume:textInput', Boolean(document.activeElement?.matches('input, select, [contenteditable="true"], textarea:not(.xterm-helper-textarea)')));
document.addEventListener('focusin', notifyFocus);
document.addEventListener('focusout', () => queueMicrotask(notifyFocus));

contextBridge.exposeInMainWorld('termlume', {
  platform: process.platform,
  postMessage(request) {
    ipcRenderer.invoke('termlume:request', request).then(emit, error => failure(request, error));
  },
  postMessageWithAdditionalObjects(request, objects) {
    try {
      if (!Array.isArray(objects) || !objects.length || objects.length > 100) throw new Error('Drop ordinary files only');
      const paths = objects.map(file => webUtils.getPathForFile(file));
      if (paths.some(value => !value)) throw new Error('Drop files from Finder');
      ipcRenderer.invoke('termlume:drop', request, paths).then(emit, error => failure(request, error));
    } catch (error) { failure(request, error); }
  },
  addEventListener(type, listener) { if (type === 'message' && typeof listener === 'function') listeners.add(listener); },
  removeEventListener(type, listener) { if (type === 'message') listeners.delete(listener); }
});
