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
  const document = { getElementById(id) { if (!nodes.has(id)) nodes.set(id, new Element()); return nodes.get(id); }, createElement: tag => new Element(tag), createDocumentFragment: () => new Element('fragment'), addEventListener(name, handler) { nodes.set('document:' + name, handler); } };
  vm.runInNewContext(source, { document, fetch, AbortController, URLSearchParams, Intl, Date, console, crypto:require('node:crypto').webcrypto, addEventListener(name, handler) { nodes.set(name, handler); } });
  return nodes;
}
const ok = body => ({ ok: true, status: 200, json: async () => body });
const denied = status => ({ ok: false, status });
const flush = async () => { for (let i = 0; i < 10; i++) await new Promise(resolve => setImmediate(resolve)); };
const deferred = () => { let resolve; const promise = new Promise(r => { resolve = r; }); return { promise, resolve }; };

test('retention completed tuple explains retained quota without format or current file proof', async()=>{const {nodes}=await retentionHarness(retainedDetail());const t=retentionText(nodes);assert.equal(nodes.get('detail-retention').hidden,false);assert.match(t,/Копирование/);assert.match(t,/не подтверждает формат/);assert.match(t,/наличие файла сейчас/);assert.match(t,/provisioning в целом не завершён/);assert.match(t,/Квота не освобождена/);assert.equal(nodes.get('detail-values').children[0].children.length,30);});
test('retention awaiting stage does not imply release or agent absence',async()=>{const {nodes}=await retentionHarness(retainedDetail({jobState:'Pending',leaseState:null,leaseExpiresAt:null,artifactId:null,artifactState:null,reconciliationCode:'AwaitingStage'}));const t=retentionText(nodes);assert.match(t,/требуется проверка/);assert.match(t,/Квота не освобождена/);assert.doesNotMatch(t,/Лизинг stage завершён/);});
test('retention expired lease is not order TTL or resource deletion',async()=>{const {nodes}=await retentionHarness(retainedDetail({jobState:'Leased',leaseState:'Active',reconciliationCode:'LeaseExpired'}));const t=retentionText(nodes);assert.match(t,/TTL заказа не доказывает/);assert.match(t,/Автоочистки нет/);assert.match(t,/срок lease не является TTL/);});
test('stage lease comparison preserves SQL fractional precision and timezone offsets', async () => {
  const observedAt = '2026-11-24T09:00:00.1234567Z';
  for (const [expires, code, coherent] of [
    ['11:00:00.1234568+02:00','ArtifactReported',true],
    ['11:00:00.1234568+02:00','LeaseExpired',false],
    ['11:00:00.1234567+02:00','LeaseExpired',true],
    ['11:00:00.1234566+02:00','ArtifactReported',false],
    ['11:00:00.1234566+02:00','LeaseExpired',true]
  ]) {
    const {nodes,calls} = await retentionHarness(retainedDetail({ jobState:'Leased', leaseState:'Active',
      observedAt, leaseExpiresAt:'2026-11-24T' + expires, reconciliationCode:code }));
    assert.equal(calls.some(c => c.route.endsWith('/cleanup-status')), coherent, `${code} ${expires}`);
    assert.equal(retentionText(nodes).includes('Квота не освобождена'), coherent, `${code} ${expires}`);
  }
});
test('retention malformed conflicting and unknown tuples fall back without completion claims',async()=>{for(const overrides of [{unsettled:false},{artifactId:null},{stagingBytes:'65536'},{leaseState:'Active'},{reconciliationCode:'<script>'},{requestState:undefined}]){const {nodes}=await retentionHarness(retainedDetail(overrides));assert.equal(retentionText(nodes),'Недостаточно согласованных метаданных: не считать ресурсы свободными.');}});
test('retention reads stage and cleanup detail endpoints and adds no actions or POST',async()=>{const {nodes,calls}=await retentionHarness(retainedDetail());assert.deepEqual(calls.map(c=>c.route),['/api/auth/me','/api/stage-operations?limit=25',`/api/test-environments/${env1}/stage-status`,`/api/test-environments/${env1}/cleanup-status`]);assert.equal(calls.some(c=>c.options?.method==='POST'),false);assert.equal(nodes.get('detail-retention-reasons').querySelectorAll('button').length,0);nodes.get('close-detail').listeners.click();assert.equal(nodes.get('detail-retention').hidden,true);assert.equal(retentionText(nodes),'');});
test('retention stale detail after logout cannot restore explanation',async()=>{const pending=deferred();const {nodes}=await retentionHarness(pending);await nodes.get('logout').listeners.click();pending.resolve(ok(retainedDetail()));await flush();assert.equal(nodes.get('detail-retention').hidden,true);assert.equal(retentionText(nodes),'');assert.equal(nodes.get('detail-panel').hidden,true);});

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
const env1 = '11111111-1111-1111-1111-111111111111';
const env2 = '22222222-2222-2222-2222-222222222222';
const item = environmentId => ({ environmentId, requestId: env2, jobId: env1, jobState: 'Leased', environmentState: 'Requested', reservationState: 'Held', stagingBytes: 10, createdAt: '2026-01-01T00:00:00Z' });
const buttons = nodes => nodes.get('operations-body').querySelectorAll('button');
const retainedDetail = overrides => ({ environmentId:env1, requestId:env2, jobId:env1, jobState:'StageCompleted', environmentState:'Requested', requestState:'Pending', reservationState:'Held', stagingBytes:65536, unsettled:true, artifactId:env2, artifactState:'ContentVerified', leaseState:'Completed', leaseExpiresAt:'2026-01-01T00:00:00Z', reconciliationCode:'StageCompleted', observedAt:'2026-01-01T00:01:00Z', restoreIntent:null, ...overrides });
async function retentionHarness(detail) {
  const calls=[]; const nodes=harness(async (route,options) => { calls.push({route,options}); if(route==='/api/auth/me') return ok({name:'operator'}); if(route.startsWith('/api/stage-operations')) return ok({items:[item(env1)],nextCursor:null}); if(route.endsWith('/cleanup-status')) return denied(404); if(route.endsWith('/stage-status')) return detail?.promise || ok(detail); if(route==='/api/auth/csrf') return ok({token:'test'}); if(route==='/api/auth/logout') return ok(null); throw new Error('Unexpected route'); });
  await flush(); buttons(nodes)[0].listeners.click(); await flush(); return {nodes,calls};
}
const retentionText = nodes => nodes.get('detail-retention-reasons').querySelectorAll('li').map(x=>x.textContent).join('\n');

test('filter clears cursor history and late prior-filter response is ignored', async () => {
  const calls = [], previous = deferred();
  const nodes = harness(async (route, options) => {
    calls.push({ route, options });
    if (route === '/api/auth/me') return ok({ name: 'operator' });
    if (route.includes('cursor=page-one')) return previous.promise;
    if (route.startsWith('/api/stage-operations')) return ok({ items: [item(env1)], nextCursor: route.includes('state=NeedsAttention') ? null : 'page-one' });
    throw new Error('Unexpected route');
  });
  await flush();
  nodes.get('next').listeners.click(); await flush();
  assert.equal(calls.some(c => c.route.includes('cursor=page-one')), true);
  nodes.get('state-filter').value = 'NeedsAttention';
  nodes.get('state-filter').listeners.change(); await flush();
  previous.resolve(ok({ items: [item(env2)], nextCursor: 'old-cursor' })); await flush();
  assert.equal(nodes.get('page-number').textContent, 'Страница 1');
  assert.equal(nodes.get('next').disabled, true);
  assert.equal(buttons(nodes).length, 1);
  assert.equal(buttons(nodes)[0].dataset.environment, env1);
  assert.equal(calls.at(-1).route, '/api/stage-operations?limit=25&state=NeedsAttention');
  assert.equal(calls.find(c => c.route.includes('cursor=page-one')).options.signal.aborted, true);
});

test('cancelled filter shows only confirmed cancellation and labels historical staging', async () => {
  const calls = [];
  const cancelled = { ...item(env1), requestId: env2, jobState: 'Cancelled', environmentState: 'Cancelled',
    reservationState: 'Released', unsettled: false, cancellationCode: 'CancelledBeforeFirstLease' };
  const nodes = harness(async (route, options) => {
    calls.push({ route, options });
    if (route === '/api/auth/me') return ok({ name: 'operator' });
    if (route.includes('state=Cancelled')) return ok({ items: [cancelled], nextCursor: null });
    if (route.endsWith('/cleanup-status')) return denied(404);
    if (route.endsWith('/stage-status')) return ok({ ...cancelled, requestId: env1 });
    return ok({ items: [], nextCursor: 'prior-page' });
  });
  await flush();
  nodes.get('state-filter').value = 'Cancelled';
  nodes.get('state-filter').listeners.change(); await flush();
  assert.equal(calls.at(-1).route, '/api/stage-operations?limit=25&state=Cancelled');
  const row = nodes.get('operations-body').querySelectorAll('tr')[0];
  assert.equal(row.children[1].children[0].textContent, 'Отменено до первой выдачи');
  assert.equal(row.children[2].textContent, 'Логический резерв освобождён');
  assert.match(row.children[2].children[0].textContent, /^Исторический staging-резерв:/);
  buttons(nodes)[0].listeners.click(); await flush();
  assert.equal(nodes.get('detail-values').children.length, 0);
  assert.equal(calls.some(c => c.options.method === 'POST'), false);
});

test('cancelled row reads stage only and requires matching cancellation and historical bytes', async () => {
  const row = { ...item(env1), requestId: env2, jobState: 'Cancelled', environmentState: 'Cancelled',
    reservationState: 'Released', unsettled: false, cancellationCode: 'CancelledBeforeFirstLease' };
  for (const [detail, accepted] of [[cancelledDetail({ stagingBytes: 10 }), true],
    [cancelledDetail({ stagingBytes: 20 }), false], [retainedDetail({ stagingBytes: 10 }), false], [null, false]]) {
    const calls = [];
    const nodes = harness(async route => {
      calls.push(route);
      if (route === '/api/auth/me') return ok({ name: 'operator' });
      if (route.startsWith('/api/stage-operations')) return ok({ items: [row], nextCursor: null });
      if (route.endsWith('/stage-status')) return detail ? ok(detail) : denied(409);
      throw new Error('Unexpected route');
    });
    await flush(); buttons(nodes)[0].listeners.click(); await flush();
    assert.ok(calls.some(route => route.endsWith('/stage-status')));
    assert.equal(calls.some(route => route.endsWith('/cleanup-status')), false);
    if (accepted) assertCancellation(nodes);
    else {
      assert.equal(nodes.get('detail-values').children.length, 0);
      assert.match(nodes.get('detail-message').textContent, /состояние неизвестно/);
      assert.doesNotMatch(nodes.get('cleanup-message').textContent, /Cleanup не применим/);
    }
  }
});

test('list refuses unconfirmed cancellation and contradictory marker without displaying quota', async () => {
  const raw = { ...item(env1), requestId: env2, jobState: 'Cancelled', environmentState: 'Cancelled',
    reservationState: 'Released', unsettled: false };
  for (const row of [raw, { ...raw, cancellationCode: 'CancelledBeforeFirstLease', unsettled: true },
    { ...item(env1), cancellationCode: 'CancelledBeforeFirstLease' },
    { ...item(env1), jobState: 'cAnCeLlEd ' }, { ...item(env1), reservationState: 'rELEAsed  ' }]) {
    const nodes = harness(async route => route === '/api/auth/me' ? ok({ name: 'operator' }) :
      ok({ items: [row], nextCursor: 'bad-page' }));
    await flush();
    assert.equal(nodes.get('operations-body').querySelectorAll('tr').length, 0);
    assert.equal(nodes.get('next').disabled, true);
  }
});

test('next and previous use opaque cursor and do not invent offset or query values', async () => {
  const calls = [];
  const nodes = harness(async route => {
    calls.push(route);
    if (route === '/api/auth/me') return ok({ name: 'operator' });
    if (route.startsWith('/api/stage-operations')) return ok({ items: [], nextCursor: route.includes('cursor=opaque%2Bpage') ? null : 'opaque+page' });
    throw new Error('Unexpected route');
  });
  await flush();
  nodes.get('next').listeners.click(); await flush();
  assert.equal(nodes.get('page-number').textContent, 'Страница 2');
  assert.equal(calls.at(-1), '/api/stage-operations?limit=25&cursor=opaque%2Bpage');
  assert.equal(nodes.get('next').disabled, true);
  nodes.get('previous').listeners.click(); await flush();
  assert.equal(nodes.get('page-number').textContent, 'Страница 1');
  assert.equal(calls.at(-1), '/api/stage-operations?limit=25');
});

test('detail renders all 15 metadata fields as text, with no mutation request', async () => {
  const calls = [];
  const nodes = harness(async route => {
    calls.push(route);
    if (route === '/api/auth/me') return ok({ name: 'operator' });
    if (route.startsWith('/api/stage-operations')) return ok({ items: [item(env1)], nextCursor: null });
    if (route.endsWith('/cleanup-status')) return denied(404);
    if (route === `/api/test-environments/${env1}/stage-status`) return ok({
      environmentId:env1, requestId:env2, jobId:env1, jobState:'StageCompleted', environmentState:'Requested', requestState:'Pending', reservationState:'Held',
      stagingBytes:10, unsettled:true, artifactId:env2, artifactState:'ContentVerified', leaseState:'Completed', leaseExpiresAt:'2026-01-01T00:00:00Z',
      reconciliationCode:'StageCompleted', observedAt:'2026-01-01T00:00:00Z', restoreIntent:null, dangerous:'<img src=x onerror=alert(1)>'
    });
    throw new Error('Unexpected route');
  });
  await flush(); assert.equal(buttons(nodes).length, 1);
  buttons(nodes)[0].listeners.click(); await flush();
  assert.equal(nodes.get('detail-panel').hidden, false);
  assert.equal(nodes.get('detail-values').children[0].children.length, 30);
  assert.equal(nodes.get('detail-values').children[0].children.some(x => x.textContent?.includes('dangerous')), false);
  assert.equal(nodes.get('detail-message').textContent.includes('не подтверждаются'), true);
  assert.deepEqual(calls, ['/api/auth/me','/api/stage-operations?limit=25',`/api/test-environments/${env1}/stage-status`,`/api/test-environments/${env1}/cleanup-status`]);
});

test('late detail is discarded after filter change and does not reopen panel', async () => {
  const pending = deferred(); const calls = [];
  const nodes = harness(async (route, options) => {
    calls.push({ route, options });
    if (route === '/api/auth/me') return ok({ name:'operator' });
    if (route.startsWith('/api/stage-operations')) return ok({ items: [item(env1)], nextCursor:null });
    if (route.endsWith('/cleanup-status')) return denied(404);
    if (route.includes('/stage-status')) return pending.promise;
    throw new Error('Unexpected route');
  });
  await flush(); buttons(nodes)[0].listeners.click(); await flush();
  nodes.get('state-filter').value = 'Pending'; nodes.get('state-filter').listeners.change(); await flush();
  pending.resolve(ok(retainedDetail())); await flush();
  assert.equal(calls.some(call => call.route.endsWith('/cleanup-status')), false);
  assert.equal(nodes.get('detail-panel').hidden, true);
  assert.equal(nodes.get('detail-values').children.length, 0);
  assert.equal(calls.find(c => c.route.includes('/stage-status')).options.signal.aborted, true);
});

test('401 from list clears metadata and returns to login', async () => {
  let lists = 0;
  const nodes = harness(async route => {
    if (route === '/api/auth/me') return ok({ name:'operator' });
    if (route.startsWith('/api/stage-operations')) return ++lists === 1 ? ok({ items:[item(env1)], nextCursor:null }) : denied(401);
    throw new Error('Unexpected route');
  });
  await flush(); assert.equal(buttons(nodes).length, 1);
  nodes.get('refresh').listeners.click(); await flush();
  assert.equal(nodes.get('workspace').hidden, true);
  assert.equal(nodes.get('login-panel').hidden, false);
  assert.equal(nodes.get('operations-body').children.length, 0);
});
test('archive browser lazily loads authorized projects only after explicit action', async () => {
  const calls = [];
  const nodes = harness(async route => {
    calls.push(route);
    if (route === '/api/auth/me') return ok({name:'operator'});
    if (route.startsWith('/api/stage-operations')) return ok({items:[],nextCursor:null});
    if (route === '/api/projects/browse?limit=25') return ok({items:[{projectId:env1,name:'Проект 1'}],nextCursor:null});
    throw new Error('Unexpected route');
  });
  await flush(); assert.equal(calls.length,2); assert.equal(nodes.get('archive-browser').hidden,true);
  nodes.get('browse-archives').listeners.click(); await flush();
  assert.equal(calls.at(-1),'/api/projects/browse?limit=25');
  assert.equal(nodes.get('archive-browser').hidden,false);
  assert.equal(nodes.get('projects-select').querySelectorAll('option')[1].value,env1);
  assert.equal(nodes.get('load-archives').disabled,true);
});

test('selected project GUID routes read-only archive requests and clears on project switch', async () => {
  const calls = [];
  const nodes = harness(async route => {
    calls.push(route);
    if (route === '/api/auth/me') return ok({name:'operator'});
    if (route.startsWith('/api/stage-operations')) return ok({items:[],nextCursor:null});
    if (route === '/api/projects/browse?limit=25') return ok({items:[{projectId:env1,name:'Первый'},{projectId:env2,name:'Второй'}],nextCursor:null});
    if (route === `/api/projects/${env1}/archive-versions?limit=25`) return ok({items:[{archiveVersionId:env2,format:'dt',sizeBytes:10,estimatedDbBytes:100}],nextCursor:null});
    throw new Error('Unexpected route');
  });
  await flush(); nodes.get('browse-archives').listeners.click(); await flush();
  nodes.get('projects-select').value=env1; nodes.get('projects-select').listeners.change();
  nodes.get('load-archives').listeners.click(); await flush();
  assert.equal(calls.at(-1),`/api/projects/${env1}/archive-versions?limit=25`);
  assert.equal(nodes.get('catalog-body').querySelectorAll('td')[0].textContent,env2);
  nodes.get('projects-select').value=env2; nodes.get('projects-select').listeners.change();
  assert.equal(nodes.get('catalog-body').children.length,0);
  nodes.get('projects-select').value='../foreign'; nodes.get('projects-select').listeners.change();
  nodes.get('load-archives').listeners.click(); await flush();
  assert.equal(nodes.get('load-archives').disabled,true);
  assert.equal(calls.length,4);
});

test('late archive response after logout cannot reveal catalog or selection', async () => {
  const pending=deferred(); let catalogSignal;
  const nodes=harness(async (route,options) => {
    if(route==='/api/auth/me')return ok({name:'operator'});
    if(route.startsWith('/api/stage-operations'))return ok({items:[],nextCursor:null});
    if(route==='/api/projects/browse?limit=25')return ok({items:[{projectId:env1,name:'Первый'}],nextCursor:null});
    if(route.includes('/archive-versions')){catalogSignal=options.signal;return pending.promise;}
    if(route==='/api/auth/csrf')return ok({token:'test-csrf'});
    if(route==='/api/auth/logout')return ok(null);
    throw new Error('Unexpected route');
  });
  await flush(); nodes.get('browse-archives').listeners.click(); await flush();
  nodes.get('projects-select').value=env1; nodes.get('projects-select').listeners.change(); nodes.get('load-archives').listeners.click(); await flush();
  await nodes.get('logout').listeners.click();
  pending.resolve(ok({items:[{archiveVersionId:env2,format:'bak',sizeBytes:10,estimatedDbBytes:20}],nextCursor:null}));await flush();
  assert.equal(catalogSignal.aborted,true);
  assert.equal(nodes.get('archive-browser').hidden,true);
  assert.equal(nodes.get('catalog-body').children.length,0);
  assert.equal(nodes.get('projects-select').value,'');
});

test('hostile project names remain literal truncated option text', async () => {
  const name='<img src=x onerror=alert(1)>'+ 'x'.repeat(300);
  const nodes=harness(async route => {
    if(route==='/api/auth/me')return ok({name:'operator'});
    if(route.startsWith('/api/stage-operations'))return ok({items:[],nextCursor:null});
    if(route==='/api/projects/browse?limit=25')return ok({items:[{projectId:env1,name}],nextCursor:null});
    throw new Error('Unexpected route');
  });
  await flush();nodes.get('browse-archives').listeners.click();await flush();
  const option=nodes.get('projects-select').querySelectorAll('option')[1];
  assert.equal(option.textContent,name.slice(0,256));assert.equal(option.children.length,0);
});

test('project and archive pagination use distinct cursor histories', async () => {
  const calls=[];
  const nodes=harness(async route => {
    calls.push(route);
    if(route==='/api/auth/me')return ok({name:'operator'});
    if(route.startsWith('/api/stage-operations'))return ok({items:[],nextCursor:null});
    if(route.startsWith('/api/projects/browse?'))return ok({items:[{projectId:route.includes('cursor=project%2B2')?env2:env1,name:'Проект'}],nextCursor:route.includes('cursor=')?null:'project+2'});
    if(route.includes('/archive-versions?'))return ok({items:[],nextCursor:route.includes('cursor=')?null:'archive+2'});
    throw new Error('Unexpected route');
  });
  await flush();nodes.get('browse-archives').listeners.click();await flush();
  nodes.get('projects-select').value=env1;nodes.get('projects-select').listeners.change();nodes.get('load-archives').listeners.click();await flush();
  nodes.get('catalog-next').listeners.click();await flush();
  assert.equal(calls.at(-1),`/api/projects/${env1}/archive-versions?limit=25&cursor=archive%2B2`);
  nodes.get('catalog-previous').listeners.click();await flush();
  assert.equal(calls.at(-1),`/api/projects/${env1}/archive-versions?limit=25`);
  nodes.get('projects-next').listeners.click();await flush();
  assert.equal(calls.at(-1),'/api/projects/browse?limit=25&cursor=project%2B2');
  assert.equal(nodes.get('projects-select').value,'');assert.equal(nodes.get('catalog-page').textContent,'Страница версий 1');
  nodes.get('projects-previous').listeners.click();await flush();assert.equal(calls.at(-1),'/api/projects/browse?limit=25');
});

test('late projects response after stage filter change cannot restore choices', async () => {
  const pending=deferred();let signal;
  const nodes=harness(async (route,options) => {
    if(route==='/api/auth/me')return ok({name:'operator'});
    if(route.startsWith('/api/stage-operations'))return ok({items:[],nextCursor:null});
    if(route.startsWith('/api/projects/browse?')){signal=options.signal;return pending.promise;}
    throw new Error('Unexpected route');
  });
  await flush();nodes.get('browse-archives').listeners.click();await flush();
  nodes.get('state-filter').value='Pending';nodes.get('state-filter').listeners.change();await flush();
  pending.resolve(ok({items:[{projectId:env1,name:'Старый проект'}],nextCursor:null}));await flush();
  assert.equal(signal.aborted,true);assert.equal(nodes.get('archive-browser').hidden,true);
  assert.equal(nodes.get('projects-select').children.length,0);assert.equal(nodes.get('load-archives').disabled,true);
});
async function orderHarness(post, logout = async()=>ok(null)) {
  const calls=[];
  const nodes=harness(async (route,options) => {
    calls.push({route,options});
    if(route==='/api/auth/me') return ok({name:'operator'});
    if(route.startsWith('/api/stage-operations')) return ok({items:[],nextCursor:null});
    if(route.startsWith('/api/projects/browse')) return ok({items:[{projectId:env1,name:'Project'}],nextCursor:null});
    if(route.includes('/archive-versions')) return ok({items:[{archiveVersionId:env2,format:'dt',sizeBytes:10,estimatedDbBytes:20}],nextCursor:null});
    if(route==='/api/auth/csrf') return ok({token:'fresh-'+calls.length});
    if(route==='/api/auth/logout') return logout();
    if(route==='/api/environment-requests') return post(options);
    throw new Error('Unexpected route '+route);
  });
  await flush(); nodes.get('browse-archives').listeners.click(); await flush();
  nodes.get('projects-select').value=env1; nodes.get('projects-select').listeners.change();
  nodes.get('load-archives').listeners.click(); await flush();
  nodes.get('catalog-body').querySelectorAll('button')[0].listeners.click();
  nodes.get('order-name').value='  My stage  ';nodes.get('order-ttl').value='7';
  return {nodes,calls,submit(){nodes.get('order-form').listeners.submit({preventDefault(){}});}};
}
const accepted=()=>({status:202,json:async()=>({environmentId:env1,requestId:env2,jobId:env1})});
test('order sends exact immutable payload UUID key and fresh CSRF only on submit',async()=>{
 const h=await orderHarness(async()=>accepted());assert.equal(h.calls.some(c=>c.route==='/api/environment-requests'),false);
 h.submit();await flush();const p=h.calls.find(c=>c.route==='/api/environment-requests');
 assert.deepEqual(JSON.parse(p.options.body),{projectId:env1,displayName:'My stage',mode:'archive',source:{archiveVersionId:env2},ttlDays:7});
 assert.match(p.options.headers['Idempotency-Key'],/^[0-9a-f-]{36}$/);assert.match(p.options.headers['X-CSRF-TOKEN'],/^fresh-/);assert.equal(p.options.credentials,'same-origin');assert.equal(h.nodes.get('order-new').hidden,false);
});
test('unknown network outcome retries identical payload and key with fresh CSRF',async()=>{
 let n=0;const h=await orderHarness(async()=>{if(++n===1)throw Error('network');return accepted();});h.submit();await flush();
 h.nodes.get('order-name').value='tampered';h.nodes.get('order-retry').listeners.click();await flush();
 const p=h.calls.filter(c=>c.route==='/api/environment-requests');assert.equal(p.length,2);assert.equal(p[0].options.body,p[1].options.body);assert.equal(p[0].options.headers['Idempotency-Key'],p[1].options.headers['Idempotency-Key']);assert.notEqual(p[0].options.headers['X-CSRF-TOKEN'],p[1].options.headers['X-CSRF-TOKEN']);
});
test('malformed 202 receipt keeps intent and does not allow a new order',async()=>{
 const h=await orderHarness(async()=>({status:202,json:async()=>({requestId:env1})}));h.submit();await flush();assert.equal(h.nodes.get('order-new').hidden,true);assert.equal(h.nodes.get('order-retry').hidden,false);assert.equal(h.nodes.get('order-name').disabled,true);
});
test('duplicate submit and retry clicks while sending create only one POST',async()=>{
 const pending=deferred();const h=await orderHarness(()=>pending.promise);h.submit();h.submit();await flush();h.nodes.get('order-retry').listeners.click();await flush();assert.equal(h.calls.filter(c=>c.route==='/api/environment-requests').length,1);pending.resolve(accepted());await flush();
});
test('late POST after logout cannot display receipt and warning survives logout',async()=>{
 const pending=deferred();const h=await orderHarness(()=>pending.promise);h.submit();await flush();await h.nodes.get('logout').listeners.click();pending.resolve(accepted());await flush();assert.equal(h.nodes.get('order-receipt').children.length,0);assert.equal(h.nodes.get('workspace').hidden,true);assert.match(h.nodes.get('message').textContent,/мог быть принят/);
});
test('invalid order input never obtains CSRF or sends POST',async()=>{
 const h=await orderHarness(async()=>accepted());for(const [name,ttl] of [['','1'],['bad\u0001name','1'],['x','0'],['x','1.5'],['x','36501']]){h.nodes.get('order-name').value=name;h.nodes.get('order-ttl').value=ttl;h.submit();await flush();}assert.equal(h.calls.some(c=>c.route==='/api/auth/csrf'),false);assert.equal(h.calls.some(c=>c.route==='/api/environment-requests'),false);
});
test('pending intent blocks project/filter mutations and beforeunload warns',async()=>{
 const h=await orderHarness(async()=>({status:409}));h.submit();await flush();const before=h.calls.length;
 h.nodes.get('projects-select').value=env2;h.nodes.get('projects-select').listeners.change();h.nodes.get('refresh').listeners.click();h.nodes.get('state-filter').listeners.change();await flush();assert.equal(h.calls.length,before);assert.equal(h.nodes.get('projects-select').value,env1);let prevented=false;h.nodes.get('beforeunload')({preventDefault(){prevented=true;}});assert.equal(prevented,true);assert.equal(h.nodes.get('order-retry').hidden,false);
});
test('session loss clears local intent rather than retrying under another identity',async()=>{
 const h=await orderHarness(async()=>({status:401}));h.submit();await flush();h.nodes.get('order-retry').listeners.click();await flush();assert.equal(h.calls.filter(c=>c.route==='/api/environment-requests').length,1);assert.equal(h.nodes.get('login-panel').hidden,false);assert.match(h.nodes.get('message').textContent,/мог быть принят/);
});
test('forgotten intent warning survives failed logout and explicit logout retry',async()=>{
 const h=await orderHarness(async()=>({status:503}),async()=>{throw Error('network');});h.submit();await flush();await h.nodes.get('logout').listeners.click();assert.match(h.nodes.get('message').textContent,/мог быть принят/);await h.nodes.get('logout').listeners.click();assert.match(h.nodes.get('message').textContent,/мог быть принят/);assert.equal(h.nodes.get('order-panel').hidden,true);
});
// end


const cleanupDetail = overrides => ({ environmentId:env1, jobId:env1, artifactId:env2, retirementId:env1,
  authorizationId:env2, resultId:env1, releaseId:env2, cleanupState:'StagingReleased', reservedStagingBytes:65536,
  heldStagingBytes:0, releasedStagingBytes:65536, observedAt:'2026-01-01T00:02:00Z', restoreIntentHold:false, ...overrides });
const restoreStatus = overrides => ({restoreIntentId:env1,targetIncarnation:env2,requestedAt:'2026-01-01T00:00:00Z',restoreIntentCode:'AwaitingTrustedTarget',executable:false,artifactRetained:true,...overrides});
test('restore hold is separate from stage history and never execution or target ownership',async()=>{
  const {nodes,calls}=await retentionHarness(retainedDetail({restoreIntent:restoreStatus()}));
  const text=nodes.get('detail-values').querySelectorAll('dd').map(x=>x.textContent).join('\n');
  assert.match(text,/Stage завершён/);assert.match(text,/AwaitingTrustedTarget/);assert.match(text,/retirement запрещён/);
  assert.match(text,/не владение целью/);assert.match(text,/Автоматического продолжения/);assert.match(text,/Выполнение|выполнение/);
  assert.equal(calls.some(x=>x.options?.method==='POST'),false);assert.equal(nodes.get('detail-values').querySelectorAll('button').length,0);
});
test('restore status rejects executable claims and malicious identifiers without rendering raw fields',async()=>{
  for(const value of [restoreStatus({executable:true}),restoreStatus({artifactRetained:false}),restoreStatus({restoreIntentId:'<img onerror=bad>'}),restoreStatus({restoreIntentCode:'<script>'})]){
    const {nodes}=await retentionHarness(retainedDetail({restoreIntent:value}));
    assert.equal(nodes.get('detail-values').querySelectorAll('dd').length,0);
    assert.doesNotMatch(nodes.get('detail-message').textContent,/<img|<script>/);
  }
});
test('restore status allowlists fields and ignores paths snapshots and commands',async()=>{
  const hostile='<img onerror=bad>';const {nodes}=await retentionHarness(retainedDetail({restoreIntent:restoreStatus({sourcePath:hostile,snapshot:hostile,command:hostile})}));
  assert.doesNotMatch(nodes.get('detail-values').querySelectorAll('dd').map(x=>x.textContent).join('\n'),/<img|sourcePath|snapshot/);
});
test('cleanup hold prevents apparent eligibility without adding actions',async()=>{
  const {nodes,calls}=await cleanupHarness(()=>ok(cleanupDetail({cleanupState:'None',retirementId:null,authorizationId:null,resultId:null,releaseId:null,heldStagingBytes:65536,releasedStagingBytes:0,restoreIntentHold:true})), () => ok(retainedDetail({restoreIntent:restoreStatus()})));
  assert.match(cleanupText(nodes),/retirement запрещён/);assert.match(cleanupText(nodes),/Постоянное удержание/);
  assert.equal(calls.some(x=>x.options?.method==='POST'),false);assert.equal(nodes.get('cleanup-values').querySelectorAll('button').length,0);
});
const cleanupText = nodes => nodes.get('cleanup-values').querySelectorAll('dd').map(x => x.textContent).join('\n');
async function cleanupHarness(respond, stage = route => ok(retainedDetail({environmentId:route.includes(env2)?env2:env1}))) {
  const calls = [];
  const nodes = harness(async (route, options) => {
    calls.push({route,options});
    if (route === '/api/auth/me') return ok({name:'operator'});
    if (route.startsWith('/api/stage-operations')) return ok({items:[item(env1),item(env2)],nextCursor:null});
    if (route.endsWith('/cleanup-status')) return respond(route,options);
    if (route.endsWith('/stage-status')) return stage(route,options);
    if (route === '/api/auth/csrf') return ok({token:'test'});
    if (route === '/api/auth/logout') return ok(null);
    throw new Error('Unexpected route');
  });
  await flush(); buttons(nodes)[0].listeners.click(); await flush(); return {nodes,calls};
}
const cancelledDetail = changes => retainedDetail({ jobState:'Cancelled', environmentState:'Cancelled', requestState:'Cancelled',
  reservationState:'Released', unsettled:false, artifactId:null, artifactState:null, leaseState:null, leaseExpiresAt:null,
  restoreIntent:null, reconciliationCode:'CancelledBeforeFirstLease', observedAt:'2026-01-01T00:01:00.1234567+00:00', ...changes });
const assertCancellation = nodes => {
  assert.match(nodes.get('detail-message').textContent, /квота заказа \(окружение, БД и staging\) освобождена/);
  assert.match(nodes.get('detail-message').textContent, /не физическое свободное место.*не Ready.*не подтверждение удаления файлов/);
  const fields = nodes.get('detail-values').querySelectorAll('dd').map(x => x.textContent);
  assert.equal(fields.filter(x => x === 'Отменено').length, 3);
  assert.ok(fields.includes('Отменено до первой выдачи агенту (lease)'));
  assert.equal(nodes.get('detail-retention').hidden, true);
  assert.equal(cleanupText(nodes), '');
  assert.match(nodes.get('cleanup-message').textContent, /Cleanup не применим/);
};

test('coherent cancellation skips cleanup entirely and adds no writes or controls', async () => {
  const stage = deferred();
  const { nodes, calls } = await cleanupHarness(() => ok(cleanupDetail()), () => stage.promise);
  assert.equal(cleanupText(nodes), '');
  assert.equal(calls.at(-1).route, `/api/test-environments/${env1}/stage-status`);
  stage.resolve(ok(cancelledDetail())); await flush();
  assertCancellation(nodes);
  assert.equal(calls.some(call => call.route.endsWith('/cleanup-status')), false);
  assert.equal(nodes.get('detail-values').querySelectorAll('button').length, 0);
  assert.equal(nodes.get('cleanup-values').querySelectorAll('button').length, 0);
  assert.ok(calls.every(call => (call.options.method || 'GET') === 'GET'));
});

test('incomplete or contradictory cancellation never shows released quota or not-applicable cleanup', async () => {
  for (const changes of [{ reconciliationCode:'Inconsistent' }, { jobState:'Pending' }, { environmentState:'Requested' },
    { requestState:'Pending' }, { reservationState:'Held' }, { unsettled:true }, { stagingBytes:0 }, { stagingBytes:'65536' },
    { artifactId:env2 }, { artifactState:'ContentVerified' }, { leaseState:'Active' }, { leaseExpiresAt:'2026-01-01T00:00:00Z' },
    { restoreIntent:restoreStatus() }, { restoreIntent:undefined }, { environmentId:env2 }, { requestId:'invalid' }, { jobId:null },
    { observedAt:'2026' }, { observedAt:'2026-02-30T00:01:00Z' }]) {
    const { nodes, calls } = await cleanupHarness(() => denied(404), () => ok(cancelledDetail(changes)));
    assert.equal(calls.some(call => call.route.endsWith('/cleanup-status')), false);
    assert.equal(nodes.get('detail-values').children.length, 0);
    assert.doesNotMatch(nodes.get('detail-message').textContent, /квота заказа.*освобождена/);
    assert.match(nodes.get('cleanup-message').textContent, /Состояние неизвестно/);
  }
});

test('alternate-case or padded cancellation metadata is not rendered as ordinary detail', async () => {
  for (const changes of [{ requestState: 'cAnCeLlEd ' }, { reservationState: 'rELEAsed  ' }]) {
    const { nodes } = await cleanupHarness(() => denied(404), () => ok(retainedDetail(changes)));
    assert.equal(nodes.get('detail-values').children.length, 0);
    assert.match(nodes.get('detail-message').textContent, /Недостаточно согласованных метаданных/);
  }
});

test('stale cancellation cannot override a newer selection’s cleanup', async () => {
  const late = deferred();
  const { nodes } = await cleanupHarness(route => route.includes(env1) ? denied(404) : ok(cleanupDetail({environmentId:env2})),
    route => route.includes(env1) ? late.promise : ok(retainedDetail({environmentId:env2})));
  buttons(nodes)[1].listeners.click(); await flush();
  late.resolve(ok(cancelledDetail())); await flush();
  assert.match(cleanupText(nodes), /Staging allocation освобождена/);
  assert.doesNotMatch(nodes.get('detail-message').textContent, /Сервер подтвердил отмену/);
});

test('cleanup waits for a valid stage body before starting and keeps independent observation times', async () => {
  const body = deferred();
  const { nodes, calls } = await cleanupHarness(() => ok(cleanupDetail()), () => ({ ok:true, json:() => body.promise }));
  assert.equal(calls.at(-1).route, `/api/test-environments/${env1}/stage-status`);
  assert.equal(cleanupText(nodes), '');
  body.resolve(retainedDetail()); await flush();
  assert.equal(calls.at(-1).route, `/api/test-environments/${env1}/cleanup-status`);
  const stageTime = nodes.get('detail-values').querySelectorAll('dd').at(-1).textContent;
  const cleanupTime = nodes.get('cleanup-values').querySelectorAll('dd').at(-1).textContent;
  assert.notEqual(stageTime, cleanupTime);
  assert.match(cleanupText(nodes), /Staging allocation освобождена/);
});

test('failed or mismatched stage skips cleanup and preserves ordinary operations ACL handling', async () => {
  const failures = [denied(401), denied(403), denied(404), denied(409), denied(503),
    {ok:true, json:async () => { throw new Error('invalid JSON'); }},
    ok(null), ...[{environmentId:env2}, {requestId:env1}, {jobId:env2}, {stagingBytes:'65536'},
      {artifactId:null}, {observedAt:2026}, {restoreIntent:undefined}, {restoreIntent:restoreStatus({executable:true})},
      {reconciliationCode:'Inconsistent'}].map(changes => ok(retainedDetail(changes)))];
  for (const failure of failures) {
    const { nodes, calls } = await cleanupHarness(() => ok(cleanupDetail()), () => failure);
    assert.equal(calls.some(call => call.route.endsWith('/cleanup-status')), false);
    assert.equal(cleanupText(nodes), '');
    if (failure.status === 401) assert.equal(nodes.get('workspace').hidden, true);
    else {
      assert.equal(nodes.get('workspace').hidden, false);
      assert.match(nodes.get('cleanup-message').textContent, /Состояние неизвестно/);
    }
  }
});

test('cleanup rejects mismatched pins and never suppresses a valid stage observation', async () => {
  const none = {cleanupState:'None',retirementId:null,authorizationId:null,resultId:null,releaseId:null,heldStagingBytes:65536,releasedStagingBytes:0};
  const cases = [
    ...[{environmentId:env2}, {jobId:env2}, {artifactId:env1}, {...none,artifactId:null},
      {reservedStagingBytes:99,releasedStagingBytes:99}, {...none,restoreIntentHold:true}, {restoreIntentHold:undefined}, {restoreIntentHold:'false'}]
      .map(changes => [retainedDetail(), cleanupDetail(changes)]),
    [retainedDetail({restoreIntent:restoreStatus()}), cleanupDetail(none)],
    [retainedDetail({jobState:'Pending',artifactId:null,artifactState:null,leaseState:null,leaseExpiresAt:null,reconciliationCode:'AwaitingStage'}), cleanupDetail(none)]
  ];
  for (const [stage, cleanup] of cases) {
    const { nodes } = await cleanupHarness(() => ok(cleanup), () => ok(stage));
    assert.ok(nodes.get('detail-values').querySelectorAll('dd').length > 0);
    assert.equal(cleanupText(nodes), '');
    assert.match(nodes.get('cleanup-message').textContent, /Состояние неизвестно/);
  }
});

test('cleanup accounting requires stage completion, not just a matching artifact', async () => {
  const stage = retainedDetail({jobState:'Leased', leaseState:'Active', leaseExpiresAt:'2026-01-01T00:03:00Z',
    reconciliationCode:'ArtifactReported'});
  let stageReads = 0;
  const {nodes,calls} = await cleanupHarness(() => ok(cleanupDetail()), () => ok(++stageReads === 1 ? stage : retainedDetail()));
  assert.ok(calls.some(call => call.route.endsWith('/cleanup-status')));
  assert.ok(nodes.get('detail-values').querySelectorAll('dd').some(node => node.textContent === 'Квитанция содержимого записана'));
  assert.equal(cleanupText(nodes), '');
  assert.match(nodes.get('cleanup-message').textContent, /Состояние неизвестно.*Обновить сведения/);
  assert.equal(stageReads, 1);
  await nodes.get('refresh-detail').listeners.click(); await flush();
  assert.equal(stageReads, 2);
  assert.match(cleanupText(nodes), /Staging allocation освобождена/);
  assert.doesNotMatch(nodes.get('cleanup-message').textContent, /Состояние неизвестно/);
});

test('refresh re-reads the selected stage before cleanup and ignores the old cleanup body', async () => {
  const oldBody = deferred();
  let requests = 0;
  const none = cleanupDetail({cleanupState:'None',retirementId:null,authorizationId:null,resultId:null,
    releaseId:null,heldStagingBytes:65536,releasedStagingBytes:0});
  const {nodes,calls} = await cleanupHarness(() => ++requests === 1
    ? {ok:true, json:() => oldBody.promise} : ok(none));
  const refresh = nodes.get('refresh-detail');
  assert.equal(refresh.disabled, false);
  await refresh.listeners.click(); await flush();
  const detailCalls = calls.filter(call => call.route.includes('/test-environments/'));
  assert.deepEqual(detailCalls.map(call => call.route), ['stage-status','cleanup-status','stage-status','cleanup-status']
    .map(endpoint => `/api/test-environments/${env1}/${endpoint}`));
  assert.equal(detailCalls[1].options.signal.aborted, true);
  assert.equal(calls.some(call => call.options?.method === 'POST'), false);
  assert.match(cleanupText(nodes), /Нет retirement intent/);
  oldBody.resolve(cleanupDetail()); await flush();
  assert.match(cleanupText(nodes), /Нет retirement intent/);
  assert.doesNotMatch(cleanupText(nodes), /Staging allocation освобождена/);
  nodes.get('close-detail').listeners.click();
  assert.equal(refresh.disabled, true);
  const count = calls.length;
  refresh.listeners.click(); await flush();
  assert.equal(calls.length, count);
});

test('ordinary cleanup 403 keeps stage visible and 401 clears protected details', async () => {
  for (const status of [401,403]) {
    const { nodes } = await cleanupHarness(() => denied(status));
    assert.equal(cleanupText(nodes), '');
    assert.equal(nodes.get('workspace').hidden, status === 401);
    assert.equal(nodes.get('detail-values').children.length > 0, status === 403);
    if (status === 403) assert.match(nodes.get('cleanup-message').textContent, /Состояние неизвестно/);
  }
});

test('cleanup renders only allowlisted metadata as text and never offers mutation',async()=>{
  const hostile = '<img src=x onerror=bad>';
  const {nodes,calls}=await cleanupHarness(()=>ok(cleanupDetail({sourcePath:hostile,token:hostile,snapshot:hostile})));
  assert.equal(nodes.get('cleanup-values').querySelectorAll('dd').length,11);
  assert.doesNotMatch(cleanupText(nodes),/onerror|sourcePath|token|snapshot/);
  assert.equal(nodes.get('cleanup-values').querySelectorAll('button').length,0);
  const read=calls.find(x=>x.route.endsWith('/cleanup-status'));
  assert.equal(read.options.credentials,'same-origin'); assert.equal(read.options.cache,'no-store');
  assert.equal(calls.some(x=>x.options?.method==='POST'),false);
});
test('cleanup late response for old selection is discarded even when fetch ignores abort',async()=>{
  const late=deferred(); const {nodes}=await cleanupHarness(route=>route.includes(env1)?late.promise:ok(cleanupDetail({environmentId:env2,reservedStagingBytes:99,releasedStagingBytes:99})),
     route=>ok(retainedDetail(route.includes(env2)?{environmentId:env2,stagingBytes:99}:{})));
  buttons(nodes)[1].listeners.click(); await flush();
  assert.match(cleanupText(nodes),/99 байт/);
  late.resolve(ok(cleanupDetail())); await flush();
  assert.match(cleanupText(nodes),/99 байт/); assert.doesNotMatch(cleanupText(nodes),/65.?536/);
});
test('closing selection aborts cleanup request and late response cannot restore fields',async()=>{
  const late=deferred(); const {nodes,calls}=await cleanupHarness(()=>late.promise);
  nodes.get('close-detail').listeners.click();
  assert.equal(calls.find(x=>x.route.endsWith('/cleanup-status')).options.signal.aborted,true);
  late.resolve(ok(cleanupDetail())); await flush();
  assert.equal(nodes.get('cleanup-values').children.length,0); assert.equal(nodes.get('cleanup-message').textContent,'');
  assert.equal(nodes.get('detail-panel').hidden,true);
});
test('released staging shows historical and net amounts separately without Ready claim',async()=>{
  const {nodes}=await cleanupHarness(()=>ok(cleanupDetail()));
  const dt=nodes.get('cleanup-values').querySelectorAll('dt').map(x=>x.textContent);
  const dd=nodes.get('cleanup-values').querySelectorAll('dd').map(x=>x.textContent);
  assert.equal(dd[dt.indexOf('Удерживается staging сейчас')],'0 байт');
  assert.match(dd[dt.indexOf('Исторически зарезервировано staging')],/65.?536 байт/);
  assert.match(nodes.get('cleanup-message').textContent,/Это не Ready/);
  assert.match(nodes.get('cleanup-message').textContent,/квоты БД и окружения не освобождаются/);
  assert.match(retentionText(nodes),/Текущий staging-учёт см. отдельно/);
});
test('cleanup failure clears prior fields and unavailable endpoint is unknown not None',async()=>{
  for(const status of [404,503]) {
    let first=true; const {nodes}=await cleanupHarness(()=>{if(first){first=false;return ok(cleanupDetail());} return denied(status);});
    assert.notEqual(cleanupText(nodes),''); buttons(nodes)[1].listeners.click(); await flush();
    assert.equal(nodes.get('cleanup-values').children.length,0);
    assert.match(nodes.get('cleanup-message').textContent,/Состояние неизвестно/);
    assert.doesNotMatch(nodes.get('cleanup-message').textContent,/Нет retirement intent/);
  }
});
test('DeletedReported is historical agent assertion not physical file proof or quota release',async()=>{
  const {nodes}=await cleanupHarness(()=>ok(cleanupDetail({cleanupState:'DeletedReported',releaseId:null,heldStagingBytes:65536,releasedStagingBytes:0})));
  assert.match(cleanupText(nodes),/Удаление сообщено агентом/);
  assert.match(nodes.get('cleanup-message').textContent,/Исторические метаданные/);
  assert.match(nodes.get('cleanup-message').textContent,/не доказывает отсутствие файла сейчас/);
  assert.doesNotMatch(cleanupText(nodes),/Staging allocation освобождена/);
});
test('cleanup malformed totals or contradictory identity chain fail closed',async()=>{
  for(const data of [cleanupDetail({heldStagingBytes:1}),cleanupDetail({resultId:null}),cleanupDetail({cleanupState:'<script>'}),cleanupDetail({observedAt:'invalid'})]) {
    const {nodes}=await cleanupHarness(()=>ok(data));
    assert.equal(nodes.get('cleanup-values').children.length,0); assert.match(nodes.get('cleanup-message').textContent,/Состояние неизвестно/);
  }
});
test('late cleanup after logout cannot restore data or stale message',async()=>{
  const late=deferred();const {nodes,calls}=await cleanupHarness(()=>late.promise);
  await nodes.get('logout').listeners.click();late.resolve(ok(cleanupDetail()));await flush();
  assert.equal(calls.find(x=>x.route.endsWith('/cleanup-status')).options.signal.aborted,true);
  assert.equal(nodes.get('cleanup-values').children.length,0);assert.equal(nodes.get('cleanup-message').textContent,'');
  assert.equal(nodes.get('workspace').hidden,true);
});
