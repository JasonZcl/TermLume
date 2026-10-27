'use strict';

const { test } = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const { pageUrl, credentialUrl, trustedFrame, validateRequest, resourcePath } = require('../security.cjs');

test('only the application main frame has access to desktop requests', () => {
  const frame = { url: pageUrl };
  const contents = { isDestroyed: () => false, mainFrame: frame };
  assert.equal(trustedFrame({ sender: contents, senderFrame: frame }, contents), true);
  assert.equal(trustedFrame({ sender: contents, senderFrame: { url: pageUrl } }, contents), false);
  assert.equal(trustedFrame({ sender: {}, senderFrame: frame }, contents), false);
  frame.url = credentialUrl;
  assert.equal(trustedFrame({ sender: contents, senderFrame: frame }, contents), false);
  assert.equal(trustedFrame({ sender: contents, senderFrame: frame }, contents, credentialUrl), true);
  frame.url = 'https://example.test/';
  assert.equal(trustedFrame({ sender: contents, senderFrame: frame }, contents), false);
});

test('renderer cannot invoke shutdown, native host calls or spoof file-drop paths', () => {
  for (const op of ['shutdown', 'status', 'credential', 'trustHost', 'sftpDropUpload', 'openExternal', 'exec']) assert.throws(() => validateRequest({ id: 'test', op, payload: {} }));
  assert.deepEqual(validateRequest({ id: 'test', op: 'workspace', payload: {}, nativePaths: ['/secret'] }), { id: 'test', op: 'workspace', payload: {} });
  assert.throws(() => validateRequest({ id: 'test', op: 'input', payload: { data: 'a'.repeat(2 * 1024 * 1024) } }));
  assert.throws(() => validateRequest({ id: 'test', op: 'workspace', payload: [] }));
  assert.throws(() => validateRequest({ id: '', op: 'workspace', payload: {} }));
});

test('native file drops use a distinct allowlisted operation', () => {
  assert.equal(validateRequest({ id: 'test', op: 'sftpDropUpload', payload: { path: '/' } }, true).op, 'sftpDropUpload');
  assert.throws(() => validateRequest({ id: 'test', op: 'sftpTransfer', payload: {} }, true));
});

test('local resource protocol cannot traverse outside shipped assets', () => {
  const root = path.resolve('synthetic-web');
  const desktop = path.resolve('synthetic-desktop');
  assert.equal(resourcePath(pageUrl, root, desktop), path.join(root, 'index.html'));
  assert.equal(resourcePath(credentialUrl, root, desktop), path.join(desktop, 'credential.html'));
  assert.equal(resourcePath('termlume://app/locales/en.json', root, desktop), path.join(root, 'locales/en.json'));
  for (const url of ['https://app/index.html', 'termlume://other/index.html', 'termlume://user@app/index.html', 'termlume://app/%2e%2e%2fsecrets', 'termlume://app/%5c..%5csecrets', 'termlume://app/%00', 'termlume://app/index.html?remote=1']) assert.throws(() => resourcePath(url, root, desktop));
});
