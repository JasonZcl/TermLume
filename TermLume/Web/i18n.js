'use strict';

let uiLanguage = 'zh-CN';
let englishMessages = {};
let localeLoadError = null;
const localeReady = fetch('locales/en.json').then(response => {
  if (!response.ok) throw new Error('Language resources could not be loaded.');
  return response.json();
}).then(messages => { englishMessages = messages; }).catch(error => { localeLoadError = error; });
// 只记录启动时的静态界面节点，避免切换语言时误翻译主机名、文件名及其他用户数据。
const shellTranslations = [];
const shellWalker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
while (shellWalker.nextNode()) {
  const node = shellWalker.currentNode;
  if (/[\u3400-\u9fff]/.test(node.nodeValue) && !node.parentElement.closest('script, style, svg')) shellTranslations.push({ node, text: node.nodeValue });
}
document.querySelectorAll('[title], [aria-label], [placeholder], [alt]').forEach(node => {
  for (const attribute of ['title', 'aria-label', 'placeholder', 'alt']) {
    const text = node.getAttribute(attribute);
    if (text && /[\u3400-\u9fff]/.test(text)) shellTranslations.push({ node, attribute, text });
  }
});

function t(text, ...values) {
  text = platformText(text);
  if (typeof text !== 'string') return text ?? '';
  const key = values[0] === 1 && Object.hasOwn(englishMessages, text + '|one') ? text + '|one' : text;
  const translated = uiLanguage === 'en' && Object.hasOwn(englishMessages, key) ? englishMessages[key] : text;
  return values.length ? translated.replace(/\{(\d+)\}/g, (match, index) => index < values.length ? String(values[index]) : match) : translated;
}
function msg(strings, ...values) { return t(strings.reduce((text, part, index) => text + part + (index < values.length ? `{${index}}` : ''), ''), ...values); }
// 此入口仅处理源码中的静态模板片段；动态用户数据不参与字典匹配。
function uiHtml(markup) {
  return markup.split(/([<>"\n])/).map(part => {
    const original = part.trim();
    const key = platformText(original);
    if (key === original && (uiLanguage !== 'en' || !Object.hasOwn(englishMessages, key))) return part;
    const translated = (uiLanguage === 'en' && Object.hasOwn(englishMessages, key) ? englishMessages[key] : key).replace(/[&<>"]/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[character]);
    return part.replace(original, () => translated);
  }).join('');
}
// 插值原样保留；需要显示用户数据的调用方仍应先使用 escapeHtml。
function ui(strings, ...values) { return strings.reduce((text, part, index) => text + uiHtml(part) + (index < values.length ? values[index] : ''), ''); }
function uiLocale() { return uiLanguage === 'en' ? 'en-US' : 'zh-CN'; }
function groupLabel(group) { return group === '未分组' ? t('未分组') : group; }
function setUiLanguage(language) {
  const previous = uiLanguage;
  uiLanguage = language === 'en' && !localeLoadError ? 'en' : 'zh-CN';
  document.documentElement.lang = uiLanguage;
  for (const item of shellTranslations) {
    if (!item.node.isConnected) continue;
    if (item.attribute) item.node.setAttribute(item.attribute, t(item.text));
    else item.node.nodeValue = t(item.text);
  }
  document.querySelectorAll('.sidebar .nav-item').forEach(button => {
    button.setAttribute('aria-label', button.querySelector('span')?.textContent || '');
    button.title = button.getAttribute('aria-label');
  });
  return previous !== uiLanguage;
}
