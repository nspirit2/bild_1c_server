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
const env1 = '11111111-1111-1111-1111-111111111111';
const env2 = '22222222-2222-2222-2222-222222222222';
const item = environmentId => ({ environmentId, jobId: env1, jobState: 'Leased', environmentState: 'Requested', reservationState: 'Held', stagingBytes: 10, createdAt: '2026-01-01T00:00:00Z' });
const buttons = nodes => nodes.get('operations-body').querySelectorAll('button');

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
// end
