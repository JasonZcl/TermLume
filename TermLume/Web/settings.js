'use strict';

const settingsSections = [
  { id: 'appearance', get label() { return t('外观'); }, get description() { return t('让工作空间更像你'); }, icon: 'grid' },
  { id: 'terminal', get label() { return t('终端'); }, get description() { return t('每一次输入都恰到好处'); }, icon: 'terminal' },
  { id: 'keyboard', get label() { return t('快捷键'); }, get description() { return t('为 Windows 定制你的操作习惯'); }, icon: 'key' },
  { id: 'privacy', get label() { return t('数据与安全'); }, get description() { return t('你的连接，只属于你的设备'); }, icon: 'shield' },
  { id: 'about', get label() { return t('关于'); }, get description() { return t('一个安静、专注的终端工作空间'); }, icon: 'bolt' }
];

function settingSelect(id, key, values, selected) {
  return `<select id="${id}" data-preference="${key}">${Object.entries(values).map(([value, label]) => `<option value="${escapeHtml(value)}" ${String(selected) === value ? 'selected' : ''}>${escapeHtml(label)}</option>`).join('')}</select>`;
}

function settingRow(title, description, control, id = '') {
  return `<div class="preference-row"><${id ? 'label for="' + id + '"' : 'div'} class="preference-label">${escapeHtml(title)}<small>${description}</small></${id ? 'label' : 'div'}><div class="preference-control">${control}</div></div>`;
}

function preferenceToggle(id, key, checked, label) {
  return `<label class="preference-toggle"><input type="checkbox" role="switch" id="${id}" data-preference="${key}" ${checked ? 'checked' : ''} aria-label="${label}"><span></span></label>`;
}

function terminalPreview(preferences) {
  return ui`<div class="terminal-preview ${preferences.terminalTheme}" style="--preview-size:${preferences.fontSize}px;--preview-line:${preferences.lineHeight};--preview-font:${escapeHtml(terminalFonts[preferences.fontFamily])}"><div class="preview-topline"><span class="preview-lights"><i></i><i></i><i></i></span><span>termlume — zsh</span><span>实时预览</span></div><div class="preview-code"><div><span class="preview-user">admin@termlume</span><span class="preview-muted"> ~/workspace</span></div><div><span class="preview-prompt">❯</span> ssh production</div><div class="preview-muted">Secure connection. Clear focus.</div><div><span class="preview-prompt">❯</span> <span class="preview-command">echo</span> <span class="preview-string">"Make yourself at home."</span></div><div>Make yourself at home.</div><div><span class="preview-prompt">❯</span> <span class="preview-cursor ${preferences.cursorStyle} ${preferences.cursorBlink ? 'blinking' : ''}"></span></div></div><div class="preview-caption">仅为外观预览，不会连接服务器或执行命令</div></div>`;
}

function appearanceSettings(preferences) {
  const languageCard = ui`<section class="preference-card"><div class="preference-card-heading"><h3>界面语言</h3><p>切换后立即生效，不会断开连接；重启后保留。</p></div>${settingRow(t('显示语言'), t('仅翻译应用界面，不改变主机名称、文件名或终端内容。'), settingSelect('interface-language', 'language', { 'zh-CN': t('简体中文'), en: 'English' }, preferences.language), 'interface-language')}</section>`;
  return languageCard + ui`<section class="preference-card"><div class="preference-card-heading"><h3>终端配色</h3><p>熟悉的深色，从一眼舒适开始。</p></div><div class="theme-choices">${[{ id: 'midnight', name: 'Midnight', caption: t('深空 · 清晰而克制') }, { id: 'nord', name: 'Nord', caption: t('极夜 · 柔和冷色调') }].map(theme => ui`<button class="theme-choice ${theme.id} ${preferences.terminalTheme === theme.id ? 'selected' : ''}" data-action="setPreference" data-key="terminalTheme" data-value="${theme.id}" aria-pressed="${preferences.terminalTheme === theme.id}" aria-label="${theme.name} 终端配色"><span class="theme-sample"><i></i><i></i><i></i><i></i></span><span class="theme-description"><strong>${theme.name}</strong><small>${theme.caption}</small></span><span class="theme-check">${icon('check')}</span></button>`).join('')}</div>${terminalPreview(preferences)}<div class="preview-controls"><label for="font-size">预览字号</label>${settingSelect('font-size', 'fontSize', Object.fromEntries(Array.from({ length: 14 }, (_, index) => [index + 11, (index + 11) + ' px'])), preferences.fontSize)}<span>同步应用到已打开的终端</span></div></section>
    <section class="preference-card"><div class="preference-card-heading"><h3>工作空间</h3><p>保持轻盈，也保留你喜欢的细节。</p></div>${settingRow(t('强调色'), t('用于选中状态、操作按钮与焦点高亮'), `<div class="accent-options">${[['lavender', t('鸢尾紫')], ['mint', t('薄荷绿')], ['ocean', t('海洋蓝')]].map(([value, label]) => `<button class="accent-swatch ${value}" data-action="setPreference" data-key="accent" data-value="${value}" aria-label="${label}" aria-pressed="${preferences.accent === value}" title="${label}">${preferences.accent === value ? icon('check') : ''}</button>`).join('')}</div>`)}${settingRow(t('主机卡片密度'), t('选择紧凑布局，或留出更多呼吸空间'), settingSelect('interface-density', 'density', { compact: t('紧凑'), comfortable: t('舒适') }, preferences.density), 'interface-density')}${settingRow(t('折叠主侧栏'), t('保留图标，让终端拥有更宽的视野'), preferenceToggle('sidebar-collapsed', 'sidebarCollapsed', preferences.sidebarCollapsed, t('折叠主侧栏')), 'sidebar-collapsed')}</section>`;
}

function terminalSettings(preferences) {
  return ui`${terminalPreview(preferences)}<section class="preference-card"><div class="preference-card-heading"><h3>字体与排版</h3><p>更改立即应用，不会断开连接。</p></div>${settingRow(t('终端字体'), t('JetBrains Mono 需已安装；缺失时自动使用备用字体'), settingSelect('terminal-font', 'fontFamily', { cascadia: 'Cascadia Code', consolas: 'Consolas', jetbrains: 'JetBrains Mono' }, preferences.fontFamily), 'terminal-font')}${settingRow(t('字体大小'), t('11–24 px，可使用终端字号快捷键调整'), settingSelect('font-size', 'fontSize', Object.fromEntries(Array.from({ length: 14 }, (_, index) => [index + 11, (index + 11) + ' px'])), preferences.fontSize), 'font-size')}${settingRow(t('行高'), t('让密集的输出更容易阅读'), settingSelect('terminal-line-height', 'lineHeight', { '1': '1.0 ×', '1.1': '1.1 ×', '1.2': '1.2 ×', '1.4': '1.4 ×', '1.6': '1.6 ×', '1.8': '1.8 ×' }, preferences.lineHeight), 'terminal-line-height')}${settingRow(t('配色方案'), t('与外观页面中的终端配色保持同步'), settingSelect('terminal-theme', 'terminalTheme', { midnight: t('Midnight · 深空'), nord: t('Nord · 极夜') }, preferences.terminalTheme), 'terminal-theme')}</section>
    <section class="preference-card"><div class="preference-card-heading"><h3>光标与滚动</h3><p>细微的习惯，也值得被照顾。</p></div>${settingRow(t('光标样式'), t('选择你熟悉的输入标记'), settingSelect('cursor-style', 'cursorStyle', { block: t('方块'), bar: t('竖线'), underline: t('下划线') }, preferences.cursorStyle), 'cursor-style')}${settingRow(t('光标闪烁'), t('关闭后以静态光标显示'), preferenceToggle('cursor-blink', 'cursorBlink', preferences.cursorBlink, t('光标闪烁')), 'cursor-blink')}${settingRow(t('滚动历史'), t('每个终端保留的历史行数；更多行会占用更多内存'), settingSelect('terminal-scrollback', 'scrollback', { 1000: t('1,000 行'), 5000: t('5,000 行'), 10000: t('10,000 行'), 20000: t('20,000 行'), 50000: t('50,000 行') }, preferences.scrollback), 'terminal-scrollback')}</section>`;
}

function keyboardSettings() {
  return ui`<div class="keyboard-intro"><span class="platform-pill">${icon('key')}Windows</span><p>快捷键仅在 TermLume 窗口内生效。点击按键区域即可重新录制，清除后可停用单个操作。</p><small>Ctrl+C / D / Z / L 保留终端控制语义，输入框保留 Ctrl+A / C / V / X 等编辑操作。Alt+F4、Win 等系统组合不由应用接管。</small></div><div class="shortcut-toolbar"><label class="shortcut-search">${icon('search')}<input id="shortcut-search" aria-label="搜索快捷键" placeholder="搜索操作或组合键…" value="${escapeHtml(shortcutQuery)}"></label><button class="secondary-button" data-action="resetAllShortcuts">恢复默认</button></div><div id="shortcut-results"></div><p class="settings-footnote">默认终端复制 / 粘贴使用 Ctrl + Shift + C / V。组合按物理键位记录；自定义后会优先于对应的远端按键生效。</p>`;
}

function renderShortcutResults() {
  const query = shortcutQuery.trim().toLocaleLowerCase();
  const shortcuts = preferenceValues().shortcuts;
  const filtered = shortcutDefinitions.filter(item => [item.label, item.description, item.group, shortcutLabel(shortcuts[item.id])].some(text => text.toLocaleLowerCase().includes(query)));
  $('#shortcut-results').innerHTML = filtered.length ? [t('工作空间'), t('终端'), t('导航')].map(group => {
    const actions = filtered.filter(item => item.group === group);
    return actions.length ? `<section class="preference-card shortcut-group"><h3>${group}<span>${actions.length}</span></h3>${actions.map(item => ui`<div class="shortcut-row"><div><strong>${item.label}</strong><small>${item.description}</small></div><button class="shortcut-binding" data-action="editShortcut" data-id="${item.id}" aria-label="修改${item.label}快捷键">${shortcutKeys(shortcuts[item.id])}</button><button class="icon-button shortcut-reset" data-action="resetShortcut" data-id="${item.id}" aria-label="恢复${item.label}默认快捷键" title="恢复默认">${icon('restore')}</button></div>`).join('')}</section>` : '';
  }).join('') : uiHtml('<div class="activity-empty"><h2>没有匹配的快捷键</h2><p>试试操作名称或组合键，例如“终端”或“Ctrl”。</p></div>');
}

function privacySettings() {
  return ui`<section class="preference-card"><div class="preference-card-heading"><h3>本地优先</h3><p>不需要账户，不上传主机配置，也不保存登录密码。</p></div><div class="privacy-grid"><div>${icon('lock')}<strong>凭据不落盘</strong><p>密码与私钥口令仅在连接时使用，不复制私钥。</p></div><div>${icon('shield')}<strong>核验服务器身份</strong><p>指纹变化时阻止连接，不会自动替换已信任指纹。</p></div></div><div class="preference-link-row"><span>服务器信任记录</span><button class="subtle-button" data-view="knownHosts">管理已知主机 ${icon('arrow')}</button></div><div class="preference-link-row"><span>连接与安全事件</span><button class="subtle-button" data-view="logs">查看日志 ${icon('arrow')}</button></div></section><section class="preference-card"><div class="preference-card-heading"><h3>数据位置与备份</h3><p>主机、备注、命令片段、快捷键与日志均为本地明文，请勿在备注或片段中保存秘密。</p></div><div class="settings-data-path">%LOCALAPPDATA%\TermLume\workspace.json</div><div class="settings-data-path">%LOCALAPPDATA%\TermLume\activity-log.json</div><p class="settings-footnote">退出应用后复制文件即可备份。恢复前先备份现有文件，再替换并重启。日志不记录终端输入输出，导出前请检查主机地址等信息。</p></section>`;
}

function aboutSettings() {
  return ui`<section class="preference-card about-card"><img src="favicon.svg" alt="TermLume 图标"><h3>TermLume<span>0.7.0</span></h3><p>Less noise. More terminal.</p><div class="about-divider"></div><div class="about-facts"><span>桌面平台<strong>Windows</strong></span><span>工作空间<strong>本地存储</strong></span><span>连接方式<strong>SSH / SFTP</strong></span></div><p class="settings-footnote">基于 WPF、WebView2、SSH.NET 与 xterm.js。独立设计，不使用 Termius 的代码、商标或素材。支持 SSH 终端与 SFTP 文件管理，暂不包含跳板机和云同步。</p></section>`;
}

function renderSettings() {
  const preferences = preferenceValues();
  const section = settingsSections.find(item => item.id === settingsSection) || settingsSections[0];
  const content = ({ appearance: appearanceSettings, terminal: terminalSettings, keyboard: keyboardSettings, privacy: privacySettings, about: aboutSettings })[section.id](preferences);
  $('#page').innerHTML = ui`<header class="settings-header"><div><span class="settings-eyebrow">YOUR WORKSPACE, YOUR WAY</span><h1>设置</h1><p>少一点干扰，多一点顺手。</p></div><span class="local-badge">${icon('lock')}本地偏好设置</span></header><div class="settings-layout"><aside class="settings-navigation" aria-label="设置分类"><div class="settings-nav-label">偏好设置</div>${settingsSections.map(item => `<button class="settings-nav-item ${item.id === section.id ? 'selected' : ''}" data-action="settingsSection" data-id="${item.id}" aria-current="${item.id === section.id ? 'page' : 'false'}">${icon(item.icon)}<span>${item.label}</span>${item.id === 'keyboard' ? '<span class="settings-nav-hint">17</span>' : ''}</button>`).join('')}<div class="settings-navigation-footer"><img src="favicon.svg" alt=""><span>TermLume<strong>0.7.0 · Windows</strong></span></div></aside><div class="settings-content"><div class="settings-content-heading"><div><h2>${section.label}</h2><p>${section.description}</p></div>${icon(section.icon)}</div>${content}<div class="preferences-save-status"><span class="status-dot"></span><span id="preferences-status" role="status"></span></div></div></div>`;
  $('#page').querySelectorAll('[data-preference]').forEach(control => control.addEventListener('change', () => {
    const key = control.dataset.preference;
    const value = control.type === 'checkbox' ? control.checked : ['fontSize', 'lineHeight', 'scrollback'].includes(key) ? Number(control.value) : control.value;
    updatePreferences({ [key]: value }).catch(error => toast(error.message, true));
  }));
  if (section.id === 'keyboard') { renderShortcutResults(); $('#shortcut-search').addEventListener('input', event => { shortcutQuery = event.target.value; renderShortcutResults(); }); }
  updatePreferenceStatus();
}
