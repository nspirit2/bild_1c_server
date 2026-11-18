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
function harness(metadataFetch, auditFetch = async url => ok(audit({ operationId: url.split('/').at(-2) })),
  evidenceFetch = async () => denied(404), registrationFetch = async () => denied(404)) {
  const nodes = new Map(), events = {}, documentEvents = {}, requests = [];
  const fetch = (url, options) => {
    requests.push({ url, options });
    const handler = url.endsWith('/registration-evidence') ? registrationFetch : url.endsWith('/execution-evidence') ? evidenceFetch
      : url.endsWith('/audit') ? auditFetch : metadataFetch;
    return handler(url, options);
  };
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
const registrationFlags = { executable: false, ownershipEstablished: false, restoreAllowed: false, retryAllowed: false,
  freshAuthority: false, requiresIndependentVerification: true, scheduledJobsMustRemainBlocked: true,
  reportedEndpointMappingsAreAttested: false };
const registrationHistory = overrides => ({ claimId: id, operationId: id, agentId: id, attemptId: second, permitId: second,
  credentialId: '33333333-3333-3333-3333-333333333333', creationEndpointMappingId: '44444444-4444-4444-4444-444444444444',
  restoreIntentId: id, fencingToken: 12, targetIncarnationId: id, reservationId: id, canonicalClusterId: id, canonicalSqlServerId: id,
  reportedInfobaseId: '55555555-5555-5555-5555-555555555555', racObservationId: '66666666-6666-6666-6666-666666666666',
  sqlObservationId: '77777777-7777-7777-7777-777777777777', racEndpointMappingId: '88888888-8888-8888-8888-888888888888',
  sqlEndpointMappingId: '99999999-9999-9999-9999-999999999999', registrationProfileId: 'rac-registration-8.3.22.2557-v1',
  infobaseName: '<img src=x onerror=bad>', sqlServer: '<svg/onload=bad>', databaseName: 'canonical-db', sqlDatabaseId: 17,
  sqlCreateDate: '2020-01-01T03:00:00.1234567+03:00', sqlState: 'ONLINE', acceptedAt: '2020-01-01T00:05:00.1234567+00:00',
  ...registrationFlags, ...overrides });
const registration = overrides => ({ operationId: id, evidenceRecorded: true, historicalEvidence: registrationHistory(), ...registrationFlags, ...overrides });
const registrationRendered = nodes => nodes.get('registration-values').children.flatMap(fragment => fragment.children.map(child => child.textContent)).join('\n');
const registrationPath = operationId => '/api/admin/target-creation-operations/' + operationId + '/registration-evidence';
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
  assert.equal(calls.length, 0); assert.equal(h.requests.length, 0);
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
  assert.equal(h.requests.length, 4);
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
    assert.equal(h.requests.at(-1).url, '/api/admin/target-creation-operations/' + id + '/audit');
    assert.equal(h.nodes.get('registration-evidence').hidden, true);
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
    '/api/admin/target-creation-operations/' + id + '/execution-evidence', registrationPath(id)]);
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

test('found registration evidence uses only GET and renders historical fields as inert text independently of execution evidence', async () => {
  const operationId = 'abcdefab-abcd-abcd-abcd-abcdefabcdef';
  const databaseName = '</dd><script>bad</script>';
  const historicalEvidence = registrationHistory({ operationId, databaseName });
  const payload = registration({ operationId, historicalEvidence });
  const h = harness(async () => ok(receipt({ operationId, sqlDbName: databaseName })), async () => denied(500),
    async () => denied(500), async () => ok(payload));
  await h.submit(operationId.toUpperCase());
  const base = '/api/admin/target-creation-operations/' + operationId;
  assert.deepEqual(h.requests.map(request => request.url), [base, base + '/audit', base + '/execution-evidence', registrationPath(operationId)]);
  for (const { options } of h.requests) {
    assert.equal(options.method, 'GET'); assert.equal(options.cache, 'no-store');
    assert.equal(options.credentials, 'same-origin'); assert.equal(options.redirect, 'error');
    assert.equal(options.body, undefined);
    assert.equal(options.signal, h.requests[0].options.signal); assert.equal(options.signal.aborted, false);
  }
  assert.equal(h.nodes.get('registration-evidence').hidden, false);
  assert.match(h.nodes.get('registration-message').textContent, /зафиксирован|сохранен|сохранён/i);
  assert.match(h.nodes.get('audit-message').textContent, /недоступна/);
  assert.match(h.nodes.get('evidence-message').textContent, /недоступны/);
  const text = registrationRendered(h.nodes);
  for (const [key, value] of Object.entries(historicalEvidence)) {
    assert.ok(text.includes(key + '\n' + value), `historical field ${key}:\n${text}`);
  }
});

test('absent registration evidence is explicit and clears historical fields without asserting physical outcome', async () => {
  let payload = registration();
  const h = harness(async () => ok(receipt()), undefined, async () => ok(evidence()), async () => ok(payload));
  await h.submit(id); assert.match(registrationRendered(h.nodes), /reportedInfobaseId/);
  payload = registration({ evidenceRecorded: false, historicalEvidence: null }); await h.submit(id);
  assert.equal(h.nodes.get('registration-evidence').hidden, false);
  assert.match(h.nodes.get('registration-message').textContent, /нет|отсутств/i);
  assert.match(h.nodes.get('registration-message').textContent, /не доказывает|не означает|не подтверждает/i);
  const text = registrationRendered(h.nodes);
  assert.doesNotMatch(text, /claimId|reportedInfobaseId|acceptedAt|2020-01-01/);
  for (const [key, flag] of Object.entries(registrationFlags)) assert.ok(text.includes(key + '\n' + flag), key);
  assert.match(evidenceRendered(h.nodes), /claimId/);
});

test('malformed or mismatched registration evidence never renders historical details or apparent authority', async () => {
  const nested = overrides => ({ historicalEvidence: registrationHistory(overrides) });
  const malformed = [
    { operationId: second }, { evidenceRecorded: 'true' }, { evidenceRecorded: false }, { historicalEvidence: null },
    { freshAuthority: 0 }, { executable: undefined }, { accessToken: 'not part of the DTO' },
    nested({ restoreAllowed: 'false' }), nested({ executable: undefined }), nested({ accessToken: 'not part of the DTO' }),
    ...Object.entries(registrationFlags).flatMap(([key, flag]) => [{ [key]: !flag }, nested({ [key]: !flag })]),
    ...['operationId', 'agentId', 'restoreIntentId', 'targetIncarnationId', 'reservationId', 'canonicalClusterId', 'canonicalSqlServerId']
      .map(key => nested({ [key]: second })),
    nested({ attemptId: id }), nested({ infobaseName: 'another infobase' }), nested({ databaseName: 'another database' }),
    nested({ claimId: '00000000-0000-0000-0000-000000000000' }), nested({ racEndpointMappingId: '<img>' }),
    nested({ sqlObservationId: registrationHistory().racObservationId }), nested({ fencingToken: 0 }),
    nested({ fencingToken: Number.MAX_SAFE_INTEGER + 1 }), nested({ sqlDatabaseId: '17' }),
    nested({ registrationProfileId: 'unknown-profile' }), nested({ sqlState: 'OFFLINE' }),
    nested({ sqlCreateDate: '2020-02-30T00:00:00.0000000' }), nested({ acceptedAt: '2020-01-01T03:05:00+03:00' })
  ];
  let change;
  const h = harness(async () => ok(receipt()), async () => ok(audit({ events: [auditEvent()] })), async () => ok(evidence()),
    async () => ok(registration(change)));
  await h.submit(id); assert.match(registrationRendered(h.nodes), /reportedInfobaseId/);
  for (change of malformed) {
    await h.submit(id);
    const detail = JSON.stringify(change);
    assert.equal(registrationRendered(h.nodes), '', detail);
    assert.match(h.nodes.get('registration-message').textContent, /недоступ/i, detail);
    assert.equal(h.nodes.get('result').hidden, false, detail);
    assert.equal(auditRendered(h.nodes).length, 1, detail);
    assert.match(evidenceRendered(h.nodes), /claimId/, detail);
    assert.equal(h.nodes.get('session-link').hidden, true, detail);
  }
});

test('registration 401 or 403 clears every panel; other failures do not imply absent evidence', async () => {
  for (const failure of [denied(401), denied(403), denied(404), denied(409), denied(500), new Error('network'),
    { ok: true, status: 200, json: async () => { throw new Error('invalid json'); } }]) {
    let response = ok(registration());
    const h = harness(async () => ok(receipt()), async () => ok(audit({ events: [auditEvent()] })), async () => ok(evidence()), async () => {
      if (response instanceof Error) throw response;
      return response;
    });
    await h.submit(id); assert.match(registrationRendered(h.nodes), /reportedInfobaseId/);
    response = failure; await h.submit(id);
    const unauthorized = failure.status === 401 || failure.status === 403;
    assert.equal(registrationRendered(h.nodes), '');
    for (const panel of ['result', 'audit', 'execution-evidence', 'registration-evidence']) {
      assert.equal(h.nodes.get(panel).hidden, unauthorized, panel);
    }
    assert.equal(h.nodes.get('session-link').hidden, failure.status !== 401);
    if (unauthorized) {
      assert.equal(rendered(h.nodes), ''); assert.deepEqual(auditRendered(h.nodes), []); assert.equal(evidenceRendered(h.nodes), '');
      assert.equal(h.nodes.get('registration-message').textContent, '');
      assert.equal(h.requests.at(-1).options.signal.aborted, true);
      assert.match(h.nodes.get('message').textContent, failure.status === 401 ? /Сеанс завершён/ : /Доступ запрещён/);
    } else {
      assert.match(h.nodes.get('registration-message').textContent, /недоступ/i);
      assert.doesNotMatch(h.nodes.get('registration-message').textContent, /В снимке нет|запись отсутствует/i);
      assert.match(rendered(h.nodes), /AttemptRecorded/); assert.equal(auditRendered(h.nodes).length, 1);
      assert.match(evidenceRendered(h.nodes), /claimId/);
    }
  }
});

test('late registration transport (including auth failures) and late JSON cannot replace a new lookup', { timeout: 2000 }, async () => {
  for (const { late, json } of [{ late: ok(registration()) }, { late: denied(401) }, { late: denied(403) }, { late: registration(), json: true }]) {
    const pending = deferred(), started = deferred();
    const h = harness(async url => ok(receipt({ operationId: url.split('/').at(-1) })), undefined, undefined, async url => {
      if (url === registrationPath(second)) return ok(registration({ operationId: second, evidenceRecorded: false, historicalEvidence: null }));
      if (json) return { ...ok(null), json: () => { started.resolve(); return pending.promise; } };
      started.resolve(); return pending.promise;
    });
    const first = h.submit(id); await started.promise;
    const oldRequest = h.requests.at(-1); assert.equal(oldRequest.url, registrationPath(id));
    await h.submit(second); assert.equal(oldRequest.options.signal.aborted, true);
    const freshText = registrationRendered(h.nodes);
    assert.match(freshText, new RegExp(second)); assert.doesNotMatch(freshText, /reportedInfobaseId/);
    pending.resolve(late); await first;
    assert.equal(registrationRendered(h.nodes), freshText);
    assert.equal(h.nodes.get('registration-evidence').hidden, false);
    assert.match(rendered(h.nodes), new RegExp(second));
    assert.equal(h.nodes.get('session-link').hidden, true);
  }
});

test('editing, page transitions and hidden tabs clear registration evidence and invalidate pending JSON', { timeout: 2000 }, async () => {
  for (const trigger of [h => h.nodes.get('operation-id').listeners.input(), h => h.events.pagehide(), h => h.events.pageshow(), h => { h.document.hidden = true; h.documentEvents.visibilitychange(); }]) {
    const body = deferred(), started = deferred(); let pending = false;
    const h = harness(async () => ok(receipt()), undefined, undefined, async () => !pending ? ok(registration())
      : { ...ok(null), json: () => { started.resolve(); return body.promise; } });
    await h.submit(id); assert.match(registrationRendered(h.nodes), /reportedInfobaseId/);
    pending = true; const request = h.submit(id); await started.promise; trigger(h);
    assert.equal(h.requests.at(-1).options.signal.aborted, true);
    assert.equal(h.nodes.get('registration-evidence').hidden, true); assert.equal(registrationRendered(h.nodes), '');
    body.resolve(registration()); await request;
    assert.equal(h.nodes.get('registration-evidence').hidden, true); assert.equal(registrationRendered(h.nodes), '');
    assert.equal(h.nodes.get('registration-message').textContent, '');
  }
});

test('resources enforce administrator authorization and same Stage CSP without inline content or mutation controls', () => {
  const mapper = fs.readFileSync(path.join(root, 'TargetCreationDiagnosticsUi.cs'), 'utf8');
  const html = fs.readFileSync(path.join(root, 'wwwroot/creation-diagnostics/index.html'), 'utf8');
  assert.match(mapper, /RequireAuthorization\(policy => policy.RequireRole\("Administrator"\)\)/);
  assert.doesNotMatch(mapper, /AllowAnonymous/); assert.match(mapper, /default-src 'none'; script-src 'self'; style-src 'self'/);
  assert.doesNotMatch(html, /<style|\sstyle=|\son\w+=/i); assert.match(html, /<script src="\/admin\/creation-diagnostics\/app.js" defer><\/script>/);
  assert.match(html, /href="\/stage"/); assert.match(html, /История аудита/); assert.match(html, /физической/);
  const registrationPanel = html.match(/<section id="registration-evidence" hidden aria-labelledby="registration-heading">([\s\S]*?)<\/section>/);
  assert.ok(registrationPanel, 'registration evidence is a separate, initially hidden panel');
  assert.match(registrationPanel[1], /id="registration-message" role="status" aria-live="polite"/);
  assert.match(registrationPanel[1], /сопоставления конечных точек RAC и SQL не подтверждены/);
  assert.match(registrationPanel[1], /не устанавливают право владения и не разрешают выполнение, восстановление или повторную попытку/);
  assert.match(registrationPanel[1], /независимая проверка; запланированные задания остаются заблокированы/);
  assert.equal((html.match(/<button/g) || []).length, 1); assert.doesNotMatch(source, /innerHTML|insertAdjacentHTML|method:\s*['"](?:POST|PUT|DELETE|PATCH)/);
});
