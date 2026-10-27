'use strict';
(() => {
  const $ = id => document.getElementById(id);
  const ui = Object.fromEntries(['current-user','logout','message','login-panel','workspace','login-form','username','password','login-submit','refresh','state-filter','observed','operations-body','empty-list','previous','next','page-number','detail-panel','detail-message','detail-values','close-detail','browse-archives','archive-browser','projects-select','projects-message','projects-observed','projects-previous','projects-next','projects-page','load-archives','catalog-message','catalog-observed','catalog-body','catalog-previous','catalog-next','catalog-page'].map(id => [id, $(id)]));
  const states = {
    Pending: 'Ожидает подготовки', Leased: 'Выдано агенту', StageCompleted: 'Stage завершён',
    NeedsAttention: 'Требует внимания', Requested: 'Запрошено', Provisioning: 'Подготовка окружения',
    Held: 'Резерв удерживается', Active: 'Активен', Completed: 'Завершён',
    ContentVerified: 'Содержимое подтверждено агентом', Ready: 'Ready',
    AwaitingStage: 'Ожидает stage', StageActive: 'Stage выполняется', ArtifactReported: 'Квитанция содержимого записана',
    LeaseExpired: 'Срок lease истёк', Inconsistent: 'Несогласованные состояния'
  };
  const allowedFilters = new Set(['', 'Pending', 'Leased', 'StageCompleted', 'NeedsAttention']);
  const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
  let generation = 0, detailGeneration = 0, controller = null, detailController = null;
  let authenticated = false, busy = false, history = [null], nextCursor = null, selected = null;
  let projectGeneration = 0, catalogGeneration = 0, projectController = null, catalogController = null;
  let projectHistory = [null], catalogHistory = [null], projectNext = null, catalogNext = null;
  let projectBusy = false, catalogBusy = false, projectIds = new Set(), selectedProject = null;
  class ApiError extends Error { constructor(status) { super('API request failed'); this.status = status; } }
  const text = value => typeof value === 'string' ? value.slice(0, 512) : '—';
  const stateText = value => typeof value === 'string' && Object.hasOwn(states, value) ? states[value] : text(value);
  const date = value => {
    if (typeof value !== 'string') return '—';
    try { const d = new Date(value); return Number.isNaN(d.getTime()) ? '—' : new Intl.DateTimeFormat('ru-RU', {dateStyle:'medium', timeStyle:'medium'}).format(d); }
    catch { return '—'; }
  };
  const bytes = value => typeof value === 'number' && Number.isSafeInteger(value) && value >= 0 ? new Intl.NumberFormat('ru-RU').format(value) + ' байт' : '—';
  function closeDetail() {
    detailGeneration++; detailController?.abort(); detailController = null;
    selected = null; ui['detail-panel'].hidden = true; ui['detail-values'].replaceChildren();
    ui['detail-message'].textContent = ''; markSelection();
  }
  function markSelection() {
    for (const button of ui['operations-body'].querySelectorAll('button')) button.setAttribute('aria-expanded', String(button.dataset.environment === selected));
  }
  function archiveControls() {
    const active = authenticated && !busy && !ui['archive-browser'].hidden;
    ui['browse-archives'].disabled = !authenticated || busy;
    ui['projects-select'].disabled = !active || projectBusy || projectIds.size === 0;
    ui['load-archives'].disabled = !active || projectBusy || catalogBusy || !selectedProject;
    ui['projects-previous'].disabled = !active || projectBusy || projectHistory.length <= 1;
    ui['projects-next'].disabled = !active || projectBusy || !projectNext;
    ui['catalog-previous'].disabled = !active || catalogBusy || catalogHistory.length <= 1;
    ui['catalog-next'].disabled = !active || catalogBusy || !catalogNext;
    ui['archive-browser'].setAttribute('aria-busy', String(projectBusy || catalogBusy));
  }
  function resetCatalog() {
    catalogGeneration++; catalogController?.abort(); catalogController = null;
    catalogHistory = [null]; catalogNext = null; catalogBusy = false;
    ui['catalog-body'].replaceChildren(); ui['catalog-observed'].textContent = '';
    ui['catalog-page'].textContent = 'Страница версий 1';
    ui['catalog-message'].textContent = 'Выберите проект для просмотра версий.';
    archiveControls();
  }
  function clearArchiveBrowser() {
    projectGeneration++; projectController?.abort(); projectController = null;
    projectHistory = [null]; projectNext = null; projectBusy = false; projectIds = new Set(); selectedProject = null;
    ui['archive-browser'].hidden = true; ui['browse-archives'].setAttribute('aria-expanded', 'false');
    ui['projects-select'].replaceChildren(); ui['projects-select'].value = '';
    ui['projects-message'].textContent = ''; ui['projects-observed'].textContent = '';
    ui['projects-page'].textContent = 'Страница проектов 1'; resetCatalog();
  }
  function validPage(page) {
    return Array.isArray(page?.items) && page.items.length <= 25 &&
      page.items.every(item => item && typeof item === 'object') &&
      (page.nextCursor == null || (typeof page.nextCursor === 'string' && page.nextCursor.length > 0 && page.nextCursor.length <= 512));
  }
  async function loadProjects(reset = false) {
    if (!authenticated || ui['archive-browser'].hidden) return;
    projectGeneration++; projectController?.abort(); projectController = new AbortController();
    const version = projectGeneration, session = generation;
    if (reset) projectHistory = [null];
    selectedProject = null; projectIds = new Set(); resetCatalog();
    projectBusy = true; ui['projects-select'].replaceChildren(); ui['projects-select'].value = '';
    ui['projects-message'].textContent = 'Загрузка проектов…'; archiveControls();
    const query = new URLSearchParams({limit:'25'});
    if (projectHistory.at(-1)) query.set('cursor', projectHistory.at(-1));
    try {
      const page = await request('/api/projects/browse?' + query.toString(), projectController.signal);
      if (version !== projectGeneration || session !== generation || !authenticated) return;
      if (!validPage(page) || page.items.some(item => !guid.test(item.projectId) || typeof item.name !== 'string')) throw new Error('Invalid projects response');
      const fragment = document.createDocumentFragment();
      const placeholder = document.createElement('option'); placeholder.value = ''; placeholder.textContent = 'Выберите проект'; fragment.append(placeholder);
      for (const item of page.items) {
        if (projectIds.has(item.projectId.toLowerCase())) throw new Error('Duplicate project');
        projectIds.add(item.projectId.toLowerCase());
        const option = document.createElement('option'); option.value = item.projectId; option.textContent = item.name.slice(0, 256);
        fragment.append(option);
      }
      ui['projects-select'].replaceChildren(fragment); ui['projects-select'].value = '';
      projectNext = page.nextCursor || null; ui['projects-page'].textContent = 'Страница проектов ' + projectHistory.length;
      ui['projects-observed'].textContent = 'Наблюдение сервера: ' + date(page.observedAt);
      ui['projects-message'].textContent = page.items.length ? 'Выберите проект для просмотра версий.' : 'Доступных проектов нет.';
    } catch (error) {
      if (version !== projectGeneration || session !== generation || error.name === 'AbortError') return;
      if (error instanceof ApiError && error.status === 401) { showLogin('Сеанс завершён. Войдите снова.'); return; }
      projectIds = new Set(); ui['projects-select'].replaceChildren(); projectNext = null;
      ui['projects-message'].textContent = errorText(error);
    } finally { if (version === projectGeneration) { projectBusy = false; archiveControls(); } }
  }
  async function loadCatalog(reset = false) {
    if (!authenticated || projectBusy || ui['archive-browser'].hidden || !selectedProject || !guid.test(selectedProject) || !projectIds.has(selectedProject.toLowerCase())) return;
    catalogGeneration++; catalogController?.abort(); catalogController = new AbortController();
    const version = catalogGeneration, session = generation, projectVersion = projectGeneration, projectId = selectedProject;
    if (reset) catalogHistory = [null];
    catalogBusy = true; ui['catalog-body'].replaceChildren(); ui['catalog-message'].textContent = 'Загрузка версий…'; archiveControls();
    const query = new URLSearchParams({limit:'25'});
    if (catalogHistory.at(-1)) query.set('cursor', catalogHistory.at(-1));
    try {
      const page = await request('/api/projects/' + encodeURIComponent(projectId) + '/archive-versions?' + query.toString(), catalogController.signal);
      if (version !== catalogGeneration || session !== generation || projectVersion !== projectGeneration || projectId !== selectedProject || !authenticated) return;
      if (!validPage(page) || page.items.some(item => !guid.test(item.archiveVersionId) || !['dt','bak'].includes(item.format))) throw new Error('Invalid catalog response');
      const fragment = document.createDocumentFragment();
      for (const item of page.items) {
        const row = document.createElement('tr'); cell(row, item.archiveVersionId); cell(row, item.format);
        cell(row, bytes(item.sizeBytes)); cell(row, bytes(item.estimatedDbBytes)); fragment.append(row);
      }
      ui['catalog-body'].replaceChildren(fragment); catalogNext = page.nextCursor || null;
      ui['catalog-page'].textContent = 'Страница версий ' + catalogHistory.length;
      ui['catalog-observed'].textContent = 'Наблюдение сервера: ' + date(page.observedAt);
      ui['catalog-message'].textContent = page.items.length ? 'Версий на странице: ' + page.items.length + '. Заказ здесь не создаётся.' : 'Доступных версий нет.';
    } catch (error) {
      if (version !== catalogGeneration || session !== generation || projectVersion !== projectGeneration || error.name === 'AbortError') return;
      if (error instanceof ApiError && error.status === 401) { showLogin('Сеанс завершён. Войдите снова.'); return; }
      catalogNext = null; ui['catalog-message'].textContent = errorText(error);
    } finally { if (version === catalogGeneration) { catalogBusy = false; archiveControls(); } }
  }
  function begin() {
    generation++; controller?.abort(); closeDetail(); clearArchiveBrowser(); controller = new AbortController();
    return { version:generation, signal:controller.signal };
  }
  function controls() {
    ui['refresh'].disabled = busy; ui['state-filter'].disabled = busy;
    ui['previous'].disabled = busy || history.length <= 1;
    ui['next'].disabled = busy || !nextCursor;
    ui['login-submit'].disabled = busy;
    for (const button of ui['operations-body'].querySelectorAll('button')) button.disabled = busy || !button.dataset.environment;
    ui['workspace'].setAttribute('aria-busy', String(busy));
    archiveControls();
  }
  function message(value, error = false) { ui.message.textContent = value; ui.message.classList.toggle('error', error); }
  function clearData() {
    history = [null]; nextCursor = null; ui['operations-body'].replaceChildren();
    ui['empty-list'].hidden = true; ui.observed.textContent = ''; ui['page-number'].textContent = 'Страница 1'; closeDetail();
  }
  function showLogin(value) {
    begin(); authenticated = false; busy = false; clearData();
    ui.workspace.hidden = true; ui['login-panel'].hidden = false; ui.logout.hidden = true;
    ui['current-user'].textContent = 'Нет активного сеанса'; ui.logout.textContent = 'Выйти'; ui.password.value = '';
    message(value); controls();
  }
  function errorText(error) {
    if (error instanceof ApiError) {
      if (error.status === 403 || error.status === 404) return 'Сведения недоступны или отсутствуют. Проверьте права доступа.';
      if (error.status === 400) return 'Запрос отклонён. Обновите список с первой страницы.';
      if (error.status === 409) return 'Состояние требует проверки оператором. Автоматические изменения не выполняются.';
      if (error.status === 429) return 'Слишком много запросов. Повторите позже.';
    }
    return 'Не удалось получить данные. Проверьте соединение и повторите запрос.';
  }
  async function request(path, signal, options = {}, json = true) {
    const response = await fetch(path, { ...options, credentials:'same-origin', cache:'no-store', redirect:'error', signal });
    if (!response.ok) throw new ApiError(response.status);
    return json ? response.json() : null;
  }
  async function csrf(signal) {
    const value = await request('/api/auth/csrf', signal);
    if (typeof value?.token !== 'string' || !value.token) throw new Error('Invalid CSRF response');
    return value.token;
  }
  function cell(row, value, className) {
    const td = document.createElement('td'); td.textContent = value;
    if (className) td.className = className; row.append(td); return td;
  }
  function renderList(items) {
    const fragment = document.createDocumentFragment();
    for (const item of items) {
      const row = document.createElement('tr');
      const identity = cell(row, '', 'identity');
      const env = document.createElement('span'); env.textContent = text(item.environmentId);
      const job = document.createElement('small'); job.textContent = 'Задание: ' + text(item.jobId); identity.append(env, job);
      const status = cell(row, ''); const badge = document.createElement('span'); badge.className = 'badge';
      if (item.jobState === 'NeedsAttention') badge.classList.add('attention');
      badge.textContent = stateText(item.jobState); status.append(badge);
      const envState = document.createElement('small'); envState.textContent = 'Окружение: ' + stateText(item.environmentState); status.append(envState);
      const resources = cell(row, stateText(item.reservationState)); const size = document.createElement('small'); size.textContent = bytes(item.stagingBytes); resources.append(size);
      cell(row, date(item.createdAt));
      const action = cell(row, ''); const button = document.createElement('button'); button.type = 'button'; button.className = 'secondary'; button.textContent = 'Подробнее';
      button.setAttribute('aria-controls', 'detail-panel'); button.setAttribute('aria-expanded', 'false');
      if (typeof item.environmentId === 'string' && guid.test(item.environmentId)) {
        button.dataset.environment = item.environmentId; button.addEventListener('click', () => loadDetail(item.environmentId));
      } else button.disabled = true;
      action.append(button); fragment.append(row);
    }
    ui['operations-body'].replaceChildren(fragment);
  }
  async function loadList(reset = false) {
    if (!authenticated) return;
    const op = begin(); if (reset) { history = [null]; nextCursor = null; }
    busy = true; ui['operations-body'].replaceChildren(); ui['empty-list'].hidden = true; controls(); message('Загрузка операций…');
    const query = new URLSearchParams({limit:'25'});
    const filter = ui['state-filter'].value;
    if (allowedFilters.has(filter) && filter) query.set('state', filter);
    if (history[history.length - 1]) query.set('cursor', history[history.length - 1]);
    try {
      const page = await request('/api/stage-operations?' + query.toString(), op.signal);
      if (op.version !== generation) return;
      if (!Array.isArray(page?.items) || page.items.length > 25 || page.items.some(item => !item || typeof item !== 'object') ||
          !(page.nextCursor == null || (typeof page.nextCursor === 'string' && page.nextCursor.length <= 512))) throw new Error('Invalid list response');
      nextCursor = page.nextCursor || null; renderList(page.items);
      ui['empty-list'].hidden = page.items.length !== 0; ui.observed.textContent = 'Наблюдение сервера: ' + date(page.observedAt);
      ui['page-number'].textContent = 'Страница ' + history.length;
      message('Получено операций: ' + page.items.length + '. Данные не обновляются автоматически.');
    } catch (error) {
      if (op.version !== generation || error.name === 'AbortError') return;
      if (error instanceof ApiError && error.status === 401) { showLogin('Сеанс завершён. Войдите снова.'); return; }
      nextCursor = null; message(errorText(error), true);
    } finally { if (op.version === generation) { busy = false; controls(); } }
  }
  async function loadDetail(id) {
    if (!authenticated || busy || !guid.test(id)) return;
    closeDetail(); selected = id; markSelection();
    const version = ++detailGeneration, session = generation; detailController = new AbortController();
    ui['detail-panel'].hidden = false; ui['detail-panel'].setAttribute('aria-busy', 'true'); ui['detail-message'].textContent = 'Загрузка сведений…';
    try {
      const value = await request('/api/test-environments/' + encodeURIComponent(id) + '/stage-status', detailController.signal);
      if (version !== detailGeneration || session !== generation || !authenticated) return;
      if (!value || value.environmentId?.toLowerCase() !== id.toLowerCase()) throw new Error('Invalid detail response');
      const entries = [
        ['Окружение', text(value.environmentId)], ['Запрос', text(value.requestId)], ['Задание', text(value.jobId)],
        ['Состояние задания', stateText(value.jobState)], ['Состояние окружения', stateText(value.environmentState)],
        ['Состояние запроса', stateText(value.requestState)], ['Reservation', stateText(value.reservationState)],
        ['Зарезервировано для staging', bytes(value.stagingBytes)], ['Provisioning не завершён', value.unsettled === true ? 'Да' : value.unsettled === false ? 'Нет' : '—'],
        ['Артефакт', text(value.artifactId)], ['Квитанция артефакта', stateText(value.artifactState)],
        ['Lease', stateText(value.leaseState)], ['Срок lease', date(value.leaseExpiresAt)],
        ['Оценка состояния', stateText(value.reconciliationCode)], ['Наблюдение сервера', date(value.observedAt)]
      ];
      const fragment = document.createDocumentFragment();
      for (const [label, content] of entries) { const dt = document.createElement('dt'), dd = document.createElement('dd'); dt.textContent = label; dd.textContent = content; fragment.append(dt, dd); }
      ui['detail-values'].replaceChildren(fragment); ui['detail-message'].textContent = 'Сведения получены. Наличие и формат файла этой проверкой не подтверждаются.';
    } catch (error) {
      if (version !== detailGeneration || session !== generation || error.name === 'AbortError') return;
      if (error instanceof ApiError && error.status === 401) { showLogin('Сеанс завершён. Войдите снова.'); return; }
      ui['detail-message'].textContent = errorText(error);
    } finally { if (version === detailGeneration) ui['detail-panel'].setAttribute('aria-busy', 'false'); }
  }
  async function checkSession() {
    const op = begin(); busy = true; controls();
    try {
      const me = await request('/api/auth/me', op.signal);
      if (op.version !== generation) return;
      authenticated = true; ui['current-user'].textContent = text(me?.name); ui.logout.textContent = 'Выйти'; ui.logout.hidden = false;
      ui['login-panel'].hidden = true; ui.workspace.hidden = false; busy = false;
      await loadList(true);
    } catch (error) {
      if (op.version !== generation || error.name === 'AbortError') return;
      showLogin(error instanceof ApiError && error.status === 401 ? 'Войдите, чтобы просмотреть операции.' : errorText(error));
    }
  }
  ui['login-form'].addEventListener('submit', async event => {
    event.preventDefault(); if (busy) return;
    const op = begin(); busy = true; controls(); message('Вход…');
    try {
      const token = await csrf(op.signal);
      if (op.version !== generation) return;
      await request('/api/auth/login', op.signal, {method:'POST', headers:{'Content-Type':'application/json','X-CSRF-TOKEN':token},
        body:JSON.stringify({userName:ui.username.value, password:ui.password.value})}, false);
      ui.password.value = '';
      if (op.version !== generation) return;
      await checkSession();
    } catch (error) {
      if (op.version !== generation || error.name === 'AbortError') return;
      message(error instanceof ApiError && error.status === 401 ? 'Вход не выполнен. Проверьте учётные данные или обратитесь к администратору.' : errorText(error), true);
    } finally { ui.password.value = ''; if (op.version === generation) { busy = false; controls(); } }
  });
  ui.logout.addEventListener('click', async () => {
    showLogin('Выход из сеанса…'); const op = begin(); busy = true; controls();
    try {
      const token = await csrf(op.signal);
      await request('/api/auth/logout', op.signal, {method:'POST',headers:{'X-CSRF-TOKEN':token}}, false);
      if (op.version === generation) message('Вы вышли из сеанса.');
    } catch (error) {
      if (op.version !== generation || error.name === 'AbortError') return;
      const expired = error instanceof ApiError && error.status === 401;
      if (!expired) {
        ui['current-user'].textContent = 'Выход не подтверждён';
        ui.logout.textContent = 'Повторить выход'; ui.logout.hidden = false;
      }
      message(expired ? 'Сеанс уже завершён.' : 'Выход не подтверждён сервером. Данные скрыты; повторите выход после восстановления связи.', !expired);
    } finally { if (op.version === generation) { busy = false; controls(); } }
  });
  ui.refresh.addEventListener('click', () => loadList(true));
  ui['state-filter'].addEventListener('change', () => loadList(true));
  ui.previous.addEventListener('click', () => { if (!busy && history.length > 1) { history.pop(); loadList(); } });
  ui.next.addEventListener('click', () => { if (!busy && nextCursor) { history.push(nextCursor); loadList(); } });
  ui['close-detail'].addEventListener('click', closeDetail);
  ui['browse-archives'].addEventListener('click', () => {
    if (!authenticated || busy) return;
    clearArchiveBrowser(); ui['archive-browser'].hidden = false; ui['browse-archives'].setAttribute('aria-expanded', 'true'); loadProjects(true);
  });
  ui['projects-select'].addEventListener('change', () => {
    resetCatalog(); const id = ui['projects-select'].value;
    selectedProject = typeof id === 'string' && guid.test(id) && projectIds.has(id.toLowerCase()) ? id : null;
    archiveControls();
  });
  ui['load-archives'].addEventListener('click', () => { if (!catalogBusy) loadCatalog(true); });
  ui['projects-previous'].addEventListener('click', () => { if (!projectBusy && projectHistory.length > 1) { projectHistory.pop(); loadProjects(); } });
  ui['projects-next'].addEventListener('click', () => { if (!projectBusy && projectNext) { projectHistory.push(projectNext); loadProjects(); } });
  ui['catalog-previous'].addEventListener('click', () => { if (!catalogBusy && catalogHistory.length > 1) { catalogHistory.pop(); loadCatalog(); } });
  ui['catalog-next'].addEventListener('click', () => { if (!catalogBusy && catalogNext) { catalogHistory.push(catalogNext); loadCatalog(); } });
  checkSession();
})();
// end
