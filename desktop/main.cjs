'use strict';

const { app, BrowserWindow, Menu, ipcMain, dialog, clipboard, protocol, net, session } = require('electron');
const path = require('node:path');
const fs = require('node:fs');
const { pathToFileURL } = require('node:url');
const { Backend } = require('./backend.cjs');
const { pageUrl, credentialUrl, trustedFrame, validateRequest, resourcePath } = require('./security.cjs');

protocol.registerSchemesAsPrivileged([{ scheme: 'termlume', privileges: { standard: true, secure: true, supportFetchAPI: true, corsEnabled: true } }]);
const dataDirectory = path.join(app.getPath('appData'), 'TermLume');
app.setPath('userData', path.join(dataDirectory, 'Desktop'));
app.setName('TermLume');
let mainWindow;
let backend;
let closing = false;
let closed = false;
let language = 'zh-CN';
let editingText = false;
let promptQueue = Promise.resolve();
const prompts = new Map();
const text = (chinese, english) => language === 'en' ? english : chinese;

function requireMain(event) {
  if (!trustedFrame(event, mainWindow?.webContents)) throw new Error('Untrusted desktop frame');
}

ipcMain.on('termlume:textInput', (event, editable) => {
  if (trustedFrame(event, mainWindow?.webContents) && typeof editable === 'boolean') editingText = editable;
});

function secureWindow(window) {
  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  window.webContents.on('will-navigate', event => event.preventDefault());
  window.webContents.on('will-attach-webview', event => event.preventDefault());
}

function enqueuePrompt(callback) {
  const result = promptQueue.then(() => {
    if (closed || !mainWindow || mainWindow.isDestroyed()) return null;
    return callback();
  });
  promptQueue = result.catch(() => {});
  return result;
}

function styledPrompt(payload) {
  return enqueuePrompt(() => new Promise(resolve => {
    const window = new BrowserWindow({
      width: 460, height: payload.mode === 'exit' ? 400 : 430, resizable: false, minimizable: false, maximizable: false,
      parent: mainWindow, modal: true, show: false, title: payload.title || 'TermLume', backgroundColor: '#1e2030',
      webPreferences: { preload: path.join(__dirname, 'credential-preload.cjs'), contextIsolation: true, sandbox: true, nodeIntegration: false, devTools: false, webviewTag: false, spellcheck: false }
    });
    secureWindow(window);
    const contentsId = window.webContents.id;
    prompts.set(contentsId, { window, payload: { ...payload, language }, resolve });
    window.once('closed', () => { prompts.delete(contentsId); resolve(null); });
    window.once('ready-to-show', () => window.show());
    window.loadURL(credentialUrl).catch(() => window.close());
  }));
}

ipcMain.handle('termlume:promptInit', event => {
  const prompt = prompts.get(event.sender.id);
  if (!prompt || !trustedFrame(event, prompt.window.webContents, credentialUrl)) throw new Error('Untrusted prompt');
  return prompt.payload;
});
ipcMain.handle('termlume:promptSubmit', (event, value) => {
  const prompt = prompts.get(event.sender.id);
  if (!prompt || !trustedFrame(event, prompt.window.webContents, credentialUrl)) throw new Error('Untrusted prompt');
  if (value !== null && (typeof value !== 'string' || value.length > 65536)) throw new Error('Invalid credential');
  prompts.delete(event.sender.id);
  prompt.resolve(value);
  prompt.window.close();
});

function refreshMenu() {
  const mac = process.platform === 'darwin';
  Menu.setApplicationMenu(Menu.buildFromTemplate([
    ...(mac ? [{ label: 'TermLume', submenu: [
      { label: text('关于 TermLume', 'About TermLume'), click: () => dialog.showMessageBox(mainWindow, { title: 'TermLume', message: `TermLume ${app.getVersion()}`, detail: 'SSH / SFTP', buttons: [text('关闭', 'Close')] }) },
      { type: 'separator' }, { role: 'hide', label: text('隐藏 TermLume', 'Hide TermLume') }, { role: 'hideOthers', label: text('隐藏其他应用', 'Hide Others') }, { role: 'unhide', label: text('显示全部', 'Show All') },
      { type: 'separator' }, { label: text('退出 TermLume', 'Quit TermLume'), accelerator: 'Command+Q', click: () => app.quit() }
    ] }] : []),
    { label: text('编辑', 'Edit'), submenu: [{ role: 'undo' }, { role: 'redo' }, { type: 'separator' }, { role: 'cut' }, { role: 'copy' }, { role: 'paste' }, { role: 'selectAll' }] },
    { label: text('窗口', 'Window'), submenu: [{ role: 'minimize' }, { role: 'togglefullscreen' }] }
  ]));
}

async function hostRequest(operation, payload = {}) {
  if (closed || !mainWindow || mainWindow.isDestroyed()) return null;
  switch (operation) {
    case 'credential': return styledPrompt({ ...payload, mode: 'credential' });
    case 'pickFolder': return enqueuePrompt(async () => {
      const result = await dialog.showOpenDialog(mainWindow, { title: payload.title, properties: ['openDirectory'] });
      return result.canceled ? null : result.filePaths[0];
    });
    case 'exportLogs': return enqueuePrompt(async () => {
      const result = await dialog.showSaveDialog(mainWindow, { title: payload.title, defaultPath: payload.name, filters: [{ name: 'JSON', extensions: ['json'] }] });
      return result.canceled ? null : result.filePath;
    });
    case 'trustHost': case 'securityNotice': return enqueuePrompt(async () => {
      const trust = operation === 'trustHost';
      const result = await dialog.showMessageBox(mainWindow, { type: trust ? 'warning' : 'error', title: payload.title, message: payload.title, detail: payload.message, buttons: trust ? [text('取消连接', 'Cancel connection'), text('信任并连接', 'Trust and connect')] : [text('关闭', 'Close')], defaultId: 0, cancelId: 0, noLink: true });
      return trust && result.response === 1;
    });
    default: throw new Error('Unknown native operation');
  }
}

ipcMain.handle('termlume:request', async (event, message) => {
  requireMain(event);
  const request = validateRequest(message);
  if (closing) throw new Error(text('应用正在退出。', 'The application is closing.'));
  let result;
  switch (request.op) {
    case 'copy':
      if (typeof request.payload.text !== 'string' || request.payload.text.length > 1024 * 1024) throw new Error('Invalid clipboard text');
      clipboard.writeText(request.payload.text); result = true; break;
    case 'paste': result = clipboard.readText(); break;
    case 'pickKey': result = await enqueuePrompt(async () => {
      const selected = await dialog.showOpenDialog(mainWindow, { title: text('选择 SSH 私钥', 'Select an SSH private key'), properties: ['openFile', 'showHiddenFiles'] });
      return selected.canceled ? null : selected.filePaths[0];
    }); break;
    case 'window':
      switch (request.payload.action) {
        case 'minimize': mainWindow.minimize(); break;
        case 'maximize': mainWindow.isMaximized() ? mainWindow.unmaximize() : mainWindow.maximize(); break;
        case 'close': void requestClose(); break;
        case 'state': break;
        default: throw new Error('Unknown window action');
      }
      result = { maximized: mainWindow.isMaximized() }; break;
    default: {
      const reply = await backend.request(request);
      if (reply.ok && ['workspace', 'preferences'].includes(request.op)) {
        language = reply.result.preferences.language;
        refreshMenu();
      }
      return reply;
    }
  }
  return { type: 'reply', id: request.id, ok: true, result };
});

ipcMain.handle('termlume:drop', async (event, message, paths) => {
  requireMain(event);
  const request = validateRequest(message, true);
  if (closing || !Array.isArray(paths) || paths.length < 1 || paths.length > 100 || paths.some(value => typeof value !== 'string' || !path.isAbsolute(value) || value.includes('\0'))) throw new Error('Invalid native file drop');
  return backend.request(request, paths);
});

async function requestClose() {
  if (closing || closed) return;
  closing = true;
  try {
    const status = await backend.request({ id: 'exit-status', op: 'status', payload: {} });
    if (!status.ok) throw new Error(status.error);
    if (status.result.ssh || status.result.sftp || status.result.transfers) {
      const confirmed = await styledPrompt({ mode: 'exit', title: text('退出 TermLume？', 'Quit TermLume?'), status: status.result });
      if (confirmed !== 'confirm') return;
    }
    const timeout = setTimeout(() => backend.child.kill(), 15000);
    try { await backend.close(); } finally { clearTimeout(timeout); }
    closed = true;
    mainWindow.destroy();
    app.quit();
  } catch {
    if (!backend.stopped) {
      await dialog.showMessageBox(mainWindow, { type: 'error', message: text('无法安全退出，请稍后重试。', 'Unable to quit safely. Please try again.'), buttons: [text('关闭', 'Close')] });
      return;
    }
    closed = true;
    mainWindow.destroy();
    app.quit();
  } finally { if (!closed) closing = false; }
}

if (!app.requestSingleInstanceLock()) app.quit();
else {
  app.on('second-instance', () => { if (mainWindow) { if (mainWindow.isMinimized()) mainWindow.restore(); mainWindow.show(); mainWindow.focus(); } });
  app.on('activate', () => { if (mainWindow && !mainWindow.isDestroyed()) { if (mainWindow.isMinimized()) mainWindow.restore(); mainWindow.show(); mainWindow.focus(); } });
  app.on('before-quit', event => { if (!closed && mainWindow) { event.preventDefault(); void requestClose(); } });
  app.on('window-all-closed', () => { if (closed) app.quit(); });
  app.whenReady().then(async () => {
    const webRoot = app.isPackaged ? path.join(process.resourcesPath, 'Web') : path.resolve(__dirname, '../TermLume.Windows/Web');
    protocol.handle('termlume', async request => {
      try { return await net.fetch(pathToFileURL(resourcePath(request.url, webRoot, __dirname)).toString()); }
      catch { return new Response('Not found', { status: 404 }); }
    });
    session.defaultSession.setPermissionRequestHandler((_contents, _permission, callback) => callback(false));
    session.defaultSession.setPermissionCheckHandler(() => false);
    session.defaultSession.on('will-download', event => event.preventDefault());
    mainWindow = new BrowserWindow({
      width: 1280, height: 820, minWidth: 880, minHeight: 600, title: 'TermLume', backgroundColor: '#1c1d2b', show: false,
      ...(process.platform === 'darwin' ? { titleBarStyle: 'hiddenInset', trafficLightPosition: { x: 16, y: 17 } } : { frame: false }),
      webPreferences: { preload: path.join(__dirname, 'preload.cjs'), contextIsolation: true, sandbox: true, nodeIntegration: false, devTools: false, webviewTag: false, spellcheck: false, navigateOnDragDrop: false }
    });
    secureWindow(mainWindow);
    mainWindow.webContents.on('before-input-event', (_event, input) => {
      const system = input.meta && ['q', 'h', 'm'].includes(input.key.toLowerCase());
      mainWindow.webContents.setIgnoreMenuShortcuts(!system && !editingText && (input.meta || input.control));
    });
    mainWindow.on('close', event => { if (!closed) { event.preventDefault(); void requestClose(); } });
    for (const event of ['maximize', 'unmaximize', 'enter-full-screen', 'leave-full-screen']) mainWindow.on(event, () => {
      mainWindow.webContents.send('termlume:event', { type: 'windowState', maximized: mainWindow.isMaximized(), fullscreen: mainWindow.isFullScreen() });
    });
    mainWindow.webContents.on('render-process-gone', () => { closed = true; backend?.child.kill(); app.quit(); });
    const runtime = `${process.platform === 'darwin' ? 'osx' : process.platform === 'win32' ? 'win' : 'linux'}-${process.arch}`;
    const executable = path.join(app.isPackaged ? path.join(process.resourcesPath, 'backend') : path.join(__dirname, 'backend', runtime), process.platform === 'win32' ? 'TermLume.DesktopBackend.exe' : 'TermLume.DesktopBackend');
    if (!fs.existsSync(executable)) throw new Error('SSH backend is missing. Run npm start or rebuild the application.');
    backend = new Backend(executable, dataDirectory, message => {
      if (!closed && !mainWindow.isDestroyed()) mainWindow.webContents.send('termlume:event', message);
    }, hostRequest, () => {
      if (!closing && !closed) {
        closed = true;
        for (const prompt of prompts.values()) prompt.window.destroy();
        dialog.showErrorBox('TermLume', text('SSH 服务意外退出。请重新打开应用；现有配置不会被覆盖。', 'The SSH service stopped. Restart TermLume; your saved workspace is not overwritten.'));
        app.quit();
      }
    });
    await backend.ready;
    refreshMenu();
    mainWindow.once('ready-to-show', () => mainWindow.show());
    await mainWindow.loadURL(pageUrl);
  }).catch(error => {
    closed = true;
    backend?.child.kill();
    dialog.showErrorBox('TermLume', text('启动失败：', 'Startup failed: ') + error.message);
    app.quit();
  });
}
