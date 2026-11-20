'use strict';

const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const crypto = require('node:crypto');
const { spawn } = require('node:child_process');
const { setTimeout: delay } = require('node:timers/promises');
const { Backend } = require('../backend.cjs');
const { createOidcFixture } = require('../../tests/oidc-fixture.cjs');

async function until(predicate, milliseconds = 15000) {
  const limit = Date.now() + milliseconds;
  while (!predicate()) { if (Date.now() >= limit) throw new Error('Timed out waiting for isolated fixture'); await delay(30); }
}

test('stdio bridge preserves SSH, SFTP, credentials, trust, logs and saved data', { timeout: 90000 }, async () => {
  const root = fs.realpathSync(fs.mkdtempSync(path.join(os.tmpdir(), 'TermLume-bridge-test-')));
  const dataDirectory = path.join(root, 'workspace');
  const fixture = spawn(process.env.TERMLUME_TEST_PYTHON || 'python', [path.resolve(__dirname, '../../tests/TermLume.SecurityTests/ssh_fixture.py'), root], { stdio: ['pipe', 'ignore', 'pipe'], windowsHide: true });
  let errors = '';
  fixture.stderr.on('data', chunk => { errors += chunk.toString(); });
  let backend;
  const oidc = await createOidcFixture();
  const originalIssuer = process.env.TERMLUME_IAM_ISSUER;
  const originalClient = process.env.TERMLUME_IAM_CLIENT_ID;
  const originalRedirect = process.env.TERMLUME_IAM_REDIRECT_URI;
  const originalWorkspaceApi = process.env.TERMLUME_WORKSPACE_API_URL;
  process.env.TERMLUME_IAM_ISSUER = oidc.issuer;
  process.env.TERMLUME_IAM_CLIENT_ID = 'termlume-desktop';
  process.env.TERMLUME_IAM_REDIRECT_URI = await oidc.callbackUri();
  process.env.TERMLUME_WORKSPACE_API_URL = oidc.issuer;
  try {
    await until(() => { if (fixture.exitCode !== null) throw new Error(errors); return fs.existsSync(path.join(root, 'port.txt')); });
    const port = Number(fs.readFileSync(path.join(root, 'port.txt'), 'utf8'));
    const runtime = `${process.platform === 'darwin' ? 'osx' : process.platform === 'win32' ? 'win' : 'linux'}-${process.arch}`;
    const executable = path.resolve(__dirname, '../backend', runtime, process.platform === 'win32' ? 'TermLume.DesktopBackend.exe' : 'TermLume.DesktopBackend');
    const events = [];
    const prompts = [];
    let secret = 'test-only';
    let exportPath = path.join(root, 'export.json');
    backend = new Backend(executable, dataDirectory, message => {
      events.push(message);
      if (message.type === 'output') void backend.request({ id: 'ack', op: 'ack', payload: { sessionId: message.sessionId } });
    }, async (operation, payload) => {
      prompts.push({ operation, payload });
      if (operation === 'openBrowser') { await oidc.openBrowser(payload.url); return true; }
      if (operation === 'credential') return secret;
      if (operation === 'trustHost') return true;
      if (operation === 'pickFolder') return root;
      if (operation === 'exportLogs') return exportPath;
      throw new Error('Unexpected native operation');
    }, () => {});
    const call = async (op, payload = {}, nativePaths) => {
      const reply = await backend.request({ id: crypto.randomUUID(), op, payload }, nativePaths);
      assert.equal(reply.ok, true, reply.error);
      return reply.result;
    };
    const denied = await backend.request({ id: 'before-login', op: 'workspace', payload: {} });
    assert.equal(denied.ok, false); assert.equal(denied.errorCode, 'authentication_required');
    await call('authLogin');
    const workspace = await call('workspace');
    assert.equal(workspace.hosts.length, 0);
    await call('saveGroup', { name: 'Synthetic group' });
    const saved = await call('saveHost', { name: 'Loopback only', address: '127.0.0.1', port, username: 'echo', authType: 'password', group: 'Synthetic group', note: '', privateKeyPath: '' });
    const hostId = saved.hosts[0].id;
    secret = null;
    const cancelled = await backend.request({ id: 'cancel', op: 'connect', payload: { hostId, sessionId: crypto.randomUUID() } });
    assert.equal(cancelled.ok, false);
    assert.deepEqual(await call('status'), { ssh: 0, sftp: 0, transfers: 0 });
    secret = 'test-only';
    const sessionId = crypto.randomUUID();
    await call('connect', { hostId, sessionId, columns: 80, rows: 24 });
    assert.ok(events.some(event => event.type === 'connected' && event.sessionId === sessionId));
    assert.ok(prompts.some(prompt => prompt.operation === 'trustHost' && prompt.payload.message.includes('SHA256:')));
    await call('input', { sessionId, data: 'finish\n' });
    await until(() => events.some(event => event.type === 'ended' && event.sessionId === sessionId));
    assert.equal((await call('knownHosts')).length, 1);
    assert.equal((await call('sftpDefaultLocal')).computer, true);
    await call('sftpPickLocal');
    const sftp = await call('sftpConnect', { hostId });
    const source = path.join(root, 'source.txt');
    fs.writeFileSync(source, 'Finder 拖放 → SFTP ✓');
    const invalidPaths = await backend.request({ id: 'invalid-paths', op: 'sftpDropUpload', payload: { sessionId: sftp.sessionId, path: '/' } }, 42);
    assert.equal(invalidPaths.ok, false);
    const forgedPaths = await backend.request({ id: 'forged-paths', op: 'sftpDropUpload', payload: { sessionId: sftp.sessionId, path: '/' }, nativePaths: [source] });
    assert.equal(forgedPaths.ok, false);
    const queued = await call('sftpDropUpload', { sessionId: sftp.sessionId, path: '/' }, [source]);
    assert.ok(queued);
    await until(() => events.some(event => event.type === 'sftpProgress' && event.job.status === 'completed'));
    assert.equal(fs.readFileSync(path.join(root, 'remote/source.txt'), 'utf8'), fs.readFileSync(source, 'utf8'));
    const download = path.join(root, 'downloads'); fs.mkdirSync(download);
    await call('sftpTransfer', { sessionId: sftp.sessionId, direction: 'download', path: '/source.txt', localPath: download });
    await until(() => fs.existsSync(path.join(download, 'source.txt')));
    assert.deepEqual(fs.readFileSync(path.join(download, 'source.txt')), fs.readFileSync(source));
    await until(() => events.filter(event => event.type === 'sftpProgress' && event.job.status === 'completed').length === 2);
    const logs = await call('logs');
    const entries = logs.entries;
    assert.ok(entries.length);
    assert.equal(await call('exportLogs', { ids: entries.map(entry => entry.id) }), true);
    assert.ok(fs.existsSync(exportPath));
    const scopedDirectory = (await call('workspaceSync')).dataDirectory;
    assert.ok(scopedDirectory.startsWith(path.join(dataDirectory, 'accounts') + path.sep));
    exportPath = path.join(scopedDirectory, 'workspace.json');
    const before = fs.readFileSync(exportPath);
    const blocked = await backend.request({ id: 'blocked-export', op: 'exportLogs', payload: { ids: entries.map(entry => entry.id) } });
    assert.equal(blocked.ok, false);
    assert.deepEqual(fs.readFileSync(exportPath), before);
    exportPath = path.join(scopedDirectory, 'WORKSPACE.JSON');
    const caseVariant = await backend.request({ id: 'case-variant-export', op: 'exportLogs', payload: { ids: entries.map(entry => entry.id) } });
    assert.equal(caseVariant.ok, false);
    assert.deepEqual(fs.readFileSync(path.join(scopedDirectory, 'workspace.json')), before);
    exportPath = path.join(dataDirectory, 'workspace-import-owner.json');
    assert.equal((await backend.request({ id: 'claim-export', op: 'exportLogs', payload: { ids: entries.map(entry => entry.id) } })).ok, false);
    assert.equal(fs.existsSync(exportPath), false);
    await backend.close();
    await until(() => backend.stopped);
    let reopenBrowserCalls = 0;
    backend = new Backend(executable, dataDirectory, () => {}, async () => { reopenBrowserCalls++; throw new Error('Restoration must not open an interactive prompt'); }, () => {});
    const restored = (await call('authStatus')).authentication;
    assert.equal(restored.isAuthenticated, true);
    assert.equal(restored.user.subject, 'synthetic-subject');
    assert.equal((await call('workspace')).hosts[0].id, hostId);
    assert.equal(reopenBrowserCalls, 0);
    const signedOut = await call('authLogout');
    assert.equal(signedOut.revocationSucceeded, true);
    assert.deepEqual(await call('status'), { ssh: 0, sftp: 0, transfers: 0 });
    assert.equal((await backend.request({ id: 'after-logout', op: 'workspace', payload: {} })).errorCode, 'authentication_required');
    await backend.close();
    await until(() => backend.stopped);
    for (const name of ['workspace.json', 'activity-log.json']) assert.ok(!fs.readFileSync(path.join(scopedDirectory, name), 'utf8').includes('test-only'));
    backend = new Backend(executable, dataDirectory, () => {}, async (operation, payload) => { if (operation === 'openBrowser') { await oidc.openBrowser(payload.url); return true; } return null; }, () => {});
    assert.equal((await call('authStatus')).authentication.isAuthenticated, false);
    await call('authLogin');
    assert.equal((await call('workspace')).hosts[0].id, hostId);
    assert.equal((await call('knownHosts')).length, 1);
    await call('authLogout');
    await backend.close();
    await until(() => backend.stopped);
    console.log(`Isolated bridge data: ${root}`);
    assert.deepEqual(oidc.browserErrors, []);
  } finally {
    if (backend && !backend.stopped) { backend.child.stdin.end(); await until(() => backend.stopped, 5000).catch(() => backend.child.kill()); }
    fixture.stdin.end('stop\n');
    await until(() => fixture.exitCode !== null, 5000).catch(() => fixture.kill());
    await oidc.close();
    if (originalIssuer === undefined) delete process.env.TERMLUME_IAM_ISSUER; else process.env.TERMLUME_IAM_ISSUER = originalIssuer;
    if (originalClient === undefined) delete process.env.TERMLUME_IAM_CLIENT_ID; else process.env.TERMLUME_IAM_CLIENT_ID = originalClient;
    if (originalRedirect === undefined) delete process.env.TERMLUME_IAM_REDIRECT_URI; else process.env.TERMLUME_IAM_REDIRECT_URI = originalRedirect;
    if (originalWorkspaceApi === undefined) delete process.env.TERMLUME_WORKSPACE_API_URL; else process.env.TERMLUME_WORKSPACE_API_URL = originalWorkspaceApi;
  }
});

test('a corrupted workspace fails closed without overwriting user data', { timeout: 15000 }, async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'TermLume-corrupt-workspace-'));
  const file = path.join(root, 'workspace.json');
  const original = '{synthetic invalid JSON';
  fs.writeFileSync(file, original);
  const runtime = `${process.platform === 'darwin' ? 'osx' : process.platform === 'win32' ? 'win' : 'linux'}-${process.arch}`;
  const executable = path.resolve(__dirname, '../backend', runtime, process.platform === 'win32' ? 'TermLume.DesktopBackend.exe' : 'TermLume.DesktopBackend');
  let failures = 0;
  const backend = new Backend(executable, root, () => {}, () => null, () => { failures++; });
  await assert.rejects(backend.ready, /SSH service stopped/);
  await until(() => backend.child.exitCode !== null || backend.child.signalCode !== null);
  assert.equal(failures, 1);
  assert.equal(fs.readFileSync(file, 'utf8'), original);
  assert.equal(fs.existsSync(path.join(root, 'activity-log.json')), false);
});
