(() => {
  'use strict';
  // 应用使用的是原始回调；仅清理浏览器地址栏，不跳转或重新发送授权参数。
  try { window.history.replaceState(null, '', window.location.pathname); } catch { }

  document.getElementById('close-page').addEventListener('click', () => {
    window.close();
    // 一些浏览器不允许脚本关闭外部打开的标签页，提供可执行的手动引导。
    window.setTimeout(() => {
      document.getElementById('close-hint').textContent = document.documentElement.lang === 'en'
        ? 'Please close this tab manually and switch back to TermLume.'
        : '请手动关闭此标签页，并切换回 TermLume。';
    }, 200);
  });
})();
