'use strict';

const path = require('node:path');

const pageUrl = 'termlume://app/index.html';
const credentialUrl = 'termlume://app/credential.html';
const allowedOperations = new Set(['workspace', 'knownHosts', 'logs', 'clearLogs', 'exportLogs', 'window', 'saveGroup', 'saveHost', 'deleteHost', 'favorite', 'saveSnippet', 'deleteSnippet', 'preferences', 'forgetKey', 'pickKey', 'connect', 'disconnect', 'input', 'resize', 'ack', 'copy', 'paste', 'sftpConnect', 'sftpDefaultLocal', 'sftpPickLocal', 'sftpLocalList', 'sftpCancel', 'sftpDisconnect', 'sftpList', 'sftpTransfer', 'sftpMutate']);

function trustedFrame(event, contents, url = pageUrl) {
  return Boolean(contents && !contents.isDestroyed() && event.sender === contents && event.senderFrame === contents.mainFrame && event.senderFrame.url === url);
}

function validateRequest(request, drop = false) {
  if (!request || typeof request !== 'object' || Array.isArray(request) || typeof request.id !== 'string' || !request.id || request.id.length > 100 || typeof request.op !== 'string') throw new Error('Invalid desktop request');
  if (drop ? request.op !== 'sftpDropUpload' : !allowedOperations.has(request.op)) throw new Error('Operation not allowed');
  if (!request.payload || typeof request.payload !== 'object' || Array.isArray(request.payload) || JSON.stringify(request).length > 2 * 1024 * 1024) throw new Error('Invalid request payload');
  return { id: request.id, op: request.op, payload: request.payload };
}

function resourcePath(rawUrl, webRoot, desktopRoot) {
  const url = new URL(rawUrl);
  if (url.protocol !== 'termlume:' || url.host !== 'app' || url.username || url.password || url.search || url.hash) throw new Error('Resource denied');
  const relative = decodeURIComponent(url.pathname).replace(/^\//, '');
  if (!relative || relative.includes('\\') || relative.includes('\0') || relative.split('/').some(part => part === '..' || part === '.')) throw new Error('Resource denied');
  const credential = ['credential.html', 'credential.css', 'credential.js'].includes(relative);
  const root = credential ? desktopRoot : webRoot;
  const target = path.resolve(root, relative);
  if (!target.startsWith(path.resolve(root) + path.sep)) throw new Error('Resource denied');
  return target;
}

module.exports = { pageUrl, credentialUrl, trustedFrame, validateRequest, resourcePath };
