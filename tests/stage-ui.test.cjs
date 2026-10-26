'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../src/Server/BuildSystem.Api/wwwroot/stage/app.js'), 'utf8');
class Element {
  constructor(tag = 'div') { this.tag = tag; this.children = []; this.listeners = {}; this.dataset = {}; this.hidden = false; this.value = ''; this.textContent = ''; this.classList = { add() {}, toggle() {} }; }
  addEventListener(name, handler) { this.listeners[name] = handler; }
  setAttribute() {}
  append(...children) { this.children.push(...children); }
  replaceChildren(...children) { this.children = children; }
  querySelectorAll(tag) { return this.children.flatMap(child => [ ...(child.tag === tag ? [child] : []), ...child.querySelectorAll(tag)]); }
}
function harness(fetch) {
  const nodes = new Map();
  const document = { getElementById(id) { if (!nodes.has(id)) nodes.set(id, new Element()); return nodes.get(id); }, createElement: tag => new Element(tag), createDocumentFragment: () => new Element('fragment') };
  vm.runInNewContext(source, { document, fetch, AbortController, URLSearchParams, Intl, Date, console });
  return nodes;
}
const ok = body => ({ ok: true, status: 200, json: async () => body });
const denied = status => ({ ok: false, status });
const flush = async () => { for (let i = 0; i < 10; i++) await new Promise(resolve => setImmediate(resolve)); };
const deferred = () => { let resolve; const promise = new Promise(r => { resolve = r; }); return { promise, resolve }; };

test('anonymous session exposes login and never requests operation data', async () => {
  const calls = [];
  const nodes = harness(async route => { calls.push(route); return denied(401); });
  await flush();
  assert.deepEqual(calls, ['/api/auth/me']);
  assert.equal(nodes.get('workspace').hidden, true);
  assert.equal(nodes.get('login-panel').hidden, false);
});

test('late list response cannot repopulate data after logout', async () => {
  const pending = deferred(); const calls = [];
  const nodes = harness(async (route, options) => {
    calls.push({ route, options });
    if (route === '/api/auth/me') return ok({ name: '<img onerror=bad>' });
    if (route.startsWith('/api/stage-operations')) return pending.promise;
    if (route === '/api/auth/csrf') return ok({ token: 'csrf-test' });
    if (route === '/api/auth/logout') return ok(null);
    throw new Error('Unexpected route');
  });
  await flush();
  assert.equal(nodes.get('current-user').textContent, '<img onerror=bad>');
  await nodes.get('logout').listeners.click();
  pending.resolve(ok({ items: [{ environmentId: '11111111-1111-1111-1111-111111111111', jobState: 'Leased' }], nextCursor: null }));
  await flush();
  assert.equal(nodes.get('workspace').hidden, true);
  assert.equal(nodes.get('operations-body').children.length, 0);
  assert.equal(calls.find(call => call.route === '/api/auth/logout').options.headers['X-CSRF-TOKEN'], 'csrf-test');
  assert.equal(calls.find(call => call.route.startsWith('/api/stage-operations')).options.signal.aborted, true);
});

test('login sends CSRF using same-origin cookies and clears password on rejection', async () => {
  const calls = [];
  const nodes = harness(async (route, options) => {
    calls.push({ route, options });
    if (route === '/api/auth/me') return denied(401);
    if (route === '/api/auth/csrf') return ok({ token: 'csrf-test' });
    if (route === '/api/auth/login') return denied(401);
    throw new Error('Unexpected route');
  });
  await flush(); nodes.get('username').value = 'operator'; nodes.get('password').value = 'test-only-password';
  await nodes.get('login-form').listeners.submit({ preventDefault() {} });
  const login = calls.find(call => call.route === '/api/auth/login');
  assert.equal(login.options.headers['X-CSRF-TOKEN'], 'csrf-test');
  assert.equal(login.options.credentials, 'same-origin');
  assert.deepEqual(JSON.parse(login.options.body), { userName: 'operator', password: 'test-only-password' });
  assert.equal(nodes.get('password').value, '');
  assert.equal(nodes.get('workspace').hidden, true);
});
test('unconfirmed logout hides data but retains an explicit retry action', async () => {
  let attempts = 0;
  const nodes = harness(async route => {
    if (route === '/api/auth/me') return ok({ name: 'operator' });
    if (route.startsWith('/api/stage-operations')) return ok({ items: [], nextCursor: null });
    if (route === '/api/auth/csrf') return ok({ token: 'csrf-test' });
    if (route === '/api/auth/logout') { attempts++; throw new Error('network unavailable'); }
    throw new Error('Unexpected route');
  });
  await flush();
  await nodes.get('logout').listeners.click();
  assert.equal(nodes.get('workspace').hidden, true);
  assert.equal(nodes.get('logout').hidden, false);
  assert.equal(nodes.get('logout').textContent, 'Повторить выход');
  await nodes.get('logout').listeners.click();
  assert.equal(attempts, 2);
});
// end
