'use strict';

// 列表和任务状态保存在页面渲染之外，切换导航或语言时不重置连接与传输。
const sftpState = {
  sessionId: null, hostId: null, connecting: false, jobs: new Map(), refreshTimer: null, paneRatio: .5,
  local: { listing: null, selected: new Set(), query: '', hidden: false, busy: false, error: '', revision: 0, initialized: false },
  remote: { listing: null, selected: new Set(), query: '', hidden: false, busy: false, error: '', revision: 0 }
};

function sftpVisible() { return !activeSession && currentView === 'sftp'; }
function sftpRenderIfVisible() { if (sftpVisible()) renderSftp(); }
function sftpSize(bytes) {
  if (!Number.isFinite(bytes)) return '—';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let unit = 0;
  while (bytes >= 1024 && unit < units.length - 1) { bytes /= 1024; unit++; }
  return `${bytes.toFixed(unit && bytes < 10 ? 1 : 0)} ${units[unit]}`;
}
function sftpSelected(side) {
  const pane = sftpState[side];
  return (pane.listing?.entries || []).filter(entry => pane.selected.has(entry.path));
}
function sftpActiveJobs() { return [...sftpState.jobs.values()].filter(job => ['queued', 'running'].includes(job.status)); }
function sftpHasLocalDirectory() { return !!sftpState.local.listing && !sftpState.local.listing.computer; }

function renderSftp() {
  updateFooter();
  if (!sftpState.hostId && workspace.hosts.length) sftpState.hostId = selectedHostId || workspace.hosts[0].id;
  const host = workspace.hosts.find(item => item.id === sftpState.hostId);
  const connected = !!sftpState.sessionId;
  $('#page').innerHTML = ui`<div class="sftp-heading"><div><span class="eyebrow">FILES, SIDE BY SIDE</span><h1>SFTP 文件传输</h1><p>本地与远端，并肩协作。</p></div><span class="sftp-security">${icon('shield')}SSH 加密 · 指纹校验</span></div>
    <div class="sftp-connection"><span class="sftp-connection-icon">${icon('server')}</span><div class="sftp-host-field"><label for="sftp-host">远端主机</label><select id="sftp-host" ${connected || sftpState.connecting ? 'disabled' : ''}><option value="">选择已保存的主机</option>${workspace.hosts.map(item => `<option value="${escapeHtml(item.id)}" ${item.id === sftpState.hostId ? 'selected' : ''}>${escapeHtml(item.name)} · ${escapeHtml(item.username)}@${escapeHtml(item.address)}</option>`).join('')}</select></div><span class="sftp-connection-state ${connected ? 'online' : ''}"><span class="status-dot"></span>${sftpState.connecting ? t('正在连接…') : connected ? t('SFTP 已连接') : t('尚未连接')}</span><button class="${connected ? 'secondary-button' : 'primary-button'}" data-action="sftpConnection" ${sftpState.connecting || (!connected && !host) ? 'disabled' : ''}>${icon(connected ? 'close' : 'bolt')}${connected ? t('断开连接') : t('连接主机')}</button></div>
    <div class="sftp-panes" style="--sftp-left:${sftpState.paneRatio}fr;--sftp-right:${1 - sftpState.paneRatio}fr"><section class="sftp-pane" id="sftp-local" aria-label="本地文件"></section><div class="sftp-pane-divider" role="separator" aria-label="调整文件面板宽度" aria-orientation="vertical" aria-valuemin="30" aria-valuemax="70" aria-valuenow="${Math.round(sftpState.paneRatio * 100)}" tabindex="0" title="左右拖动调整宽度；双击复原">${icon('transfer')}</div><section class="sftp-pane" id="sftp-remote" aria-label="远端文件"></section></div>
    <div class="sftp-safety-note">${icon('lock')}左右拖动文件即可上传 / 下载，也可拖入资源管理器中的文件 · 同名不覆盖 · 不传输文件夹或链接</div><section class="sftp-transfers" id="sftp-transfers" aria-label="传输队列"></section>`;
  renderSftpPane('local'); renderSftpPane('remote'); renderSftpQueue();
  if (ready && !sftpState.local.initialized) sftpLoad('local');
}

function renderSftpPane(side) {
  const element = $(`#sftp-${side}`);
  if (!element) return;
  const pane = sftpState[side], local = side === 'local';
  const selected = sftpSelected(side), fileCount = selected.filter(entry => !entry.directory && !entry.link).length;
  const connected = !!sftpState.sessionId, busy = pane.busy;
  element.innerHTML = ui`<header class="sftp-pane-header"><div class="sftp-pane-label">${icon(local ? 'computer' : 'server')}<strong>${local ? t('本地文件') : t('远端文件')}</strong><small>${local ? 'THIS DEVICE' : 'REMOTE'}</small></div><div>${local ? uiHtml('<button class="icon-button" data-action="sftpComputer" aria-label="此电脑：全部磁盘" title="此电脑：全部磁盘"><svg class="icon"><use href="#i-computer"/></svg></button><button class="subtle-button" data-action="sftpPickLocal">选择文件夹</button>') : ui`<button class="icon-button" data-action="sftpMkdir" aria-label="远端新建文件夹" title="新建文件夹" ${!connected || busy ? 'disabled' : ''}>${icon('plus')}</button>`}<button class="icon-button" data-action="sftpRefresh" data-side="${side}" aria-label="刷新${local ? t('本地') : t('远端')}" ${!pane.listing || busy ? 'disabled' : ''}>${icon('refresh')}</button></div></header>
    <form class="sftp-path" data-sftp-path="${side}"><button class="icon-button" type="button" data-action="sftpUp" data-side="${side}" aria-label="${local ? t('本地') : t('远端')}上级目录" ${!pane.listing?.parent || busy ? 'disabled' : ''}>${icon('up')}</button><input aria-label="${local ? t('本地') : t('远端')}路径" value="${escapeHtml(pane.listing?.computer ? t('此电脑') : pane.listing?.path || '')}" placeholder="${local ? t('输入磁盘路径或选择文件夹') : connected ? t('输入远端路径，按 Enter') : t('连接后显示远端目录')}" ${!pane.listing || busy ? 'disabled' : ''}><button class="icon-button" aria-label="转到${local ? t('本地') : t('远端')}路径" ${!pane.listing || busy ? 'disabled' : ''}>${icon('arrow')}</button></form>
    <div class="sftp-filter">${icon('search')}<input data-sftp-search="${side}" aria-label="筛选${local ? t('本地') : t('远端')}文件" placeholder="筛选名称…" value="${escapeHtml(pane.query)}"><button class="subtle-button ${pane.hidden ? 'selected' : ''}" data-action="sftpHidden" data-side="${side}" aria-pressed="${pane.hidden}" title="显示点开头的隐藏文件">隐藏文件${pane.hidden ? ' ✓' : ''}</button></div>
    ${pane.error ? `<div class="sftp-error" role="alert">${escapeHtml(t(pane.error))}</div>` : ''}
    <div class="sftp-file-columns"><span>名称</span><span>大小</span><span>修改时间</span></div><div class="sftp-files" data-side="${side}" aria-busy="${busy}"></div>
    <footer class="sftp-pane-footer"><span>${busy ? t('读取中…') : msg`${pane.listing?.entries.length || 0} 项 · 已选 ${selected.length} 项`}</span><div>${!local ? ui`<button class="icon-button" data-action="sftpRename" title="重命名" aria-label="远端重命名" ${selected.length !== 1 || busy ? 'disabled' : ''}>${icon('edit')}</button><button class="icon-button" data-action="sftpDelete" title="删除文件或空目录" aria-label="删除远端项目" ${selected.length !== 1 || busy ? 'disabled' : ''}>${icon('trash')}</button>` : ''}<button class="sftp-transfer-button" data-action="sftpTransfer" data-side="${side}" ${!connected || !sftpHasLocalDirectory() || !fileCount || busy ? 'disabled' : ''}>${icon(local ? 'upload' : 'download')}${local ? t('上传') : t('下载')}${fileCount ? ` (${fileCount})` : ''}</button></div></footer>`;
  renderSftpRows(side);
  updateSftpMutationControls();
}

function updateSftpMutationControls() {
  const pane = sftpState.remote;
  const locked = !sftpState.sessionId || pane.busy || sftpActiveJobs().some(job => job.sessionId === sftpState.sessionId);
  document.querySelectorAll('[data-action="sftpMkdir"], [data-action="sftpRename"], [data-action="sftpDelete"]').forEach(button => {
    button.disabled = locked || (button.dataset.action !== 'sftpMkdir' && sftpSelected('remote').length !== 1);
    button.title = locked ? t('连接并等待当前传输结束后可操作') : ({ sftpMkdir: t('新建文件夹'), sftpRename: t('重命名'), sftpDelete: t('删除文件或空目录') })[button.dataset.action];
  });
}

function renderSftpRows(side) {
  const pane = sftpState[side], target = $(`#sftp-${side} .sftp-files`);
  if (!target) return;
  if (!pane.listing) {
    target.innerHTML = `<div class="sftp-empty"><div>${icon(side === 'local' ? 'folder' : 'server')}</div><h3>${side === 'local' ? pane.busy ? t('正在加载本地磁盘…') : t('本地磁盘暂不可用') : t('连接你的远端主机')}</h3><p>${side === 'local' ? pane.busy ? t('默认显示此电脑的全部磁盘。') : t('可以重新加载，或选择其他本地文件夹。') : t('使用已有主机和安全认证，浏览服务器文件。')}</p>${side === 'local' ? pane.busy ? '' : uiHtml('<button class="secondary-button" data-action="sftpDefaultLocal">重新加载</button>') : uiHtml('<span>密码与私钥口令不会保存</span>')}</div>`;
    return;
  }
  const entries = pane.listing.entries.map(entry => entry.drive ? { ...entry, name: isMacOS ? entry.path === '/' ? t('系统磁盘 (/)') : entry.name : t('磁盘 ({0})', entry.path.replace(/[\\/]/g, '')) } : entry).filter(entry => (pane.hidden || !entry.name.startsWith('.')) && entry.name.toLocaleLowerCase().includes(pane.query.toLocaleLowerCase()));
  target.innerHTML = entries.length ? entries.map(entry => ui`<div class="sftp-file-row ${pane.selected.has(entry.path) ? 'selected' : ''} ${entry.link ? 'link' : ''}" data-sftp-entry="${escapeHtml(entry.path)}" data-side="${side}" draggable="${!entry.directory && !entry.link && !pane.busy}" tabindex="0" aria-label="${escapeHtml(entry.name)}${entry.directory ? t(' 文件夹') : ''}" title="${escapeHtml(entry.name)}${entry.link ? t('（符号链接，不跟随）') : entry.directory ? t('（双击进入）') : ''}"><span class="sftp-file-name"><input type="checkbox" tabindex="-1" aria-label="选择 ${escapeHtml(entry.name)}" ${pane.selected.has(entry.path) ? 'checked' : ''}>${icon(entry.drive ? 'computer' : entry.link ? 'link' : entry.directory ? 'folder' : 'file')}<span>${escapeHtml(entry.name)}</span>${entry.link ? uiHtml('<small>链接</small>') : ''}</span><span class="sftp-file-size">${entry.directory || entry.link ? '—' : sftpSize(entry.size)}</span><time>${entry.drive ? '—' : escapeHtml(new Date(entry.modified).toLocaleDateString(uiLocale()))}</time></div>`).join('') : `<div class="sftp-list-empty">${pane.query ? t('没有匹配的文件') : t('此目录中没有可显示的项目')}</div>`;
}

function renderSftpQueue() {
  updateSftpMutationControls();
  const element = $('#sftp-transfers');
  if (!element) return;
  const jobs = [...sftpState.jobs.values()], active = sftpActiveJobs().length;
  element.innerHTML = ui`<header><div>${icon('transfer')}<h2>传输队列</h2><span class="sftp-count">${active ? msg`${active} 个进行中` : msg`${jobs.length} 个任务`}</span></div><button class="subtle-button" data-action="sftpClearJobs" ${jobs.some(job => !['running', 'queued'].includes(job.status)) ? '' : 'disabled'}>清除已结束</button></header><div class="sftp-job-list">${jobs.length ? jobs.slice().reverse().map(job => {
    const running = ['queued', 'running'].includes(job.status);
    const percent = job.total ? Math.min(100, Math.round(job.bytes / job.total * 100)) : job.status === 'completed' ? 100 : 0;
    return `<article class="sftp-job" data-job="${escapeHtml(job.id)}"><span class="sftp-job-icon ${job.status}">${icon(job.status === 'completed' ? 'check' : job.direction === 'upload' ? 'upload' : 'download')}</span><div class="sftp-job-details"><strong title="${escapeHtml(job.name)}">${escapeHtml(job.name)}</strong><span>${job.direction === 'upload' ? t('上传到远端') : t('下载到本地')} · ${escapeHtml(({ queued: t('排队中'), running: `${percent}% · ${sftpSize(job.bytes)} / ${sftpSize(job.total)}`, completed: t('已完成'), cancelled: t('已取消'), failed: t('失败') })[job.status] || job.status)}</span>${running ? `<progress max="100" value="${percent}"></progress>` : job.error ? `<span class="sftp-job-error">${escapeHtml(t(job.error))}</span>` : ''}</div>${running ? ui`<button class="icon-button" data-action="sftpCancel" data-id="${escapeHtml(job.id)}" aria-label="取消传输 ${escapeHtml(job.name)}" title="取消传输">${icon('close')}</button>` : ''}</article>`;
  }).join('') : ui`<div class="sftp-queue-empty">${icon('transfer')}选择文件，点击上传或下载。传输进度会显示在这里。</div>`}</div>`;
}

// 目录请求带版本号及会话检查，异步返回的旧结果不能覆盖用户当前浏览位置。
async function sftpLoad(side, path) {
  const pane = sftpState[side], sessionId = sftpState.sessionId;
  if (side === 'local' && path === t('此电脑')) path = 'computer:';
  if (pane.busy || (side === 'remote' && !sessionId)) return;
  if (side === 'local') pane.initialized = true;
  const revision = ++pane.revision;
  pane.busy = true; pane.error = ''; renderSftpPane(side);
  try {
    const operation = side === 'local' ? path === undefined || path === 'computer:' ? 'sftpDefaultLocal' : 'sftpLocalList' : 'sftpList';
    const listing = await request(operation, { sessionId, path });
    if (revision !== pane.revision || (side === 'remote' && sessionId !== sftpState.sessionId)) return;
    pane.listing = listing; pane.selected.clear();
  } catch (error) { if (revision === pane.revision) pane.error = error.message; }
  finally {
    if (revision === pane.revision) {
      pane.busy = false;
      if (sftpVisible()) {
        renderSftpPane(side);
        if (side === 'local') renderSftpPane('remote');
      }
    }
  }
}

async function openSftp(hostId) {
  showView('sftp');
  if (sftpState.connecting) return;
  if (sftpState.sessionId && hostId !== sftpState.hostId) {
    if (!await confirmAction(t('切换 SFTP 主机？'), t('当前 SFTP 连接将关闭，未完成的传输会取消。SSH 终端不会受影响。'))) return;
    await sftpDisconnect();
  }
  sftpState.hostId = hostId; sftpRenderIfVisible();
}

async function sftpDisconnect() {
  const sessionId = sftpState.sessionId;
  await request('sftpDisconnect', { sessionId });
  sftpState.sessionId = null;
  sftpState.remote.revision++; sftpState.remote.busy = false;
  sftpState.remote.listing = null; sftpState.remote.selected.clear(); sftpState.remote.error = '';
  sftpRenderIfVisible();
}

async function sftpStartTransfer(side) {
  const sessionId = sftpState.sessionId;
  const localDirectory = sftpState.local.listing?.path, remoteDirectory = sftpState.remote.listing?.path;
  if (!sessionId || !sftpHasLocalDirectory() || !localDirectory || !remoteDirectory) return;
  const entries = sftpSelected(side).filter(entry => !entry.directory && !entry.link);
  await sftpQueueEntries(side, entries, side === 'local' ? remoteDirectory : localDirectory, sessionId);
}

async function sftpQueueEntries(side, entries, destination, sessionId) {
  if (!entries.length) return;
  if (sftpActiveJobs().length + entries.length > 100) throw new Error(t('传输队列最多 100 个活动任务，请等待部分任务结束。'));
  for (const entry of entries) {
    if (sessionId !== sftpState.sessionId) throw new Error(t('SFTP 会话已切换，请重新拖动文件。'));
    const job = await request('sftpTransfer', { sessionId, direction: side === 'local' ? 'upload' : 'download', path: side === 'local' ? destination : entry.path, localPath: side === 'local' ? entry.path : destination });
    if (!sftpState.jobs.has(job.id)) sftpState.jobs.set(job.id, job);
    if (sftpVisible()) renderSftpQueue();
  }
}

function sftpNameEditor(action) {
  const entry = sftpSelected('remote')[0], sessionId = sftpState.sessionId, directory = sftpState.remote.listing?.path;
  if (!sessionId || !directory || (action === 'rename' && !entry)) return;
  openEditor(editorHeader(action === 'mkdir' ? t('新建远端文件夹') : t('重命名远端项目'), t('不会覆盖已存在的同名项目。'), 'REMOTE FILES') + ui`<div class="field"><label for="sftp-name">名称</label><input id="sftp-name" name="name" maxlength="240" value="${escapeHtml(action === 'rename' ? entry.name : '')}" required autofocus></div>${editorFooter(action === 'mkdir' ? t('创建文件夹') : t('保存名称'))}`, async form => {
    const name = String(new FormData(form).get('name') || '');
    await request('sftpMutate', { sessionId, action, path: action === 'mkdir' ? directory : entry.path, name });
    sftpLoad('remote', directory); toast(action === 'mkdir' ? t('文件夹已创建') : t('已重命名'));
  });
}

document.addEventListener('click', async event => {
  const row = event.target.closest('[data-sftp-entry]');
  if (row && ready) {
    const pane = sftpState[row.dataset.side];
    if (pane.busy) return;
    const path = row.dataset.sftpEntry;
    if (event.ctrlKey || event.target.matches('input')) pane.selected.has(path) ? pane.selected.delete(path) : pane.selected.add(path);
    else { pane.selected.clear(); pane.selected.add(path); }
    const element = $(`#sftp-${row.dataset.side}`);
    element.querySelectorAll('[data-sftp-entry]').forEach(item => {
      const selected = pane.selected.has(item.dataset.sftpEntry);
      item.classList.toggle('selected', selected); item.querySelector('input').checked = selected;
    });
    const footer = element.querySelector('.sftp-pane-footer');
    footer.querySelector('span').textContent = msg`${pane.listing.entries.length} 项 · 已选 ${pane.selected.size} 项`;
    const selected = sftpSelected(row.dataset.side);
    const files = selected.filter(entry => !entry.directory && !entry.link).length;
    const transfer = footer.querySelector('[data-action="sftpTransfer"]');
    transfer.disabled = !sftpState.sessionId || !sftpHasLocalDirectory() || !files || pane.busy;
    transfer.innerHTML = `${icon(row.dataset.side === 'local' ? 'upload' : 'download')}${row.dataset.side === 'local' ? t('上传') : t('下载')}${files ? ` (${files})` : ''}`;
    footer.querySelectorAll('[data-action="sftpRename"], [data-action="sftpDelete"]').forEach(button => button.disabled = selected.length !== 1 || pane.busy);
    updateSftpMutationControls();
    return;
  }
  const button = event.target.closest('[data-action^="sftp"]');
  if (!button || button.disabled || !ready) return;
  const { action, side, id } = button.dataset;
  try {
    if (action === 'sftpConnection') {
      if (sftpState.sessionId) {
        if (await confirmAction(t('断开 SFTP 连接？'), t('未完成的传输会取消。SSH 终端保持连接。'))) await sftpDisconnect();
      } else {
        if (sftpState.connecting) return;
        sftpState.connecting = true; sftpRenderIfVisible();
        try {
          const result = await request('sftpConnect', { hostId: sftpState.hostId });
          sftpState.remote.revision++; sftpState.remote.busy = false;
          sftpState.sessionId = result.sessionId; sftpState.remote.listing = result.listing; sftpState.remote.error = ''; sftpState.remote.selected.clear();
        } finally { sftpState.connecting = false; sftpRenderIfVisible(); }
      }
    } else if (action === 'sftpPickLocal') {
      const listing = await request('sftpPickLocal');
      if (listing) { sftpState.local.initialized = true; sftpState.local.revision++; sftpState.local.busy = false; sftpState.local.listing = listing; sftpState.local.selected.clear(); sftpState.local.error = ''; sftpRenderIfVisible(); }
    } else if (action === 'sftpDefaultLocal' || action === 'sftpComputer') await sftpLoad('local');
    else if (action === 'sftpRefresh') await sftpLoad(side, sftpState[side].listing.path);
    else if (action === 'sftpUp') await sftpLoad(side, sftpState[side].listing.parent);
    else if (action === 'sftpHidden') { sftpState[side].hidden = !sftpState[side].hidden; renderSftpPane(side); }
    else if (action === 'sftpTransfer') await sftpStartTransfer(side);
    else if (action === 'sftpCancel') await request('sftpCancel', { id });
    else if (action === 'sftpClearJobs') { for (const [jobId, job] of sftpState.jobs) if (!['running', 'queued'].includes(job.status)) sftpState.jobs.delete(jobId); renderSftpQueue(); }
    else if (action === 'sftpMkdir' || action === 'sftpRename') sftpNameEditor(action === 'sftpMkdir' ? 'mkdir' : 'rename');
    else if (action === 'sftpDelete') {
      const entry = sftpSelected('remote')[0], sessionId = sftpState.sessionId, path = sftpState.remote.listing.path;
      if (entry && await confirmAction(t('删除远端项目？'), msg`将永久删除「${entry.name}」。不经过回收站；目录必须为空，不会递归删除。`)) {
        await request('sftpMutate', { sessionId, action: 'delete', path: entry.path }); await sftpLoad('remote', path); toast(t('远端项目已删除'));
      }
    }
  } catch (error) { toast(error.message, true); }
});

document.addEventListener('dblclick', event => {
  const row = event.target.closest('[data-sftp-entry]');
  if (!row || event.target.matches('input')) return;
  const entry = sftpState[row.dataset.side].listing?.entries.find(item => item.path === row.dataset.sftpEntry);
  if (entry?.directory && !entry.link) sftpLoad(row.dataset.side, entry.path);
});
document.addEventListener('keydown', event => {
  const row = event.target.closest('[data-sftp-entry]');
  if (!row || event.key !== 'Enter') return;
  event.preventDefault();
  const entry = sftpState[row.dataset.side].listing?.entries.find(item => item.path === row.dataset.sftpEntry);
  if (entry?.directory && !entry.link) sftpLoad(row.dataset.side, entry.path);
});
document.addEventListener('submit', event => {
  const form = event.target.closest('[data-sftp-path]');
  if (!form) return;
  event.preventDefault(); sftpLoad(form.dataset.sftpPath, form.querySelector('input').value);
});
document.addEventListener('input', event => {
  if (!event.target.matches('[data-sftp-search]')) return;
  const side = event.target.dataset.sftpSearch;
  sftpState[side].query = event.target.value; renderSftpRows(side);
});
document.addEventListener('change', event => { if (event.target.id === 'sftp-host' && !sftpState.sessionId && !sftpState.connecting) { sftpState.hostId = event.target.value; sftpRenderIfVisible(); } });

function onSftpProgress(job) {
  sftpState.jobs.set(job.id, job);
  for (const [id, existing] of sftpState.jobs) {
    if (sftpState.jobs.size <= 200) break;
    if (!['running', 'queued'].includes(existing.status)) sftpState.jobs.delete(id);
  }
  if (sftpVisible()) renderSftpQueue();
  if (job.status !== 'completed') return;
  clearTimeout(sftpState.refreshTimer);
  sftpState.refreshTimer = setTimeout(() => {
    if (sftpState.local.listing) sftpLoad('local', sftpState.local.listing.path);
    if (job.sessionId === sftpState.sessionId && sftpState.remote.listing) sftpLoad('remote', sftpState.remote.listing.path);
  }, 350);
}

function onSftpEnded(message) {
  if (message.sessionId !== sftpState.sessionId) return;
  sftpDrag = null; sftpClearDropTarget();
  sftpState.sessionId = null;
  const pane = sftpState.remote;
  pane.revision++; pane.busy = false; pane.listing = null; pane.selected.clear();
  pane.error = message.error || t('SFTP 连接已断开，请重新连接。');
  updateFooter(); sftpRenderIfVisible(); toast(pane.error, true);
}

// 应用内拖放只在当前进程内保留源项目，拖放数据仅携带一次性令牌用于核对。
let sftpDrag = null;
const sftpDragType = 'application/x-termlume-files';

function sftpClearDropTarget() {
  document.querySelectorAll('.sftp-drop-active, .sftp-drop-folder').forEach(element => {
    element.classList.remove('sftp-drop-active', 'sftp-drop-folder');
    delete element.dataset.dropLabel;
  });
}

function sftpDropTarget(event) {
  if (!ready || !sftpVisible() || !sftpState.sessionId || !event.dataTransfer) return null;
  const files = event.target.closest('.sftp-files');
  if (!files) return null;
  const side = files.dataset.side, pane = sftpState[side];
  const external = !sftpDrag && [...event.dataTransfer.types].includes('Files');
  if (pane.busy || !pane.listing || (external ? side !== 'remote' : !sftpDrag || side === sftpDrag.side || sftpDrag.sessionId !== sftpState.sessionId)) return null;
  const row = event.target.closest('[data-sftp-entry]');
  const entry = pane.listing.entries.find(item => item.path === row?.dataset.sftpEntry);
  const folder = entry?.directory && !entry.link ? entry : null;
  if (pane.listing.computer && !folder) return null;
  return { side, path: folder?.path || pane.listing.path, row: folder ? row : null, external };
}

document.addEventListener('dragstart', event => {
  const row = event.target.closest('[data-sftp-entry]');
  if (!row || !sftpVisible()) return;
  const side = row.dataset.side, pane = sftpState[side];
  const entry = pane.listing?.entries.find(item => item.path === row.dataset.sftpEntry);
  if (!event.dataTransfer || !entry || entry.directory || entry.link || pane.busy || !sftpState.sessionId) { event.preventDefault(); return; }
  const entries = pane.selected.has(entry.path) ? sftpSelected(side).filter(item => !item.directory && !item.link) : [entry];
  sftpDrag = { side, entries, sessionId: sftpState.sessionId, token: crypto.randomUUID() };
  event.dataTransfer.effectAllowed = 'copy';
  event.dataTransfer.setData(sftpDragType, sftpDrag.token);
  event.dataTransfer.setData('text/plain', entries.map(item => item.name).join('\n'));
  const preview = document.createElement('div');
  preview.className = 'sftp-drag-preview'; preview.textContent = msg`${side === 'local' ? t('上传') : t('下载')} ${entries.length} 个文件`;
  document.body.append(preview); event.dataTransfer.setDragImage(preview, 20, 20);
  setTimeout(() => preview.remove(), 0);
});

document.addEventListener('dragover', event => {
  const target = sftpDropTarget(event);
  const fileDrag = sftpDrag || [...(event.dataTransfer?.types || [])].includes('Files');
  if (!fileDrag) return;
  event.preventDefault(); sftpClearDropTarget();
  event.dataTransfer.dropEffect = target ? 'copy' : 'none';
  if (!target) return;
  const pane = $(`#sftp-${target.side}`);
  pane.classList.add('sftp-drop-active');
  pane.dataset.dropLabel = `${target.side === 'remote' ? t('松开上传到') : t('松开下载到')} ${target.row ? target.row.querySelector('.sftp-file-name > span').textContent : t('当前目录')}`;
  target.row?.classList.add('sftp-drop-folder');
});

document.addEventListener('dragleave', event => {
  const pane = event.target.closest('.sftp-pane');
  if (pane && (!event.relatedTarget || !pane.contains(event.relatedTarget))) sftpClearDropTarget();
});
document.addEventListener('dragend', () => { sftpDrag = null; sftpClearDropTarget(); });
window.addEventListener('blur', () => { sftpDrag = null; sftpClearDropTarget(); });

document.addEventListener('drop', async event => {
  const drag = sftpDrag, target = sftpDropTarget(event);
  const fileDrag = drag || [...(event.dataTransfer?.types || [])].includes('Files');
  if (!fileDrag) return;
  event.preventDefault(); sftpDrag = null; sftpClearDropTarget();
  if (!target) { if (sftpVisible()) toast(sftpState.sessionId ? t('请拖到另一侧的文件列表或文件夹；下载时需选择磁盘或文件夹。') : t('请先连接 SFTP 主机，再拖动文件。'), true); return; }
  try {
    if (target.external) {
      // 外部拖入只转交真实 File 对象；普通文件和路径边界由原生端再次校验。
      const items = [...event.dataTransfer.items];
      if (items.some(item => item.webkitGetAsEntry?.()?.isDirectory)) throw new Error(t('暂不支持拖入文件夹，请打开文件夹后拖入普通文件。'));
      const files = [...event.dataTransfer.files];
      if (!files.length || files.length + sftpActiveJobs().length > 100) throw new Error(t('请拖入普通文件，队列最多 100 个活动任务。'));
      const jobs = await request('sftpDropUpload', { sessionId: sftpState.sessionId, path: target.path }, files);
      for (const job of jobs) if (!sftpState.jobs.has(job.id)) sftpState.jobs.set(job.id, job);
      if (sftpVisible()) renderSftpQueue();
    } else {
      if (event.dataTransfer.getData(sftpDragType) !== drag.token) throw new Error(t('拖放已失效，请重新拖动文件。'));
      await sftpQueueEntries(drag.side, drag.entries, target.path, drag.sessionId);
    }
  } catch (error) { toast(error.message, true); }
});

function sftpSetPaneRatio(divider, ratio) {
  const panes = divider.parentElement, available = panes.clientWidth - divider.offsetWidth;
  const minimum = Math.min(.5, Math.max(.3, 340 / available));
  sftpState.paneRatio = Math.max(minimum, Math.min(1 - minimum, ratio));
  panes.style.setProperty('--sftp-left', `${sftpState.paneRatio}fr`);
  panes.style.setProperty('--sftp-right', `${1 - sftpState.paneRatio}fr`);
  divider.setAttribute('aria-valuenow', Math.round(sftpState.paneRatio * 100));
}
document.addEventListener('pointerdown', event => {
  const divider = event.target.closest('.sftp-pane-divider');
  if (!divider || event.button !== 0) return;
  event.preventDefault(); divider.focus(); divider.setPointerCapture(event.pointerId);
  divider.classList.add('resizing');
  const move = pointer => {
    const bounds = divider.parentElement.getBoundingClientRect();
    sftpSetPaneRatio(divider, (pointer.clientX - bounds.left - divider.offsetWidth / 2) / (bounds.width - divider.offsetWidth));
  };
  const finish = () => { divider.classList.remove('resizing'); divider.removeEventListener('pointermove', move); divider.removeEventListener('lostpointercapture', finish); };
  divider.addEventListener('pointermove', move); divider.addEventListener('lostpointercapture', finish);
});
document.addEventListener('keydown', event => {
  const divider = event.target.closest('.sftp-pane-divider');
  if (!divider || !['ArrowLeft', 'ArrowRight', 'Home'].includes(event.key)) return;
  event.preventDefault(); sftpSetPaneRatio(divider, event.key === 'Home' ? .5 : sftpState.paneRatio + (event.key === 'ArrowLeft' ? -.02 : .02));
});
document.addEventListener('dblclick', event => { const divider = event.target.closest('.sftp-pane-divider'); if (divider) sftpSetPaneRatio(divider, .5); });
window.addEventListener('resize', () => { const divider = $('.sftp-pane-divider'); if (divider) sftpSetPaneRatio(divider, sftpState.paneRatio); });
