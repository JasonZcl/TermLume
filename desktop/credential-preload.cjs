'use strict';

const { contextBridge, ipcRenderer } = require('electron');
contextBridge.exposeInMainWorld('credential', {
  initialize: () => ipcRenderer.invoke('termlume:promptInit'),
  submit: value => ipcRenderer.invoke('termlume:promptSubmit', value)
});
