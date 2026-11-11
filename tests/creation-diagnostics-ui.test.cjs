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
function harness(metadataFetch, auditFetch = async url => ok(audit({ operationId: url.split('/').at(-2) })), evidenceFetch = async () => denied(404)) {
  const nodes = new Map(), events = {}, documentEvents = {}, requests = [];
  const fetch = (url, options) => { requests.push({ url, options }); return (url.endsWith('/execution-evidence') ? evidenceFetch : url.endsWith('/audit') ? auditFetch : metadataFetch)(url, options); };
  const document = { hidden: false, getElementById(id) { if (!nodes.has(id)) nodes.set(id, new Element()); return nodes.get(id); }, createElement: () => new Element(), createDocumentFragment: () => new Element(), addEventListener(name, handler) { documentEvents[name] = handler; } };
  vm.runInNewContext(source, { document, fetch, AbortController, addEventListener(name, handler) { events[name] = handler; } });
  const submit = id => { nodes.get('operation-id').value = id; return nodes.get('lookup-form').listeners.submit({ preventDefault() {} }); };
  return { nodes, events, document, documentEvents, submit, requests };
}
const id = '11111111-1111-1111-1111-111111111111';
const second = '22222222-2222-2222-2222-222222222222';
const receipt = overrides => ({ operationId: id, restoreIntentId: id, environmentId: id, stageJobId: id, reservationId: id, artifactId: id, agentId: id, targetIncarnationId: id, canonicalClusterId: id, canonicalSqlServerId: id, infobaseName: '<img src=x onerror=bad>', sqlDbName: 'canonical-db', state: 'AttemptRecorded', revision: 2, attemptId: second, attemptExpectedRevision: 1, quarantineReason: null, newlyRecorded: false, executable: false, scheduledJobsMustRemainBlocked: true, ...overrides });
const auditEvent = overrides => ({ id: second, actorId: id, action: 'TargetCreationRegistered', createdAt: '2026-11-06T08:00:00Z', ...overrides });
const audit = overrides => ({ operationId: id, events: [], truncated: false, executable: false, scheduledJobsMustRemainBlocked: true, ...overrides });
const claim = overrides => ({ claimId: id, permitId: second, attemptId: second, agentId: id,
  credentialId: '33333333-3333-3333-3333-333333333333', endpointMappingId: '44444444-4444-4444-4444-444444444444',
  issuedAt: '2020-01-01T00:00:00.0000000Z', expiresAt: '2020-01-01T00:05:00+00:00', ...overrides });
const evidence = overrides => ({ operationId: id, claimRecorded: true, historicalClaim: claim(), executable: false,
  freshAuthority: false, retryAllowed: false, scheduledJobsMustRemainBlocked: true,
  reconciliationRequired: true, physicalOutcomeKnown: false, ...overrides });
const evidenceRendered = nodes => nodes.get('evidence-values').children.flatMap(fragment => fragment.children.map(child => child.textContent)).join('\n');
const auditRendered = nodes => nodes.get('audit-events').children.flatMap(fragment => fragment.children.map(child => child.textContent));
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

test('audit follows validated metadata with safe text rendering and unchanged server order', async () => {
  const entries = [auditEvent({ action: 'TargetCreationQuarantined', createdAt: '2026-11-05T08:00:00Z' }), auditEvent({ id, action: 'TargetCreationAttemptRecorded' })];
  const h = harness(async () => ok(receipt()), async () => ok(audit({ events: entries })));
  await h.submit(id);
  assert.equal(h.requests.length, 3);
  assert.equal(h.requests[1].url, '/api/admin/target-creation-operations/' + id + '/audit');
  const options = h.requests[1].options;
  assert.equal(options.method, 'GET'); assert.equal(options.cache, 'no-store');
  assert.equal(options.credentials, 'same-origin'); assert.equal(options.redirect, 'error');
  assert.equal(options.signal, h.requests[0].options.signal);
  const text = auditRendered(h.nodes);
  assert.match(text[0], /TargetCreationQuarantined/); assert.match(text[1], /TargetCreationAttemptRecorded/);
  assert.match(text[0], new RegExp('Actor UUID: ' + id));
  assert.equal(h.nodes.get('audit').hidden, false);
  assert.equal(h.nodes.get('audit-truncated').hidden, true);
  const invalid = harness(async () => ok(receipt({ executable: true })));
  await invalid.submit(id); assert.equal(invalid.requests.length, 1);
});

test('audit empty state is explicit and bounded truncated history displays its hint', async () => {
  let response = audit();
  const h = harness(async () => ok(receipt()), async () => ok(response));
  await h.submit(id);
  assert.deepEqual(auditRendered(h.nodes), []);
  assert.match(h.nodes.get('audit-message').textContent, /отсутствуют/);
  response = audit({ events: Array.from({ length: 50 }, () => auditEvent()), truncated: true });
  await h.submit(id);
  assert.equal(auditRendered(h.nodes).length, 50);
  assert.equal(h.nodes.get('audit-truncated').hidden, false);
});

test('audit authentication failures discard all data and only 401 offers session link', async () => {
  for (const status of [401, 403]) {
    let response = ok(audit({ events: [auditEvent()] }));
    const h = harness(async () => ok(receipt()), async () => response);
    await h.submit(id); assert.equal(auditRendered(h.nodes).length, 1);
    response = denied(status); await h.submit(id);
    assert.equal(h.nodes.get('result').hidden, true); assert.equal(rendered(h.nodes), '');
    assert.equal(h.nodes.get('audit').hidden, true); assert.deepEqual(auditRendered(h.nodes), []);
    assert.equal(h.nodes.get('session-link').hidden, status !== 401);
    assert.match(h.nodes.get('message').textContent, status === 401 ? /Сеанс завершён/ : /Доступ запрещён/);
  }
});

test('audit failures and malformed DTOs clear history without mislabelling successful metadata', async () => {
  const bad = [denied(404), denied(500), new Error('network'), { ok: true, status: 200, json: async () => { throw new Error('json'); } },
    ...[{ operationId: second }, { executable: true }, { scheduledJobsMustRemainBlocked: false }, { truncated: 'true' },
      { events: Array.from({ length: 51 }, () => auditEvent()) }, { events: [auditEvent({ id: 'bad' })] },
      { events: [auditEvent({ actorId: '<img src=x onerror=bad>' })] }, { events: [auditEvent({ action: '<script>bad</script>' })] },
      { events: [auditEvent({ action: 'targetcreationregistered' })] }, { events: [auditEvent({ createdAt: 'invalid-date' })] }].map(value => ok(audit(value)))];
  for (const failure of bad) {
    let response = ok(audit({ events: [auditEvent()], truncated: true }));
    const h = harness(async () => ok(receipt()), async () => { if (response instanceof Error) throw response; return response; });
    await h.submit(id); assert.equal(auditRendered(h.nodes).length, 1);
    response = failure; await h.submit(id);
    assert.equal(h.nodes.get('result').hidden, false); assert.match(rendered(h.nodes), /AttemptRecorded/);
    assert.deepEqual(auditRendered(h.nodes), []); assert.equal(h.nodes.get('audit-truncated').hidden, true);
    assert.match(h.nodes.get('audit-message').textContent, /История аудита недоступна/);
    assert.match(h.nodes.get('message').textContent, /Показаны только метаданные/);
    assert.equal(h.nodes.get('session-link').hidden, true);
  }
});

test('late audit responses including authentication errors cannot replace a new lookup', async () => {
  for (const response of [ok(audit({ events: [auditEvent()] })), denied(401)]) {
    const pending = deferred(), started = deferred();
    const h = harness(async url => ok(receipt({ operationId: url.split('/').at(-1) })), async url => {
      if (url.includes(id)) { started.resolve(); return pending.promise; }
      return ok(audit({ operationId: second, events: [auditEvent({ action: 'TargetCreationQuarantined' })] }));
    });
    const first = h.submit(id); await started.promise;
    await h.submit(second); assert.equal(h.requests[1].options.signal.aborted, true);
    pending.resolve(response); await first;
    assert.match(rendered(h.nodes), new RegExp(second));
    assert.match(auditRendered(h.nodes)[0], /TargetCreationQuarantined/);
    assert.equal(h.nodes.get('session-link').hidden, true);
  }
});

test('editing, hiding and BFCache transitions clear both sections and invalidate pending audit bodies', async () => {
  for (const trigger of [h => h.nodes.get('operation-id').listeners.input(), h => h.events.pagehide(), h => h.events.pageshow(), h => { h.document.hidden = true; h.documentEvents.visibilitychange(); }]) {
    const body = deferred(), started = deferred(); let pending = false;
    const h = harness(async () => ok(receipt()), async () => {
      if (!pending) return ok(audit({ events: [auditEvent()] }));
      return { ok: true, status: 200, json: () => { started.resolve(); return body.promise; } };
    });
    await h.submit(id); assert.equal(auditRendered(h.nodes).length, 1);
    pending = true; const request = h.submit(id); await started.promise;
    trigger(h); assert.equal(h.requests.at(-1).options.signal.aborted, true);
    body.resolve(audit({ events: [auditEvent()] })); await request;
    assert.equal(h.nodes.get('result').hidden, true); assert.equal(rendered(h.nodes), '');
    assert.equal(h.nodes.get('audit').hidden, true); assert.deepEqual(auditRendered(h.nodes), []);
  }
});

test('expired claimed evidence follows main and audit, displays six pins and original UTC times without authority', async () => {
  const payload = evidence();
  const h = harness(async () => ok(receipt()), undefined, async () => ok(payload));
  await h.submit(id.toUpperCase());
  assert.deepEqual(h.requests.map(request => request.url), [
    '/api/admin/target-creation-operations/' + id,
    '/api/admin/target-creation-operations/' + id + '/audit',
    '/api/admin/target-creation-operations/' + id + '/execution-evidence']);
  const options = h.requests[2].options;
  assert.equal(options.method, 'GET'); assert.equal(options.credentials, 'same-origin');
  assert.equal(options.cache, 'no-store'); assert.equal(options.redirect, 'error');
  assert.equal(options.signal, h.requests[0].options.signal);
  assert.equal(h.nodes.get('execution-evidence').hidden, false);
  assert.equal(h.nodes.get('evidence-message').textContent, 'Историческая выдача claim зафиксирована');
  const text = evidenceRendered(h.nodes);
  for (const key of ['claimId', 'permitId', 'attemptId', 'agentId', 'credentialId', 'endpointMappingId', 'issuedAt', 'expiresAt']) {
    assert.ok(text.includes(key + '\n' + payload.historicalClaim[key]));
  }
  for (const [key, expected] of Object.entries({ executable: false, freshAuthority: false, retryAllowed: false,
    scheduledJobsMustRemainBlocked: true, reconciliationRequired: true, physicalOutcomeKnown: false })) {
    assert.ok(text.includes(key + '\n' + expected));
  }
});

test('absent claim explicitly leaves physical launch unknown and does not show stale pins', async () => {
  let payload = evidence();
  const h = harness(async () => ok(receipt()), undefined, async () => ok(payload));
  await h.submit(id); assert.match(evidenceRendered(h.nodes), /claimId/);
  payload = evidence({ claimRecorded: false, historicalClaim: null }); await h.submit(id);
  assert.equal(h.nodes.get('evidence-message').textContent,
    'В снимке нет сохранённой записи выдачи claim. Это не доказывает отсутствие физического запуска.');
  assert.doesNotMatch(evidenceRendered(h.nodes), /claimId|issuedAt|expiresAt/);
  assert.match(evidenceRendered(h.nodes), /physicalOutcomeKnown\nfalse/);
});

test('evidence rejects unsafe pins, unknown shapes, mismatch, nonliteral flags and invalid UTC windows', async () => {
  const malformed = [
    { operationId: second }, { operationId: id.toUpperCase().replace('1', 'A') }, { claimRecorded: 'true' },
    { claimRecorded: false }, { historicalClaim: null }, { extra: 'unknown' },
    ...['executable', 'freshAuthority', 'retryAllowed', 'physicalOutcomeKnown'].map(key => ({ [key]: true })),
    ...['scheduledJobsMustRemainBlocked', 'reconciliationRequired'].map(key => ({ [key]: false })),
    { executable: 0 }, { freshAuthority: null },
    ...['claimId', 'permitId', 'attemptId', 'agentId', 'credentialId', 'endpointMappingId'].map(key => ({ historicalClaim: claim({ [key]: '<img src=x onerror=bad>' }) })),
    ...[{ attemptId: id }, { agentId: second }, { extra: true }, { claimId: '00000000-0000-0000-0000-000000000000' },
      { issuedAt: 'invalid-date' }, { issuedAt: '2020-01-01T00:00:00' }, { issuedAt: '2020-01-01T00:00:00+01:00' },
      { issuedAt: '2020-02-30T00:00:00Z' }, { expiresAt: '2020-01-01T00:00:00Z' },
      { expiresAt: '2019-12-31T23:59:59Z' }, { expiresAt: '2020-01-01T00:05:00.001Z' },
      { expiresAt: '2020-01-01T00:05:00.0000001Z' }].map(value => ({ historicalClaim: claim(value) }))
  ];
  for (const change of malformed) {
    const h = harness(async () => ok(receipt()), undefined, async () => ok(evidence(change)));
    await h.submit(id);
    assert.equal(evidenceRendered(h.nodes), '', JSON.stringify(change));
    assert.match(h.nodes.get('evidence-message').textContent, /недоступны/, JSON.stringify(change));
    assert.equal(h.nodes.get('result').hidden, false);
    assert.equal(h.nodes.get('audit').hidden, false);
  }
  for (const attemptId of [null, id]) {
    const h = harness(async () => ok(receipt({ attemptId })), undefined, async () => ok(evidence()));
    await h.submit(id); assert.equal(evidenceRendered(h.nodes), '');
  }
});

test('evidence authorization clears all sections; other failures clear only evidence and never imply no claim', async () => {
  for (const failure of [denied(401), denied(403), denied(404), denied(409), denied(500), new Error('network'),
    { ok: true, status: 200, json: async () => { throw new Error('invalid json'); } }]) {
    let response = ok(evidence());
    const h = harness(async () => ok(receipt()), async () => ok(audit({ events: [auditEvent()] })), async () => {
      if (response instanceof Error) throw response; return response;
    });
    await h.submit(id); assert.match(evidenceRendered(h.nodes), /claimId/);
    response = failure; await h.submit(id);
    const unauthorized = failure.status === 401 || failure.status === 403;
    assert.equal(evidenceRendered(h.nodes), '');
    assert.equal(h.nodes.get('result').hidden, unauthorized);
    assert.equal(h.nodes.get('audit').hidden, unauthorized);
    assert.equal(h.nodes.get('execution-evidence').hidden, unauthorized);
    assert.equal(h.nodes.get('session-link').hidden, failure.status !== 401);
    if (unauthorized) { assert.equal(rendered(h.nodes), ''); assert.deepEqual(auditRendered(h.nodes), []); }
    else {
      assert.match(h.nodes.get('evidence-message').textContent, /недоступны/);
      assert.doesNotMatch(h.nodes.get('evidence-message').textContent, /В снимке нет/);
      assert.match(rendered(h.nodes), /AttemptRecorded/); assert.equal(auditRendered(h.nodes).length, 1);
    }
  }
});

test('stale evidence transport and body completions including auth cannot repopulate or clear new lookup', async () => {
  for (const bodyPending of [false, true]) {
    for (const late of [ok(evidence()), denied(401), denied(403)]) {
      const pending = deferred(), started = deferred();
      const h = harness(async url => ok(receipt({ operationId: url.split('/').at(-1) })), undefined, async url => {
        if (!url.includes(id)) return ok(evidence({ operationId: second, claimRecorded: false, historicalClaim: null }));
        if (!bodyPending) { started.resolve(); return pending.promise; }
        return { ok: true, status: 200, json: () => { started.resolve(); return pending.promise; } };
      });
      const first = h.submit(id); await started.promise; await h.submit(second);
      assert.equal(h.requests[2].options.signal.aborted, true);
      pending.resolve(bodyPending ? evidence() : late); await first;
      assert.match(rendered(h.nodes), new RegExp(second));
      assert.match(evidenceRendered(h.nodes), new RegExp(second));
      assert.match(h.nodes.get('evidence-message').textContent, /В снимке нет/);
      assert.equal(h.nodes.get('session-link').hidden, true);
    }
  }
});

test('editing, page transitions and hidden tabs clear evidence and invalidate pending bodies', async () => {
  for (const trigger of [h => h.nodes.get('operation-id').listeners.input(), h => h.events.pagehide(), h => h.events.pageshow(), h => { h.document.hidden = true; h.documentEvents.visibilitychange(); }]) {
    const body = deferred(), started = deferred(); let pending = false;
    const h = harness(async () => ok(receipt()), undefined, async () => !pending ? ok(evidence())
      : { ok: true, status: 200, json: () => { started.resolve(); return body.promise; } });
    await h.submit(id); assert.match(evidenceRendered(h.nodes), /claimId/);
    pending = true; const request = h.submit(id); await started.promise; trigger(h);
    assert.equal(h.requests.at(-1).options.signal.aborted, true);
    body.resolve(evidence()); await request;
    assert.equal(h.nodes.get('execution-evidence').hidden, true); assert.equal(evidenceRendered(h.nodes), '');
    assert.equal(h.nodes.get('evidence-message').textContent, '');
    assert.equal(h.nodes.get('result').hidden, true); assert.equal(h.nodes.get('audit').hidden, true);
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
