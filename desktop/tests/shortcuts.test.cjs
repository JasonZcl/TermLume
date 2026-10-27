'use strict';

const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

function fixture(platform) {
  const context = vm.createContext({ window: { termlume: { platform } }, document: { body: { dataset: {} }, addEventListener() {} }, t: text => text });
  for (const file of ['platform.js', 'preferences.js']) vm.runInContext(fs.readFileSync(path.resolve(__dirname, '../../TermLume.Windows/Web', file), 'utf8'), context);
  return vm.runInContext('({ defaultShortcuts, normalizeShortcut, shortcutLabel, bindingFromEvent, platformDataFile, platformText })', context);
}

test('Windows shortcut defaults remain unchanged', () => {
  const api = fixture('win32');
  assert.equal(api.defaultShortcuts.copy, 'Ctrl+Shift+KeyC');
  assert.equal(api.defaultShortcuts.snippets, 'Ctrl+Shift+KeyS');
  assert.equal(api.platformDataFile('workspace.json'), '%LOCALAPPDATA%\\TermLume\\workspace.json');
  assert.throws(() => api.normalizeShortcut('Meta+KeyN'));
});

test('macOS defaults use Command without taking over app switching or terminal Ctrl+C', () => {
  const api = fixture('darwin');
  assert.equal(api.defaultShortcuts.copy, 'Meta+KeyC');
  assert.equal(api.defaultShortcuts.paste, 'Meta+KeyV');
  assert.equal(api.defaultShortcuts.newHost, 'Meta+KeyN');
  assert.equal(api.defaultShortcuts.nextTab, 'Ctrl+Tab');
  assert.equal(api.defaultShortcuts.closeTab, 'Meta+KeyW');
  assert.equal(api.shortcutLabel('Meta+Alt+KeyN'), '⌘ + ⌥ + N');
  for (const binding of ['Meta+KeyQ', 'Meta+KeyH', 'Meta+KeyM', 'Meta+Tab', 'Meta+Space', 'Ctrl+KeyC']) assert.throws(() => api.normalizeShortcut(binding));
  assert.equal(api.normalizeShortcut('Shift+Meta+Equal'), 'Meta+Equal');
  assert.equal(api.bindingFromEvent({ metaKey: true, code: 'KeyN' }), 'Meta+KeyN');
  assert.equal(api.platformDataFile('workspace.json'), '~/Library/Application Support/TermLume/workspace.json');
  assert.equal(api.platformText('此电脑'), '本机');
});
