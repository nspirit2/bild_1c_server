'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../src/Server/BuildSystem.Api/wwwroot/stage/app.js'), 'utf8');

// Independent, deliberately small DOM: all requests below are fixtures, never network.
class Element {
  constructor(tag = 'div') {
    this.tag = tag; this.children = []; this.listeners = {}; this.dataset = {};
    this.hidden = false; this.disabled = false; this.value = ''; this.textContent = '';
    this.attributes = {}; this.parentElement = null;
    this.classList = { add() {}, remove() {}, toggle() {} };
  }
  addEventListener(name, handler) { (this.listeners[name] ||= []).push(handler); }
  setAttribute(name, value) { this.attributes[name] = value; }
  getAttribute(name) { return this.attributes[name] ?? null; }
  append(...children) {
    for (const child of children) {
      if (child.tag === 'fragment') this.append(...child.children);
      else { child.parentElement = this; this.children.push(child); }
    }
  }
  replaceChildren(...children) { this.children = []; this.textContent = ''; this.append(...children); }
  querySelectorAll(tag) { return this.children.flatMap(child => [...(child.tag === tag ? [child] : []), ...child.querySelectorAll(tag)]); }
  closest(selector) {
    if (selector === 'button[data-environment]' && this.tag === 'button' && this.dataset.environment) return this;
    return this.parentElement?.closest(selector) || null;
  }
  contains(node) { return node === this || this.children.some(child => child.contains(node)); }
}
const ok = body => ({ ok: true, status: 200, json: async () => body });
const denied = status => ({ ok: false, status });
const deferred = () => { let resolve; const promise = new Promise(r => { resolve = r; }); return { promise, resolve }; };
const flush = async () => { for (let i = 0; i < 8; i++) await new Promise(resolve => setImmediate(resolve)); };
const id1 = '11111111-1111-1111-1111-111111111111';
const id2 = '22222222-2222-2222-2222-222222222222';
const project = '33333333-3333-3333-3333-333333333333';
const row = (id = id1, overrides = {}) => ({ id, projectId: project, displayName: 'Environment ' + id, state: 'Requested', expiresAt: null, ...overrides });
const content = element => [element.textContent, ...element.children.map(content)].join('\n');
const listRoute = '/api/test-environments';
function harness(routeFixture = () => ok([]), authenticated = true) {
  const nodes = new Map(), events = {}, documentEvents = {}, calls = [], timers = [];
  const document = {
    hidden: false, visibilityState: 'visible',
    getElementById(id) { if (!nodes.has(id)) nodes.set(id, new Element()); return nodes.get(id); },
    createElement: tag => new Element(tag), createDocumentFragment: () => new Element('fragment'),
    addEventListener(name, handler) { (documentEvents[name] ||= []).push(handler); }
  };
  const fetch = async (route, options = {}) => {
    calls.push({ route, options });
    if (route === '/api/auth/me') return authenticated ? ok({ name: 'fixture-operator' }) : denied(401);
    if (route.startsWith('/api/stage-operations')) return ok({ items: [], nextCursor: null });
    if (route === '/api/auth/csrf') return ok({ token: 'fixture-token' });
    if (route === '/api/auth/logout') return ok(null);
    return routeFixture(route, options);
  };
  vm.runInNewContext(source, {
    document, fetch, AbortController, URLSearchParams, Intl, Date, console,
    crypto: require('node:crypto').webcrypto,
    addEventListener(name, handler) { (events[name] ||= []).push(handler); },
    setInterval(...args) { timers.push(args); return timers.length; },
    setTimeout(...args) { timers.push(args); return timers.length; }, clearInterval() {}, clearTimeout() {}
  });
  const fire = async (element, name = 'click', event = {}) => {
    const e = { target: element, preventDefault() {}, ...event };
    for (let node = element; node; node = node.parentElement) for (const handler of node.listeners[name] || []) handler(e);
    await flush();
  };
  return {
    nodes, calls, timers, document, fire,
    refresh: () => fire(nodes.get('environments-refresh')),
    buttons: () => nodes.get('environments-list').querySelectorAll('button'),
    async emit(name, event = {}) { for (const handler of [...(events[name] || []), ...(documentEvents[name] || [])]) await handler(event); await flush(); }
  };
}
const historyCalls = h => h.calls.filter(call => call.route === listRoute || /^\/api\/test-environments\/[^/]+$/.test(call.route));
const cleared = h => {
  assert.equal(h.nodes.get('environments-list').children.length, 0);
  assert.equal(h.nodes.get('environment-details').children.length, 0);
};

test('history is explicit-refresh only, unauthenticated disabled, and never polls or posts', async () => {
  for (const signedIn of [false, true]) {
    const h = harness(() => ok([row()]), signedIn); await flush();
    assert.equal(h.nodes.get('environments-refresh').disabled, !signedIn);
    assert.equal(historyCalls(h).length, 0);
    await h.refresh();
    assert.equal(historyCalls(h).length, signedIn ? 1 : 0);
    await flush(); assert.equal(h.timers.length, 0);
    assert.equal(h.calls.some(c => c.options.method === 'POST'), false);
  }
});

test('list and selected detail use protected GET options, text nodes, and strict row identity', async () => {
  const attack = '<img src=x onerror=alert(1)>';
  const value = row(id1, { displayName: attack, state: 'UnrecognizedServerState' });
  const h = harness(route => route === listRoute ? ok([value]) : ok(value)); await flush(); await h.refresh();
  assert.match(content(h.nodes.get('environments-list')), /<img src=x onerror=alert\(1\)>/);
  assert.equal(h.nodes.get('environments-list').querySelectorAll('img').length, 0);
  assert.equal(h.nodes.get('environments-list').querySelectorAll('script').length, 0);
  assert.equal(h.buttons().length, 1); assert.equal(h.buttons()[0].dataset.environment, id1);
  await h.fire(h.buttons()[0]);
  assert.match(content(h.nodes.get('environment-details')), /<img src=x onerror=alert\(1\)>/);
  assert.deepEqual(historyCalls(h).map(c => c.route), [listRoute, `${listRoute}/${id1}`]);
  for (const { options } of historyCalls(h)) {
    assert.equal(options.method || 'GET', 'GET'); assert.equal(options.credentials, 'same-origin');
    assert.equal(options.cache, 'no-store'); assert.equal(options.redirect, 'error'); assert.ok(options.signal);
    assert.equal(options.body, undefined);
  }
});

test('detail rejects a foreign environment or project identity', async () => {
  for (const mismatch of [{ id: id2 }, { projectId: id2 }]) {
    const h = harness(route => route === listRoute ? ok([row()]) : ok(row(id1, mismatch)));
    await flush(); await h.refresh(); await h.fire(h.buttons()[0]);
    assert.equal(h.nodes.get('environment-details').children.length, 0);
    assert.ok(h.nodes.get('environment-detail-status').textContent);
  }
});

test('latest selected detail wins even when stale response body ignores abort', async () => {
  const body = deferred(); let firstSignal;
  const h = harness((route, options) => {
    if (route === listRoute) return ok([row(), row(id2)]);
    if (route === `${listRoute}/${id1}`) { firstSignal = options.signal; return { ok: true, status: 200, json: () => body.promise }; }
    return ok(row(id2, { displayName: 'Latest selection' }));
  });
  await flush(); await h.refresh(); await h.fire(h.buttons()[0]); await h.fire(h.buttons()[1]);
  assert.equal(firstSignal.aborted, true);
  body.resolve(row(id1, { displayName: 'STALE BODY' })); await flush();
  assert.match(content(h.nodes.get('environment-details')), /Latest selection/);
  assert.doesNotMatch(content(h.nodes.get('environment-details')), /STALE BODY/);
});

test('refresh invalidates selected detail transport and late data cannot repopulate details', async () => {
  const transport = deferred(); let signal;
  const h = harness((route, options) => {
    if (route === listRoute) return ok([row()]);
    signal = options.signal; return transport.promise;
  });
  await flush(); await h.refresh(); await h.fire(h.buttons()[0]); await h.refresh();
  assert.equal(signal.aborted, true);
  transport.resolve(ok(row(id1, { displayName: 'STALE TRANSPORT' }))); await flush();
  assert.equal(h.nodes.get('environment-details').children.length, 0);
});

test('new list refresh aborts stale list transport and body without restoring old rows', async () => {
  for (const pendingBody of [false, true]) {
    const pending = deferred(); let count = 0, oldSignal;
    const h = harness((route, options) => {
      assert.equal(route, listRoute);
      if (++count === 1) { oldSignal = options.signal; return pendingBody ? { ok: true, status: 200, json: () => pending.promise } : pending.promise; }
      return ok([row(id2, { displayName: 'Newest list' })]);
    });
    await flush(); await h.refresh(); await h.refresh(); assert.equal(oldSignal.aborted, true);
    pending.resolve(pendingBody ? [row()] : ok([row()])); await flush();
    assert.equal(h.buttons().length, 1); assert.equal(h.buttons()[0].dataset.environment, id2);
  }
});

test('401 and 403 on either endpoint clear session, history and existing form password', async () => {
  for (const status of [401, 403]) for (const endpoint of ['list', 'detail']) {
    let lists = 0;
    const h = harness(route => route === listRoute ? (++lists === 1 || endpoint === 'detail' ? ok([row()]) : denied(status)) : denied(status));
    await flush(); await h.refresh(); h.nodes.get('password').value = 'must-not-leak';
    if (endpoint === 'list') await h.refresh(); else await h.fire(h.buttons()[0]);
    cleared(h); assert.equal(h.nodes.get('workspace').hidden, true);
    assert.equal(h.nodes.get('login-panel').hidden, false); assert.equal(h.nodes.get('password').value, '');
    assert.equal(h.nodes.get('environments-refresh').disabled, true);
    assert.doesNotMatch(content(h.nodes.get('environments-panel')), /must-not-leak|fixture-token/);
  }
});

test('logout and operation navigation clear history and abort pending detail', async () => {
  for (const action of ['logout', 'refresh', 'state-filter']) {
    const pending = deferred(); let signal;
    const h = harness((route, options) => route === listRoute ? ok([row()]) : (signal = options.signal, pending.promise));
    await flush(); await h.refresh(); await h.fire(h.buttons()[0]);
    await h.fire(h.nodes.get(action), action === 'state-filter' ? 'change' : 'click');
    assert.equal(signal.aborted, true); cleared(h);
    pending.resolve(ok(row())); await flush(); cleared(h);
  }
});

test('pagehide, BFCache restoration and hidden visibility clear history and abort pending list', async () => {
  for (const event of ['pagehide', 'pageshow', 'visibilitychange']) {
    const pending = deferred(); let count = 0, signal;
    const h = harness((route, options) => { assert.equal(route, listRoute); if (++count === 1) return ok([row()]); signal = options.signal; return pending.promise; });
    await flush(); await h.refresh(); await h.refresh();
    if (event === 'visibilitychange') { h.document.hidden = true; h.document.visibilityState = 'hidden'; }
    await h.emit(event, { persisted: true }); assert.equal(signal.aborted, true); cleared(h);
    pending.resolve(ok([row()])); await flush(); cleared(h);
  }
});

test('malformed lists clear previous rows rather than inventing a usable state', async () => {
  for (const invalid of [{ items: [row()] }, [row(id1, { projectId: '../foreign' })], [row(id1, { displayName: null })], [row(id1, { state: null })], [row(id1, { expiresAt: 'not-a-date' })], [row(), row()]]) {
    let count = 0; const h = harness(() => ok(++count === 1 ? [row()] : invalid));
    await flush(); await h.refresh(); assert.equal(h.buttons().length, 1); await h.refresh(); cleared(h);
    assert.ok(h.nodes.get('environments-status').textContent);
  }
});

test('malformed detail clears prior fields instead of retaining an earlier selection', async () => {
  for (const invalid of [null, row(id1, { displayName: 17 }), row(id1, { state: null }), row(id1, { expiresAt: 'invalid-date' })]) {
    let count = 0; const h = harness(route => route === listRoute ? ok([row()]) : ok(++count === 1 ? row() : invalid));
    await flush(); await h.refresh(); await h.fire(h.buttons()[0]); assert.ok(h.nodes.get('environment-details').children.length);
    await h.fire(h.buttons()[0]); assert.equal(h.nodes.get('environment-details').children.length, 0);
    assert.ok(h.nodes.get('environment-detail-status').textContent);
  }
});

test('empty list has explicit message; large list caps rendering with honest shown count', async () => {
  const empty = harness(() => ok([])); await flush(); await empty.refresh(); cleared(empty);
  assert.match(empty.nodes.get('environments-status').textContent, /нет|пуст|не найден/i);
  const rows = Array.from({ length: 205 }, (_, i) => row(`00000001-0000-0000-0000-${String(i + 1).padStart(12, '0')}`));
  const h = harness(() => ok(rows)); await flush(); await h.refresh();
  assert.equal(h.buttons().length, 200);
  assert.match(h.nodes.get('environments-status').textContent, /показан\S*\s+(?:первые\s+)?200/i);
  assert.match(h.nodes.get('environments-status').textContent, /205/);
});
