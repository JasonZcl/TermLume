'use strict';

const $ = selector => document.querySelector(selector);
const escapeHtml = value => String(value ?? '').replace(/[&<>"']/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[character]);
const icon = name => `<svg class="icon" aria-hidden="true"><use href="#i-${name}"/></svg>`;
const bridge = window.chrome?.webview;
const pending = new Map();
const sessions = new Map();
let workspace = { groups: [], hosts: [], snippets: [], knownHosts: {}, preferences: { fontSize: 14, terminalTheme: 'midnight' } };
let currentView = 'hosts';
let currentGroup = null;
let activeSession = null;
let query = '';
let listView = false;
let toastTimer;
let editorSubmit = null;
let editorGeneration = 0;
let ready = false;
let selectedHostId = null;
let sortRecent = false;
let windowMaximized = false;

function updateWindowState(maximized) {
  windowMaximized = maximized;
  const button = $('#maximize-window');
  button.innerHTML = icon(maximized ? 'restore' : 'maximize');
  button.title = maximized ? t('向下还原') : t('最大化');
  button.setAttribute('aria-label', button.title);
  document.body.classList.toggle('window-maximized', maximized);
}

document.addEventListener('click', async event => {
  const navigation = event.target.closest('[data-view]');
  if (navigation) return showView(navigation.dataset.view);
  const button = event.target.closest('[data-action]');
  if (!button || button.disabled) return;
  const { action, id } = button.dataset;
  if (['minimize', 'maximize', 'closeWindow'].includes(action)) {
    try {
      const result = await request('window', { action: action === 'closeWindow' ? 'close' : action });
      updateWindowState(result.maximized);
    } catch (error) { toast(error.message, true); }
    return;
  }
  if (!ready) return;
  const host = workspace.hosts.find(item => item.id === id);
  const snippet = workspace.snippets.find(item => item.id === id);
  try {
    switch (action) {
      case 'toggleSidebar': {
        const collapsed = document.body.classList.toggle('sidebar-collapsed');
        $('.sidebar-toggle').setAttribute('aria-expanded', String(!collapsed));
        await updatePreferences({ sidebarCollapsed: collapsed });
        break;
      }
      case 'settingsSection': settingsSection = id; renderSettings(); $('#page-scroll').scrollTop = 0; break;
      case 'setPreference': await updatePreferences({ [button.dataset.key]: button.dataset.value }); break;
      case 'editShortcut': openShortcutEditor(id); break;
      case 'clearShortcut': shortcutRecording.value = ''; shortcutRecording.error = ''; renderShortcutCapture(); break;
      case 'resetShortcut': {
        const binding = validateShortcut(id, defaultShortcuts[id]);
        await updatePreferences({ shortcuts: { ...preferenceValues().shortcuts, [id]: binding } });
        toast(t('已恢复默认快捷键')); break;
      }
      case 'resetAllShortcuts':
        if (await confirmAction(t('恢复默认快捷键？'), t('所有自定义快捷键都将恢复为 Windows 默认方案。其他设置不受影响。'))) {
          await updatePreferences({ shortcuts: { ...defaultShortcuts } }); toast(t('所有快捷键已恢复默认'));
        }
        break;
      case 'knownHosts': showView('knownHosts'); break;
      case 'refreshKnownHosts': await loadKnownHosts(); break;
      case 'refreshLogs': await loadActivityLogs(); break;
      case 'copyFingerprint': {
        const entry = knownHostRecords.find(item => item.endpoint === button.dataset.endpoint);
        if (entry) { await request('copy', { text: entry.fingerprint }); toast(t('指纹已复制，请通过可信渠道核验')); }
        break;
      }
      case 'logDetails': openLogDetails(id); break;
      case 'copyLog': {
        const entry = logSnapshot.entries.find(item => item.id === id);
        if (entry) { await request('copy', { text: JSON.stringify(entry, null, 2) }); toast(t('日志详情已复制')); }
        break;
      }
      case 'logPrevious': logPage = Math.max(1, logPage - 1); renderLogResults(); break;
      case 'logNext': logPage++; renderLogResults(); break;
      case 'exportLogs': {
        button.disabled = true;
        try { if (await request('exportLogs', { ids: filteredLogs().map(entry => entry.id) })) toast(t('日志已导出，分享前请检查主机地址等信息')); }
        finally { button.disabled = false; }
        break;
      }
      case 'clearLogs':
        if (await confirmAction(t('清空全部日志？'), t('将永久删除本机的全部连接与安全事件日志，而不仅是当前筛选结果。不会删除主机配置和信任记录。'))) {
          logSnapshot = await request('clearLogs'); logPage = 1; renderPage(); toast(t('日志已清空'));
        }
        break;
      case 'sort': sortRecent = !sortRecent; renderPage(); break;
      case 'selectHost':
        selectedHostId = id;
        document.querySelectorAll('.host-card').forEach(card => card.classList.toggle('selected', card.dataset.id === id));
        break;
      case 'openTerminal': {
        if (selectedHostId && workspace.hosts.some(item => item.id === selectedHostId)) await connectHost(selectedHostId);
        else {
          const latest = [...sessions.entries()].reverse().find(([, session]) => session.status === 'connected');
          if (latest) showSession(latest[0]);
          else { showView('hosts'); toast(t('选择一台主机后打开终端，也可以双击主机直接连接')); }
        }
        break;
      }
      case 'home': showView(currentView, currentGroup); break;
      case 'group': showView('hosts', button.dataset.group); break;
      case 'newGroup': openGroupEditor(); break;
      case 'newHost': openHostEditor(); break;
      case 'editHost': if (host) openHostEditor(host); break;
      case 'deleteHost':
        if (host && await confirmAction(t('删除主机？'), msg`将从工作空间移除「${host.name}」。已打开的会话不会断开。`)) { refreshWorkspace(await request('deleteHost', { id })); toast(t('主机已删除')); }
        break;
      case 'favorite': refreshWorkspace(await request('favorite', { id })); break;
      case 'grid': case 'list': listView = action === 'list'; renderPage(); break;
      case 'closeEditor': $('#editor-dialog').close(); break;
      case 'pickKey': { const path = await request('pickKey'); if (path) $('#host-key').value = path; break; }
      case 'connect': await connectHost(id); break;
      case 'openSftp': if (host) await openSftp(id); break;
      case 'session': showSession(id); break;
      case 'closeSession': await closeSession(id); break;
      case 'clearTerminal': sessions.get(activeSession)?.terminal.clear(); break;
      case 'disconnect':
        if (await confirmAction(t('断开 SSH 连接？'), t('终端输出将保留，远端运行的任务可能被终止。'))) {
          const session = sessions.get(activeSession);
          await request('disconnect', { sessionId: activeSession });
          session.status = 'closed';
          session.terminal.writeln(t('\r\n\x1b[90m[已手动断开连接]\x1b[0m'));
          renderTabs(); updateTerminalToolbar(); updateFooter();
        }
        break;
      case 'reconnect': { const hostId = sessions.get(activeSession)?.host.id; if (hostId) await connectHost(hostId); break; }
      case 'newSnippet': openSnippetEditor(); break;
      case 'editSnippet': if (snippet) openSnippetEditor(snippet); break;
      case 'deleteSnippet':
        if (snippet && await confirmAction(t('删除命令片段？'), msg`将永久删除「${snippet.name}」。`)) refreshWorkspace(await request('deleteSnippet', { id }));
        break;
      case 'copySnippet': if (snippet) { await request('copy', { text: snippet.command }); toast(t('命令已复制')); } break;
      case 'insertSnippet': {
        const entry = [...sessions.entries()].reverse().find(([, session]) => session.status === 'connected');
        if (!entry) throw new Error(t('请先连接一台主机，再填入命令片段。'));
        if (!snippet) break;
        if (/[\r\n\x00-\x1f\x7f]/.test(snippet.command)) {
          await request('copy', { text: snippet.command });
          toast(t('多行或含控制字符的片段已复制，请检查后手动粘贴'));
          break;
        }
        if (await confirmAction(t('填入终端？'), msg`目标：${entry[1].host.name}\n\n${snippet.command}\n\n不会自动按 Enter；请确认当前终端适合输入此命令。`)) { showSession(entry[0]); entry[1].terminal.paste(snippet.command); toast(t('命令已填入，检查后按 Enter 执行')); }
        break;
      }
      case 'forgetKey':
        if (await confirmAction(t('移除服务器信任？'), msg`目标：${button.dataset.endpoint}\n\n下次连接将重新请求确认指纹。请务必先通过可信渠道核验新指纹；移除记录不会断开现有会话。`)) {
          refreshWorkspace(await request('forgetKey', { endpoint: button.dataset.endpoint }));
          await loadKnownHosts(); toast(t('已移除信任，下次连接请重新核验指纹'));
        }
        break;
    }
  } catch (error) { toast(error.message, true); }
});

$('#editor-form').addEventListener('submit', async event => {
  event.preventDefault();
  const button = $('#editor-form [type="submit"]');
  if (button.disabled) return;
  button.disabled = true;
  const generation = editorGeneration;
  $('#form-error').textContent = '';
  try { await editorSubmit(event.target); if (generation === editorGeneration) $('#editor-dialog').close(); }
  catch (error) { if (generation === editorGeneration) $('#form-error').textContent = t(error.message); else toast(error.message, true); }
  finally { button.disabled = false; }
});

document.addEventListener('keydown', event => {
  if (event.target.closest('.tab') && (event.key === 'Enter' || event.key === ' ') && event.target.matches('[role="tab"]')) { event.preventDefault(); event.target.click(); return; }
  if ($('#editor-dialog').open || $('#confirm-dialog').open || !ready) return;
  if (event.target.matches('.host-card') && ['Enter', ' '].includes(event.key)) {
    event.preventDefault();
    if (event.key === 'Enter') connectHost(event.target.dataset.id).catch(error => toast(error.message, true));
    else event.target.click();
  }
});

document.addEventListener('dblclick', event => {
  const card = event.target.closest('.host-card');
  if (card && !event.target.closest('button') && ready) connectHost(card.dataset.id).catch(error => toast(error.message, true));
});

function request(op, payload = {}, additionalObjects = null) {
  if (!bridge) return Promise.reject(new Error(t('请从 TermLume 桌面应用打开此工作空间。')));
  const id = crypto.randomUUID();
  return new Promise((resolve, reject) => {
    const timeout = setTimeout(() => { pending.delete(id); reject(new Error(t('操作等待超时，请检查连接状态。'))); }, ['connect', 'exportLogs'].includes(op) || op.startsWith('sftp') ? 600000 : 30000);
    pending.set(id, { resolve, reject, timeout });
    try {
      if (additionalObjects) {
        if (!bridge.postMessageWithAdditionalObjects) throw new Error(t('当前 WebView2 版本不支持外部拖入，请更新 WebView2 或在左侧选择文件上传。'));
        bridge.postMessageWithAdditionalObjects({ id, op, payload }, additionalObjects);
      } else bridge.postMessage({ id, op, payload });
    } catch (error) { clearTimeout(timeout); pending.delete(id); reject(error); }
  });
}

bridge?.addEventListener('message', event => {
  const message = event.data;
  if (message.type === 'sftpEnded') {
    onSftpEnded(message);
  } else if (message.type === 'sftpProgress') {
    onSftpProgress(message.job);
  } else if (message.type === 'activityChanged') {
    scheduleLogRefresh();
  } else if (message.type === 'knownHostsChanged') {
    knownHostRecords = message.records; knownHostsLoaded = true;
    if (!activeSession && currentView === 'knownHosts') keepActivityFocus(renderPage);
  } else if (message.type === 'windowState') {
    updateWindowState(message.maximized);
  } else if (message.type === 'reply') {
    const callback = pending.get(message.id);
    if (!callback) return;
    clearTimeout(callback.timeout);
    pending.delete(message.id);
    if (message.ok) callback.resolve(message.result);
    else callback.reject(new Error(message.error));
  } else if (message.type === 'output') {
    sessions.get(message.sessionId)?.terminal.write(message.data, () => request('ack', { sessionId: message.sessionId }).catch(() => {}));
  } else if (message.type === 'connected') {
    const session = sessions.get(message.sessionId);
    if (session) {
      session.status = 'connected';
      renderTabs(); updateTerminalToolbar(); updateFooter();
    }
  } else if (message.type === 'ended') {
    const session = sessions.get(message.sessionId);
    if (!session) return;
    session.status = 'closed';
    session.terminal.write(msg`\r\n\x1b[90m[连接已关闭${message.error ? ': ' + t(message.error) : ''}]\x1b[0m\r\n`);
    renderTabs(); updateTerminalToolbar(); updateFooter();
    if (!activeSession) renderPage();
  }
});

function toast(message, error = false) {
  message = t(message);
  clearTimeout(toastTimer);
  const element = $('#toast');
  element.textContent = message;
  element.className = error ? 'visible error' : 'visible';
  toastTimer = setTimeout(() => element.className = '', error ? 6500 : 3500);
}

function viewLabel() {
  return groupLabel(currentGroup) || ({ hosts: t('所有主机'), favorites: t('收藏夹'), recent: t('最近连接'), snippets: t('命令片段'), settings: t('设置'), knownHosts: t('已知主机'), logs: t('日志'), sftp: t('SFTP 文件传输') })[currentView];
}

function refreshWorkspace(value) {
  workspace = value;
  applyPreferences();
  renderSidebar();
  if (!activeSession) renderPage();
}

function workspaceGroups() {
  return [...new Set([...(workspace.groups || []), ...workspace.hosts.map(host => host.group)])].sort((first, second) => first.localeCompare(second, uiLocale()));
}

function renderSidebar() {
  $('#host-count').textContent = workspace.hosts.length;
  document.querySelectorAll('[data-view]').forEach(button => button.classList.toggle('active', button.dataset.view === currentView && !currentGroup));
  const groups = workspaceGroups();
  $('#groups').innerHTML = groups.length ? groups.map(group => `<button class="group-button ${currentGroup === group ? 'active' : ''}" data-action="group" data-group="${escapeHtml(group)}">${icon('folder')}<span>${escapeHtml(groupLabel(group))}</span><small>${workspace.hosts.filter(host => host.group === group).length}</small></button>`).join('') : uiHtml('<div class="group-empty">为主机设置分组，<br>让每个环境井井有条。</div>');
  $('#breadcrumb-label').textContent = activeSession ? t('终端会话') : viewLabel();
  refreshShortcutHints();
  updateFooter();
}

function renderTabs() {
  $('#tabbar').innerHTML = ui`<button class="tab workspace-tab ${activeSession ? '' : 'active'}" role="tab" aria-selected="${!activeSession}" data-action="home"><img src="favicon.svg" alt="" class="workspace-tab-icon"><span class="tab-name">工作空间</span>${icon('chevron')}</button>` + [...sessions.entries()].map(([id, session]) => ui`<div class="tab ${activeSession === id ? 'active' : ''}" role="tab" tabindex="0" aria-selected="${activeSession === id}" data-action="session" data-id="${id}" data-status="${session.status}"><span class="status-dot"></span><span class="tab-name">${escapeHtml(session.host.name)}</span><button class="icon-button" data-action="closeSession" data-id="${id}" title="关闭会话" aria-label="关闭 ${escapeHtml(session.host.name)}">${icon('close')}</button></div>`).join('');
}

function updateFooter() {
  const count = [...sessions.values()].filter(session => session.status === 'connected').length;
  $('#footer-status').textContent = [count ? msg`${count} 个活跃 SSH 连接` : '', sftpState.sessionId ? t('SFTP 已连接') : ''].filter(Boolean).join(' · ') || t('工作空间就绪');
  if (!activeSession) $('#footer-right').textContent = currentView === 'sftp' ? t('SFTP · SSH 加密文件传输') : t('SSH · 端到端加密连接');
}

function showView(view, group = null) {
  currentView = view;
  currentGroup = group;
  activeSession = null;
  query = '';
  $('#page-scroll').classList.remove('hidden');
  $('#terminal-workspace').classList.add('hidden');
  renderSidebar(); renderTabs(); renderPage();
}

function heading(eyebrow, title, subtitle, action = '') {
  return `<div class="page-heading"><div><div class="eyebrow">${eyebrow}</div><h1>${escapeHtml(title)}</h1><p>${subtitle}</p></div>${action}</div>`;
}

function renderPage() {
  $('#breadcrumb-label').textContent = viewLabel();
  $('#page').classList.toggle('settings-page', currentView === 'settings');
  $('#page').classList.toggle('sftp-page', currentView === 'sftp');
  $('#page').classList.toggle('host-page', !['snippets', 'settings', 'knownHosts', 'logs', 'sftp'].includes(currentView));
  if (currentView === 'sftp') return renderSftp();
  if (currentView === 'knownHosts') return renderKnownHosts();
  if (currentView === 'logs') return renderActivityLogs();
  if (currentView === 'snippets') return renderSnippets();
  if (currentView === 'settings') return renderSettings();
  $('#page').innerHTML = ui`
    <form class="quick-connect" id="quick-connect-form">
      ${icon('search')}
      <input id="quick-address" aria-label="快速连接地址" placeholder="查找主机，或输入 ssh user@hostname…" value="${escapeHtml(query)}" autocomplete="off" spellcheck="false">
      <span class="keyboard">Enter ↵</span><button type="submit" class="quick-connect-button">快速连接</button>
    </form>
    <div class="library-toolbar">
      <button class="secondary-button new-host-button" data-action="newHost">${icon('plus')}新建主机</button>
      <button class="toolbar-button" data-action="openTerminal">${icon('terminal')}终端</button>
      <button class="toolbar-button" data-action="newGroup">${icon('folder')}新建分组</button>
      <div class="toolbar-spacer"></div>
      <div class="view-switch"><button class="${listView ? '' : 'active'}" data-action="grid" title="卡片视图" aria-label="卡片视图">${icon('grid')}</button><button class="${listView ? 'active' : ''}" data-action="list" title="列表视图" aria-label="列表视图">${icon('list')}</button></div>
      <button class="icon-button" data-view="favorites" title="查看收藏夹" aria-label="查看收藏夹">${icon('star')}</button>
      <button class="icon-button ${sortRecent ? 'sort-active' : ''}" data-action="sort" title="${sortRecent ? t('当前按最近连接排序，点击按名称排序') : t('当前按名称排序，点击按最近连接排序')}" aria-label="切换主机排序">${icon('sort')}</button>
    </div>
    <div class="library-content"><div class="library-heading"><h1>${escapeHtml(viewLabel())}</h1><span class="count-badge" id="filtered-count">0</span><span class="library-summary">${workspace.hosts.length} 台主机<span>·</span>${workspaceGroups().length} 个分组</span></div><div id="host-results"></div></div>`;
  $('#quick-address').addEventListener('input', event => { query = event.target.value; renderHosts(); });
  $('#quick-connect-form').addEventListener('submit', async event => {
    event.preventDefault();
    try {
      const address = $('#quick-address').value.trim();
      const saved = workspace.hosts.find(host => host.name === address || host.address === address);
      if (saved) await connectHost(saved.id);
      else openHostEditor(parseQuickAddress(address));
    } catch (error) { toast(error.message, true); }
  });
  renderHosts();
  refreshShortcutHints();
}

function visibleHosts() {
  let hosts = workspace.hosts.filter(host => (!currentGroup || host.group === currentGroup) && (currentView !== 'favorites' || host.favorite) && (currentView !== 'recent' || host.lastConnectedAt));
  const normalized = query.trim().toLocaleLowerCase();
  if (normalized) hosts = hosts.filter(host => [host.name, host.address, host.username, host.group, groupLabel(host.group), host.note].some(value => value.toLocaleLowerCase().includes(normalized)));
  return [...hosts].sort((first, second) => currentView === 'recent' || sortRecent ? new Date(second.lastConnectedAt || 0) - new Date(first.lastConnectedAt || 0) || first.name.localeCompare(second.name, uiLocale()) : first.name.localeCompare(second.name, uiLocale()));
}

function renderHosts() {
  const hosts = visibleHosts();
  $('#filtered-count').textContent = hosts.length;
  const target = $('#host-results');
  if (!hosts.length) {
    const firstHost = !workspace.hosts.length && !query && currentView === 'hosts' && !currentGroup;
    const emptyGroup = currentGroup && !query;
    const message = query ? t('没有找到匹配的主机，试试其他关键词，或按 Enter 创建连接。') : emptyGroup ? t('分组已准备好，添加主机时会自动归入此分组。') : currentView === 'favorites' ? t('还没有收藏，点击主机上的星标即可添加。') : currentView === 'recent' ? t('连接过的主机会出现在这里。') : t('添加第一台主机，开始你的终端工作空间。');
    target.innerHTML = `<div class="library-empty"><div class="empty-terminal-mark">${icon(emptyGroup ? 'folder' : firstHost ? 'terminal' : 'search')}</div><h2>${emptyGroup ? t('分组中还没有主机') : firstHost ? t('连接，从这里开始') : t('这里还没有主机')}</h2><p>${message}</p>${firstHost || emptyGroup ? ui`<button class="secondary-button" data-action="newHost">${icon('plus')}${emptyGroup ? t('添加主机到此分组') : t('添加第一台主机')}</button><span class="empty-shortcut">或使用 Ctrl + N 快速创建</span>` : ''}</div>`;
    refreshShortcutHints();
    return;
  }
  target.innerHTML = `<div class="host-grid ${listView ? 'list' : ''}">${hosts.map(host => {
    const online = [...sessions.values()].some(session => session.host.id === host.id && session.status === 'connected');
    const details = `${host.username}@${host.address}:${host.port}\n${groupLabel(host.group)} · ${host.authType === 'key' ? t('私钥认证') : t('密码认证')}${host.note ? '\n' + host.note : ''}`;
    return ui`<article class="host-card ${selectedHostId === host.id ? 'selected' : ''}" data-action="selectHost" data-id="${escapeHtml(host.id)}" tabindex="0" aria-label="选择主机 ${escapeHtml(host.name)}" title="${escapeHtml(details)}">
      <div class="host-avatar ${['blue', 'purple', 'amber'].includes(host.color) ? host.color : 'mint'}">${icon('terminal')}${online ? '<span class="host-online-dot"></span>' : ''}</div>
      <div class="host-identity"><div class="host-name">${escapeHtml(host.name)}</div><div class="host-subtitle"><span>ssh, ${escapeHtml(host.username)}</span><span class="host-address">${escapeHtml(host.address)}${host.port === 22 ? '' : ':' + host.port}</span></div></div>
      <span class="host-group-label">${escapeHtml(groupLabel(host.group))}</span>
      <div class="host-card-actions"><button class="icon-button host-favorite ${host.favorite ? 'favorite-on' : ''}" data-action="favorite" data-id="${escapeHtml(host.id)}" title="${host.favorite ? t('取消收藏') : t('收藏主机')}" aria-label="${host.favorite ? t('取消收藏') : t('收藏主机')}">${icon('star')}</button><button class="icon-button" data-action="editHost" data-id="${escapeHtml(host.id)}" title="编辑主机" aria-label="编辑主机">${icon('edit')}</button><button class="icon-button host-delete" data-action="deleteHost" data-id="${escapeHtml(host.id)}" title="删除主机" aria-label="删除主机">${icon('trash')}</button><button class="icon-button" data-action="openSftp" data-id="${escapeHtml(host.id)}" title="SFTP 文件传输" aria-label="打开 SFTP">${icon('folder')}</button><button class="connect-button" data-action="connect" data-id="${escapeHtml(host.id)}" title="连接到 ${escapeHtml(host.name)}">${icon('arrow')}<span>连接</span></button></div>
    </article>`;
  }).join('')}</div>`;
}

function terminalTheme() {
  return workspace.preferences.terminalTheme === 'nord' ? { background: '#2e3440', foreground: '#d8dee9', cursor: '#88c0d0', selectionBackground: '#4c566a', black: '#3b4252', red: '#bf616a', green: '#a3be8c', yellow: '#ebcb8b', blue: '#81a1c1', magenta: '#b48ead', cyan: '#88c0d0', white: '#e5e9f0', brightBlack: '#4c566a', brightRed: '#bf616a', brightGreen: '#a3be8c', brightYellow: '#ebcb8b', brightBlue: '#81a1c1', brightMagenta: '#b48ead', brightCyan: '#8fbcbb', brightWhite: '#eceff4' } : { background: '#101419', foreground: '#c9d4df', cursor: '#91e1bd', selectionBackground: '#335447', black: '#202832', red: '#e58b8b', green: '#91e1bd', yellow: '#e4c48c', blue: '#8ab6e9', magenta: '#ba9de5', cyan: '#86cad2', white: '#d7dfe8', brightBlack: '#68798c', brightRed: '#f0a6a6', brightGreen: '#b4edce', brightYellow: '#f2d6a5', brightBlue: '#accbf1', brightMagenta: '#d4b9f5', brightCyan: '#a3e1e8', brightWhite: '#f2f5f8' };
}

async function connectHost(hostId) {
  const host = workspace.hosts.find(item => item.id === hostId);
  if (!host) throw new Error(t('该主机已被删除。'));
  if (sessions.size >= 12) throw new Error(t('最多打开 12 个终端标签，请先关闭部分会话。'));
  const id = crypto.randomUUID();
  const terminal = new Terminal({ ...terminalOptions(), allowProposedApi: false, rightClickSelectsWord: true });
  const fit = new FitAddon.FitAddon();
  terminal.loadAddon(fit);
  const pane = document.createElement('div');
  pane.className = 'terminal-pane';
  pane.id = 'terminal-' + id;
  $('#terminals').append(pane);
  const session = { host: { ...host }, terminal, fit, pane, status: 'connecting' };
  sessions.set(id, session);
  showSession(id);
  terminal.open(pane);
  fit.fit();
  terminal.onData(data => { if (session.status === 'connected') request('input', { sessionId: id, data }).catch(error => toast(error.message, true)); });
  terminal.onResize(({ cols, rows }) => { if (session.status === 'connected') request('resize', { sessionId: id, columns: cols, rows }).catch(() => {}); });
  terminal.writeln(t('\x1b[90m正在准备 SSH 连接，请在系统对话框中输入凭据…\x1b[0m'));
  try {
    const result = await request('connect', { hostId, sessionId: id, columns: terminal.cols, rows: terminal.rows });
    if (!sessions.has(id)) { await request('disconnect', { sessionId: id }); return; }
    if (session.status === 'closed') return;
    session.status = 'connected';
    refreshWorkspace(result.workspace);
    renderTabs(); updateTerminalToolbar();
    if (activeSession === id) { fitActiveTerminal(); terminal.focus(); }
  } catch (error) {
    if (!sessions.has(id)) return;
    session.status = 'closed';
    terminal.writeln(msg`\r\n\x1b[31m连接未建立：${t(error.message)}\x1b[0m`);
    toast(error.message, true);
    renderTabs(); updateTerminalToolbar();
  }
  updateFooter();
}

function showSession(id) {
  if (!sessions.has(id)) return;
  activeSession = id;
  $('#page-scroll').classList.add('hidden');
  $('#terminal-workspace').classList.remove('hidden');
  sessions.forEach((session, sessionId) => session.pane.classList.toggle('hidden', sessionId !== id));
  $('#breadcrumb-label').textContent = t('终端会话');
  renderTabs(); updateTerminalToolbar();
  requestAnimationFrame(() => { fitActiveTerminal(); sessions.get(id)?.terminal.focus(); });
}

function updateTerminalToolbar() {
  const session = sessions.get(activeSession);
  if (!session) return;
  $('#terminal-endpoint').textContent = `${session.host.username}@${session.host.address}:${session.host.port}`;
  $('#terminal-state').textContent = ({ connecting: t('正在连接…'), connected: t('● 已连接'), closed: t('连接已关闭') })[session.status];
  $('#reconnect-button').classList.toggle('hidden', session.status !== 'closed');
  $('[data-action="disconnect"]').disabled = session.status !== 'connected';
}

function fitActiveTerminal() {
  const session = sessions.get(activeSession);
  if (!session || !session.terminal.element) return;
  session.fit.fit();
  $('#footer-right').textContent = `UTF-8  ·  xterm-256color  ·  ${session.terminal.cols} × ${session.terminal.rows}`;
}

async function closeSession(id, ask = true) {
  const session = sessions.get(id);
  if (!session) return;
  if (ask && session.status !== 'closed' && !await confirmAction(t('关闭终端会话？'), msg`将断开 ${session.host.name} 的 SSH 连接。远端运行的任务可能被终止。`)) return;
  await request('disconnect', { sessionId: id });
  sessions.delete(id);
  session.terminal.dispose();
  session.pane.remove();
  if (activeSession === id) {
    const next = [...sessions.keys()].at(-1);
    if (next) showSession(next);
    else showView(currentView, currentGroup);
  }
  renderTabs(); updateFooter();
}

function confirmAction(title, description) {
  const dialog = $('#confirm-dialog');
  if (dialog.open) return Promise.resolve(false);
  $('#confirm-title').textContent = title;
  $('#confirm-description').textContent = description;
  return new Promise(resolve => {
    let settled = false;
    const finish = accepted => {
      if (settled) return;
      settled = true;
      dialog.removeEventListener('cancel', cancel);
      $('#confirm-accept').onclick = null;
      $('#confirm-cancel').onclick = null;
      dialog.close();
      resolve(accepted);
    };
    const cancel = event => { event.preventDefault(); finish(false); };
    $('#confirm-accept').onclick = () => finish(true);
    $('#confirm-cancel').onclick = () => finish(false);
    dialog.addEventListener('cancel', cancel);
    dialog.showModal();
    $('#confirm-cancel').focus();
  });
}

function parseQuickAddress(value) {
  const match = value.trim().replace(/^ssh\s+/i, '').match(/^(?:([^@\s]+)@)?(\[[a-fA-F0-9:.%]+\]|[^:\s/@]+)(?::(\d+))?$/);
  if (!match) throw new Error(t('请输入 user@hostname:port，IPv6 请使用 [::1]:22 格式。'));
  const port = Number(match[3] || 22);
  if (port < 1 || port > 65535) throw new Error(t('端口必须在 1–65535 之间。'));
  const address = match[2].replace(/^\[|\]$/g, '');
  return { name: address, address, username: match[1] || 'root', port };
}

function openEditor(html, submit) {
  editorGeneration++;
  $('#editor-form').innerHTML = html;
  editorSubmit = submit;
  $('#editor-dialog').showModal();
}

function editorHeader(title, description, eyebrow = 'HOST CONFIGURATION') {
  return ui`<div class="dialog-heading"><div><div class="eyebrow">${eyebrow}</div><h2>${title}</h2><p>${description}</p></div><button type="button" class="icon-button" data-action="closeEditor" aria-label="关闭">${icon('close')}</button></div>`;
}

function editorFooter(label) {
  return ui`<div id="form-error" class="form-error" role="alert"></div><div class="dialog-footer"><button class="secondary-button" type="button" data-action="closeEditor">取消</button><button class="primary-button" type="submit">${label}${icon('check')}</button></div>`;
}

function openGroupEditor() {
  openEditor(editorHeader(t('新建分组'), t('先创建分组，再按环境或用途添加主机。'), 'HOST GROUP') + ui`<div class="form-grid">
    <div class="field full"><label for="group-name">分组名称</label><input id="group-name" name="name" placeholder="例如：生产环境、开发环境" maxlength="80" required autofocus><span class="field-hint">支持空分组，不需要填写服务器地址或登录信息。</span></div>
    </div>${editorFooter(t('创建分组'))}`, async form => {
    const name = String(new FormData(form).get('name') || '').trim();
    if (!name) throw new Error(t('请填写分组名称。'));
    if (workspaceGroups().includes(name)) throw new Error(t('该分组已存在，请使用其他名称。'));
    refreshWorkspace(await request('saveGroup', { name }));
    showView('hosts', name);
    toast(t('分组已创建，可以稍后添加主机'));
  });
}

function openHostEditor(initial = {}, group = currentGroup) {
  const host = { name: '', address: '', port: 22, username: 'root', group: group || '未分组', authType: 'password', privateKeyPath: '', note: '', color: 'mint', favorite: false, ...initial };
  openEditor(editorHeader(host.id ? t('编辑主机') : t('新建主机'), t('保存连接信息，登录凭据将在连接时单独询问。')) + ui`<div class="form-grid">
    <div class="field full"><label for="host-name">名称</label><input id="host-name" name="name" placeholder="例如：Production · Singapore" maxlength="100" value="${escapeHtml(host.name)}" required autofocus></div>
    <div class="field"><label for="host-address">主机地址</label><input id="host-address" name="address" placeholder="192.168.1.10 / example.com" maxlength="255" value="${escapeHtml(host.address)}" required spellcheck="false"></div>
    <div class="field"><label for="host-port">端口</label><input id="host-port" name="port" type="number" min="1" max="65535" value="${host.port}" required></div>
    <div class="field"><label for="host-username">用户名</label><input id="host-username" name="username" value="${escapeHtml(host.username)}" maxlength="100" required autocomplete="off" spellcheck="false"></div>
    <div class="field"><label for="host-group">分组<span>可输入新分组</span></label><input id="host-group" name="group" list="group-options" value="${escapeHtml(host.group === '未分组' && uiLanguage === 'en' ? '' : host.group)}" placeholder="${escapeHtml(t('未分组'))}" maxlength="80"><datalist id="group-options">${workspaceGroups().map(item => `<option value="${escapeHtml(item)}">`).join('')}</datalist></div>
    <div class="field full"><label for="host-auth">认证方式</label><select id="host-auth" name="authType"><option value="password" ${host.authType === 'password' ? 'selected' : ''}>密码认证 · Password</option><option value="key" ${host.authType === 'key' ? 'selected' : ''}>私钥认证 · SSH Key</option></select></div>
    <div class="field full ${host.authType === 'key' ? '' : 'hidden'}" id="key-field"><label for="host-key">本地私钥路径</label><div class="key-picker"><input id="host-key" name="privateKeyPath" value="${escapeHtml(host.privateKeyPath)}" placeholder="选择 id_ed25519 或 id_rsa" readonly><button type="button" class="secondary-button" data-action="pickKey">${icon('folder')}选择</button></div><span class="field-hint">只保存文件路径，不复制私钥。连接时可输入私钥口令。</span></div>
    <div class="field full"><label for="host-note">备注<span>可选</span></label><textarea id="host-note" name="note" maxlength="2000" placeholder="关于这台主机的备注…">${escapeHtml(host.note)}</textarea></div>
    <div class="field full"><label>标识颜色</label><div class="color-picker">${['mint', 'blue', 'purple', 'amber'].map(color => `<label class="${color}" aria-label="${color}"><input type="radio" name="color" value="${color}" ${host.color === color ? 'checked' : ''}></label>`).join('')}</div></div>
    </div><div class="form-security">${icon('shield')}配置仅保存在本机，密码不会写入配置文件。</div>${editorFooter(t('保存主机'))}`, async form => {
    const values = Object.fromEntries(new FormData(form));
    refreshWorkspace(await request('saveHost', { ...host, ...values, port: Number(values.port) }));
    toast(host.id ? t('主机配置已更新') : t('主机已添加，点击「连接」开始工作'));
  });
  $('#host-auth').addEventListener('change', event => $('#key-field').classList.toggle('hidden', event.target.value !== 'key'));
}

function renderSnippets() {
  $('#page').innerHTML = heading('LESS TYPING, MORE DOING', t('命令片段'), t('保存常用命令。一键填入终端，由你确认后再执行。'), ui`<button class="primary-button" data-action="newSnippet">${icon('plus')}新建片段</button>`) + (workspace.snippets.length ? `<div class="snippet-grid">${workspace.snippets.map(snippet => ui`<article class="snippet-card"><div class="snippet-heading">${icon('code')}<span>${escapeHtml(snippet.name)}</span><div class="host-actions"><button class="icon-button" data-action="editSnippet" data-id="${escapeHtml(snippet.id)}" title="编辑片段" aria-label="编辑片段">${icon('edit')}</button><button class="icon-button" data-action="deleteSnippet" data-id="${escapeHtml(snippet.id)}" title="删除片段" aria-label="删除片段">${icon('trash')}</button></div></div><pre>${escapeHtml(snippet.command)}</pre><div class="snippet-footer"><button class="subtle-button" data-action="copySnippet" data-id="${escapeHtml(snippet.id)}">${icon('copy')}复制</button><button class="connect-button" data-action="insertSnippet" data-id="${escapeHtml(snippet.id)}">填入终端${icon('arrow')}</button></div></article>`).join('')}</div>` : ui`<div class="no-results">${icon('code')}<br>把重复的敲击，变成一个片段。<br><button class="subtle-button" data-action="newSnippet">创建你的第一个命令片段 ${icon('plus')}</button></div>`) + uiHtml('<p class="settings-note">安全提示：片段为本地明文存储，请勿在其中保存密码、令牌或私钥。</p>');
}

function openSnippetEditor(snippet = {}) {
  openEditor(editorHeader(snippet.id ? t('编辑片段') : t('新建命令片段'), t('片段不会自动执行；填入终端后仍需按 Enter。'), 'COMMAND SNIPPET') + ui`<div class="form-grid"><div class="field full"><label for="snippet-name">名称</label><input id="snippet-name" name="name" required maxlength="100" value="${escapeHtml(snippet.name)}" placeholder="例如：查看磁盘空间" autofocus></div><div class="field full"><label for="snippet-command">命令</label><textarea class="command-textarea" id="snippet-command" name="command" required maxlength="20000" spellcheck="false" placeholder="df -h">${escapeHtml(snippet.command)}</textarea></div></div>${editorFooter(t('保存片段'))}`, async form => {
    refreshWorkspace(await request('saveSnippet', { ...snippet, ...Object.fromEntries(new FormData(form)) }));
    toast(t('命令片段已保存'));
  });
}


new ResizeObserver(() => fitActiveTerminal()).observe($('#terminals'));
renderTabs();
renderSidebar();
renderPage();
Promise.all([request('workspace'), localeReady]).then(([value]) => { ready = true; refreshWorkspace(value); if (localeLoadError) toast(t('语言资源加载失败，请检查安装文件后重启应用。'), true); }).catch(error => toast(error.message, true));
request('window', { action: 'state' }).then(result => updateWindowState(result.maximized)).catch(() => {});
document.querySelectorAll('.nav-item').forEach(button => {
  button.title = button.querySelector('span')?.textContent || '';
  button.setAttribute('aria-label', button.title);
});
