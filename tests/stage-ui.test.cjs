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
test('retention malformed conflicting and unknown tuples fall back without completion claims',async()=>{for(const overrides of [{unsettled:false},{artifactId:null},{stagingBytes:'65536'},{leaseState:'Active'},{reconciliationCode:'<script>'},{requestState:undefined}]){const {nodes}=await retentionHarness(retainedDetail(overrides));assert.equal(retentionText(nodes),'Недостаточно согласованных метаданных: не считать ресурсы свободными.');}});
test('retention only reads existing detail endpoint and adds no actions or POST',async()=>{const {nodes,calls}=await retentionHarness(retainedDetail());assert.deepEqual(calls.map(c=>c.route),['/api/auth/me','/api/stage-operations?limit=25',`/api/test-environments/${env1}/stage-status`]);assert.equal(calls.some(c=>c.options?.method==='POST'),false);assert.equal(nodes.get('detail-retention-reasons').querySelectorAll('button').length,0);nodes.get('close-detail').listeners.click();assert.equal(nodes.get('detail-retention').hidden,true);assert.equal(retentionText(nodes),'');});
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
const item = environmentId => ({ environmentId, jobId: env1, jobState: 'Leased', environmentState: 'Requested', reservationState: 'Held', stagingBytes: 10, createdAt: '2026-01-01T00:00:00Z' });
const buttons = nodes => nodes.get('operations-body').querySelectorAll('button');
const retainedDetail = overrides => ({ environmentId:env1, requestId:env2, jobId:env1, jobState:'StageCompleted', environmentState:'Requested', requestState:'Pending', reservationState:'Held', stagingBytes:65536, unsettled:true, artifactId:env2, artifactState:'ContentVerified', leaseState:'Completed', leaseExpiresAt:'2026-01-01T00:00:00Z', reconciliationCode:'StageCompleted', observedAt:'2026-01-01T00:01:00Z', ...overrides });
async function retentionHarness(detail) {
  const calls=[]; const nodes=harness(async (route,options) => { calls.push({route,options}); if(route==='/api/auth/me') return ok({name:'operator'}); if(route.startsWith('/api/stage-operations')) return ok({items:[item(env1)],nextCursor:null}); if(route.endsWith('/stage-status')) return detail?.promise || ok(detail); if(route==='/api/auth/csrf') return ok({token:'test'}); if(route==='/api/auth/logout') return ok(null); throw new Error('Unexpected route'); });
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
    if (route === `/api/test-environments/${env1}/stage-status`) return ok({
      environmentId:env1, requestId:env2, jobId:env1, jobState:'StageCompleted', environmentState:'Requested', requestState:'Pending', reservationState:'Held',
      stagingBytes:10, unsettled:true, artifactId:env2, artifactState:'ContentVerified', leaseState:'Completed', leaseExpiresAt:'2026-01-01T00:00:00Z',
      reconciliationCode:'StageCompleted', observedAt:'2026-01-01T00:00:00Z', dangerous:'<img src=x onerror=alert(1)>'
    });
    throw new Error('Unexpected route');
  });
  await flush(); assert.equal(buttons(nodes).length, 1);
  buttons(nodes)[0].listeners.click(); await flush();
  assert.equal(nodes.get('detail-panel').hidden, false);
  assert.equal(nodes.get('detail-values').children[0].children.length, 30);
  assert.equal(nodes.get('detail-values').children[0].children.some(x => x.textContent?.includes('dangerous')), false);
  assert.equal(nodes.get('detail-message').textContent.includes('не подтверждаются'), true);
  assert.deepEqual(calls, ['/api/auth/me','/api/stage-operations?limit=25',`/api/test-environments/${env1}/stage-status`]);
});

test('late detail is discarded after filter change and does not reopen panel', async () => {
  const pending = deferred(); const calls = [];
  const nodes = harness(async (route, options) => {
    calls.push({ route, options });
    if (route === '/api/auth/me') return ok({ name:'operator' });
    if (route.startsWith('/api/stage-operations')) return ok({ items: [item(env1)], nextCursor:null });
    if (route.includes('/stage-status')) return pending.promise;
    throw new Error('Unexpected route');
  });
  await flush(); buttons(nodes)[0].listeners.click(); await flush();
  nodes.get('state-filter').value = 'Pending'; nodes.get('state-filter').listeners.change(); await flush();
  pending.resolve(ok({ environmentId:env1, reconciliationCode:'StageCompleted' })); await flush();
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
