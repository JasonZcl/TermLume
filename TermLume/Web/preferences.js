'use strict';

const shortcutDefinitions = [
  { id: 'snippets', get label() { return t('打开 / 关闭命令片段'); }, get description() { return t('在当前终端查找并使用保存的命令'); }, get group() { return t('终端'); }, binding: 'Ctrl+Shift+KeyS', terminalOnly: true },
  { id: 'newHost', get label() { return t('新建主机'); }, get description() { return t('打开主机配置表单'); }, get group() { return t('工作空间'); }, binding: 'Ctrl+KeyN' },
  { id: 'newGroup', get label() { return t('新建分组'); }, get description() { return t('创建一个独立的空分组'); }, get group() { return t('工作空间'); }, binding: 'Ctrl+Shift+KeyN' },
  { id: 'searchHosts', get label() { return t('搜索与快速连接'); }, get description() { return t('返回主机列表并聚焦搜索框'); }, get group() { return t('工作空间'); }, binding: 'Ctrl+KeyK' },
  { id: 'settings', get label() { return t('打开设置'); }, get description() { return t('管理外观、终端和快捷键'); }, get group() { return t('工作空间'); }, binding: 'Ctrl+Comma' },
  { id: 'workspace', get label() { return t('返回工作空间'); }, get description() { return t('保留会话，返回上次浏览的页面'); }, get group() { return t('导航'); }, binding: 'Ctrl+Shift+KeyH' },
  { id: 'openTerminal', get label() { return t('打开终端'); }, get description() { return t('连接选中主机，或返回最近的在线终端'); }, get group() { return t('终端'); }, binding: 'Ctrl+Shift+KeyT' },
  { id: 'closeTab', get label() { return t('关闭当前会话'); }, get description() { return t('关闭前确认，不会直接终止连接'); }, get group() { return t('终端'); }, binding: 'Ctrl+Shift+KeyW', terminalOnly: true },
  { id: 'nextTab', get label() { return t('下一个标签'); }, get description() { return t('在工作空间与终端标签间循环'); }, get group() { return t('导航'); }, binding: 'Ctrl+Tab' },
  { id: 'previousTab', get label() { return t('上一个标签'); }, get description() { return t('按相反方向切换标签'); }, get group() { return t('导航'); }, binding: 'Ctrl+Shift+Tab' },
  { id: 'reconnect', get label() { return t('重新连接'); }, get description() { return t('为当前主机打开一个新连接'); }, get group() { return t('终端'); }, binding: 'Ctrl+Shift+KeyR', terminalOnly: true },
  { id: 'copy', get label() { return t('复制终端选区'); }, get description() { return t('复制选中的文本，不发送远端命令'); }, get group() { return t('终端'); }, binding: 'Ctrl+Shift+KeyC', terminalOnly: true },
  { id: 'paste', get label() { return t('粘贴到终端'); }, get description() { return t('粘贴可能包含换行，请先检查来源'); }, get group() { return t('终端'); }, binding: 'Ctrl+Shift+KeyV', terminalOnly: true },
  { id: 'clearTerminal', get label() { return t('清空终端显示'); }, get description() { return t('只清除本地显示，不执行 clear 命令'); }, get group() { return t('终端'); }, binding: 'Ctrl+Shift+KeyL', terminalOnly: true },
  { id: 'toggleSidebar', get label() { return t('展开 / 折叠侧栏'); }, get description() { return t('为终端腾出更多空间'); }, get group() { return t('导航'); }, binding: 'Ctrl+Shift+KeyB' },
  { id: 'fontIncrease', get label() { return t('增大终端字号'); }, get description() { return t('应用到所有已打开的终端'); }, get group() { return t('终端'); }, binding: 'Ctrl+Equal', terminalOnly: true },
  { id: 'fontDecrease', get label() { return t('减小终端字号'); }, get description() { return t('最小 11 px，最大 24 px'); }, get group() { return t('终端'); }, binding: 'Ctrl+Minus', terminalOnly: true },
  { id: 'fontReset', get label() { return t('重置终端字号'); }, get description() { return t('恢复为 14 px'); }, get group() { return t('终端'); }, binding: 'Ctrl+Digit0', terminalOnly: true }
];
const defaultShortcuts = Object.fromEntries(shortcutDefinitions.map(item => [item.id, item.binding]));
const defaultPreferences = { language: 'zh-CN', fontSize: 14, terminalTheme: 'midnight', fontFamily: 'cascadia', lineHeight: 1.2, scrollback: 10000, cursorStyle: 'block', cursorBlink: true, accent: 'lavender', density: 'compact', sidebarCollapsed: false };
const terminalFonts = { cascadia: '"Cascadia Code", Consolas, monospace', consolas: 'Consolas, "Courier New", monospace', jetbrains: '"JetBrains Mono", "Cascadia Code", Consolas, monospace' };
const reservedShortcuts = new Set(['Alt+F4', 'Alt+Tab', 'Alt+Shift+Tab', 'Alt+Space', 'Ctrl+Alt+Delete', 'Ctrl+Escape', 'Ctrl+Shift+Escape', 'Ctrl+KeyA', 'Ctrl+KeyC', 'Ctrl+KeyV', 'Ctrl+KeyX', 'Ctrl+KeyY', 'Ctrl+KeyZ', 'Ctrl+Shift+KeyZ', 'Ctrl+KeyD', 'Ctrl+KeyL']);
let settingsSection = 'appearance';
let shortcutQuery = '';
let shortcutRecording = null;
let preferencesQueue = Promise.resolve();
let preferencesPending = 0;
let preferencesError = '';
let preferenceRuntimeReady = false;

function preferenceValues() {
  return { ...defaultPreferences, ...workspace.preferences, shortcuts: { ...defaultShortcuts, ...workspace.preferences?.shortcuts } };
}

function terminalOptions() {
  const preferences = preferenceValues();
  return { fontSize: preferences.fontSize, fontFamily: terminalFonts[preferences.fontFamily], lineHeight: preferences.lineHeight, scrollback: preferences.scrollback, cursorStyle: preferences.cursorStyle, cursorBlink: preferences.cursorBlink, theme: terminalTheme() };
}

// 更新已有终端对象的选项而非重建终端，切换外观或语言不会清空输出或断开连接。
function applyPreferences() {
  preferenceRuntimeReady = true;
  workspace.preferences = preferenceValues();
  const preferences = workspace.preferences;
  const languageChanged = setUiLanguage(preferences.language);
  updateWindowState(windowMaximized);
  document.body.dataset.accent = preferences.accent;
  document.body.dataset.density = preferences.density;
  document.body.classList.toggle('sidebar-collapsed', preferences.sidebarCollapsed);
  $('.sidebar-toggle')?.setAttribute('aria-expanded', String(!preferences.sidebarCollapsed));
  sessions.forEach(session => Object.entries(terminalOptions()).forEach(([key, value]) => { session.terminal.options[key] = value; }));
  if (activeSession) requestAnimationFrame(fitActiveTerminal);
  refreshShortcutHints();
  if (languageChanged) { renderSidebar(); renderTabs(); if (activeSession) updateTerminalToolbar(); else if (currentView !== 'settings') renderPage(); }
}

// 串行保存，后一个改动基于前一个已保存结果合并；原生确认成功后才更新界面状态。
function updatePreferences(patch) {
  preferencesPending++;
  preferencesError = '';
  updatePreferenceStatus();
  const operation = preferencesQueue.then(async () => {
    const changes = typeof patch === 'function' ? patch(preferenceValues()) : patch;
    if (changes.language === 'en' && localeLoadError) throw new Error('语言资源加载失败，请检查安装文件后重启应用。');
    const value = await request('preferences', { ...preferenceValues(), ...changes });
    preferencesError = '';
    workspace = value;
    applyPreferences();
    if (currentView === 'settings' && !activeSession) renderSettingsPreservingFocus();
    return value;
  }).catch(error => {
    preferencesError = error.message;
    applyPreferences();
    if (currentView === 'settings' && !activeSession) renderSettingsPreservingFocus();
    throw error;
  }).finally(() => { preferencesPending--; updatePreferenceStatus(); });
  preferencesQueue = operation.catch(() => {});
  return operation;
}

function updatePreferenceStatus() {
  const status = $('#preferences-status');
  if (!status) return;
  status.textContent = preferencesError ? t('保存失败：') + t(preferencesError) : preferencesPending ? t('正在保存更改…') : t('所有更改已保存在此设备');
  status.classList.toggle('save-error', Boolean(preferencesError));
}

function renderSettingsPreservingFocus() {
  const focused = document.activeElement;
  const id = focused?.id;
  const selection = focused instanceof HTMLInputElement && typeof focused.selectionStart === 'number' ? [focused.selectionStart, focused.selectionEnd] : null;
  const scroll = $('#page-scroll').scrollTop;
  renderSettings();
  const replacement = id ? document.getElementById(id) : null;
  if (replacement && $('#page').contains(replacement)) {
    replacement.focus({ preventScroll: true });
    if (selection) replacement.setSelectionRange(...selection);
  }
  $('#page-scroll').scrollTop = scroll;
}

function shortcutLabel(binding) {
  if (!binding) return t('未设置');
  const names = { Equal: '+', Minus: '−', Comma: ',', Period: '.', Slash: '/', Backslash: '\\', Semicolon: ';', Quote: "'", BracketLeft: '[', BracketRight: ']', Backquote: '`', ArrowLeft: '←', ArrowRight: '→', ArrowUp: '↑', ArrowDown: '↓', Space: 'Space' };
  return binding.split('+').map(part => names[part] || part.replace(/^Key|^Digit/, '')).join(' + ');
}

function shortcutKeys(binding) {
  return binding ? shortcutLabel(binding).split(' + ').map(part => `<kbd>${escapeHtml(part)}</kbd>`).join('<span class="key-join">+</span>') : uiHtml('<span class="shortcut-unbound">未设置</span>');
}

function normalizeShortcut(value) {
  if (value === '') return '';
  const parts = value.split('+');
  let code = parts.pop();
  code = ({ NumpadAdd: 'Equal', NumpadSubtract: 'Minus' })[code] || code;
  const functionKey = /^F([1-9]|1[0-2])$/.test(code);
  if (new Set(parts).size !== parts.length || parts.some(part => !['Ctrl', 'Alt', 'Shift'].includes(part)) || !functionKey && !parts.some(part => ['Ctrl', 'Alt'].includes(part)) || !/^(Key[A-Z]|Digit[0-9]|F([1-9]|1[0-2])|Comma|Period|Slash|Backslash|Semicolon|Quote|BracketLeft|BracketRight|Minus|Equal|Backquote|Tab|Space|Enter|Backspace|Delete|Insert|Home|End|PageUp|PageDown|ArrowLeft|ArrowRight|ArrowUp|ArrowDown)$/.test(code)) throw new Error(t('请使用 Ctrl / Alt 组合或 F1–F12 功能键；不支持 Windows 键或仅 Shift 加普通按键。'));
  let binding = [...['Ctrl', 'Alt', 'Shift'].filter(part => parts.includes(part)), code].join('+');
  if (binding === 'Ctrl+Shift+Equal') binding = 'Ctrl+Equal';
  if (reservedShortcuts.has(binding)) throw new Error(t('该组合由 Windows、文本编辑或终端控制使用，请选择其他快捷键。'));
  return binding;
}

// 按物理键位保存，避免输入法或键盘字符布局改变快捷键标识；原生端还会再次校验。
function bindingFromEvent(event) {
  if (event.metaKey || event.getModifierState?.('AltGraph')) throw new Error(t('不支持 Windows 键或 AltGr，请使用 Ctrl / Alt 组合或功能键。'));
  return normalizeShortcut([event.ctrlKey && 'Ctrl', event.altKey && 'Alt', event.shiftKey && 'Shift', event.code].filter(Boolean).join('+'));
}

function validateShortcut(action, value) {
  const binding = normalizeShortcut(value);
  const conflict = shortcutDefinitions.find(item => item.id !== action && binding && preferenceValues().shortcuts[item.id] === binding);
  if (conflict) throw new Error(msg`与「${conflict.label}」冲突，请先修改或清除该操作的快捷键。`);
  return binding;
}

function refreshShortcutHints() {
  const bindings = preferenceValues().shortcuts;
  const selectors = { newHost: '[data-action="newHost"]', newGroup: '[data-action="newGroup"]', settings: '.sidebar [data-view="settings"]', openTerminal: '[data-action="openTerminal"]', toggleSidebar: '.sidebar-toggle', clearTerminal: '[data-action="clearTerminal"]' };
  Object.entries(selectors).forEach(([action, selector]) => document.querySelectorAll(selector).forEach(button => {
    const label = shortcutDefinitions.find(item => item.id === action).label;
    button.title = label + (bindings[action] ? ' · ' + shortcutLabel(bindings[action]) : '');
  }));
  document.querySelectorAll('.empty-shortcut').forEach(element => { element.textContent = bindings.newHost ? t('或使用 ') + shortcutLabel(bindings.newHost) + t(' 快速创建') : t('也可以使用上方「新建主机」'); });
}

async function launchTerminal() {
  if (selectedHostId && workspace.hosts.some(host => host.id === selectedHostId)) return connectHost(selectedHostId);
  const latest = [...sessions.entries()].reverse().find(([, session]) => session.status === 'connected');
  if (latest) showSession(latest[0]);
  else { showView('hosts'); toast(t('选择一台主机后打开终端，也可以双击主机直接连接')); }
}

async function runShortcut(action) {
  const session = sessions.get(activeSession);
  switch (action) {
    case 'newHost': openHostEditor(); break;
    case 'newGroup': openGroupEditor(); break;
    case 'searchHosts': showView('hosts'); $('#quick-address')?.focus(); break;
    case 'settings': showView('settings'); break;
    case 'workspace': showView(currentView, currentGroup); break;
    case 'openTerminal': await launchTerminal(); break;
    case 'closeTab': if (activeSession) await closeSession(activeSession); break;
    case 'nextTab': case 'previousTab': {
      const tabs = [null, ...sessions.keys()];
      const next = tabs[(tabs.indexOf(activeSession) + (action === 'nextTab' ? 1 : tabs.length - 1)) % tabs.length];
      if (next) showSession(next); else showView(currentView, currentGroup);
      break;
    }
    case 'reconnect': if (session) await connectHost(session.host.id); break;
    case 'copy': if (session?.terminal.hasSelection()) await request('copy', { text: session.terminal.getSelection() }); break;
    case 'paste': if (session?.status === 'connected') session.terminal.paste(await request('paste')); break;
    case 'clearTerminal': session?.terminal.clear(); break;
    case 'snippets': toggleSnippets(); break;
    case 'toggleSidebar': {
      const collapsed = document.body.classList.toggle('sidebar-collapsed');
      $('.sidebar-toggle')?.setAttribute('aria-expanded', String(!collapsed));
      await updatePreferences({ sidebarCollapsed: collapsed }); break;
    }
    case 'fontIncrease': case 'fontDecrease': case 'fontReset':
      await updatePreferences(preferences => ({ fontSize: action === 'fontReset' ? 14 : Math.max(11, Math.min(24, preferences.fontSize + (action === 'fontIncrease' ? 1 : -1))) }));
      toast(msg`终端字号：${preferenceValues().fontSize} px`);
      break;
  }
}

document.addEventListener('keydown', event => {
  if (event.isComposing || event.keyCode === 229) return;
  if (shortcutRecording && $('#editor-dialog').open && event.target.closest('#shortcut-capture')) {
    if (/^(Control|Alt|Shift|Meta)(Left|Right)$/.test(event.code) || event.code === 'Escape' || (event.code === 'Tab' && !event.ctrlKey && !event.altKey)) return;
    event.preventDefault(); event.stopImmediatePropagation();
    if (event.repeat || shortcutRecording.saving) return;
    try { shortcutRecording.value = validateShortcut(shortcutRecording.action, bindingFromEvent(event)); shortcutRecording.error = ''; }
    catch (error) { shortcutRecording.error = error.message; }
    renderShortcutCapture();
    return;
  }
  if (!preferenceRuntimeReady || !ready || $('#editor-dialog').open || $('#confirm-dialog').open || event.defaultPrevented) return;
  let binding;
  try { binding = bindingFromEvent(event); } catch { return; }
  const definition = shortcutDefinitions.find(item => preferenceValues().shortcuts[item.id] === binding);
  if (!definition || definition.terminalOnly && !activeSession) return;
  event.preventDefault(); event.stopImmediatePropagation();
  if (!event.repeat) runShortcut(definition.id).catch(error => toast(error.message, true));
}, true);

function renderShortcutCapture() {
  $('#shortcut-capture').innerHTML = shortcutKeys(shortcutRecording.value);
  $('#shortcut-record-error').textContent = t(shortcutRecording.error);
  $('#editor-form [type="submit"]').disabled = Boolean(shortcutRecording.error || shortcutRecording.saving);
}

function openShortcutEditor(action) {
  const definition = shortcutDefinitions.find(item => item.id === action);
  if (!definition) return;
  shortcutRecording = { action, value: preferenceValues().shortcuts[action], error: '' };
  openEditor(editorHeader(t('修改快捷键'), definition.label + ' · ' + definition.description, 'WINDOWS SHORTCUT') + ui`<div class="shortcut-editor"><label for="shortcut-capture">点击下方区域，按下新的组合键</label><button id="shortcut-capture" class="shortcut-capture" type="button" autofocus aria-label="录制快捷键"></button><div id="shortcut-record-error" class="form-error" role="alert"></div><p>支持 Ctrl / Alt 组合或 F1–F12；Esc 取消，Tab 切换焦点。Windows 系统保留组合及 Ctrl+C / V 等文本编辑组合不能覆盖。</p><button class="subtle-button" type="button" data-action="clearShortcut">清除快捷键</button></div>${editorFooter(t('保存快捷键'))}`, async () => {
    if (shortcutRecording.error) throw new Error(shortcutRecording.error);
    const value = validateShortcut(action, shortcutRecording.value);
    shortcutRecording.saving = true;
    try {
      await updatePreferences({ shortcuts: { ...preferenceValues().shortcuts, [action]: value } });
      toast(value ? t('快捷键已更新') : t('该快捷键已停用'));
    } finally { if (shortcutRecording) shortcutRecording.saving = false; }
  });
  renderShortcutCapture();
}

document.addEventListener('DOMContentLoaded', () => $('#editor-dialog').addEventListener('close', () => { if (!$('#editor-dialog').open) shortcutRecording = null; }));
