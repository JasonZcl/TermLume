'use strict';

let authenticationState = { isAuthenticated: false, isLoggingIn: false, issuer: '', user: null };
let authenticationError = '';
let authenticationGeneration = 0;
let workspaceLoad = null;
let workspaceLoadGeneration = -1;
let loginPending = false;
let workspaceSyncState = { enabled: true, connected: false, legacyImportAvailable: false };
let legacyImportOffered = false;

function renderAuthentication() {
  const busy = loginPending || authenticationState.isLoggingIn;
  $('#authentication-gate').innerHTML = ui`<div class="authentication-card"><div class="authentication-brand"><img src="favicon.svg" alt="">TermLume</div><div class="eyebrow">YOUR TERMINAL WORKSPACE</div><h1>登录工作空间</h1><p>使用 Aegis-IAM 账号登录，继续你的 SSH 与 SFTP 工作。</p><div class="authentication-provider">${icon('shield')}<div><strong>Aegis-IAM</strong><small>${escapeHtml(authenticationState.issuer || t('正在读取登录配置…'))}</small></div></div><div class="authentication-error" role="alert">${escapeHtml(t(authenticationError))}</div><button class="primary-button" data-action="accountLogin" ${busy || !authenticationState.issuer ? 'disabled' : ''}>${icon(busy ? 'clock' : 'arrow')}${busy ? t('等待浏览器登录…') : authenticationState.isAuthenticated ? t('重新打开工作区') : t('使用浏览器登录')}</button>${busy ? ui`<button class="secondary-button" data-action="accountCancel">取消登录</button>` : ''}<p class="authentication-note">登录将在系统浏览器中完成。工作区随账号同步，SSH 凭据在连接时单独输入。</p></div>`;
  const name = $('#account-name');
  if (name) name.textContent = authenticationState.user?.name || authenticationState.user?.username || t('账号');
}

function lockWorkspace() {
  authenticationGeneration++;
  resetCollaboration();
  $('#workspace-sync-notice')?.classList.add('hidden');
  ready = false;
  document.body.classList.add('authentication-required');
  $('.sidebar').inert = true;
  $('.main').inert = true;
  editorGeneration++; editorSubmit = null;
  $('#editor-dialog').close();
  $('#editor-form').replaceChildren();
  $('#confirm-cancel').click();
  $('#confirm-dialog').close();
  $('#confirm-title').textContent = $('#confirm-description').textContent = '';
  $('#terminal-snippets').replaceChildren();
  for (const session of sessions.values()) session.terminal.dispose();
  sessions.clear();
  activeSession = selectedHostId = currentGroup = null;
  currentView = 'hosts';
  workspace = { groups: [], hosts: [], snippets: [], knownHosts: {}, preferences: {} };
  workspaceSyncState = { enabled: true, connected: false, legacyImportAvailable: false };
  $('#terminals').replaceChildren();
  $('#page').replaceChildren();
  $('#groups').replaceChildren();
  knownHostRecords = []; knownHostsLoaded = logsLoaded = false;
  logSnapshot = { entries: [], error: null, limit: 2000 };
  clearTimeout(logRefreshTimer); clearTimeout(sftpState.refreshTimer);
  sftpState.sessionId = sftpState.hostId = null; sftpState.connecting = false; sftpState.jobs.clear();
  for (const side of ['local', 'remote']) {
    Object.assign(sftpState[side], { listing: null, query: '', error: '', busy: false, initialized: false, revision: sftpState[side].revision + 1 });
    sftpState[side].selected.clear();
  }
  renderTabs();
}

async function unlockWorkspace() {
  if (ready) return;
  const generation = authenticationGeneration;
  if (workspaceLoad && workspaceLoadGeneration === generation) return workspaceLoad;
  workspaceLoadGeneration = generation;
  const load = (async () => {
    const value = await request('workspace');
    const sync = await request('workspaceSync');
    if (!authenticationState.isAuthenticated || generation !== authenticationGeneration) return;
    workspaceSyncState = sync;
    ready = true;
    document.body.classList.remove('authentication-required');
    $('.sidebar').inert = false; $('.main').inert = false;
    refreshWorkspace(value);
    showView('hosts');
    if (sync.legacyImportAvailable && sync.connected && !legacyImportOffered) {
      legacyImportOffered = true;
      try { await importLegacyWorkspace(); } catch (error) { toast(error.message, true); }
    }
  })().finally(() => { if (workspaceLoad === load) workspaceLoad = null; });
  workspaceLoad = load;
  return load;
}

function receiveAuthentication(value) {
  const wasAuthenticated = authenticationState.isAuthenticated;
  if (wasAuthenticated && value.isAuthenticated &&
      (authenticationState.issuer !== value.issuer || authenticationState.clientId !== value.clientId ||
       authenticationState.user?.subject !== value.user?.subject)) lockWorkspace();
  authenticationState = value;
  if (!value.isAuthenticated) {
    if (wasAuthenticated || ready) {
      authenticationError = t('登录已结束，请重新登录。');
      lockWorkspace();
    }
    renderAuthentication();
    return;
  }
  authenticationError = '';
  renderAuthentication();
  const generation = authenticationGeneration;
  unlockWorkspace().catch(error => {
    if (generation !== authenticationGeneration || !authenticationState.isAuthenticated) return;
    lockWorkspace(); authenticationError = error.message; renderAuthentication();
  });
}

async function initializeAuthentication() {
  lockWorkspace();
  renderAuthentication();
  try {
    const [result] = await Promise.all([request('authStatus'), localeReady]);
    setUiLanguage(result.language);
    receiveAuthentication(result.authentication);
    if (localeLoadError) toast(t('语言资源加载失败，请检查安装文件后重启应用。'), true);
  } catch (error) { authenticationError = error.message; renderAuthentication(); }
}

function accountSettings() {
  const user = authenticationState.user || {};
  return ui`<section class="preference-card"><div class="preference-card-heading"><h3>Aegis-IAM 账号</h3><p>登录状态由认证服务验证，重新打开应用时自动恢复。退出账号会关闭当前 SSH / SFTP 连接并取消传输。</p></div><dl class="account-details"><dt>显示名称</dt><dd>${escapeHtml(user.name || '—')}</dd><dt>用户名</dt><dd>${escapeHtml(user.username || '—')}</dd><dt>邮箱</dt><dd>${escapeHtml(user.email || '—')}</dd><dt>认证服务</dt><dd>${escapeHtml(authenticationState.issuer)}</dd></dl><button class="secondary-button" data-action="accountLogout">退出登录</button><p class="settings-footnote">每个账号拥有独立工作区。主机、收藏、最近连接、命令片段与偏好设置随账号同步。</p></section>${workspaceSynchronizationSettings()}`;
}

function workspaceSynchronizationSettings() {
  return ui`<section class="preference-card"><div class="preference-card-heading"><h3>工作区同步</h3><p>${workspaceSyncState.connected ? t('已连接，可同步当前账号的工作区。') : t('服务暂时离线，修改会保存在本机等待同步。')}</p></div><button class="secondary-button" data-action="refreshWorkspace">刷新工作区</button>${workspaceSyncState.legacyImportAvailable ? ui`<button class="secondary-button" data-action="importLegacyWorkspace">导入旧版本地数据</button>` : ''}<p class="settings-footnote">旧版数据只在确认后导入当前账号，原文件和备份会保留。密码与私钥文件留在本机；日志可同步，服务器指纹须在各设备核验。</p><div class="collaboration-quick-links"><button class="secondary-button" data-view="collaboration">管理工作区与设备</button><button class="secondary-button" data-collab="export">导出草稿</button><button class="secondary-button" data-collab="import">导入配置</button><button class="secondary-button" data-collab="activityToggle">${workspaceSyncState.activitySyncEnabled ? t('暂停活动日志同步') : t('启用活动日志同步')}</button></div>${workspaceSyncState.pending ? `<p>${escapeHtml(msg`有 ${workspaceSyncState.pending} 项待同步修改，已保存在本机。`)}</p><div class="collaboration-quick-links">${collabButton('重试同步', 'retry')}${collabButton('放弃待同步修改', 'discard')}${collabButton('导出草稿', 'export')}</div>` : ''}</section>`;
}

async function refreshWorkspaceSynchronization() {
  const generation = authenticationGeneration;
  const value = await request('workspace');
  const sync = await request('workspaceSync');
  if (!ready || generation !== authenticationGeneration) return;
  workspaceSyncState = sync;
  refreshWorkspace(value);
}

async function importLegacyWorkspace() {
  if (!ready || !workspaceSyncState.legacyImportAvailable || !await confirmAction(t('导入旧版本地数据？'), t('旧主机、分组、命令片段和偏好设置将归入当前登录账号。原文件与备份会保留，其他账号不会自动获得这些数据。'))) return;
  const generation = authenticationGeneration;
  const value = await request('importLegacyWorkspace');
  const sync = await request('workspaceSync');
  if (!ready || generation !== authenticationGeneration) return;
  workspaceSyncState = sync;
  refreshWorkspace(value);
  toast(t('旧版本地数据已导入当前账号。'));
}

document.addEventListener('click', async event => {
  const button = event.target.closest('[data-action]');
  if (!button || button.disabled) return;
  switch (button.dataset.action) {
    case 'refreshWorkspace':
    case 'importLegacyWorkspace':
      if (!ready) return;
      button.disabled = true;
      try {
        if (button.dataset.action === 'refreshWorkspace') await refreshWorkspaceSynchronization();
        else await importLegacyWorkspace();
      } catch (error) { toast(error.message, true); }
      finally { if (button.isConnected) button.disabled = false; }
      break;
    case 'accountLogin':
      if (loginPending) return;
      loginPending = true; authenticationError = ''; renderAuthentication();
      try { receiveAuthentication(await request('authLogin')); }
      catch (error) { authenticationError = error.message; }
      finally { loginPending = false; renderAuthentication(); }
      break;
    case 'accountCancel':
      button.disabled = true;
      try { await request('authCancel'); } catch (error) { authenticationError = error.message; renderAuthentication(); }
      break;
    case 'account':
      if (ready) { settingsSection = 'account'; showView('settings'); }
      break;
    case 'accountLogout':
      if (!ready || !await confirmAction(t('退出登录？'), t('将断开所有 SSH / SFTP 连接并取消传输，随后返回登录页。'))) return;
      button.disabled = true;
      try {
        const result = await request('authLogout');
        receiveAuthentication(result.authentication);
        if (!result.revocationSucceeded) {
          authenticationError = t('已退出本机登录，但服务端撤销未完成。可在 Aegis-IAM 中退出原会话。');
          renderAuthentication();
        }
      } catch (error) { toast(error.message, true); button.disabled = false; }
      break;
  }
});
