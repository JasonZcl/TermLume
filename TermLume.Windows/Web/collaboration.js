'use strict';

let collaborationTab = 'workspaces';
let collaborationData = null;
let collaborationError = '';
let collaborationLoading = false;
let collaborationEpoch = 0;
let remoteLogOffset = 0;
const workspaceRoles = { owner: '所有者', admin: '管理员', editor: '编辑者', viewer: '只读成员' };
const collaborationTabs = { workspaces: '工作区', members: '成员与邀请', history: '历史版本', devices: '设备', activity: '跨设备日志', audit: '服务端审计', trust: '指纹参考' };
const adminRequest = (action, args = {}) => request('workspaceAdmin', { action, arguments: args });
const collabButton = (label, action, attributes = '', danger = false) => `<button type="button" class="${danger ? 'danger' : 'secondary'}-button" data-collab="${action}" ${attributes}>${escapeHtml(t(label))}</button>`;
const roleName = role => t(workspaceRoles[role] || role);

function resetCollaboration() {
  collaborationEpoch++;
  collaborationData = null; collaborationError = ''; collaborationLoading = false; remoteLogOffset = 0;
}

function updateSynchronizationNotice() {
  const state = workspaceSyncState;
  const notice = $('#workspace-sync-notice');
  const selector = $('#workspace-selector-name');
  if (selector) { selector.textContent = state.context?.name || t('工作区'); selector.parentElement.title = state.context ? `${state.context.name} · ${roleName(state.context.role)}` : t('切换和管理工作区'); }
  if (!notice) return;
  const pending = Number(state.pending || 0);
  notice.classList.toggle('hidden', !ready || (!pending && state.connected && state.context?.canWrite !== false));
  notice.classList.toggle('sync-conflict', Boolean(state.conflict));
  notice.innerHTML = `<span>${escapeHtml(state.conflict ? t('待同步修改与远端冲突，请选择处理方式。') : pending ? msg`有 ${pending} 项待同步修改，已保存在本机。` : state.context?.canWrite === false ? t('当前工作区为只读，收藏与个人偏好仍可修改。') : t('服务暂时离线，修改会保存在本机等待同步。'))}</span><div>${pending ? collabButton('重试同步', 'retry') + collabButton('放弃待同步修改', 'discard') : collabButton('刷新', 'refresh')}${collabButton('导出草稿', 'export')}</div>`;
  document.querySelectorAll('[data-action="newHost"], [data-action="newGroup"], [data-action="newSnippet"], [data-action="editHost"], [data-action="deleteHost"], [data-action="editSnippet"], [data-action="deleteSnippet"]').forEach(button => button.disabled = state.context?.canWrite === false);
}

async function loadCollaboration() {
  if (collaborationLoading || !ready) return;
  const epoch = collaborationEpoch;
  const generation = authenticationGeneration;
  collaborationLoading = true;
  try {
    let data;
    if (collaborationTab === 'workspaces') data = await adminRequest('workspace.list');
    else if (collaborationTab === 'members') {
      const members = await adminRequest('workspace.members');
      const invites = workspaceSyncState.context?.canManage ? await adminRequest('workspace.invites') : [];
      data = { members, invites };
    } else {
      const action = ({ history: 'workspace.history', devices: 'device.list', activity: 'activity.list', audit: 'audit.list', trust: 'trust.list' })[collaborationTab];
      data = await adminRequest(action, { offset: remoteLogOffset, limit: 50 });
    }
    if (!ready || epoch !== collaborationEpoch || generation !== authenticationGeneration) return;
    collaborationData = data; collaborationError = '';
  } catch (error) { if (epoch === collaborationEpoch) collaborationError = error.message; }
  finally {
    if (epoch === collaborationEpoch) {
      collaborationLoading = false;
      if (ready && currentView === 'collaboration' && !activeSession) renderCollaboration();
    }
  }
}

function collaborationCard(title, subtitle, controls = '', body = '') {
  return `<article class="collaboration-card"><div class="collaboration-card-top"><div><h3>${escapeHtml(title)}</h3><p>${escapeHtml(subtitle)}</p></div><div class="collaboration-actions">${controls}</div></div>${body}</article>`;
}

function renderCollaboration() {
  if (collaborationData === null && !collaborationLoading && !collaborationError) loadCollaboration();
  const context = workspaceSyncState.context || {};
  const navigation = Object.entries(collaborationTabs).map(([key, label]) => `<button role="tab" aria-selected="${key === collaborationTab}" class="${key === collaborationTab ? 'active' : ''}" data-collab-tab="${key}">${escapeHtml(t(label))}</button>`).join('');
  let actions = collabButton('刷新', 'reload');
  if (collaborationTab === 'workspaces') actions += collabButton('新建共享工作区', 'create') + collabButton('使用邀请码加入', 'join');
  if (collaborationTab === 'members' && context.canManage && context.kind === 'shared') actions += collabButton('邀请成员', 'invite');
  let content = '';
  const data = collaborationData;
  if (data && collaborationTab === 'workspaces') content = data.map(item => {
    const selected = context.id === item.id;
    const buttons = (selected ? '' : collabButton('切换工作区', 'select', `data-id="${escapeHtml(item.id)}"`)) + (selected && item.canManage ? collabButton('重命名', 'rename') : '') + (selected && item.kind === 'shared' ? item.role === 'owner' ? collabButton('删除工作区', 'delete', '', true) : collabButton('退出工作区', 'leave', '', true) : '');
    return collaborationCard(item.name, `${item.kind === 'personal' ? t('个人工作区') : t('共享工作区')} · ${roleName(item.role)}${selected ? ' · ' + t('正在使用') : ''}`, buttons);
  }).join('');
  if (data && collaborationTab === 'members') {
    content = data.members.map(member => collaborationCard(member.name || member.id.slice(0, 8), `${roleName(member.role)}${member.current ? ' · ' + t('当前账号') : ''}`, member.role !== 'owner' && context.canManage ? `<select data-member-role="${escapeHtml(member.id)}" aria-label="${escapeHtml(t('成员权限'))}">${['admin', 'editor', 'viewer'].map(role => `<option value="${role}" ${role === member.role ? 'selected' : ''}>${escapeHtml(roleName(role))}</option>`).join('')}</select>${context.role === 'owner' ? collabButton('转让所有权', 'transfer', `data-id="${escapeHtml(member.id)}"`) : ''}${collabButton('移除成员', 'removeMember', `data-id="${escapeHtml(member.id)}"`, true)}` : '')).join('');
    if (data.invites.length) content += `<h2 class="collaboration-section-title">${escapeHtml(t('成员邀请'))}</h2>` + data.invites.map(invite => collaborationCard(roleName(invite.role), `${activityTime(invite.expiresAt)} · ${invite.revoked ? t('已撤销') : invite.used ? t('已使用') : t('等待加入')}`, !invite.revoked && !invite.used ? collabButton('撤销邀请', 'revokeInvite', `data-id="${escapeHtml(invite.id)}"`, true) : '')).join('');
  }
  if (data && collaborationTab === 'history') content = data.map(item => collaborationCard(`${t('版本')} ${item.revision}`, `${activityTime(item.createdAt)} · ${item.actor || '—'} · ${item.operation}`, collabButton(context.canWrite ? '查看与恢复' : '查看版本', 'history', `data-revision="${item.revision}"`))).join('');
  if (data && collaborationTab === 'devices') content = data.map(item => collaborationCard(item.name, `${item.platform} · ${item.version} · ${t('最近访问')} ${activityTime(item.lastSeen)}${item.current ? ' · ' + t('当前设备') : ''}${item.active ? '' : ' · ' + t('已撤销')}`, collabButton('重命名', 'deviceRename', `data-id="${escapeHtml(item.id)}" data-name="${escapeHtml(item.name)}"`) + (item.active ? collabButton('撤销设备', 'deviceRevoke', `data-id="${escapeHtml(item.id)}"`, true) : ''))).join('');
  if (data && ['activity', 'audit'].includes(collaborationTab)) {
    content = data.entries.map(item => collaborationTab === 'audit' ? collaborationCard(item.summary, `${activityTime(item.createdAt)} · ${item.actor || '—'} · ${item.action}`) : collaborationCard(item.entry.hostName || item.entry.event, `${activityTime(item.entry.timestamp)} · ${item.deviceName} · ${t('客户端上报')}`, '', `<code class="collaboration-fingerprint">${escapeHtml([item.entry.event, item.entry.address, item.entry.username].filter(Boolean).join(' · '))}</code>`)).join('');
    content += `<div class="collaboration-pagination">${collabButton('上一页', 'previous', remoteLogOffset ? '' : 'disabled')}<span>${remoteLogOffset / 50 + 1}</span>${collabButton('下一页', 'next', data.hasMore ? '' : 'disabled')}${collaborationTab === 'activity' ? collabButton('清空云端个人日志', 'clearActivity', '', true) : ''}</div>`;
  }
  if (data && collaborationTab === 'trust') content = data.map(item => collaborationCard(serverEndpoint(item.entry.address, item.entry.port), `${item.entry.algorithm} · ${item.publishedBy || '—'} · ${activityTime(item.createdAt)}`, collabButton('核验并采用到本机', 'adopt', `data-endpoint="${escapeHtml(item.entry.endpoint)}"`) + (context.canManage ? collabButton('移除参考', 'removeTrust', `data-endpoint="${escapeHtml(item.entry.endpoint)}"`, true) : ''), `<code class="collaboration-fingerprint">${escapeHtml(item.entry.fingerprint)}</code>`)).join('');
  const notes = {
    workspaces: '切换工作区会关闭当前连接并取消传输。个人工作区始终保留，待同步修改按工作区分别保存。',
    members: '编辑者可以维护主机和片段；只读成员可以连接主机。SSH 登录凭据仍由每位成员单独提供。邀请码仅可使用一次。',
    history: '保留最近 50 个配置版本。恢复主机、分组和片段；个人偏好、收藏、本机私钥路径与信任不随版本回退。',
    devices: '撤销会立即阻止该设备的原登录授权访问工作区。在线桌面端会在下一次请求或 30 秒检查时退出并关闭连接。',
    activity: '当前账号上报的连接与安全事件，服务端保留 7 天。消息原文、异常、路径、密码和终端内容不会上传。可在账号设置暂停日志同步。',
    audit: '由服务端生成的成员、配置和安全操作记录，保留 7 天。仅所有者和管理员可查看，客户端不能修改或清空。',
    trust: '工作区指纹仅作为核验参考。采用前须通过可信渠道确认，并在原生窗口再次确认；已有本机信任不能覆盖。'
  };
  $('#page').innerHTML = heading('WORKSPACE', t(collaborationTabs[collaborationTab]), escapeHtml(context.name || t('账号工作区')), `<div class="collaboration-actions">${actions}</div>`) + `<div class="collaboration-tabs" role="tablist">${navigation}</div><div class="activity-notice">${icon('shield')}<p>${escapeHtml(t(notes[collaborationTab]))}</p></div>${collaborationError ? `<div class="activity-warning" role="alert">${escapeHtml(t(collaborationError))}</div>` : ''}<div class="collaboration-list">${content || `<div class="collaboration-empty">${escapeHtml(t(collaborationLoading ? '正在加载…' : '暂无记录'))}</div>`}</div>`;
}

function collabTextEditor(title, label, submit, initial = '', max = 100) {
  openEditor(editorHeader(escapeHtml(t(title)), '', 'WORKSPACE') + `<div class="form-grid"><div class="field full"><label for="collab-input">${escapeHtml(t(label))}</label><input id="collab-input" name="value" value="${escapeHtml(initial)}" maxlength="${max}" required autofocus autocomplete="off"></div></div>${editorFooter(t('确认'))}`, async form => submit(String(new FormData(form).get('value') || '').trim()));
}

async function reloadCollaboration() { collaborationData = null; collaborationError = ''; collaborationEpoch++; collaborationLoading = false; if (currentView === 'collaboration') renderCollaboration(); }

async function switchWorkspace(id) {
  if (!await confirmAction(t('切换工作区？'), t('当前 SSH / SFTP 连接会关闭，传输会取消。待同步修改保留在原工作区。'))) return;
  const result = await request('selectWorkspace', { id });
  // 让切换之前的网页回调失效，原生端也会核验工作区代次。
  lockWorkspace(); authenticationError = '';
  await unlockWorkspace();
  refreshWorkspace(result);
  resetCollaboration(); showView('hosts');
}

document.addEventListener('click', async event => {
  if (!ready) return;
  const tab = event.target.closest('[data-collab-tab]');
  if (tab) { collaborationTab = tab.dataset.collabTab; resetCollaboration(); showView('collaboration'); return; }
  const button = event.target.closest('[data-collab]');
  if (!button || button.disabled) return;
  button.disabled = true;
  const { collab: action, id, endpoint } = button.dataset;
  try {
    switch (action) {
      case 'reload': await reloadCollaboration(); break;
      case 'refresh': await refreshWorkspaceSynchronization(); break;
      case 'export': if (await request('exportWorkspace')) toast(t('工作区草稿已导出，不含私钥路径与本机信任。')); break;
      case 'import':
        if (await confirmAction(t('导入工作区？'), t('将合并文件中的配置。相同标识存在不同内容时会拒绝导入，请先导出备份。'))) { refreshWorkspace(await request('importWorkspace')); await refreshWorkspaceSynchronization(); }
        break;
      case 'retry': case 'discard':
        if (await confirmAction(t(action === 'retry' ? '重试待同步修改？' : '放弃待同步修改？'), t(action === 'retry' ? '会读取最新配置，再将本机待同步操作应用到最新版本。请先导出草稿留存。' : '会移除尚未确认的本机操作并读取服务端最新配置；已经提交的操作不会回退。请先导出草稿留存。'))) { refreshWorkspace(await request(action === 'retry' ? 'retryPendingChanges' : 'discardPendingChanges')); await refreshWorkspaceSynchronization(); }
        break;
      case 'select': await switchWorkspace(id); break;
      case 'create': collabTextEditor('新建共享工作区', '工作区名称', async name => { const item = await adminRequest('workspace.create', { name }); await reloadCollaboration(); toast(t('共享工作区已创建，请点击切换使用。')); }); break;
      case 'rename': collabTextEditor('重命名工作区', '工作区名称', async name => { await adminRequest('workspace.rename', { name }); await refreshWorkspaceSynchronization(); await reloadCollaboration(); }, workspaceSyncState.context?.name); break;
      case 'join': collabTextEditor('加入共享工作区', '一次性邀请码', async code => { await adminRequest('workspace.join', { code }); await reloadCollaboration(); toast(t('已加入工作区，请点击切换使用。')); }, '', 64); break;
      case 'delete': case 'leave':
        if (await confirmAction(t(action === 'delete' ? '删除共享工作区？' : '退出共享工作区？'), t(action === 'delete' ? '所有成员都会失去访问权限，云端配置和历史版本将删除。请先导出备份。' : '你将失去此工作区的访问权限。再次加入需要新的邀请。'))) {
          await adminRequest(action === 'delete' ? 'workspace.delete' : 'workspace.leave');
          const personal = (await adminRequest('workspace.list')).find(item => item.kind === 'personal');
          await request('selectWorkspace', { id: personal.id }); lockWorkspace(); await unlockWorkspace(); resetCollaboration(); showView('hosts');
        }
        break;
      case 'invite':
        openEditor(editorHeader(t('邀请成员'), t('邀请码仅显示一次，24 小时内有效。'), 'WORKSPACE') + `<div class="form-grid"><div class="field full"><label for="invite-role">${escapeHtml(t('成员权限'))}</label><select id="invite-role" name="role">${['viewer', 'editor', 'admin'].map(role => `<option value="${role}">${escapeHtml(roleName(role))}</option>`).join('')}</select></div></div>${editorFooter(t('创建邀请'))}`, async form => {
          const invite = await adminRequest('workspace.invite', { role: new FormData(form).get('role'), hours: 24 });
          openEditor(editorHeader(t('成员邀请码'), t('请仅分享给预期成员，使用一次后失效。'), 'INVITATION') + `<div class="form-grid"><div class="field full"><textarea readonly rows="3" aria-label="${escapeHtml(t('成员邀请码'))}">${escapeHtml(invite.code)}</textarea></div></div><div id="form-error" class="form-error"></div><div class="dialog-footer">${collabButton('复制邀请码', 'copyInvite', `data-code="${escapeHtml(invite.code)}"`)}<button type="button" class="primary-button" data-action="closeEditor">${escapeHtml(t('关闭'))}</button></div>`, async () => {});
          await reloadCollaboration();
        }); break;
      case 'copyInvite': await request('copy', { text: button.dataset.code }); toast(t('邀请码已复制')); break;
      case 'revokeInvite': await adminRequest('workspace.revokeInvite', { id }); await reloadCollaboration(); break;
      case 'removeMember': case 'transfer':
        if (await confirmAction(t(action === 'transfer' ? '转让所有权？' : '移除此成员？'), t(action === 'transfer' ? '该成员将成为唯一所有者，你将成为管理员。只有新所有者可以再次转让或删除工作区。' : '该成员将无法访问工作区，在线连接会在下一次检查时关闭。'))) { await adminRequest(action === 'transfer' ? 'workspace.transfer' : 'workspace.removeMember', { id }); await refreshWorkspaceSynchronization(); await reloadCollaboration(); }
        break;
      case 'history': {
        const revision = Number(button.dataset.revision);
        const item = await adminRequest('workspace.historyItem', { revision });
        openEditor(editorHeader(`${t('历史版本')} ${revision}`, t('恢复前建议导出当前草稿。恢复会生成一个新版本。'), 'WORKSPACE HISTORY') + `<div class="form-grid"><div class="field full"><textarea readonly rows="12" aria-label="${escapeHtml(t('版本内容'))}">${escapeHtml(JSON.stringify(item, null, 2))}</textarea></div></div>${workspaceSyncState.context?.canWrite ? editorFooter(t('恢复此版本')) : `<div class="dialog-footer"><button type="button" class="secondary-button" data-action="closeEditor">${escapeHtml(t('关闭'))}</button></div>`}`, async () => {
          if (!await confirmAction(t('恢复此版本？'), t('将用选定版本替换主机、分组和命令片段。个人偏好与本机信任保留。'))) return;
          await adminRequest('workspace.restore', { revision, expectedRevision: workspaceSyncState.revision }); await refreshWorkspaceSynchronization(); await reloadCollaboration();
        }); break;
      }
      case 'deviceRename': collabTextEditor('重命名设备', '设备名称', async name => { await adminRequest('device.rename', { id, name }); await reloadCollaboration(); }, button.dataset.name, 80); break;
      case 'deviceRevoke': if (await confirmAction(t('撤销此设备？'), t('该设备的原登录授权将无法访问工作区，在线客户端随后会退出。重新使用需要再次登录。'))) { await adminRequest('device.revoke', { id }); await reloadCollaboration(); } break;
      case 'previous': remoteLogOffset = Math.max(0, remoteLogOffset - 50); await reloadCollaboration(); break;
      case 'next': remoteLogOffset += 50; await reloadCollaboration(); break;
      case 'clearActivity': if (await confirmAction(t('清空云端个人日志？'), t('删除当前工作区中此账号上报的日志，服务端审计保留。本机现有记录不会再次上传。'))) { await adminRequest('activity.clear'); remoteLogOffset = 0; await reloadCollaboration(); } break;
      case 'publish': if (await confirmAction(t('发布指纹参考？'), t('请确认已通过可信渠道核验此指纹。其他成员采用时仍须单独核验。'))) { await adminRequest('trust.publish', { endpoint }); toast(t('指纹参考已发布')); } break;
      case 'removeTrust': if (await confirmAction(t('移除指纹参考？'), t('将移除工作区中的参考记录，各设备已保存的本机信任仍需分别管理。'))) { await adminRequest('trust.remove', { endpoint }); await reloadCollaboration(); } break;
      case 'adopt': if (await request('adoptTrustReference', { endpoint })) toast(t('已核验并保存到本机信任')); break;
      case 'activityToggle': await adminRequest('activity.settings', { enabled: !workspaceSyncState.activitySyncEnabled }); await refreshWorkspaceSynchronization(); break;
    }
  } catch (error) { toast(error.message, true); }
  finally { if (button.isConnected) button.disabled = false; }
});

document.addEventListener('change', async event => {
  const select = event.target.closest('[data-member-role]');
  if (!select || !ready) return;
  select.disabled = true;
  try { await adminRequest('workspace.memberRole', { id: select.dataset.memberRole, role: select.value }); await refreshWorkspaceSynchronization(); await reloadCollaboration(); }
  catch (error) { toast(error.message, true); await reloadCollaboration(); }
});
