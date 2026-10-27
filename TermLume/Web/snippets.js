'use strict';

let snippetPanelOpen = false;
let snippetSearch = '';
let lastSnippetSession = null;
let snippetPanelLanguage = null;

function toggleSnippets(open = !snippetPanelOpen) {
  snippetPanelOpen = open;
  $('#terminal-snippets').classList.toggle('hidden', !open);
  $('[data-action="toggleSnippets"]').setAttribute('aria-expanded', String(open));
  renderSnippetPanel();
  requestAnimationFrame(() => {
    fitActiveTerminal();
    if (open) $('#snippet-search')?.focus();
    else sessions.get(activeSession)?.terminal.focus();
  });
}

function renderSnippetPanel() {
  if (!snippetPanelOpen) return;
  const panel = $('#terminal-snippets');
  if (!panel.querySelector('#snippet-search') || snippetPanelLanguage !== uiLanguage) {
    snippetPanelLanguage = uiLanguage;
    panel.innerHTML = ui`<div class="snippet-panel-heading"><div>${icon('code')}<strong>命令片段</strong></div><button class="icon-button" data-action="closeSnippets" title="关闭片段侧栏" aria-label="关闭片段侧栏">${icon('close')}</button></div><div class="snippet-panel-search"><input id="snippet-search" type="search" placeholder="搜索名称或命令" aria-label="搜索名称或命令" maxlength="200"></div><div class="snippet-panel-context"></div><div class="snippet-panel-list"></div><div class="snippet-panel-bottom"><button class="subtle-button" data-action="newSnippet">${icon('plus')}新建片段</button><span>本地存储 · 谨慎执行</span></div>`;
    $('#snippet-search').value = snippetSearch;
    $('#snippet-search').addEventListener('input', event => { snippetSearch = event.target.value; renderSnippetPanel(); });
    $('#snippet-search').addEventListener('keydown', event => {
      if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); toggleSnippets(false); }
    });
  }
  const session = sessions.get(activeSession);
  panel.querySelector('.snippet-panel-context').textContent = session?.status === 'connected'
    ? t('当前终端：{0}', session.host.name) : t('连接主机后即可使用片段');
  const search = snippetSearch.trim().toLocaleLowerCase();
  const snippets = workspace.snippets.filter(snippet => `${snippet.name}\n${snippet.command}`.toLocaleLowerCase().includes(search));
  panel.querySelector('.snippet-panel-list').innerHTML = snippets.length ? snippets.map(snippet => ui`<article class="terminal-snippet"><div class="terminal-snippet-heading"><strong>${escapeHtml(snippet.name)}</strong><button class="icon-button" data-action="editSnippet" data-id="${escapeHtml(snippet.id)}" title="编辑片段" aria-label="编辑片段">${icon('edit')}</button></div><pre>${escapeHtml(snippet.command)}</pre><div class="terminal-snippet-actions"><button class="icon-button" data-action="copySnippet" data-id="${escapeHtml(snippet.id)}" title="复制" aria-label="复制">${icon('copy')}</button><button class="subtle-button" data-action="useSnippet" data-id="${escapeHtml(snippet.id)}">填入终端</button><button class="snippet-run-button" data-action="useSnippet" data-mode="run" data-id="${escapeHtml(snippet.id)}">执行…${icon('arrow')}</button></div></article>`).join('') : ui`<div class="snippet-panel-empty">${icon('code')}<p>${escapeHtml(t(search ? '没有匹配的片段' : '还没有命令片段'))}</p><span>保存常用命令，在这里快速使用。</span></div>`;
}

function snippetCommand(command, mode) {
  const normalized = command.replace(/\r\n/g, '\n');
  if (!normalized.trim() || normalized.length > 20000 || /[\x00-\x09\x0b-\x1f\x7f-\x9f]/.test(normalized)) {
    throw new Error(t('片段为空、过长或包含控制字符，请编辑后重试。'));
  }
  if (mode !== 'run' && normalized.includes('\n')) throw new Error(t('多行片段不能安全地直接填入，请复制检查，或选择确认执行。'));
  return normalized;
}

async function applySnippet(snippet, sessionId, mode) {
  const command = snippetCommand(snippet.command, mode);
  const session = sessions.get(sessionId);
  if (session?.status !== 'connected') throw new Error(t('目标会话已断开，请重新选择在线终端。'));
  if (mode === 'run' && !await confirmAction(t('在此终端执行片段？'), t('目标：{0}\n地址：{1}@{2}:{3}\n\n{4}\n\n将立即发送命令及回车，多行命令会依次发送。请确认终端处于 shell 提示符且没有未提交的输入；这不是独立的脚本任务。', session.host.name, session.host.username, session.host.address, session.host.port, command))) return false;
  if (sessions.get(sessionId) !== session || session.status !== 'connected') throw new Error(t('目标会话已断开，请重新选择在线终端。'));
  if (mode === 'run') await request('input', { sessionId, data: command.replace(/\n/g, '\r').replace(/\r+$/, '') + '\r' });
  else session.terminal.paste(command);
  showSession(sessionId);
  toast(t(mode === 'run' ? '片段已发送到目标终端' : '命令已填入，检查后按 Enter 执行'));
  return true;
}

function openSnippetLauncher(snippet, mode = 'insert') {
  const connected = [...sessions.entries()].filter(([, session]) => session.status === 'connected');
  if (!connected.length) { toast(t('请先连接一台主机，再填入命令片段。'), true); return; }
  const preferred = activeSession || lastSnippetSession;
  const selected = connected.some(([id]) => id === preferred) ? preferred : connected.length === 1 ? connected[0][0] : '';
  const snapshot = { ...snippet };
  openEditor(editorHeader(t('使用命令片段'), escapeHtml(snapshot.name), 'COMMAND SNIPPET') + ui`<div class="form-grid"><div class="field full"><label for="snippet-target">目标终端</label><select id="snippet-target" name="sessionId" required><option value="">${escapeHtml(t('请选择在线终端'))}</option>${connected.map(([id, session]) => `<option value="${escapeHtml(id)}" ${id === selected ? 'selected' : ''}>${escapeHtml(`${session.host.name} · ${session.host.username}@${session.host.address}:${session.host.port} · ${id.slice(0, 8)}`)}</option>`).join('')}</select></div><div class="field full"><label for="snippet-mode">使用方式</label><select id="snippet-mode" name="mode"><option value="insert" ${mode !== 'run' ? 'selected' : ''}>${escapeHtml(t('仅填入，不按回车（单行命令）'))}</option><option value="run" ${mode === 'run' ? 'selected' : ''}>${escapeHtml(t('确认后执行（支持多行）'))}</option></select></div><div class="field full"><label for="snippet-preview">命令预览</label><textarea id="snippet-preview" class="command-textarea" readonly spellcheck="false">${escapeHtml(snapshot.command)}</textarea></div></div><p class="settings-note">请先确认目标终端处于 shell 提示符，且没有未提交的输入。多行片段可复制检查，或选择确认执行。</p>${editorFooter(t('继续'))}`, async form => {
    const values = new FormData(form);
    if (!await applySnippet(snapshot, values.get('sessionId'), values.get('mode'))) throw new Error(t('未发送命令，可修改选择后重试。'));
  });
}
