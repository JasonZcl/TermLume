'use strict';

const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../TermLume.Windows/Web/authentication.js'), 'utf8');

function fixture() {
  const context = vm.createContext({ document: { addEventListener() {} }, t: value => value, ready: false });
  vm.runInContext(source, context);
  vm.runInContext(`
    let locks = 0;
    const loads = [];
    renderAuthentication = () => {};
    lockWorkspace = () => { locks++; authenticationGeneration++; ready = false; };
    unlockWorkspace = () => new Promise((resolve, reject) => loads.push({ resolve, reject, wasReady: ready }));
  `, context);
  return expression => vm.runInContext(expression, context);
}
const account = subject => JSON.stringify({ isAuthenticated: true, issuer: 'https://iam.test', clientId: 'termlume-desktop', user: { subject } });
const settle = () => new Promise(resolve => setImmediate(resolve));

test('an earlier account load failure cannot lock a newly opened account', async () => {
  const run = fixture();
  run(`receiveAuthentication(${account('alpha')}); receiveAuthentication({ isAuthenticated: false, user: null }); receiveAuthentication(${account('beta')}); ready = true; loads[0].reject(new Error('Old account denied'));`);
  await settle();
  assert.equal(run('ready'), true);
  assert.equal(run('locks'), 1);
  assert.equal(run('authenticationError'), '');
  assert.equal(run('authenticationState.user.subject'), 'beta');
});

test('a current account load failure still locks the workspace', async () => {
  const run = fixture();
  run(`receiveAuthentication(${account('alpha')}); loads[0].reject(new Error('Current account denied'));`);
  await settle();
  assert.equal(run('locks'), 1);
  assert.equal(run('ready'), false);
  assert.equal(run('authenticationError'), 'Current account denied');
});

test('replacing the authenticated subject clears the previous workspace before loading', () => {
  const run = fixture();
  run(`authenticationState = ${account('alpha')}; ready = true; receiveAuthentication(${account('beta')});`);
  assert.equal(run('locks'), 1);
  assert.equal(run('loads[0].wasReady'), false);
});
