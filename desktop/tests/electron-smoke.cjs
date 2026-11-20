'use strict';

const { app, dialog, shell, clipboard } = require('electron');
const { createOidcFixture } = require('../../tests/oidc-fixture.cjs');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const assert = require('node:assert/strict');

const root = fs.mkdtempSync(path.join(os.tmpdir(), 'TermLume-desktop-test-'));
app.setPath('appData', root);
app.setPath('userData', path.join(root, 'browser'));
let completed = false;
let clipboardReads = 0;
let clipboardWrites = 0;
let filePickers = 0;
async function captureCurrentPage(window, filename) {
  // Hidden windows may return their previous compositor frame on the first capture.
  await window.webContents.capturePage();
  await new Promise(resolve => setTimeout(resolve, 100));
  fs.writeFileSync(path.join(root, filename), (await window.webContents.capturePage()).toPNG());
}
clipboard.readText = () => { clipboardReads++; return 'synthetic-clipboard-only'; };
clipboard.writeText = () => { clipboardWrites++; };
dialog.showOpenDialog = async () => { filePickers++; return { canceled: true, filePaths: [] }; };
const timeout = setTimeout(() => { console.error('Desktop smoke test timed out'); app.exit(1); }, 30000);
dialog.showErrorBox = (_title, message) => { console.error(message); clearTimeout(timeout); app.exit(1); };
app.on('browser-window-created', (_event, window) => {
  window.show = () => {};
  window.webContents.setBackgroundThrottling(false);
  window.webContents.on('console-message', details => { if (details.level === 'error') console.error(details.message); });
  window.webContents.on('did-fail-load', (_failure, code, message) => { console.error(code, message); app.exit(1); });
  window.webContents.once('did-finish-load', async () => {
    try {
      if (window.webContents.getURL() === 'termlume://app/credential.html') {
        for (let attempt = 0; attempt < 100 && !await window.webContents.executeJavaScript("document.getElementById('cancel').textContent.length > 0"); attempt++) await new Promise(resolve => setTimeout(resolve, 30));
        assert.equal(await window.webContents.executeJavaScript('typeof require'), 'undefined');
        assert.equal(await window.webContents.executeJavaScript("document.getElementById('secret').type"), 'password');
        assert.equal(await window.webContents.executeJavaScript("document.getElementById('cancel').textContent"), 'Cancel');
        await window.webContents.executeJavaScript("document.getElementById('cancel').click()").catch(error => {
          // 提交取消会立即销毁模态窗口，Electron 可能来不及送回 executeJavaScript 的结果。
          if (!window.isDestroyed() || !/Render frame was disposed/.test(error.message)) throw error;
        });
        return;
      }
      assert.equal(window.webContents.getURL(), 'termlume://app/index.html');
      assert.equal(await window.webContents.executeJavaScript('typeof require'), 'undefined');
      assert.equal(await window.webContents.executeJavaScript('window.termlume.platform'), process.platform);
      for (let attempt = 0; attempt < 100 && !await window.webContents.executeJavaScript('!!authenticationState.issuer'); attempt++) await new Promise(resolve => setTimeout(resolve, 30));
      assert.equal(await window.webContents.executeJavaScript('ready'), false);
      assert.equal(await window.webContents.executeJavaScript("document.body.classList.contains('authentication-required')"), true);
      assert.equal(await window.webContents.executeJavaScript("request('workspace').then(() => false, () => true)"), true);
      for (const op of ['paste', 'copy', 'pickKey']) {
        assert.equal(await window.webContents.executeJavaScript(`request('${op}', { text: 'synthetic' }).then(() => false, () => true)`), true, `${op} must require login`);
      }
      assert.equal(clipboardReads + clipboardWrites + filePickers, 0, 'unauthenticated requests must not reach native capabilities');
      fs.writeFileSync(path.join(root, 'login.png'), (await window.webContents.capturePage()).toPNG());
      await window.webContents.executeJavaScript("document.querySelector('[data-action=accountLogin]').click()");
      for (let attempt = 0; attempt < 100 && !await window.webContents.executeJavaScript('ready'); attempt++) await new Promise(resolve => setTimeout(resolve, 50));
      assert.equal(await window.webContents.executeJavaScript('ready'), true);
      assert.equal(await window.webContents.executeJavaScript("request('copy', { text: 'synthetic' })"), true);
      assert.equal(await window.webContents.executeJavaScript("request('paste')"), 'synthetic-clipboard-only');
      assert.equal(await window.webContents.executeJavaScript("request('pickKey')"), null);
      assert.equal(clipboardReads, 1); assert.equal(clipboardWrites, 1); assert.equal(filePickers, 1);
      assert.equal(await window.webContents.executeJavaScript('localeLoadError === null'), true);
      await window.webContents.executeJavaScript("showView('collaboration')");
      for (let attempt = 0; attempt < 100 && !await window.webContents.executeJavaScript('collaborationData !== null || !!collaborationError'); attempt++) await new Promise(resolve => setTimeout(resolve, 20));
      assert.equal(await window.webContents.executeJavaScript('collaborationError'), '');
      assert.equal(await window.webContents.executeJavaScript('collaborationData[0].role'), 'owner');
      assert.equal(await window.webContents.executeJavaScript("document.querySelectorAll('.collaboration-tabs button').length"), 7);
      await captureCurrentPage(window, 'workspaces-0.8.png');
      for (const tab of ['members', 'history', 'devices', 'activity', 'audit', 'trust']) {
        await window.webContents.executeJavaScript(`collaborationTab = '${tab}'; resetCollaboration(); renderCollaboration()`);
        for (let attempt = 0; attempt < 100 && !await window.webContents.executeJavaScript('collaborationData !== null || !!collaborationError'); attempt++) await new Promise(resolve => setTimeout(resolve, 20));
        assert.equal(await window.webContents.executeJavaScript('collaborationError'), '', tab + ' must load through the native API bridge');
        assert.equal(await window.webContents.executeJavaScript("document.querySelector('#page').scrollWidth <= document.querySelector('#page').clientWidth + 2"), true, tab + ' must fit the page');
        if (tab === 'devices') await captureCurrentPage(window, 'devices-0.8.png');
      }
      await window.webContents.executeJavaScript(`collaborationTab = 'workspaces'; collaborationData = [{ id: 'a'.repeat(32), name: '<img src=x onerror=alert(1)>', kind: 'shared', role: 'viewer', canWrite: false, canManage: false }]; renderCollaboration()`);
      assert.equal(await window.webContents.executeJavaScript("document.querySelector('#page img') === null"), true, 'workspace names are escaped');
      await window.webContents.executeJavaScript("settingsSection = 'account'; showView('settings')");
      assert.equal(await window.webContents.executeJavaScript("document.querySelector('[data-collab=export]') !== null && document.querySelector('[data-collab=activityToggle]') !== null"), true);
      await captureCurrentPage(window, 'account-0.8.png');

      assert.equal(await window.webContents.executeJavaScript("JSON.stringify(workspace.hosts)"), '[]');
      await window.webContents.executeJavaScript("showView('settings'); settingsSection = 'privacy'; renderSettings();");
      const cachePath = await window.webContents.executeJavaScript("document.querySelector('.settings-data-path').textContent");
      assert.ok(cachePath.includes(path.sep + 'accounts' + path.sep));
      assert.ok(cachePath.endsWith(path.sep + 'workspace.json'));
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
      await window.webContents.executeJavaScript("settingsSection = 'account'; showView('settings')");
      assert.ok((await window.webContents.executeJavaScript("document.querySelector('.account-details').textContent")).includes('Synthetic <account>'));
      assert.equal(await window.webContents.executeJavaScript("document.querySelector('.account-details account') === null"), true);
      await window.webContents.executeJavaScript('new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))');
      fs.writeFileSync(path.join(root, 'account.png'), (await window.webContents.capturePage()).toPNG());
      await window.webContents.executeJavaScript(`(() => { openEditor(editorHeader('Synthetic private editor', '') + '<input value="synthetic-private-draft">' + editorFooter('Confirm'), async () => {}); globalThis.syntheticPendingConfirmation = confirmAction('Synthetic confirmation', 'synthetic-private-description'); return true; })()`);
      await window.webContents.executeJavaScript("request('authLogout').then(result => receiveAuthentication(result.authentication))");
      assert.equal(await window.webContents.executeJavaScript('ready'), false);
      assert.equal(await window.webContents.executeJavaScript('workspace.hosts.length'), 0);
      assert.equal(await window.webContents.executeJavaScript("document.querySelector('#editor-form').children.length"), 0);
      assert.equal(await window.webContents.executeJavaScript("document.querySelector('#confirm-description').textContent"), '');
      assert.equal(await window.webContents.executeJavaScript('globalThis.syntheticPendingConfirmation'), false);
      for (const op of ['paste', 'copy', 'pickKey']) {
        assert.equal(await window.webContents.executeJavaScript(`request('${op}', { text: 'synthetic' }).then(() => false, () => true)`), true, `${op} must remain protected after logout`);
      }
      assert.equal(clipboardReads, 1); assert.equal(clipboardWrites, 1); assert.equal(filePickers, 1);
      completed = true;
      clearTimeout(timeout);
      console.log(`PASS: isolated Electron login gate, browser callback, account, logout, sandbox, localization, workspace administration and native credentials (${root})`);
      window.close();
    } catch (error) { console.error(error); clearTimeout(timeout); app.exit(1); }
  });
});
app.on('quit', () => { if (!completed) process.exitCode = 1; });
(async () => {
  const oidc = await createOidcFixture();
  process.env.TERMLUME_IAM_ISSUER = oidc.issuer;
  process.env.TERMLUME_IAM_CLIENT_ID = 'termlume-desktop';
  process.env.TERMLUME_IAM_REDIRECT_URI = await oidc.callbackUri();
  process.env.TERMLUME_WORKSPACE_API_URL = oidc.issuer;
  shell.openExternal = raw => oidc.openBrowser(raw);
  app.on('quit', () => { void oidc.close(); });
  require('../main.cjs');
})().catch(error => { console.error(error); app.exit(1); });
