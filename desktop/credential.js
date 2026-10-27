'use strict';

(async () => {
  const data = await window.credential.initialize();
  const english = data.language === 'en';
  const text = (chinese, translation) => english ? translation : chinese;
  const element = id => document.getElementById(id);
  const exit = data.mode === 'exit';
  document.documentElement.lang = english ? 'en' : 'zh-CN';
  element('title').textContent = data.title;
  element('description').textContent = exit ? text('退出将断开所有连接并取消未完成的传输，工作空间配置会保留。', 'Quitting disconnects sessions and cancels unfinished transfers. Your saved workspace is kept.') : data.endpoint;
  element('label').textContent = data.key ? text('私钥口令', 'Private key passphrase') : text('登录密码', 'Login password');
  element('secret').placeholder = data.key ? text('未加密私钥可留空', 'Leave empty for an unencrypted key') : text('输入 SSH 登录密码', 'Enter your SSH password');
  element('privacy').textContent = text('仅用于此次连接，密码和口令不会保存到磁盘。', 'Used for this connection only. Passwords are never saved to disk.');
  const capsLock = document.createElement('span');
  capsLock.className = 'caps-lock'; capsLock.hidden = true;
  capsLock.textContent = text('大写锁定已开启，请留意密码大小写。', 'Caps Lock is on. Check your password capitalization.');
  capsLock.setAttribute('role', 'status');
  element('privacy').before(capsLock);
  for (const type of ['keydown', 'keyup']) element('secret').addEventListener(type, event => { capsLock.hidden = !event.getModifierState('CapsLock'); });
  element('cancel').textContent = exit ? text('继续使用', 'Keep working') : text('取消', 'Cancel');
  element('submit').textContent = exit ? text('断开并退出', 'Disconnect and quit') : text('连接', 'Connect');
  element('reveal').textContent = text('显示', 'Show');
  element('reveal').addEventListener('click', () => {
    const visible = element('secret').type === 'password';
    element('secret').type = visible ? 'text' : 'password';
    element('reveal').textContent = visible ? text('隐藏', 'Hide') : text('显示', 'Show');
    element('reveal').setAttribute('aria-pressed', String(visible));
  });
  const finish = value => {
    element('submit').disabled = true;
    element('secret').value = '';
    return window.credential.submit(value);
  };
  element('cancel').addEventListener('click', () => finish(null));
  document.addEventListener('keydown', event => { if (event.key === 'Escape') { event.preventDefault(); finish(null); } });
  element('form').addEventListener('submit', event => { event.preventDefault(); finish(exit ? 'confirm' : element('secret').value); });
  if (exit) {
    for (const selector of ['#label', '.secret-field', '#privacy']) document.querySelector(selector).hidden = true;
    element('stats').hidden = false;
    for (const [key, label] of [['ssh', 'SSH'], ['sftp', 'SFTP'], ['transfers', text('传输任务', 'Transfers')]]) {
      const stat = document.createElement('div'); stat.className = 'stat';
      const count = document.createElement('strong'); count.textContent = String(data.status[key]); stat.append(count, label); element('stats').append(stat);
    }
    element('cancel').focus();
  } else element('secret').focus();
})().catch(() => window.close());
