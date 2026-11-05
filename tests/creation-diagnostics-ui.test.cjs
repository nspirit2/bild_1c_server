'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
const root = path.join(__dirname, '../src/Server/BuildSystem.Api');
const source = fs.readFileSync(path.join(root, 'wwwroot/creation-diagnostics/app.js'), 'utf8');
class Element {
  constructor() { this.children = []; this.listeners = {}; this.hidden = false; this.value = ''; this.textContent = ''; this.attributes = {}; }
  addEventListener(name, handler) { this.listeners[name] = handler; }
  setAttribute(name, value) { this.attributes[name] = value; }
  append(...children) { this.children.push(...children); }
  replaceChildren(...children) { this.children = children; }
  set innerHTML(_) { throw new Error('Unsafe HTML rendering'); }
}
function harness(fetch) {
  const nodes = new Map(), events = {}, documentEvents = {};
  const document = { hidden: false, getElementById(id) { if (!nodes.has(id)) nodes.set(id, new Element()); return nodes.get(id); }, createElement: () => new Element(), createDocumentFragment: () => new Element(), addEventListener(name, handler) { documentEvents[name] = handler; } };
  vm.runInNewContext(source, { document, fetch, AbortController, addEventListener(name, handler) { events[name] = handler; } });
  const submit = id => { nodes.get('operation-id').value = id; return nodes.get('lookup-form').listeners.submit({ preventDefault() {} }); };
  return { nodes, events, document, documentEvents, submit };
}
const id = '11111111-1111-1111-1111-111111111111';
const second = '22222222-2222-2222-2222-222222222222';
const receipt = overrides => ({ operationId: id, restoreIntentId: id, environmentId: id, stageJobId: id, reservationId: id, artifactId: id, agentId: id, targetIncarnationId: id, canonicalClusterId: id, canonicalSqlServerId: id, infobaseName: '<img src=x onerror=bad>', sqlDbName: 'canonical-db', state: 'AttemptRecorded', revision: 2, attemptId: second, attemptExpectedRevision: 1, quarantineReason: null, newlyRecorded: false, executable: false, scheduledJobsMustRemainBlocked: true, ...overrides });
const ok = value => ({ ok: true, status: 200, json: async () => value });
const denied = status => ({ ok: false, status });
const deferred = () => { let resolve; const promise = new Promise(r => { resolve = r; }); return { promise, resolve }; };
const rendered = nodes => nodes.get('values').children.flatMap(fragment => fragment.children.map(child => child.textContent)).join('\n');

test('strict nonempty D-format UUID validation sends no requests for invalid input', async () => {
  const calls = []; const h = harness(async (...args) => { calls.push(args); return ok(receipt()); });
  for (const value of ['', '00000000-0000-0000-0000-000000000000', ' ' + id, id + ' ', '{' + id + '}', id.replaceAll('-', ''), id + '?secret=x']) {
    await h.submit(value); assert.equal(h.nodes.get('result').hidden, true);
  }
  assert.equal(calls.length, 0);
});

test('lookup is GET-only no-store same-origin without query; hostile names stay plain text', async () => {
  const calls = []; const h = harness(async (url, options) => { calls.push({ url, options }); return ok(receipt()); });
  await h.submit(id);
  assert.equal(calls.length, 1); assert.equal(calls[0].url, '/api/admin/target-creation-operations/' + id);
  assert.equal(calls[0].options.method, 'GET'); assert.equal(calls[0].options.cache, 'no-store'); assert.equal(calls[0].options.credentials, 'same-origin');
  assert.equal(calls[0].options.redirect, 'error'); assert.equal(calls[0].options.signal.aborted, false);
  assert.match(rendered(h.nodes), /<img src=x onerror=bad>/); assert.match(rendered(h.nodes), /AttemptRecorded/);
  assert.match(rendered(h.nodes), /executable\)\nfalse/); assert.match(rendered(h.nodes), /scheduledJobsMustRemainBlocked\)\ntrue/);
  assert.equal(h.nodes.get('result').hidden, false);
});

test('uppercase D-format input is requested in canonical lowercase', async () => {
  const canonical = 'abcdefab-abcd-abcd-abcd-abcdefabcdef'; let requested;
  const h = harness(async url => { requested = url; return ok(receipt({ operationId: canonical })); });
  await h.submit(canonical.toUpperCase());
  assert.equal(requested, '/api/admin/target-creation-operations/' + canonical);
  assert.equal(h.nodes.get('result').hidden, false);
});

test('all API errors, malformed data, network and abort clear previous result; only 401 offers login', async () => {
  let response; const h = harness(async () => { if (response instanceof Error) throw response; return response; });
  for (const failure of [400, 401, 403, 404, 409, 500, new Error('network'), Object.assign(new Error('abort'), { name: 'AbortError' }), ok(receipt({ executable: true })), ok(receipt({ operationId: second }))]) {
    response = ok(receipt()); await h.submit(id); assert.equal(h.nodes.get('result').hidden, false);
    response = typeof failure === 'number' ? denied(failure) : failure;
    await h.submit(id); assert.equal(h.nodes.get('result').hidden, true); assert.equal(rendered(h.nodes), '');
    assert.equal(h.nodes.get('session-link').hidden, failure !== 401);
  }
});

test('new lookup aborts and invalidates older response even when transport ignores abort', async () => {
  const pending = deferred(), calls = []; const h = harness(async (url, options) => { calls.push({ url, options }); return calls.length === 1 ? pending.promise : ok(receipt({ operationId: second })); });
  const first = h.submit(id); assert.match(h.nodes.get('message').textContent, /Загрузка/);
  await h.submit(second); assert.equal(calls[0].options.signal.aborted, true);
  pending.resolve(ok(receipt())); await first;
  assert.equal(h.nodes.get('values').children[0].children[5].textContent, second);
});

test('editing input and leaving tab discard data and prevent in-flight resurrection', async () => {
  for (const trigger of [h => h.nodes.get('operation-id').listeners.input(), h => h.events.pagehide(), h => h.events.pageshow(), h => { h.document.hidden = true; h.documentEvents.visibilitychange(); }]) {
    const pending = deferred(); let signal; const h = harness(async (_, options) => { signal = options.signal; return pending.promise; });
    const request = h.submit(id); trigger(h); assert.equal(signal.aborted, true);
    pending.resolve(ok(receipt())); await request;
    assert.equal(h.nodes.get('result').hidden, true); assert.equal(rendered(h.nodes), '');
  }
});

test('resources enforce administrator authorization and same Stage CSP without inline content or mutation controls', () => {
  const mapper = fs.readFileSync(path.join(root, 'TargetCreationDiagnosticsUi.cs'), 'utf8');
  const html = fs.readFileSync(path.join(root, 'wwwroot/creation-diagnostics/index.html'), 'utf8');
  assert.match(mapper, /RequireAuthorization\(policy => policy.RequireRole\("Administrator"\)\)/);
  assert.doesNotMatch(mapper, /AllowAnonymous/); assert.match(mapper, /default-src 'none'; script-src 'self'; style-src 'self'/);
  assert.doesNotMatch(html, /<style|\sstyle=|\son\w+=/i); assert.match(html, /<script src="\/admin\/creation-diagnostics\/app.js" defer><\/script>/);
  assert.match(html, /href="\/stage"/); assert.match(html, /История аудита/); assert.match(html, /физической/);
  assert.equal((html.match(/<button/g) || []).length, 1); assert.doesNotMatch(source, /innerHTML|insertAdjacentHTML|method:\s*['"](?:POST|PUT|DELETE|PATCH)/);
});
