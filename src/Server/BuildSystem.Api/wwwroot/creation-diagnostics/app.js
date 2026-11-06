'use strict';
(() => {
  const ui = Object.fromEntries(['lookup-form', 'operation-id', 'lookup-button', 'message', 'session-link', 'result', 'values', 'audit', 'audit-message', 'audit-events', 'audit-truncated'].map(id => [id, document.getElementById(id)]));
  const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
  const hasGuid = value => typeof value === 'string' && guid.test(value) && value !== '00000000-0000-0000-0000-000000000000';
  const pins = [
    ['operationId', 'Operation ID'], ['restoreIntentId', 'Restore intent ID'],
    ['environmentId', 'Окружение UUID'], ['stageJobId', 'Задание Stage UUID'],
    ['reservationId', 'Резерв UUID'], ['artifactId', 'Артефакт UUID'],
    ['agentId', 'Агент UUID'], ['targetIncarnationId', 'Инкарнация цели UUID'],
    ['canonicalClusterId', 'Канонический кластер UUID'], ['canonicalSqlServerId', 'Канонический SQL-сервер UUID']
  ];
  const auditActions = ['TargetCreationRegistered', 'TargetCreationAttemptRecorded', 'TargetCreationQuarantined'];
  let generation = 0, controller = null;
  function clearAudit() {
    ui.audit.hidden = true;
    ui['audit-events'].replaceChildren();
    ui['audit-message'].textContent = '';
    ui['audit-truncated'].hidden = true;
  }
  function clear() {
    generation++; controller?.abort(); controller = null;
    ui.result.hidden = true; ui.values.replaceChildren(); clearAudit();
    ui['lookup-form'].setAttribute('aria-busy', 'false');
    ui['session-link'].hidden = true;
  }
  function validate(value, id) {
    return value && pins.every(([key]) => hasGuid(value[key])) && value.operationId.toLowerCase() === id.toLowerCase()
      && ['Registered', 'AttemptRecorded', 'ReconciliationRequired'].includes(value.state)
      && Number.isSafeInteger(value.revision) && value.revision > 0
      && (value.attemptId === null || hasGuid(value.attemptId))
      && (value.attemptExpectedRevision === null || (Number.isSafeInteger(value.attemptExpectedRevision) && value.attemptExpectedRevision > 0))
      && (value.quarantineReason === null || typeof value.quarantineReason === 'string')
      && typeof value.infobaseName === 'string' && typeof value.sqlDbName === 'string'
      && value.newlyRecorded === false && value.executable === false && value.scheduledJobsMustRemainBlocked === true;
  }
  function render(value) {
    const fragment = document.createDocumentFragment();
    const rows = [['Состояние записи', value.state], ['Ревизия', value.revision],
      ...pins.map(([key, label]) => [label, value[key]]),
      ['Каноническое имя информационной базы', value.infobaseName], ['Каноническое имя SQL-базы', value.sqlDbName],
      ['Попытка UUID', value.attemptId], ['Ожидаемая ревизия попытки', value.attemptExpectedRevision],
      ['Причина карантина', value.quarantineReason], ['Выполнение разрешено (executable)', false],
      ['Задания должны оставаться заблокированы (scheduledJobsMustRemainBlocked)', true]];
    for (const [label, value] of rows) {
      const dt = document.createElement('dt'), dd = document.createElement('dd');
      dt.textContent = label; dd.textContent = value === null ? '—' : String(value);
      fragment.append(dt, dd);
    }
    ui.values.replaceChildren(fragment); ui.result.hidden = false;
  }
  function validateAudit(value, id) {
    return value && hasGuid(value.operationId) && value.operationId.toLowerCase() === id
      && value.executable === false && value.scheduledJobsMustRemainBlocked === true
      && typeof value.truncated === 'boolean' && Array.isArray(value.events) && value.events.length <= 50
      && value.events.every(event => event && hasGuid(event.id) && hasGuid(event.actorId)
        && auditActions.includes(event.action) && typeof event.createdAt === 'string'
        && Number.isFinite(Date.parse(event.createdAt)));
  }
  function renderAudit(value) {
    const fragment = document.createDocumentFragment();
    // Preserve the server order, including ties; never infer a chronology from UUIDs.
    for (const event of value.events) {
      const item = document.createElement('li');
      item.textContent = event.createdAt + ' · ' + event.action + ' · Actor UUID: ' + event.actorId + ' · Event UUID: ' + event.id;
      fragment.append(item);
    }
    ui['audit-events'].replaceChildren(fragment);
    ui['audit-message'].textContent = value.events.length ? 'Показаны сохранённые исторические события.' : 'Исторические события аудита отсутствуют.';
    ui['audit-truncated'].hidden = !value.truncated;
    ui.audit.hidden = false;
  }
  async function loadAudit(id, version, request) {
    ui.audit.hidden = false; ui['audit-message'].textContent = 'Загрузка истории аудита…';
    try {
      const response = await fetch('/api/admin/target-creation-operations/' + id + '/audit', {
        method: 'GET', credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal: request.signal
      });
      if (version !== generation) return;
      if (response.status === 401 || response.status === 403) {
        clear(); ui.message.textContent = errors[response.status];
        ui['session-link'].hidden = response.status !== 401;
        return;
      }
      if (!response.ok) throw new Error('Audit unavailable');
      const value = await response.json();
      if (version !== generation) return;
      if (!validateAudit(value, id)) throw new Error('Invalid audit response');
      renderAudit(value);
    } catch (error) {
      if (version !== generation) return;
      clearAudit(); ui.audit.hidden = false;
      ui['audit-message'].textContent = 'История аудита недоступна. Повторите поиск; метаданные записи показаны отдельно.';
    }
  }
  const errors = {
    400: 'Некорректный идентификатор операции.',
    401: 'Сеанс завершён. Войдите через существующую страницу Stage.',
    403: 'Доступ запрещён: требуется роль администратора.',
    404: 'Запись операции не найдена.',
    409: 'Диагностика журнала недоступна: функция отключена или запрос отклонён.'
  };
  ui['lookup-form'].addEventListener('submit', async event => {
    event.preventDefault(); clear();
    const id = ui['operation-id'].value.toLowerCase();
    if (!hasGuid(id)) { ui.message.textContent = 'Введите непустой UUID строго в формате 8-4-4-4-12, без пробелов и скобок.'; return; }
    const version = generation, request = new AbortController(); controller = request;
    ui['lookup-form'].setAttribute('aria-busy', 'true'); ui.message.textContent = 'Загрузка метаданных…';
    try {
      const response = await fetch('/api/admin/target-creation-operations/' + id, {
        method: 'GET', credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal: request.signal
      });
      if (version !== generation) return;
      if (!response.ok) {
        ui.message.textContent = errors[response.status] || 'Не удалось прочитать метаданные журнала.';
        ui['session-link'].hidden = response.status !== 401;
        return;
      }
      const value = await response.json();
      if (version !== generation) return;
      if (!validate(value, id)) throw new Error('Invalid diagnostic response');
      render(value); ui.message.textContent = 'Показаны только метаданные журнала. Выполнение не разрешено.';
      await loadAudit(id, version, request);
    } catch (error) {
      if (version !== generation) return;
      ui.result.hidden = true; ui.values.replaceChildren();
      ui.message.textContent = error.name === 'AbortError' ? 'Чтение отменено. Результат очищен.' : 'Ошибка сети или некорректный ответ. Результат очищен; повторите поиск.';
    } finally {
      if (version === generation) { controller = null; ui['lookup-form'].setAttribute('aria-busy', 'false'); }
    }
  });
  ui['operation-id'].addEventListener('input', () => { clear(); ui.message.textContent = 'Введите идентификатор и выполните поиск.'; });
  // Do not retain administrator metadata on navigation, BFCache restore or while this tab is hidden.
  addEventListener('pagehide', clear);
  addEventListener('pageshow', clear);
  document.addEventListener('visibilitychange', () => {
    if (document.hidden) { clear(); ui.message.textContent = 'Результат очищен. Для актуальных данных повторите поиск.'; }
  });
})();
