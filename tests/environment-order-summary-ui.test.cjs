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
const detailRoute = '/api/test-environments';
const listRoute = detailRoute + '/browse?limit=25';
const page = (items = [], nextCursor = null, overrides = {}) => ({ items, nextCursor, observedAt: '2026-11-12T07:00:00Z', ...overrides });
function harness(routeFixture = () => ok(page()), authenticated = true) {
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
const requestId = '44444444-4444-4444-4444-444444444444';
const jobId = '55555555-5555-5555-5555-555555555555';
const archiveId = '66666666-6666-6666-6666-666666666666';
const summary = (changes = {}) => ({ environmentId: id1, projectId: project, displayName: '<b>Raw order</b>', environmentState: 'Requested',
  requestId, requestState: 'Pending', createdAt: '2026-11-12T07:00:00Z', ttlDays: 2, jobId, jobKind: 'ProvisionArchive', jobState: 'Pending',
  source: { archiveVersionId: archiveId, format: 'dt', sha256: 'a'.repeat(64), sizeBytes: 4096 }, ...changes });
const summaryRoute = `${detailRoute}/${id1}/order-summary`;
const stageRoute = `${detailRoute}/${id1}/stage-status`;
const cleanupRoute = `${detailRoute}/${id1}/cleanup-status`;
const stage = (changes = {}) => ({ environmentId: id1, requestId, jobId, ...changes });
const cleanup = (changes = {}) => ({ environmentId: id1, jobId, cleanupState: 'None', artifactId: null, retirementId: null,
  authorizationId: null, resultId: null, releaseId: null, reservedStagingBytes: 4096, heldStagingBytes: 4096, releasedStagingBytes: 0,
  observedAt: '2026-11-12T07:00:00Z', ...changes });
function setup(summaryFixture = () => ok(summary()), statusFixture = route => route === stageRoute ? ok(stage()) : ok(cleanup())) {
  return harness((route, options) => {
    if (route === listRoute) return ok(page([row(), row(id2)]));
    if (route === `${detailRoute}/${id1}`) return ok(row());
    if (route === `${detailRoute}/${id2}`) return ok(row(id2));
    if (route.endsWith('/order-summary')) return summaryFixture(route, options);
    if (route === stageRoute || route === cleanupRoute) return statusFixture(route, options);
    throw new Error('Unexpected fixture route: ' + route);
  });
}
const open = async h => { await flush(); await h.refresh(); await h.fire(h.buttons()[0]); };
const load = h => h.fire(h.nodes.get('order-summary-load'));
const loadStage = h => h.fire(h.nodes.get('order-stage-load'));
const noSummary = h => assert.equal(h.nodes.get('order-summary-values').children.length, 0);
const allCleared = h => {
  noSummary(h); assert.equal(h.nodes.get('environment-details').children.length, 0);
  assert.equal(h.nodes.get('environments-list').children.length, 0);
  assert.equal(h.nodes.get('detail-values').children.length, 0);
  assert.equal(h.nodes.get('cleanup-values').children.length, 0);
  assert.equal(h.nodes.get('workspace').hidden, true);
};

test('explicit summary renders only closed pathless metadata as text; original detail stays five fields', async () => {
  const h = setup(); await open(h);
  assert.equal(h.calls.filter(c => c.route.endsWith('/order-summary')).length, 0);
  assert.equal(h.nodes.get('environment-details').children.length, 10);
  await load(h);
  assert.equal(h.nodes.get('order-summary-values').children.length, 30);
  assert.match(content(h.nodes.get('order-summary-values')), /<b>Raw order<\/b>/);
  assert.equal(h.nodes.get('order-summary-values').querySelectorAll('b').length, 0);
  assert.match(content(h.nodes.get('order-summary-values')), new RegExp(requestId));
  assert.match(content(h.nodes.get('order-summary-values')), new RegExp(jobId));
  const call = h.calls.find(c => c.route === summaryRoute);
  assert.equal(call.options.method || 'GET', 'GET'); assert.equal(call.options.credentials, 'same-origin');
  assert.equal(call.options.cache, 'no-store'); assert.equal(call.options.redirect, 'error'); assert.ok(call.options.signal);
  assert.equal(h.calls.some(c => c.options.method === 'POST'), false); assert.equal(h.timers.length, 0);
});

test('summary validates every boundary and rejects root/source additions without clearing legacy detail', async () => {
  const bad = [ { path: '/private/source' }, { environmentId: id2 }, { projectId: id2 }, { requestId: '00000000-0000-0000-0000-000000000000' },
    { jobId: '' }, { jobKind: 'UnknownKind' }, { displayName: 'x'.repeat(121) }, { jobState: 'x'.repeat(65) }, { createdAt: '2026-11-12T07:00:00+03:00' },
    { createdAt: 'nonsense' }, { ttlDays: 0 }, { ttlDays: 36501 }, { ttlDays: 1.5 },
    ...[{ path: '/hidden' }, { format: 'zip' }, { sha256: 'x'.repeat(64) }, { sha256: 'a'.repeat(64) + '\n' }, { sizeBytes: 0 }, { sizeBytes: Number.MAX_SAFE_INTEGER + 1 }, { archiveVersionId: '' }]
      .map(changes => ({ source: { ...summary().source, ...changes } })) ];
  for (const changes of bad) {
    const h = setup(() => ok(summary(changes))); await open(h); await load(h); noSummary(h);
    assert.equal(h.nodes.get('environment-details').children.length, 10); assert.equal(h.nodes.get('order-stage-load').hidden, true);
  }
});

test('summary 401 and 403 clear all protected panels; unavailable summary preserves environment details', async () => {
  for (const status of [401,403,404,500]) {
    const h = setup(() => denied(status)); await open(h); await load(h);
    if (status === 401 || status === 403) allCleared(h);
    else { noSummary(h); assert.equal(h.nodes.get('environment-details').children.length, 10); assert.match(h.nodes.get('order-summary-status').textContent, /неизвестно/); }
  }
});

test('stale summary body cannot repopulate after selection, pager/list reset, hidden navigation or logout', async () => {
  for (const action of ['selection','refresh','pagehide','visibilitychange','logout']) {
    const pending = deferred(); let signal;
    const h = setup((route, options) => { signal = options.signal; return { ok: true, json: () => pending.promise }; });
    await open(h); await load(h);
    if (action === 'selection') await h.fire(h.buttons()[1]);
    else if (action === 'refresh') await h.refresh();
    else if (action === 'logout') await h.fire(h.nodes.get('logout'));
    else { if (action === 'visibilitychange') h.document.hidden = true; await h.emit(action); }
    assert.equal(signal.aborted, true); pending.resolve(summary()); await flush(); noSummary(h);
    assert.equal(h.nodes.get('order-stage-load').hidden, true);
  }
});

test('job-kind gate requires explicit status action and ObserveArchiveStage issues no status requests', async () => {
  for (const kind of ['ProvisionArchive','ObserveArchiveStage']) {
    const h = setup(() => ok(summary({ jobKind: kind }))); await open(h); await load(h);
    assert.equal(h.calls.some(c => c.route === stageRoute || c.route === cleanupRoute), false);
    assert.equal(h.nodes.get('order-stage-load').hidden, kind !== 'ProvisionArchive');
    await loadStage(h);
    assert.equal(h.calls.filter(c => c.route === stageRoute || c.route === cleanupRoute).length, kind === 'ProvisionArchive' ? 2 : 0);
    if (kind === 'ObserveArchiveStage') assert.match(h.nodes.get('order-summary-status').textContent, /не поддерживаются/);
  }
});

test('stage pins request and job independently while cleanup pins only its available job identity', async () => {
  for (const mismatch of [{ requestId: id2 }, { jobId: id2 }]) {
    const h = setup(undefined, route => route === stageRoute ? ok(stage(mismatch)) : ok(cleanup()));
    await open(h); await load(h); await loadStage(h);
    assert.equal(h.nodes.get('detail-values').children.length, 0);
    assert.ok(h.nodes.get('cleanup-values').children.length > 0);
  }
  const h = setup(undefined, route => route === stageRoute ? ok(stage()) : ok(cleanup({ jobId: id2 })));
  await open(h); await load(h); await loadStage(h);
  assert.ok(h.nodes.get('detail-values').children.length > 0); assert.equal(h.nodes.get('cleanup-values').children.length, 0);
  assert.match(h.nodes.get('cleanup-message').textContent, /неизвестно/);
});

test('correlated status 403 clears all; 404 means unavailable/unknown, never a no-hold inference', async () => {
  for (const route of [stageRoute, cleanupRoute]) for (const status of [403,404]) {
    const h = setup(undefined, r => r === route ? denied(status) : r === stageRoute ? ok(stage()) : ok(cleanup()));
    await open(h); await load(h); await loadStage(h);
    if (status === 403) allCleared(h);
    else {
      assert.match(h.nodes.get(route === stageRoute ? 'detail-message' : 'cleanup-message').textContent, /неизвестно/);
      assert.equal(h.nodes.get('environment-details').children.length, 10);
    }
  }
});

test('history-owned pending stage and cleanup abort on history invalidation without late resurrection', async () => {
  const bodies = { [stageRoute]: deferred(), [cleanupRoute]: deferred() }, signals = [];
  const h = setup(undefined, (route, options) => { signals.push(options.signal); return { ok: true, json: () => bodies[route].promise }; });
  await open(h); await load(h); await loadStage(h); await h.fire(h.buttons()[1]);
  assert.equal(signals.length, 2); assert.ok(signals.every(signal => signal.aborted));
  bodies[stageRoute].resolve(stage()); bodies[cleanupRoute].resolve(cleanup()); await flush();
  assert.equal(h.nodes.get('detail-panel').hidden, true); assert.equal(h.nodes.get('detail-values').children.length, 0);
  assert.equal(h.nodes.get('cleanup-values').children.length, 0);
});
