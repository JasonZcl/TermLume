'use strict';

let knownHostRecords = [];
let knownHostsLoaded = false;
let knownHostsLoading = false;
let knownHostsError = '';
let knownHostQuery = '';
let knownHostStatus = 'all';
let logSnapshot = { entries: [], error: null, limit: 2000 };
let logsLoaded = false;
let logsLoading = false;
let logRefreshTimer;
let logPage = 1;
const logPageSize = 50;
const logFilters = { query: '', level: 'all', category: 'all', period: 'all' };
const logLevelNames = { get info() { return t('信息'); }, get warning() { return t('警告'); }, get error() { return t('错误'); } };
const logCategoryNames = { get connection() { return t('连接'); }, get security() { return t('安全'); }, get application() { return t('应用'); } };

function keepActivityFocus(render) {
  const focused = document.activeElement;
  const selection = focused instanceof HTMLInputElement ? [focused.selectionStart, focused.selectionEnd] : null;
  const id = focused?.id;
  render();
  const replacement = id ? document.getElementById(id) : null;
  if (replacement) {
    replacement.focus();
    if (selection && replacement instanceof HTMLInputElement) replacement.setSelectionRange(...selection);
  }
}

function activityTime(value, fallback = t('暂无记录')) {
  if (!value) return fallback;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? fallback : date.toLocaleString(uiLocale(), { hour12: false });
}

function serverEndpoint(address, port) {
  return `${address?.includes(':') ? '[' + address + ']' : address || ''}${port ? ':' + port : ''}`;
}

async function loadKnownHosts() {
  if (knownHostsLoading) return;
  knownHostsLoading = true;
  try { knownHostRecords = await request('knownHosts'); knownHostsError = ''; }
  catch (error) { knownHostsError = error.message; }
  finally {
    knownHostsLoaded = true; knownHostsLoading = false;
    if (currentView === 'knownHosts' && !activeSession) keepActivityFocus(renderKnownHosts);
  }
}

function renderKnownHosts() {
  if (!knownHostsLoaded && !knownHostsLoading) loadKnownHosts();
  $('#page').innerHTML = heading('SERVER IDENTITY', t('已知主机'), t('管理已信任的服务器指纹，每一次连接都有据可核。'), uiHtml('<button class="secondary-button" data-action="refreshKnownHosts">刷新记录</button>')) + ui`
    <div class="activity-notice">${icon('shield')}<p>首次信任前，请通过独立可信渠道核对指纹。指纹变化会阻止连接，不会自动覆盖旧记录。<br>这里管理的是服务器身份，不是登录密码或用户私钥。</p></div>
    <section id="known-hosts-section" aria-label="服务器信任记录">
    <div class="activity-filters"><label class="activity-search">搜索记录<input id="known-host-search" placeholder="地址、主机名、算法或指纹" value="${escapeHtml(knownHostQuery)}" autocomplete="off"></label><label>状态<select id="known-host-status"><option value="all">全部记录</option><option value="trusted" ${knownHostStatus === 'trusted' ? 'selected' : ''}>未发现异常</option><option value="changed" ${knownHostStatus === 'changed' ? 'selected' : ''}>指纹变化</option></select></label><span class="activity-counter" id="known-host-counter"></span></div>
    ${knownHostsError ? ui`<div class="activity-warning" role="alert">无法刷新信任记录：${escapeHtml(t(knownHostsError))}，可点击「刷新记录」重试。</div>` : ''}<div id="known-host-results" aria-live="polite"></div></section>`;
  $('#known-host-search').addEventListener('input', event => { knownHostQuery = event.target.value; renderKnownHostResults(); });
  $('#known-host-status').addEventListener('change', event => { knownHostStatus = event.target.value; renderKnownHostResults(); });
  renderKnownHostResults();
}

function renderKnownHostResults() {
  const query = knownHostQuery.trim().toLocaleLowerCase();
  const matches = entry => workspace.hosts.filter(host => host.address.toLocaleLowerCase() === entry.address.toLocaleLowerCase() && host.port === entry.port).map(host => host.name).join('、');
  const entries = knownHostRecords.filter(entry => (knownHostStatus === 'all' || (entry.metadata.lastMismatchFingerprint ? 'changed' : 'trusted') === knownHostStatus) && [entry.endpoint, entry.fingerprint, entry.metadata.lastMismatchFingerprint, matches(entry)].some(value => String(value || '').toLocaleLowerCase().includes(query)));
  $('#known-host-counter').textContent = msg`${entries.length} / ${knownHostRecords.length} 条记录`;
  $('#known-host-results').innerHTML = entries.length ? `<div class="trust-list">${entries.map(entry => {
    const changed = Boolean(entry.metadata.lastMismatchFingerprint);
    return ui`<article class="trust-card ${changed ? 'trust-changed' : ''}"><div class="trust-heading"><div class="trust-symbol">${icon('shield')}</div><div class="trust-identity"><h2>${escapeHtml(serverEndpoint(entry.address, entry.port))}</h2><p>${escapeHtml(matches(entry) || t('未关联到已保存主机'))}<span>·</span>${escapeHtml(entry.algorithm)}</p></div><span class="activity-badge ${changed ? 'error' : 'info'}">${changed ? t('指纹变化 · 已阻止') : t('已保存信任')}</span></div>
      <div class="fingerprint-line"><span>已保存指纹</span><code>${escapeHtml(entry.fingerprint)}</code><button class="subtle-button" data-action="copyFingerprint" data-endpoint="${escapeHtml(entry.endpoint)}" aria-label="复制指纹 ${escapeHtml(entry.endpoint)}">${icon('copy')}复制</button></div>
      ${changed ? ui`<div class="trust-mismatch"><strong>最近收到不同的指纹</strong><code>${escapeHtml(entry.metadata.lastMismatchFingerprint)}</code><span>${escapeHtml(activityTime(entry.metadata.lastMismatchAt))} · 请先联系管理员核验，不要直接移除旧指纹。</span></div>` : ''}
      <div class="trust-footer"><span>首次信任：${escapeHtml(activityTime(entry.metadata.firstTrustedAt, t('旧版记录，时间未知')))}<br>最近核验：${escapeHtml(activityTime(entry.metadata.lastVerifiedAt, t('尚无核验时间')))}</span><button class="danger-button" data-action="forgetKey" data-endpoint="${escapeHtml(entry.endpoint)}" aria-label="移除信任 ${escapeHtml(entry.endpoint)}">移除信任</button></div></article>`;
  }).join('')}</div>` : `<div class="activity-empty">${icon('shield')}<h2>${knownHostsLoading ? t('正在读取信任记录…') : knownHostRecords.length ? t('没有匹配的信任记录') : t('尚未信任任何服务器')}</h2><p>${knownHostRecords.length ? t('调整搜索或状态筛选后重试。') : t('连接主机并确认指纹后，记录会保存在这里。')}</p></div>`;
}

async function loadActivityLogs() {
  if (logsLoading) return;
  logsLoading = true;
  try { logSnapshot = await request('logs'); }
  catch (error) { logSnapshot.error = t('无法刷新日志：') + t(error.message); }
  finally {
    logsLoaded = true; logsLoading = false;
    if (currentView === 'logs' && !activeSession) keepActivityFocus(renderActivityLogs);
  }
}

// 合并短时间内的日志变更通知，只在日志页可见时拉取，避免连接事件反复触发重绘。
function scheduleLogRefresh() {
  logsLoaded = false;
  clearTimeout(logRefreshTimer);
  if (currentView === 'logs' && !activeSession) logRefreshTimer = setTimeout(loadActivityLogs, 200);
}

// 筛选只改变展示与导出记录集合，不修改原始事件内容或持久化日志。
function filteredLogs() {
  const query = logFilters.query.trim().toLocaleLowerCase();
  const now = new Date();
  const since = logFilters.period === 'today' ? new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime() : logFilters.period === '7' || logFilters.period === '30' ? now.getTime() - Number(logFilters.period) * 86400000 : 0;
  return logSnapshot.entries.filter(entry => (logFilters.level === 'all' || entry.level === logFilters.level) && (logFilters.category === 'all' || entry.category === logFilters.category) && (!since || new Date(entry.timestamp).getTime() >= since) && [entry.hostName, entry.address, entry.username, entry.sessionId, entry.event, entry.message, t(entry.message), JSON.stringify(entry.details)].some(value => String(value || '').toLocaleLowerCase().includes(query)));
}

function renderActivityLogs() {
  if (!logsLoaded && !logsLoading) loadActivityLogs();
  const options = (values, selected) => Object.entries(values).map(([value, label]) => `<option value="${value}" ${selected === value ? 'selected' : ''}>${label}</option>`).join('');
  $('#page').innerHTML = heading('ACTIVITY & SECURITY', t('日志'), t('连接过程与服务器身份事件，只保存在这台设备。'), uiHtml('<div class="activity-actions"><button class="secondary-button" data-action="refreshLogs">刷新</button><button class="secondary-button" data-action="exportLogs">导出筛选结果</button><button class="danger-button" data-action="clearLogs">清空日志</button></div>')) + ui`
    <div class="activity-notice">${icon('lock')}<p>最多保留最近 ${logSnapshot.limit} 条事件，不记录密码、私钥内容、终端输入或输出。<br>日志包含主机地址、用户名和安全指纹，导出或分享前请检查。关闭程序或移除信任不会删除历史日志。</p></div>
    ${logSnapshot.error ? `<div class="activity-warning" role="alert">${escapeHtml(t(logSnapshot.error))}</div>` : ''}
    <div class="activity-filters log-filters"><label class="activity-search">搜索日志<input id="log-search" placeholder="主机、地址、事件或会话 ID" value="${escapeHtml(logFilters.query)}" autocomplete="off"></label><label>级别<select id="log-level">${options({ all: t('全部级别'), ...logLevelNames }, logFilters.level)}</select></label><label>类型<select id="log-category">${options({ all: t('全部类型'), ...logCategoryNames }, logFilters.category)}</select></label><label>时间<select id="log-period">${options({ all: t('全部时间'), today: t('今天'), 7: t('最近 7 天'), 30: t('最近 30 天') }, logFilters.period)}</select></label></div>
    <div class="activity-list-heading"><span id="log-counter"></span><span>本地时间 · 最新事件在前</span></div><div id="log-results" aria-live="polite"></div><div id="log-pagination" class="log-pagination"></div>`;
  $('#log-search').addEventListener('input', event => { logFilters.query = event.target.value; logPage = 1; renderLogResults(); });
  ['level', 'category', 'period'].forEach(key => $('#log-' + key).addEventListener('change', event => { logFilters[key] = event.target.value; logPage = 1; renderLogResults(); }));
  renderLogResults();
}

function renderLogResults() {
  const entries = filteredLogs();
  const pages = Math.max(1, Math.ceil(entries.length / logPageSize));
  logPage = Math.max(1, Math.min(logPage, pages));
  $('#log-counter').textContent = msg`${entries.length} / ${logSnapshot.entries.length} 条事件`;
  $('[data-action="exportLogs"]').disabled = !entries.length;
  $('[data-action="clearLogs"]').disabled = !logSnapshot.entries.length && !logSnapshot.error;
  $('#log-results').innerHTML = entries.length ? ui`<div class="log-table-wrap"><table class="log-table"><thead><tr><th scope="col">时间</th><th scope="col">级别 / 类型</th><th scope="col">主机</th><th scope="col">事件</th><th scope="col"><span class="sr-only">详情</span></th></tr></thead><tbody>${entries.slice((logPage - 1) * logPageSize, logPage * logPageSize).map(entry => ui`<tr><td class="log-time">${escapeHtml(activityTime(entry.timestamp))}</td><td><span class="activity-badge ${escapeHtml(entry.level)}">${escapeHtml(logLevelNames[entry.level] || entry.level)}</span><small>${escapeHtml(logCategoryNames[entry.category] || entry.category)}</small></td><td class="log-host"><strong>${escapeHtml(entry.hostName || (entry.address ? serverEndpoint(entry.address, entry.port) : t('本地应用')))}</strong><small>${escapeHtml(entry.hostName ? serverEndpoint(entry.address, entry.port) : '')}</small></td><td class="log-message"><span>${escapeHtml(t(entry.message))}</span><small>${escapeHtml(entry.event)}${entry.durationSeconds !== null && entry.durationSeconds !== undefined ? t(' · 在线 ') + entry.durationSeconds + t(' 秒') : ''}</small></td><td><button class="subtle-button" data-action="logDetails" data-id="${escapeHtml(entry.id)}" aria-label="查看日志详情 ${escapeHtml(entry.id)}">详情</button></td></tr>`).join('')}</tbody></table></div>` : `<div class="activity-empty">${icon('clock')}<h2>${logsLoading ? t('正在读取日志…') : logSnapshot.entries.length ? t('没有匹配的日志') : t('还没有日志')}</h2><p>${logSnapshot.entries.length ? t('调整搜索关键词、级别、类型或时间范围。') : t('新的连接与安全事件将自动出现在这里。')}</p></div>`;
  $('#log-pagination').innerHTML = entries.length ? ui`<span>每页 ${logPageSize} 条 · 第 ${logPage} / ${pages} 页</span><div><button class="subtle-button" data-action="logPrevious" ${logPage <= 1 ? 'disabled' : ''}>上一页</button><button class="subtle-button" data-action="logNext" ${logPage >= pages ? 'disabled' : ''}>下一页</button></div>` : '';
}

function openLogDetails(id) {
  const entry = logSnapshot.entries.find(item => item.id === id);
  if (!entry) return;
  openEditor(editorHeader(t('事件详情'), activityTime(entry.timestamp), 'ACTIVITY DETAILS') + ui`<pre class="log-detail">${escapeHtml(JSON.stringify(entry, null, 2))}</pre><div class="dialog-footer"><button type="button" class="secondary-button" data-action="copyLog" data-id="${escapeHtml(entry.id)}">${icon('copy')}复制详情</button><button type="button" class="primary-button" data-action="closeEditor">关闭</button></div>`, async () => {});
}
