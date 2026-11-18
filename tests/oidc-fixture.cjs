'use strict';

// 隔离测试专用的 Aegis-IAM 协议夹具，不读取真实账号、数据库或签名密钥。
const http = require('node:http');
const net = require('node:net');
const crypto = require('node:crypto');
const assert = require('node:assert/strict');

async function callbackUri() {
  const listener = net.createServer();
  await new Promise(resolve => listener.listen(0, '127.0.0.1', resolve));
  const port = listener.address().port;
  await new Promise(resolve => listener.close(resolve));
  return `http://127.0.0.1:${port}/callback/`;
}

async function createOidcFixture() {
  const key = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 });
  const other = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 });
  const publicKey = { ...key.publicKey.export({ format: 'jwk' }), kid: 'fixture-key', alg: 'RS256', use: 'sig' };
  const codes = new Map(); const refreshTokens = new Set(); const accessTokens = new Set();
  const requests = [];
  const browserErrors = [];
  let behavior = {};
  let issuer;
  const profile = { sub: 'synthetic-subject', name: 'Synthetic <account>', preferred_username: 'synthetic', email: 'synthetic@example.test', email_verified: false };
  function identityToken(nonce, access) {
    const now = Math.floor(Date.now() / 1000);
    const payload = { ...profile, iss: issuer, aud: 'termlume-desktop', azp: 'termlume-desktop', iat: now, exp: now + 600,
      auth_time: now, amr: ['pwd'], at_hash: crypto.createHash('sha256').update(access).digest().subarray(0, 16).toString('base64url'), ...(nonce ? { nonce } : {}) };
    if (behavior.bad === 'issuer') payload.iss = 'https://invalid.example.test';
    if (behavior.bad === 'audience') payload.aud = 'another-client';
    if (behavior.bad === 'nonce') payload.nonce = 'invalid-nonce';
    if (behavior.bad === 'subject') payload.sub = 'another-subject';
    if (behavior.bad === 'expired') { payload.exp = now - 600; payload.iat = now - 1200; }
    if (behavior.bad === 'hash') payload.at_hash = 'invalid-hash';
    const parts = [Buffer.from(JSON.stringify({ alg: 'RS256', kid: 'fixture-key', typ: 'JWT' })).toString('base64url'), Buffer.from(JSON.stringify(payload)).toString('base64url')].join('.');
    return parts + '.' + crypto.sign('sha256', Buffer.from(parts), behavior.bad === 'signature' ? other.privateKey : key.privateKey).toString('base64url');
  }
  function tokens(nonce, refresh = false) {
    const access = crypto.randomBytes(32).toString('hex'); const rotated = crypto.randomBytes(32).toString('hex');
    accessTokens.add(access); refreshTokens.add(rotated);
    return { access_token: access, refresh_token: rotated, token_type: 'Bearer', scope: 'openid profile email offline_access',
      expires_in: !refresh && behavior.shortExpiry ? 30 : 600,
      ...(behavior.bad === 'missing_id_token' ? {} : { id_token: identityToken(nonce, access) }) };
  }
  const server = http.createServer(async (req, res) => {
    let body = ''; for await (const part of req) body += part;
    const url = new URL(req.url, issuer);
    const parameters = new URLSearchParams(body);
    const send = (status, value) => { res.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' }); res.end(JSON.stringify(value)); };
    requests.push({ path: url.pathname, method: req.method, body: Object.fromEntries(parameters), authorization: req.headers.authorization });
    if (url.pathname === '/test/behavior') { behavior = JSON.parse(body || '{}'); send(200, {}); return; }
    if (url.pathname === '/test/stats') { send(200, { requests }); return; }
    if (url.pathname === '/.well-known/openid-configuration') {
      if (behavior.discoveryFailure) { send(503, { error: 'temporarily_unavailable' }); return; }
      send(200, { issuer, authorization_endpoint: issuer + '/oauth/authorize', token_endpoint: issuer + '/oauth/token', userinfo_endpoint: issuer + '/oauth/userinfo',
        jwks_uri: issuer + '/.well-known/jwks.json', revocation_endpoint: issuer + '/oauth/revoke', response_types_supported: ['code'],
        subject_types_supported: ['public'], id_token_signing_alg_values_supported: ['RS256'], code_challenge_methods_supported: ['S256'], token_endpoint_auth_methods_supported: ['none'] }); return;
    }
    if (url.pathname === '/.well-known/jwks.json') { send(200, { keys: [publicKey] }); return; }
    if (url.pathname === '/oauth/authorize') {
      assert.equal(url.searchParams.get('response_type'), 'code'); assert.equal(url.searchParams.get('client_id'), 'termlume-desktop');
      assert.equal(url.searchParams.get('scope'), 'openid profile email offline_access'); assert.equal(url.searchParams.get('prompt'), 'login');
      assert.equal(url.searchParams.get('code_challenge_method'), 'S256'); assert.ok(url.searchParams.get('nonce').length >= 32);
      const redirect = new URL(url.searchParams.get('redirect_uri'));
      redirect.searchParams.set('state', url.searchParams.get('state')); redirect.searchParams.set('iss', issuer);
      if (behavior.deny) redirect.searchParams.set('error', 'access_denied');
      else {
        const code = crypto.randomBytes(32).toString('hex');
        codes.set(code, { challenge: url.searchParams.get('code_challenge'), nonce: url.searchParams.get('nonce'), redirectUri: url.searchParams.get('redirect_uri') });
        redirect.searchParams.set('code', code);
      }
      res.writeHead(302, { Location: redirect.href }); res.end(); return;
    }
    if (url.pathname === '/oauth/token') {
      assert.equal(parameters.get('client_id'), 'termlume-desktop'); assert.equal(parameters.has('client_secret'), false);
      if (parameters.get('grant_type') === 'authorization_code') {
        const code = codes.get(parameters.get('code'));
        if (!code) { send(400, { error: 'invalid_grant' }); return; }
        assert.equal(parameters.get('redirect_uri'), code.redirectUri);
        assert.equal(crypto.createHash('sha256').update(parameters.get('code_verifier')).digest('base64url'), code.challenge);
        codes.delete(parameters.get('code')); send(200, tokens(code.nonce)); return;
      }
      const token = parameters.get('refresh_token');
      if (!refreshTokens.delete(token) || behavior.revoke) { send(400, { error: 'invalid_grant' }); return; }
      send(200, tokens(null, true)); return;
    }
    if (url.pathname === '/oauth/userinfo') {
      if (behavior.revoke || !accessTokens.has((req.headers.authorization || '').replace(/^Bearer /, ''))) { send(401, { error: 'invalid_token' }); return; }
      send(200, { ...profile, ...(behavior.bad === 'userinfo' ? { sub: 'another-subject' } : {}) }); return;
    }
    if (url.pathname === '/oauth/revoke') {
      assert.equal(parameters.get('client_id'), 'termlume-desktop');
      refreshTokens.clear(); accessTokens.clear(); send(behavior.revokeFailure ? 503 : 200, {}); return;
    }
    send(404, {});
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  issuer = `http://127.0.0.1:${server.address().port}`;
  return { issuer, requests, browserErrors, setBehavior: value => { behavior = value; }, callbackUri,
    async openBrowser(raw) {
      const result = await fetch(raw, { redirect: 'manual' }); assert.equal(result.status, 302);
      // 启动浏览器后立即返回，让后端开始读取回环请求。
      void fetch(result.headers.get('location')).then(callback => assert.equal(callback.status, 200)).catch(error => browserErrors.push(error));
    },
    async close() { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); } };
}

module.exports = { createOidcFixture, callbackUri };
if (require.main === module) {
  createOidcFixture().then(fixture => {
    console.log(fixture.issuer);
    process.stdin.resume(); process.stdin.on('end', async () => { await fixture.close(); });
  });
}
