'use strict';

const { spawn } = require('node:child_process');
const { StringDecoder } = require('node:string_decoder');

class Backend {
  constructor(executable, dataDirectory, onEvent, onHostRequest, onFailure) {
    this.pending = new Map();
    this.counter = 0;
    this.stopped = false;
    this.ready = new Promise((resolve, reject) => { this.resolveReady = resolve; this.rejectReady = reject; });
    this.child = spawn(executable, ['--data-dir', dataDirectory], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true, shell: false });
    const decoder = new StringDecoder('utf8');
    let buffer = '';
    const fail = () => {
      if (this.stopped) return;
      this.stopped = true;
      clearTimeout(startupTimer);
      this.rejectReady(new Error('SSH service stopped'));
      for (const item of this.pending.values()) { clearTimeout(item.timer); item.reject(new Error('SSH service stopped')); }
      this.pending.clear();
      onFailure();
    };
    const startupTimer = setTimeout(() => { this.child.kill(); fail(); }, 30000);
    this.child.stdout.on('data', chunk => {
      buffer += decoder.write(chunk);
      let newline;
      while ((newline = buffer.indexOf('\n')) >= 0) {
        if (newline >= 16 * 1024 * 1024) { this.child.kill(); fail(); return; }
        const line = buffer.slice(0, newline);
        buffer = buffer.slice(newline + 1);
        if (!line.trim()) continue;
        let message;
        try { message = JSON.parse(line); } catch { this.child.kill(); fail(); return; }
        if (message.type === 'ready') { clearTimeout(startupTimer); this.resolveReady(); }
        else if (message.type === 'reply') {
          const item = this.pending.get(message.id);
          if (!item) continue;
          this.pending.delete(message.id); clearTimeout(item.timer);
          item.resolve({ ...message, id: item.id });
        } else if (message.type === 'hostRequest') {
          Promise.resolve().then(() => onHostRequest(message.operation, message.payload)).then(result => this.send({ type: 'hostReply', id: message.id, ok: true, result: result ?? null }), () => this.send({ type: 'hostReply', id: message.id, ok: false }));
        } else if (message.type === 'fatal') { this.child.kill(); fail(); }
        else onEvent(message);
      }
      if (buffer.length >= 16 * 1024 * 1024) { this.child.kill(); fail(); }
    });
    this.child.stderr.on('data', () => {});
    this.child.stdin.on('error', fail);
    this.child.on('error', fail);
    this.child.on('exit', fail);
  }

  send(message) {
    if (this.stopped || this.child.stdin.destroyed) return;
    if (this.child.stdin.writableLength > 4 * 1024 * 1024) { this.child.kill(); return; }
    this.child.stdin.write(JSON.stringify(message) + '\n');
  }

  async request(request, nativePaths) {
    await this.ready;
    if (this.stopped || this.pending.size >= 256) throw new Error('SSH service unavailable or busy');
    const id = `native-${++this.counter}`;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { this.pending.delete(id); reject(new Error('SSH service request timed out')); }, 610000);
      this.pending.set(id, { resolve, reject, timer, id: request.id });
      this.send({ id, op: request.op, payload: request.payload, ...(nativePaths ? { nativePaths } : {}) });
    });
  }

  async close() {
    try { await this.request({ id: 'shutdown', op: 'shutdown', payload: {} }); }
    finally { this.child.stdin.end(); }
  }
}

module.exports = { Backend };
