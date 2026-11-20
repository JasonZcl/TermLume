'use strict';

// 使用实际 API 进程及本机 Docker PostgreSQL；签名颁发方仅在隔离测试端口运行。
// 密码只从 gitignored 的服务器配置读取，不进入参数、日志或桌面程序。
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const http = require('node:http');
const crypto = require('node:crypto');
const { spawn, spawnSync } = require('node:child_process');
const { setTimeout: delay } = require('node:timers/promises');
const repository = path.resolve(__dirname, '..');
const local = path.join(repository, '.local');
const container = process.env.TERMLUME_TEST_POSTGRES_CONTAINER || 'aegis-iam-test-postgres-1';
const database = process.env.TERMLUME_TEST_DATABASE || 'termlume';
const dockerImage = process.env.TERMLUME_TEST_DOCKER_IMAGE;
if (dockerImage) assert.match(dockerImage, /^[a-z0-9][a-z0-9./:_-]+$/);
assert.match(database, /^[a-z0-9_]+$/);
const literal = value => "'" + value.replaceAll("'", "''") + "'";
function sql(statement, expectFailure = false) {
  const result = spawnSync('docker', ['exec', '-i', container, 'sh', '-c', 'PGPASSWORD="$POSTGRES_PASSWORD" psql -X -qAt -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d ' + database],
    { input: statement, encoding: 'utf8', windowsHide: true, timeout: 15000 });
  if (expectFailure) assert.notEqual(result.status, 0, 'PostgreSQL must reject a forbidden write');
  else assert.equal(result.status, 0, 'PostgreSQL fixture query failed');
  return (result.stdout || '').trim();
}
async function listen(server) {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  return `http://127.0.0.1:${server.address().port}`;
}
async function close(server) { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }

test('PostgreSQL workspace: real persistence, JWT authorization, isolation and concurrency', { timeout: 90000 }, async t => {
  const runtime = JSON.parse(fs.readFileSync(process.env.TERMLUME_TEST_API_SETTINGS || path.join(local, 'api-settings.local.json'), 'utf8'));
  const executable = process.env.TERMLUME_TEST_API_EXECUTABLE || path.join(local, 'api', process.platform === 'win32' ? 'TermLume.Api.exe' : 'TermLume.Api');
  if (!dockerImage) assert.ok(fs.existsSync(executable), 'Run tools/Start-WorkspaceApi.ps1 before the integration test');
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'TermLume-postgres-test-'));
  const rsa = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 });
  const rotated = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 });
  const attacker = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 });
  const jwk = (key, kid) => ({ ...key.publicKey.export({ format: 'jwk' }), kid, use: 'sig', alg: 'RS256' });
  let keys = [jwk(rsa, 'original')];
  const profiles = new Map(); const revoked = new Set();
  let userInfoUnavailable = false; let jwksRequests = 0;
  const issuerServer = http.createServer((request, response) => {
    const send = (status, value) => { response.writeHead(status, { 'Content-Type': 'application/json' }); response.end(JSON.stringify(value)); };
    if (request.url === '/.well-known/jwks.json') { jwksRequests++; send(200, { keys }); return; }
    if (request.url === '/oauth/userinfo') {
      if (userInfoUnavailable) { send(503, {}); return; }
      const token = (request.headers.authorization || '').replace(/^Bearer /, '');
      if (!profiles.has(token) || revoked.has(token)) { send(401, {}); return; }
      send(200, { sub: profiles.get(token) }); return;
    }
    send(404, {});
  });
  const issuer = await listen(issuerServer);
  function issue(subject, { claims = {}, typ = 'at+jwt', signingKey = rsa, kid = 'original' } = {}) {
    const now = Math.floor(Date.now() / 1000);
    const payload = { iss: issuer, sub: subject, aud: ['termlume-desktop'], client_id: 'termlume-desktop', token_use: 'oauth_access',
      sid: crypto.randomUUID(), gid: crypto.randomUUID(), scope: 'openid profile email offline_access', iat: now, exp: now + 600, ...claims };
    const input = [Buffer.from(JSON.stringify({ alg: 'RS256', kid, typ })), Buffer.from(JSON.stringify(payload))].map(value => value.toString('base64url')).join('.');
    const token = input + '.' + crypto.sign('sha256', Buffer.from(input), signingKey.privateKey).toString('base64url');
    profiles.set(token, subject);
    return token;
  }
  const portServer = http.createServer(); const apiUrl = await listen(portServer); await close(portServer);
  runtime.Urls = apiUrl; runtime.Aegis = { Issuer: issuer, ClientId: 'termlume-desktop' };
  // 强制只复用一条连接，验证事务上下文不会跨用户泄漏。
  runtime.ConnectionStrings.Workspace = runtime.ConnectionStrings.Workspace.replace(/Maximum Pool Size=\d+/i, 'Maximum Pool Size=1');
  const dockerName = 'termlume-api-integration-' + crypto.randomUUID();
  if (dockerImage) {
    runtime.Urls = 'http://0.0.0.0:8080';
    runtime.ConnectionStrings.Workspace = runtime.ConnectionStrings.Workspace.replace(/Host=127\.0\.0\.1(?=;)/i, 'Host=host.docker.internal');
    runtime.ConnectionStrings.Workspace += ';GSS Encryption Mode=Disable';
    runtime.Aegis.BackchannelUrl = issuer.replace('127.0.0.1', 'host.docker.internal');
    runtime.Aegis.AllowLocalDockerBackchannel = true;
  }
  const settings = path.join(root, 'api-settings.local.json'); fs.writeFileSync(settings, JSON.stringify(runtime), { mode: 0o600 });
  const startApi = () => dockerImage
    ? spawn('docker', ['run', '--rm', '--name', dockerName, '--publish', `127.0.0.1:${new URL(apiUrl).port}:8080`,
      '--add-host', 'host.docker.internal:host-gateway', '--read-only', '--tmpfs', '/tmp:size=16m,mode=1777',
      '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges:true', '--pids-limit', '128',
      '--volume', `${settings}:/run/secrets/settings.json:ro`, dockerImage],
      { stdio: ['ignore', 'ignore', 'ignore'], windowsHide: true })
    : spawn(executable, ['--settings', settings], { cwd: path.dirname(executable), stdio: ['ignore', 'ignore', 'ignore'], windowsHide: true });
  let child = startApi();
  async function stopApi() {
    if (child.exitCode !== null || child.signalCode !== null) return;
    if (dockerImage) {
      const stopped = spawnSync('docker', ['stop', '--time', '5', dockerName], { encoding: 'utf8', windowsHide: true, timeout: 15000 });
      assert.equal(stopped.status, 0, 'Temporary integration container must stop cleanly');
    } else child.kill();
    if (child.exitCode === null && child.signalCode === null) await new Promise(resolve => child.once('exit', resolve));
  }
  async function healthy() {
    for (let i = 0; ; i++) {
      if (child.exitCode !== null || child.signalCode !== null) throw new Error('API process exited during startup');
      try { if ((await request(null, '/healthz')).status === 200) return; } catch { }
      if (i >= (dockerImage ? 300 : 100)) throw new Error('API did not become healthy');
      await delay(50);
    }
  }
  async function request(token, route = '/v1/workspace', method = 'GET', body, extraHeaders = {}) {
    const response = await fetch(apiUrl + route, { method, redirect: 'manual', signal: AbortSignal.timeout(10000),
      headers: { ...(token ? { Authorization: 'Bearer ' + token } : {}), ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}), ...extraHeaders },
      ...(body !== undefined ? { body: typeof body === 'string' ? body : JSON.stringify(body) } : {}) });
    const text = await response.text(); let data;
    try { data = JSON.parse(text); } catch { data = null; }
    return { status: response.status, data, text, headers: response.headers };
  }
  const alpha = issue('alpha'); const beta = issue('beta');
  let snapshot;
  async function change(operation, payload, token = alpha, platform = 'windows') {
    const current = token === alpha ? snapshot : (await request(token)).data;
    const response = await request(token, '/v1/workspace/changes', 'POST', { expectedRevision: current.revision, operation, payload }, { 'X-TermLume-Platform': platform });
    assert.equal(response.status, 200, `Failed operation: ${operation}`);
    if (token === alpha) snapshot = response.data;
    return response.data;
  }
  try {
    await healthy();
    await t.test('ABP host preserves health and exposes only the desktop API surface', async () => {
      const health = await request(null, '/healthz');
      assert.equal(health.status, 200);
      assert.equal(health.data.framework, 'ABP');
      assert.equal(health.data.version, '0.9.0');
      const capabilities = await request(alpha, '/v1/workspace/actions', 'POST', { action: 'capabilities', payload: {} });
      assert.equal(capabilities.status, 200);
      assert.equal(capabilities.data.version, health.data.version, 'Service metadata must report the deployed API version');
      for (const route of ['/api/abp/application-configuration', '/api/abp/api-definition', '/api/abp/application-localization', '/api/app/workspace']) {
        assert.equal((await request(null, route)).status, 404, 'Framework infrastructure must not expose extra routes');
        assert.equal((await request(alpha, route)).status, 404);
      }
    });
    await t.test('rejects missing, forged, wrong-purpose, expired and foreign-client tokens', async () => {
      const invalid = [null, 'opaque-access-token', issue('alpha', { typ: 'JWT' }), issue('alpha', { claims: { token_use: 'console_access' } }),
        issue('alpha', { signingKey: attacker }), issue('alpha', { claims: { exp: 1 } }), issue('alpha', { claims: { nbf: Math.floor(Date.now() / 1000) + 600 } }),
        issue('alpha', { claims: { iss: 'https://other.example.test' } }), issue('alpha', { claims: { aud: ['other'] } }),
        issue('alpha', { claims: { aud: ['termlume-desktop', 'other'] } }), issue('alpha', { claims: { client_id: 'other' } }),
        issue('alpha', { claims: { sid: '' } }), issue('alpha', { claims: { gid: '' } }), issue('alpha', { claims: { scope: 'profile' } })];
      for (const token of invalid) assert.equal((await request(token)).status, 401);
      for (const route of ['/v1/workspace/changes', '/v1/workspace/import', '/v1/workspace/actions']) {
        for (const token of [null, 'opaque-access-token']) {
          const response = await request(token, route, 'POST', {});
          assert.equal(response.status, 401, 'Authentication must precede MVC input validation');
          assert.equal(response.headers.get('www-authenticate'), 'Bearer');
        }
        assert.equal((await request(alpha, route, 'POST', {}, { Origin: 'http://evil.example.test' })).status, 403);
      }
      assert.equal((await request(alpha, '/v1/workspace', 'GET', undefined, { Origin: 'http://evil.example.test' })).status, 403);
    });
    await t.test('MVC validation preserves safe desktop error responses', async () => {
      for (const body of [{}, { action: 'workspace.list', payload: {}, extra: 'rejected' }, '{invalid-json']) {
        const response = await request(alpha, '/v1/workspace/actions', 'POST', body);
        assert.equal(response.status, 400);
        assert.equal(typeof response.data.error, 'string');
        assert.deepEqual(Object.keys(response.data), ['error']);
        assert.equal(response.text.includes('System.'), false);
      }
      const invalidDevice = await request(alpha, '/v1/workspace', 'GET', undefined, { 'X-TermLume-Device': 'invalid-device' });
      assert.equal(invalidDevice.status, 400);
      assert.equal(typeof invalidDevice.data.error, 'string');
    });
    await t.test('persists hosts, groups, favorites, recent connections and snippets', async () => {
      const initial = await request(alpha); assert.equal(initial.status, 200); snapshot = initial.data;
      assert.equal(snapshot.revision, 0); assert.deepEqual(snapshot.workspace.hosts, []);
      assert.equal(initial.headers.get('cache-control'), 'no-store');
      await change('saveGroup', { name: "Production ' ; DROP TABLE hosts; --" });
      await change('saveHost', { name: 'Alpha host', address: 'alpha.example.test', username: 'root', port: 2222, group: 'Production', authType: 'key', color: 'mint' });
      const host = snapshot.workspace.hosts[0]; assert.equal(host.privateKeyPath, '');
      await change('favorite', { id: host.id }); await change('connected', { id: host.id });
      await change('saveSnippet', { name: 'Unicode snippet', command: 'echo 你好' });
      const reloaded = (await request(alpha)).data;
      assert.equal(reloaded.workspace.hosts[0].favorite, true); assert.ok(reloaded.workspace.hosts[0].lastConnectedAt);
      assert.equal(reloaded.workspace.snippets[0].command, 'echo 你好');
      assert.equal(sql(`SELECT count(*) FROM termlume.hosts h JOIN termlume.workspaces w ON w.id=h.workspace_id JOIN termlume.app_users u ON u.id=w.owner_id WHERE u.issuer=${literal(issuer)}`), '1');
    });
    await t.test('isolates users and ignores forged ownership headers', async () => {
      const ownHost = snapshot.workspace.hosts[0].id; const ownSnippet = snapshot.workspace.snippets[0].id;
      const other = await request(beta, '/v1/workspace', 'GET', undefined, { 'X-User-Id': 'alpha', 'X-Subject': 'alpha' });
      assert.equal(other.status, 200); assert.deepEqual(other.data.workspace.hosts, []);
      for (const [operation, payload] of [['favorite', { id: ownHost }], ['deleteHost', { id: ownHost }],
        ['saveHost', { id: ownHost, name: 'Hijacked', address: 'bad.test' }], ['deleteSnippet', { id: ownSnippet }],
        ['saveSnippet', { id: ownSnippet, name: 'Hijacked', command: 'false' }]]) {
        const response = await request(beta, '/v1/workspace/changes', 'POST', { expectedRevision: 0, operation, payload });
        assert.equal(response.status, 404);
      }
      await change('saveHost', { name: 'Beta host', address: 'beta.example.test' }, beta);
      for (let i = 0; i < 12; i++) {
        assert.equal((await request(alpha)).data.workspace.hosts[0].name, 'Alpha host');
        assert.equal((await request(beta)).data.workspace.hosts[0].name, 'Beta host');
      }
    });
    await t.test('row-level security rejects direct cross-user reads and writes', () => {
      const scope = `BEGIN; SET LOCAL ROLE termlume_app; SELECT set_config('termlume.issuer',${literal(issuer)},true),set_config('termlume.subject','beta',true);`;
      const foreign = `SELECT w.id FROM termlume.workspaces w JOIN termlume.app_users u ON u.id=w.owner_id WHERE u.issuer=${literal(issuer)} AND u.subject='alpha'`;
      const workspaceId = sql(foreign); assert.match(workspaceId, /^[0-9a-f-]+$/);
      const states = sql(`SET ROLE termlume_app; SELECT (SELECT count(*) FROM termlume.app_users),(SELECT count(*) FROM termlume.hosts);`);
      assert.equal(states, '0|0');
      assert.equal(sql(`${scope} SELECT count(*) FROM termlume.hosts WHERE workspace_id=${literal(workspaceId)}; COMMIT; SET ROLE termlume_app; SELECT count(*) FROM termlume.hosts;`).split('\n').slice(-2).join('|'), '0|0');
      assert.equal(sql(`${scope} WITH changed AS (UPDATE termlume.hosts SET name='Hijacked' WHERE workspace_id=${literal(workspaceId)} RETURNING id) SELECT count(*) FROM changed; ROLLBACK;`).split('\n').at(-1), '0');
      sql(`${scope} INSERT INTO termlume.snippets(id,workspace_id,name,command,sort_order) VALUES(gen_random_uuid(),${literal(workspaceId)},'Hijacked','false',0); COMMIT;`, true);
      assert.equal(sql("SELECT rolsuper,rolbypassrls,rolcreatedb,rolcreaterole FROM pg_roles WHERE rolname='termlume_app'"), 'f|f|f|f');
      assert.equal(sql("SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='termlume' AND c.relkind='r' AND c.relrowsecurity"), '16');
      assert.equal(sql("SELECT count(*) FROM information_schema.columns WHERE table_schema='termlume' AND column_name <> 'token_hash' AND column_name ~ '(password|secret|private|token|fingerprint)'"), '0');
    });
    await t.test('simultaneous edits detect conflicts without losing a successful update', async () => {
      const before = (await request(alpha)).data;
      const responses = await Promise.all(['one', 'two'].map(name => request(alpha, '/v1/workspace/changes', 'POST',
        { expectedRevision: before.revision, operation: 'saveSnippet', payload: { name, command: 'echo ' + name } })));
      assert.deepEqual(responses.map(response => response.status).sort(), [200, 409]);
      snapshot = (await request(alpha)).data;
      assert.equal(snapshot.revision, before.revision + 1); assert.equal(snapshot.workspace.snippets.length, before.workspace.snippets.length + 1);
    });
    await t.test('shares portable preferences while separating Windows and macOS shortcuts', async () => {
      const windows = { ...snapshot.workspace.preferences, fontSize: 18, fontFamily: 'consolas', sidebarCollapsed: true,
        shortcuts: { ...snapshot.workspace.preferences.shortcuts, newHost: 'Ctrl+Alt+KeyN' } };
      await change('preferences', windows);
      const mac = (await request(alpha, '/v1/workspace', 'GET', undefined, { 'X-TermLume-Platform': 'macos' })).data;
      assert.equal(mac.workspace.preferences.fontSize, 18); assert.equal(mac.workspace.preferences.fontFamily, 'cascadia');
      assert.equal(mac.workspace.preferences.shortcuts.newHost, 'Meta+KeyN'); assert.equal(mac.workspace.preferences.sidebarCollapsed, false);
      await change('preferences', { ...mac.workspace.preferences, fontSize: 20, shortcuts: { ...mac.workspace.preferences.shortcuts, newHost: 'Meta+Alt+KeyN' } }, alpha, 'macos');
      snapshot = (await request(alpha)).data;
      assert.equal(snapshot.workspace.preferences.fontSize, 20); assert.equal(snapshot.workspace.preferences.shortcuts.newHost, 'Ctrl+Alt+KeyN');
      assert.equal(snapshot.workspace.preferences.fontFamily, 'consolas'); assert.equal(snapshot.workspace.preferences.sidebarCollapsed, true);
    });
    await t.test('rejects secret fields, device paths, malformed imports and oversized bodies', async () => {
      for (const payload of [{ name: 'Invalid', address: 'x.test', password: 'synthetic-only' }, { name: 'Invalid', address: 'x.test', privateKeyPath: 'C:\\private-key' },
        { name: 'Invalid', address: 'x.test', port: 99999 }, { name: 'Invalid\0name', address: 'x.test' }, { name: 'Invalid', address: 'https://x.test/path' }])
        assert.equal((await request(alpha, '/v1/workspace/changes', 'POST', { expectedRevision: snapshot.revision, operation: 'saveHost', payload })).status, 400);
      for (const value of [null, { ...snapshot.workspace, knownHosts: { 'x.test': 'SHA256:local' } },
        { ...snapshot.workspace, hosts: [{ ...snapshot.workspace.hosts[0], id: 'invalid' }] }])
        assert.equal((await request(alpha, '/v1/workspace/import', 'POST', { expectedRevision: snapshot.revision, workspace: value })).status, 400);
      assert.equal((await request(alpha, '/v1/workspace/import', 'POST', { expectedRevision: snapshot.revision })).status, 400);
      assert.equal((await request(alpha, '/v1/workspace/changes', 'POST', { expectedRevision: snapshot.revision, operation: 'saveGroup', payload: [], ownerId: 'beta' })).status, 400);
      assert.equal((await request(alpha, '/v1/workspace', 'GET', undefined, { 'X-TermLume-Platform': 'unknown' })).status, 400);
      const full = { ...snapshot.workspace, snippets: Array.from({ length: 110 }, (_, index) => ({ id: crypto.randomUUID(), name: 'Large ' + index, command: 'x'.repeat(20000) })) };
      assert.equal((await request(alpha, '/v1/workspace/import', 'POST', { expectedRevision: snapshot.revision, workspace: full })).status, 400);
      assert.equal((await request(alpha, '/v1/workspace/changes', 'POST', JSON.stringify({ expectedRevision: snapshot.revision, operation: 'saveGroup', payload: { name: 'x'.repeat(3200000) } }))).status, 413);
      assert.equal((await request(alpha)).data.revision, snapshot.revision);
    });
    await t.test('imports merge data and preserve existing records', async () => {
      const importedId = crypto.randomUUID().replaceAll('-', '');
      const imported = { groups: ['Imported'], hosts: [{ id: importedId, name: 'Imported host', address: 'imported.test', group: 'Imported' }], snippets: [],
        preferences: {}, knownHosts: {}, knownHostDetails: {} };
      const response = await request(alpha, '/v1/workspace/import', 'POST', { expectedRevision: snapshot.revision, workspace: imported });
      assert.equal(response.status, 200); snapshot = response.data;
      assert.equal(snapshot.workspace.hosts.length, 2); assert.equal(snapshot.workspace.preferences.fontSize, 20);
      const again = await request(alpha, '/v1/workspace/import', 'POST', { expectedRevision: snapshot.revision, workspace: imported });
      assert.equal(again.status, 200); snapshot = again.data; assert.equal(snapshot.workspace.hosts.length, 2);
      const conflicting = { ...imported, hosts: [{ ...imported.hosts[0], address: 'overwrite.test' }] };
      assert.equal((await request(alpha, '/v1/workspace/import', 'POST', { expectedRevision: snapshot.revision, workspace: conflicting })).status, 400);
      assert.equal(sql(`SELECT count(*) FROM termlume.hosts h JOIN termlume.workspaces w ON w.id=h.workspace_id JOIN termlume.app_users u ON u.id=w.owner_id WHERE u.issuer=${literal(issuer)} AND u.subject='alpha'`), '2');
    });
    async function action(token, name, payload = {}, selected, headers = {}) {
      return request(token, '/v1/workspace/actions', 'POST', { action: name, payload }, { ...(selected ? { 'X-TermLume-Workspace': selected } : {}), ...headers });
    }
    let team; let betaMember;
    await t.test('creates shared workspaces and redeems single-use invitations without exposing invite codes', async () => {
      const created = await action(alpha, 'workspace.create', { name: 'Team <production>' });
      assert.equal(created.status, 200); team = created.data.id; assert.equal(created.data.role, 'owner');
      assert.equal((await request(beta, '/v1/workspace', 'GET', undefined, { 'X-TermLume-Workspace': team })).status, 403);
      const invite = await action(alpha, 'workspace.invite', { role: 'viewer' }, team); assert.equal(invite.status, 200); assert.match(invite.data.code, /^[A-F0-9]{64}$/);
      const listed = await action(alpha, 'workspace.invites', {}, team); assert.equal(listed.status, 200); assert.ok(!JSON.stringify(listed.data).includes(invite.data.code));
      const joined = await action(beta, 'workspace.join', { code: invite.data.code }); assert.equal(joined.status, 200); assert.equal(joined.data.role, 'viewer');
      assert.equal((await action(beta, 'workspace.join', { code: invite.data.code })).status, 200, 'Lost join response can safely retry');
      assert.equal((await action(issue('outsider'), 'workspace.join', { code: invite.data.code })).status, 400);
      const members = (await action(alpha, 'workspace.members', {}, team)).data; betaMember = members.find(member => member.role === 'viewer').id;
      assert.equal((await action(beta, 'workspace.invite', { role: 'owner' }, team)).status, 403);
      assert.equal((await action(alpha, 'workspace.invite', { role: 'owner' }, team)).status, 400);
      assert.equal((await action(alpha, 'workspace.invite', { role: 'viewer', hours: 'invalid' }, team)).status, 400);
      assert.equal((await action(alpha, 'trust.publish', {}, team)).status, 400);
      const revokedInvite = (await action(alpha, 'workspace.invite', { role: 'editor' }, team)).data;
      assert.equal((await action(alpha, 'workspace.revokeInvite', { id: revokedInvite.id }, team)).status, 200);
      assert.equal((await action(issue('outsider'), 'workspace.join', { code: revokedInvite.code })).status, 400);
    });
    await t.test('viewer permissions preserve personal favorites while denying shared assets and direct SQL writes', async () => {
      let current = (await request(alpha, '/v1/workspace', 'GET', undefined, { 'X-TermLume-Workspace': team })).data;
      const saved = await request(alpha, '/v1/workspace/changes', 'POST', { expectedRevision: current.revision, operation: 'saveHost', payload: { name: 'Shared host', address: 'shared.test' } }, { 'X-TermLume-Workspace': team });
      assert.equal(saved.status, 200); current = saved.data;
      assert.equal((await request(beta, '/v1/workspace/changes', 'POST', { expectedRevision: current.revision, operation: 'deleteHost', payload: { id: current.workspace.hosts[0].id } }, { 'X-TermLume-Workspace': team })).status, 403);
      const favorite = await request(beta, '/v1/workspace/changes', 'POST', { expectedRevision: current.revision, operation: 'favorite', payload: { id: current.workspace.hosts[0].id } }, { 'X-TermLume-Workspace': team });
      assert.equal(favorite.status, 200); assert.equal(favorite.data.workspace.hosts[0].favorite, true);
      assert.equal((await request(alpha, '/v1/workspace', 'GET', undefined, { 'X-TermLume-Workspace': team })).data.workspace.hosts[0].favorite, false);
      const scoped = `BEGIN; SET LOCAL ROLE termlume_app; SELECT set_config('termlume.issuer',${literal(issuer)},true),set_config('termlume.subject','beta',true);`;
      assert.equal(sql(`${scoped} WITH changed AS (DELETE FROM termlume.hosts WHERE workspace_id=${literal(team)} RETURNING id) SELECT count(*) FROM changed; ROLLBACK;`).split('\n').at(-1), '0');
      sql(`${scoped} INSERT INTO termlume.host_groups(id,workspace_id,name,sort_order) VALUES(gen_random_uuid(),${literal(team)},'forbidden',0); COMMIT;`, true);
      assert.equal((await action(beta, 'audit.list', {}, team)).status, 403);
      assert.equal((await action(beta, 'workspace.history', {}, team)).status, 200);
      assert.equal((await action(alpha, 'workspace.memberRole', { id: betaMember, role: 'editor' }, team)).status, 200);
      current = (await request(beta, '/v1/workspace', 'GET', undefined, { 'X-TermLume-Workspace': team })).data;
      assert.equal((await request(beta, '/v1/workspace/changes', 'POST', { expectedRevision: current.revision, operation: 'saveSnippet', payload: { name: 'Team command', command: 'uptime' } }, { 'X-TermLume-Workspace': team })).status, 200);
      assert.equal((await action(beta, 'workspace.invite', { role: 'viewer' }, team)).status, 403);
    });
    await t.test('operation IDs prevent duplicate creates and favorite toggles after lost responses', async () => {
      const before = (await request(alpha)).data;
      const change = { expectedRevision: before.revision, operation: 'saveHost', operationId: crypto.randomUUID().replaceAll('-', ''), newId: crypto.randomUUID().replaceAll('-', ''), payload: { name: 'Idempotent host', address: 'idempotent.test' } };
      const saved = await request(alpha, '/v1/workspace/changes', 'POST', change); assert.equal(saved.status, 200);
      const repeated = await request(alpha, '/v1/workspace/changes', 'POST', change); assert.equal(repeated.status, 200); assert.equal(repeated.data.revision, saved.data.revision);
      assert.equal(repeated.data.workspace.hosts.filter(host => host.id === change.newId).length, 1);
      assert.equal((await request(alpha, '/v1/workspace/changes', 'POST', { ...change, payload: { ...change.payload, name: 'Tampered' } })).status, 400);
      const toggle = { expectedRevision: saved.data.revision, operation: 'favorite', operationId: crypto.randomUUID().replaceAll('-', ''), payload: { id: change.newId } };
      assert.equal((await request(alpha, '/v1/workspace/changes', 'POST', toggle)).status, 200);
      const again = await request(alpha, '/v1/workspace/changes', 'POST', toggle); assert.equal(again.status, 200); assert.equal(again.data.workspace.hosts.find(h => h.id === change.newId).favorite, true);
      snapshot = again.data;
      { const deleted = await request(alpha, '/v1/workspace/changes', 'POST', { expectedRevision: snapshot.revision, operation: 'deleteHost', payload: { id: change.newId } }); assert.equal(deleted.status, 200); snapshot = deleted.data; }
    });
    await t.test('history restores shared assets without resetting personal state and audit rows are immutable', async () => {
      const history = await action(alpha, 'workspace.history', {}, team); assert.equal(history.status, 200); assert.ok(history.data.length >= 3);
      const baseline = history.data.find(item => item.revision === 0); assert.ok(baseline);
      const current = (await request(alpha, '/v1/workspace', 'GET', undefined, { 'X-TermLume-Workspace': team })).data;
      assert.equal((await action(beta, 'workspace.restore', { revision: 0, expectedRevision: current.revision - 1 }, team)).status, 409);
      const restore = await action(alpha, 'workspace.restore', { revision: 0, expectedRevision: current.revision }, team); assert.equal(restore.status, 200); assert.deepEqual(restore.data.workspace.hosts, []); assert.deepEqual(restore.data.workspace.snippets, []);
      const audit = await action(alpha, 'audit.list', {}, team); assert.equal(audit.status, 200); assert.ok(audit.data.entries.some(row => row.action === 'workspace.restored'));
      const scope = `BEGIN; SET LOCAL ROLE termlume_app; SELECT set_config('termlume.issuer',${literal(issuer)},true),set_config('termlume.subject','alpha',true);`;
      sql(`${scope} DELETE FROM termlume.audit_events; COMMIT;`, true); sql(`${scope} UPDATE termlume.audit_events SET summary='forged'; COMMIT;`, true);
      assert.equal((await action(alpha, 'workspace.delete', {}, (await request(alpha)).data.context.id)).status, 403);
    });
    await t.test('cloud trust requires managers, validates fingerprints and refuses silent replacement', async () => {
      const entry = { endpoint: '[shared.test]:22 ssh-ed25519', address: 'shared.test', port: 22, algorithm: 'ssh-ed25519', fingerprint: 'SHA256:' + Buffer.alloc(32, 1).toString('base64').replace(/=$/, ''), metadata: {} };
      assert.equal((await action(beta, 'trust.publish', { entry }, team)).status, 403);
      assert.equal((await action(alpha, 'trust.publish', { entry }, team)).status, 200);
      assert.equal((await action(beta, 'trust.list', {}, team)).data[0].entry.fingerprint, entry.fingerprint);
      assert.equal((await action(alpha, 'trust.publish', { entry: { ...entry, fingerprint: 'SHA256:' + Buffer.alloc(32, 2).toString('base64').replace(/=$/, '') } }, team)).status, 400);
      assert.equal((await action(alpha, 'trust.publish', { entry: { ...entry, endpoint: '[forged.test]:22 ssh-ed25519' } }, team)).status, 400);
      assert.equal((await action(alpha, 'trust.remove', { endpoint: entry.endpoint }, team)).status, 200);
      assert.deepEqual((await action(alpha, 'trust.list', {}, team)).data, []);
    });
    await t.test('device revocation binds grants and privacy sanitization removes client secrets from uploaded activity', async () => {
      const device = crypto.randomUUID().replaceAll('-', ''); const otherDevice = crypto.randomUUID().replaceAll('-', ''); const deviceToken = issue('device-user');
      const headers = { 'X-TermLume-Device': device, 'X-TermLume-Version': '0.8.0' };
      assert.equal((await request(deviceToken, '/v1/workspace', 'GET', undefined, headers)).status, 200);
      const list = (await action(deviceToken, 'device.list', {}, null, headers)).data; assert.equal(list[0].id, device); assert.equal(list[0].version, '0.8.0');
      assert.equal((await action(deviceToken, 'device.rename', { id: device, name: 'Test laptop' }, null, headers)).status, 200);
      const entry = { id: crypto.randomUUID().replaceAll('-', ''), timestamp: new Date().toISOString(), event: 'connection.connected', category: 'connection', level: 'info', message: 'password=must-never-upload', details: { password: 'secret', localPath: '/private/path', bytes: '1024' } };
      assert.equal((await action(deviceToken, 'activity.upload', { entries: [entry] }, null, headers)).status, 200);
      assert.equal((await action(deviceToken, 'activity.upload', { entries: [entry] }, null, headers)).status, 200);
      const logs = (await action(deviceToken, 'activity.list', {}, null, headers)).data; assert.equal(logs.entries.length, 1); const text = JSON.stringify(logs); assert.ok(!text.includes('must-never-upload') && !text.includes('/private/path') && !text.includes('secret')); assert.equal(logs.entries[0].entry.details.bytes, '1024');
      const secondToken = issue('device-user'); const secondHeaders = { 'X-TermLume-Device': otherDevice };
      assert.equal((await request(secondToken, '/v1/workspace', 'GET', undefined, secondHeaders)).status, 200);
      assert.equal((await action(secondToken, 'device.revoke', { id: device }, null, secondHeaders)).status, 200);
      assert.equal((await request(deviceToken, '/v1/workspace', 'GET', undefined, headers)).status, 401);
      assert.equal((await request(deviceToken)).status, 401, 'Omitting device header cannot bypass revocation');
      assert.equal((await request(deviceToken, '/v1/workspace', 'GET', undefined, secondHeaders)).status, 401);
      assert.equal((await request(secondToken, '/v1/workspace', 'GET', undefined, secondHeaders)).status, 200);
      assert.equal((await request(secondToken, '/v1/workspace', 'GET', undefined, { 'X-TermLume-Device': device })).status, 401, 'A live grant cannot change its bound device');
    });
    await t.test('activity sanitization safely drops null details and rejects malformed batches', async () => {
      const token = issue('malformed-activity');
      const headers = { 'X-TermLume-Device': crypto.randomUUID().replaceAll('-', '') };
      const entry = { id: crypto.randomUUID(), timestamp: new Date().toISOString(), event: 'connection.opened',
        level: 'info', category: 'connection', message: 'must-never-upload',
        details: { fingerprint: null, bytes: null, direction: null, password: 'must-never-upload' } };
      assert.equal((await action(token, 'activity.upload', { entries: [entry] }, null, headers)).status, 200);
      const logs = (await action(token, 'activity.list', {}, null, headers)).data;
      assert.equal(logs.entries.length, 1);
      assert.deepEqual(logs.entries[0].entry.details, {});
      assert.equal(JSON.stringify(logs).includes('must-never-upload'), false);
      for (const entries of [null, [null], [{ ...entry, details: { fingerprint: 123 } }]]) {
        const response = await action(token, 'activity.upload', { entries }, null, headers);
        assert.equal(response.status, 400);
        assert.equal(typeof response.data.error, 'string');
      }
    });
    await t.test('ownership transfer, member removal and leave revoke visibility immediately', async () => {
      assert.equal((await action(alpha, 'workspace.memberRole', { id: betaMember, role: 'admin' }, team)).status, 200);
      assert.equal((await action(beta, 'workspace.rename', { name: 'Renamed by admin' }, team)).status, 200);
      assert.equal((await action(beta, 'workspace.transfer', { id: betaMember }, team)).status, 403);
      const ownId = (await action(alpha, 'workspace.members', {}, team)).data.find(item => item.role === 'owner').id;
      assert.equal((await action(alpha, 'workspace.transfer', { id: betaMember }, team)).status, 200);
      assert.equal((await action(alpha, 'workspace.delete', {}, team)).status, 403);
      assert.equal((await action(beta, 'workspace.removeMember', { id: ownId }, team)).status, 200);
      assert.equal((await request(alpha, '/v1/workspace', 'GET', undefined, { 'X-TermLume-Workspace': team })).status, 403);
      assert.equal((await action(alpha, 'workspace.list')).data.some(item => item.id === team), false);
      assert.equal((await action(beta, 'workspace.leave', {}, team)).status, 400);
      assert.equal((await action(beta, 'workspace.delete', {}, team)).status, 200);
      assert.equal((await request(beta)).status, 200, 'Deleting a shared workspace preserves the personal workspace');
    });
    await t.test('checks live grant revocation and fails closed when IAM is unavailable', async () => {
      const revokedToken = issue('alpha'); revoked.add(revokedToken); assert.equal((await request(revokedToken)).status, 401);
      const mismatch = issue('alpha'); profiles.set(mismatch, 'beta'); assert.equal((await request(mismatch)).status, 401);
      userInfoUnavailable = true; assert.equal((await request(alpha)).status, 503); userInfoUnavailable = false;
      assert.equal((await request(alpha)).status, 200);
    });
    await t.test('server logs expire after seven days and startup purges inactive accounts without widening audit permissions', async () => {
      const accounts = [];
      for (const subject of ['retention-active', 'retention-inactive']) {
        const token = issue(subject); const device = crypto.randomUUID().replaceAll('-', '');
        const headers = { 'X-TermLume-Device': device };
        const created = await request(token, '/v1/workspace', 'GET', undefined, headers);
        assert.equal(created.status, 200);
        const workspace = created.data.context.id;
        const user = sql(`SELECT id FROM termlume.app_users WHERE issuer=${literal(issuer)} AND subject=${literal(subject)}`);
        for (const [age, label] of [[8, 'expired'], [6, 'recent']]) {
          const id = crypto.randomUUID();
          const entry = { id, timestamp: new Date().toISOString(), event: 'connection.connected', category: 'connection', level: 'info', message: label, details: {} };
          // 客户端时间可以与接收时间不同；保留期由服务端时钟决定。
          sql(`INSERT INTO termlume.device_activity(user_id,device_id,entry_id,workspace_id,event_time,entry,received_at)
            VALUES(${literal(user)},${literal(device)},${literal(id)},${literal(workspace)},now(),${literal(JSON.stringify(entry))}::jsonb,now()-interval '${age} days');
            INSERT INTO termlume.audit_events(workspace_id,actor_id,actor_name,action,summary,created_at)
            VALUES(${literal(workspace)},${literal(user)},'Retention test',${literal('retention.' + label)},'Retention test',now()-interval '${age} days');`);
        }
        accounts.push({ token, headers, user });
      }
      const active = accounts[0];
      const activity = await action(active.token, 'activity.list', {}, null, active.headers);
      assert.equal(activity.status, 200); assert.equal(activity.data.entries.length, 1);
      assert.equal(activity.data.entries[0].entry.message, 'recent');
      const audit = await action(active.token, 'audit.list', {}, null, active.headers);
      assert.equal(audit.status, 200);
      assert.ok(audit.data.entries.some(row => row.action === 'retention.recent'));
      assert.ok(audit.data.entries.every(row => row.action !== 'retention.expired'));
      const users = accounts.map(account => literal(account.user)).join(',');
      assert.equal(sql(`SELECT count(*) FROM termlume.device_activity WHERE user_id IN(${users})`), '4', 'Read filtering must work before physical cleanup');
      await stopApi(); child = startApi(); await healthy();
      // 后台清理不依赖账号再次登录或上传，启动后只轮询数据库。
      for (let i = 0; ; i++) {
        if (sql(`SELECT count(*) FROM termlume.device_activity WHERE user_id IN(${users})`) === '2') break;
        assert.ok(i < 50, 'Startup must purge expired logs for both active and inactive accounts');
        await delay(100);
      }
      assert.equal(sql(`SELECT count(*) FROM termlume.audit_events WHERE actor_id IN(${users}) AND action='retention.expired'`), '0');
      assert.equal(sql(`SELECT count(*) FROM termlume.audit_events WHERE actor_id IN(${users}) AND action='retention.recent'`), '2');
      assert.equal(sql("SELECT has_table_privilege('termlume_app','termlume.audit_events','DELETE'),has_table_privilege('termlume_app','termlume.audit_events','UPDATE')"), 'f|f');
      assert.equal(sql("SELECT has_function_privilege('termlume_app','termlume.prune_expired_logs()','EXECUTE')"), 't');
      assert.equal(sql("SELECT EXISTS(SELECT FROM pg_proc p CROSS JOIN LATERAL aclexplode(p.proacl) a WHERE p.oid='termlume.prune_expired_logs()'::regprocedure AND a.grantee=0 AND a.privilege_type='EXECUTE')"), 'f');
    });
    await t.test('restarting the API preserves all committed workspace data', async () => {
      const expected = structuredClone(snapshot);
      await stopApi(); child = startApi(); await healthy();
      snapshot = (await request(alpha)).data;
      assert.deepEqual(snapshot, expected);
    });
    await t.test('accepts a newly rotated signing key without waiting for cache expiry', async () => {
      // 未知 kid 导致的一次受限刷新不能掩盖合法的签名密钥轮换。
      keys = [jwk(rsa, 'original'), jwk(rotated, 'rotated')];
      const before = jwksRequests;
      assert.equal((await request(issue('alpha', { signingKey: rotated, kid: 'rotated' }))).status, 200);
      assert.equal(jwksRequests, before + 1);
      for (let i = 0; i < 3; i++) assert.equal((await request(issue('alpha', { signingKey: attacker, kid: 'untrusted-' + i }))).status, 401);
      assert.equal(jwksRequests, before + 1);
    });
    await t.test('deletes preserve other hosts and cascade only the selected host state', async () => {
      await change('deleteSnippet', { id: snapshot.workspace.snippets[0].id });
      await change('deleteHost', { id: snapshot.workspace.hosts[0].id });
      assert.equal(snapshot.workspace.hosts.length, 1); assert.equal((await request(beta)).data.workspace.hosts.length, 1);
      assert.equal(sql(`SELECT count(*) FROM termlume.user_host_state s JOIN termlume.app_users u ON u.id=s.user_id WHERE u.issuer=${literal(issuer)} AND u.subject='alpha'`), '1');
    });
  } finally {
    try { await stopApi(); }
    finally {
      try { sql(`DELETE FROM termlume.app_users WHERE issuer=${literal(issuer)};`); }
      finally {
        await close(issuerServer);
        fs.rmSync(root, { recursive: true, force: true });
      }
    }
  }
});
