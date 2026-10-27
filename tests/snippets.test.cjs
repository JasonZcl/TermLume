'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { test } = require('node:test');

const source = fs.readFileSync(path.join(__dirname, '../TermLume/Web/snippets.js'), 'utf8');
const escapeHtml = value => String(value ?? '').replace(/[&<>"']/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[character]);

function fixture() {
  const sent = [];
  const pasted = [];
  const shown = [];
  const notices = [];
  const prompts = [];
  const session = { status: 'connected', host: { name: 'Synthetic server', username: 'test', address: 'example.test', port: 22 }, terminal: { paste: command => pasted.push(command) } };
  const context = vm.createContext({
    sessions: new Map([['session-a', session]]), activeSession: 'session-a',
    t: (text, ...values) => text.replace(/\{(\d+)\}/g, (match, index) => values[index] ?? match),
    request: async (op, payload) => sent.push({ op, ...payload }),
    confirmAction: async (title, description) => { prompts.push({ title, description }); return true; },
    showSession: id => shown.push(id), toast: (...notice) => notices.push(notice),
    escapeHtml, ui: (strings, ...values) => strings.reduce((text, part, index) => text + part + (values[index] ?? ''), ''),
    editorHeader: (title, description) => `<header>${title}<p>${description}</p></header>`,
    editorFooter: label => `<button>${label}</button>`,
    openEditor: html => { context.editorHtml = html; }
  });
  vm.runInContext(source, context);
  const api = vm.runInContext('({ snippetCommand, applySnippet, openSnippetLauncher })', context);
  return { api, context, sent, pasted, shown, notices, prompts, session };
}

test('single-line insertion pastes into the chosen terminal without Enter', async () => {
  const state = fixture();
  assert.equal(await state.api.applySnippet({ command: 'df -h' }, 'session-a', 'insert'), true);
  assert.deepEqual(state.pasted, ['df -h']);
  assert.equal(state.sent.length, 0);
  assert.equal(state.prompts.length, 0);
  assert.deepEqual(state.shown, ['session-a']);
});

test('multiline insertion is rejected even for CRLF input', async () => {
  const state = fixture();
  await assert.rejects(state.api.applySnippet({ command: 'pwd\r\ndf -h' }, 'session-a', 'insert'), /多行/);
  assert.equal(state.pasted.length + state.sent.length, 0);
});

test('confirmed execution submits the previewed command to the selected session', async () => {
  const state = fixture();
  assert.equal(await state.api.applySnippet({ command: 'pwd\r\ndf -h\r\n' }, 'session-a', 'run'), true);
  assert.equal(state.prompts.length, 1);
  assert.match(state.prompts[0].description, /Synthetic server/);
  assert.match(state.prompts[0].description, /test@example.test:22/);
  assert.match(state.prompts[0].description, /pwd\ndf -h/);
  assert.deepEqual(state.sent, [{ op: 'input', sessionId: 'session-a', data: 'pwd\rdf -h\r' }]);
  assert.equal(state.pasted.length, 0);
});

test('cancelled confirmation never sends or pastes commands', async () => {
  const state = fixture();
  state.context.confirmAction = async () => false;
  assert.equal(await state.api.applySnippet({ command: 'echo synthetic' }, 'session-a', 'run'), false);
  assert.equal(state.sent.length + state.pasted.length + state.shown.length, 0);
});

test('disconnect while confirmation is open prevents execution', async () => {
  const state = fixture();
  state.context.confirmAction = async () => { state.session.status = 'closed'; return true; };
  await assert.rejects(state.api.applySnippet({ command: 'pwd' }, 'session-a', 'run'), /已断开/);
  assert.equal(state.sent.length, 0);
});

test('replacing a session while confirming cannot redirect execution', async () => {
  const state = fixture();
  state.context.confirmAction = async () => { state.context.sessions.set('session-a', { ...state.session }); return true; };
  await assert.rejects(state.api.applySnippet({ command: 'pwd' }, 'session-a', 'run'), /已断开/);
  assert.equal(state.sent.length, 0);
});

test('missing and disconnected sessions cannot receive snippets', async () => {
  const state = fixture();
  await assert.rejects(state.api.applySnippet({ command: 'pwd' }, 'missing', 'insert'), /已断开/);
  state.session.status = 'closed';
  await assert.rejects(state.api.applySnippet({ command: 'pwd' }, 'session-a', 'run'), /已断开/);
  assert.equal(state.sent.length + state.pasted.length, 0);
});

for (const [name, command] of [
  ['empty', ''], ['whitespace', '  \n  '], ['oversized', 'x'.repeat(20001)],
  ['NUL', 'pwd\x00'], ['ESC', 'pwd\x1b[31m'], ['DEL', 'pwd\x7f'],
  ['tab completion', 'pwd\t'], ['bare carriage return', 'pwd\rwhoami'], ['C1 control', 'pwd\x9b']
]) {
  test(`${name} cannot be sent in either mode`, async () => {
    const state = fixture();
    for (const mode of ['insert', 'run']) await assert.rejects(state.api.applySnippet({ command }, 'session-a', mode));
    assert.equal(state.sent.length + state.pasted.length, 0);
  });
}

test('Unicode commands and the 20000-character limit remain supported', () => {
  const { api } = fixture();
  assert.equal(api.snippetCommand('echo 你好', 'insert'), 'echo 你好');
  assert.equal(api.snippetCommand('x'.repeat(20000), 'run').length, 20000);
});

test('launcher escapes names, commands, endpoints and identifiers', () => {
  const state = fixture();
  state.session.host.name = '<img src=x onerror=alert(1)>';
  state.api.openSnippetLauncher({ name: '<svg onload=alert(1)>', command: '</textarea><script>bad()</script>' });
  assert.doesNotMatch(state.context.editorHtml, /<svg|<script|<img/);
  assert.match(state.context.editorHtml, /&lt;svg/);
  assert.match(state.context.editorHtml, /&lt;\/textarea&gt;/);
  assert.match(state.context.editorHtml, /value="session-a" selected/);
});

test('multiple sessions without a known active target require explicit selection', () => {
  const state = fixture();
  state.context.activeSession = null;
  state.context.sessions.set('session-b', { ...state.session });
  state.api.openSnippetLauncher({ name: 'Synthetic', command: 'pwd' });
  assert.doesNotMatch(state.context.editorHtml, /value="session-[ab]" selected/);
});

test('launcher uses the active session instead of the last-created session', () => {
  const state = fixture();
  state.context.sessions.set('session-b', { ...state.session });
  state.api.openSnippetLauncher({ name: 'Synthetic', command: 'pwd' });
  assert.match(state.context.editorHtml, /value="session-a" selected/);
  assert.doesNotMatch(state.context.editorHtml, /value="session-b" selected/);
});

test('launcher excludes closed sessions and offers guidance when offline', () => {
  const state = fixture();
  state.session.status = 'closed';
  state.api.openSnippetLauncher({ name: 'Synthetic', command: 'pwd' });
  assert.equal(state.context.editorHtml, undefined);
  assert.equal(state.notices.length, 1);
  assert.equal(state.sent.length, 0);
});

test('bridge failures are reported rather than claiming execution success', async () => {
  const state = fixture();
  state.context.request = async () => { throw new Error('Synthetic queue failure'); };
  await assert.rejects(state.api.applySnippet({ command: 'pwd' }, 'session-a', 'run'), /queue failure/);
  assert.equal(state.notices.length + state.shown.length, 0);
});
