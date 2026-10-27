'use strict';

const { app, dialog } = require('electron');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const assert = require('node:assert/strict');

const root = fs.mkdtempSync(path.join(os.tmpdir(), 'TermLume-desktop-test-'));
app.setPath('appData', root);
app.setPath('userData', path.join(root, 'browser'));
let completed = false;
const timeout = setTimeout(() => { console.error('Desktop smoke test timed out'); app.exit(1); }, 30000);
dialog.showErrorBox = (_title, message) => { console.error(message); clearTimeout(timeout); app.exit(1); };
app.on('browser-window-created', (_event, window) => {
  window.show = () => {};
  window.webContents.on('console-message', details => { if (details.level === 'error') console.error(details.message); });
  window.webContents.on('did-fail-load', (_failure, code, message) => { console.error(code, message); app.exit(1); });
  window.webContents.once('did-finish-load', async () => {
    try {
      if (window.webContents.getURL() === 'termlume://app/credential.html') {
        for (let attempt = 0; attempt < 100 && !await window.webContents.executeJavaScript("document.getElementById('cancel').textContent.length > 0"); attempt++) await new Promise(resolve => setTimeout(resolve, 30));
        assert.equal(await window.webContents.executeJavaScript('typeof require'), 'undefined');
        assert.equal(await window.webContents.executeJavaScript("document.getElementById('secret').type"), 'password');
        assert.equal(await window.webContents.executeJavaScript("document.getElementById('cancel').textContent"), 'Cancel');
        await window.webContents.executeJavaScript("document.getElementById('cancel').click()");
        return;
      }
      assert.equal(window.webContents.getURL(), 'termlume://app/index.html');
      assert.equal(await window.webContents.executeJavaScript('typeof require'), 'undefined');
      assert.equal(await window.webContents.executeJavaScript('window.termlume.platform'), process.platform);
      for (let attempt = 0; attempt < 100 && !await window.webContents.executeJavaScript('ready'); attempt++) await new Promise(resolve => setTimeout(resolve, 50));
      assert.equal(await window.webContents.executeJavaScript('ready'), true);
      assert.equal(await window.webContents.executeJavaScript('localeLoadError === null'), true);
      assert.equal(await window.webContents.executeJavaScript("JSON.stringify(workspace.hosts)"), '[]');
      await window.webContents.executeJavaScript("showView('settings'); settingsSection = 'privacy'; renderSettings();");
      const expected = process.platform === 'darwin' ? '~/Library/Application Support/TermLume/workspace.json' : '%LOCALAPPDATA%\\TermLume\\workspace.json';
      assert.ok((await window.webContents.executeJavaScript("document.querySelector('.settings-data-path').textContent")).includes(expected));
      await window.webContents.executeJavaScript("updatePreferences({ language: 'en' })");
      assert.equal(await window.webContents.executeJavaScript('document.documentElement.lang'), 'en');
      assert.equal(await window.webContents.executeJavaScript("(async () => JSON.stringify(await request('sftpDefaultLocal')).includes('computer:'))()"), true);
      assert.equal(await window.webContents.executeJavaScript(`(async () => {
        const saved = await request('saveHost', { name: 'Synthetic cancelled host', address: '127.0.0.1', port: 9, username: 'synthetic', authType: 'password', group: 'Test', note: '', privateKeyPath: '' });
        try { await request('connect', { hostId: saved.hosts[0].id, sessionId: crypto.randomUUID() }); return false; }
        catch { return true; }
      })()`), true);
      const context = window.webContents.getLastWebPreferences();
      assert.equal(context.nodeIntegration, false); assert.equal(context.contextIsolation, true); assert.equal(context.sandbox, true);
      completed = true;
      clearTimeout(timeout);
      console.log(`PASS: isolated Electron workspace, custom protocol, sandbox, localization, native credential cancellation and bridge (${root})`);
      window.close();
    } catch (error) { console.error(error); clearTimeout(timeout); app.exit(1); }
  });
});
app.on('quit', () => { if (!completed) process.exitCode = 1; });
require('../main.cjs');
