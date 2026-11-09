"use strict";
(() => {
  class UiError extends Error {}
  const byId = id => document.getElementById(id);
  const api = "/api/admin/target-credentials";
  const secretIds = ["clusterUser", "clusterPassword", "sqlUser", "sqlPassword"];
  const bindingKeys = ["credentialId", "canonicalClusterId", "canonicalSqlServerId", "endpointMappingId"];
  const metadataKeys = [...bindingKeys, "state", "revision", "createdAt", "revokedAt"];
  const unknown = "Результат запроса неизвестен. Проверьте метаданные по ID; не повторяйте импорт автоматически.";
  const errors = new Map([
    ["InvalidTargetCredential", "Некорректные данные запроса."],
    ["TargetCredentialBodyTooLarge", "Размер запроса превышает 16 КиБ."],
    ["CredentialConflict", "Этот ID уже существует. Импорт не заменяет запись; проверьте её метаданные."],
    ["RevisionConflict", "Ревизия изменилась. Загрузите метаданные заново."],
    ["InvalidTransition", "Отзыв в текущем состоянии невозможен."],
    ["InvalidServerTime", "Время сервера не позволяет выполнить отзыв."],
    ["TargetCredentialMismatch", "Метаданные записи не прошли проверку."],
    ["TargetCredentialNotFound", "Запись не найдена."],
    ["TargetCredentialsUnavailable", "Хранилище отключено или временно недоступно."],
    ["TargetCreationForbidden", "Недостаточно прав текущего администратора."],
    ["Forbidden", "Доступ запрещён."],
    ["InvalidCsrfToken", "Проверка безопасности не пройдена. Обновите страницу."]
  ]);
  let busy = false;
  let selected = null;

  function clearSecrets() {
    for (const id of secretIds) byId(id).value = "";
  }
  function setBusy(value) {
    busy = value;
    document.querySelectorAll("button, input").forEach(control => { control.disabled = value; });
    byId("revoke").disabled = value || !selected || selected.state !== 0 || selected.revision !== 1;
    document.querySelectorAll("form").forEach(form => form.setAttribute("aria-busy", String(value)));
  }
  function message(text) { byId("status").textContent = text; }
  function guid(value) {
    if (typeof value !== "string" || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value) || /^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(value)) {
      throw new UiError("Укажите четыре корректных ненулевых GUID; адреса и URL не принимаются.");
    }
    return value.toLowerCase();
  }
  function timestamp(value) {
    if (typeof value !== "string" || value.length > 40 || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/.test(value) || !Number.isFinite(Date.parse(value))) {
      throw new UiError("Недопустимый формат метаданных.");
    }
    return new Date(value).toISOString();
  }
  function normalizeMetadata(raw) {
    if (!raw || typeof raw !== "object" || !raw.binding || typeof raw.binding !== "object") throw new UiError("Недопустимый формат метаданных.");
    if (raw.executable !== false || Reflect.ownKeys(raw).length !== 6 || Reflect.ownKeys(raw.binding).length !== 4) throw new UiError("Недопустимый формат метаданных.");
    const safe = {};
    for (const key of bindingKeys) safe[key] = guid(raw.binding[key]);
    if (!((raw.state === 0 && raw.revision === 1 && raw.revokedAt === null) ||
          (raw.state === 1 && raw.revision === 2 && typeof raw.revokedAt === "string"))) throw new UiError("Недопустимый формат метаданных.");
    safe.state = raw.state;
    safe.revision = raw.revision;
    safe.createdAt = timestamp(raw.createdAt);
    safe.revokedAt = raw.revokedAt === null ? null : timestamp(raw.revokedAt);
    if (safe.revokedAt && Date.parse(safe.revokedAt) < Date.parse(safe.createdAt)) throw new UiError("Недопустимый формат метаданных.");
    return safe;
  }
  function render(safe) {
    selected = safe;
    byId("metadata").replaceChildren();
    for (const key of metadataKeys) {
      const name = document.createElement("dt");
      const value = document.createElement("dd");
      name.textContent = key;
      value.textContent = safe[key] === null ? "—" : String(safe[key]);
      byId("metadata").append(name, value);
    }
    byId("lookupId").value = safe.credentialId;
  }
  function clearSelection() {
    selected = null;
    byId("metadata").replaceChildren();
  }
  const requestOptions = {
    credentials: "same-origin", cache: "no-store", redirect: "error", referrerPolicy: "no-referrer"
  };
  async function checked(response, expectedStatus) {
    if (response.status !== expectedStatus) {
      if (response.status === 401) throw new UiError("Требуется вход администратора.");
      if (response.status === 403) throw new UiError("Доступ запрещён.");
      let code;
      try { code = (await response.json()).code; } catch { /* Never display a raw response body. */ }
      throw new UiError(errors.get(code) || "Запрос отклонён. Проверьте метаданные по ID.");
    }
    return response.json();
  }
  async function csrf() {
    const response = await fetch("/api/auth/csrf", { ...requestOptions });
    const data = await checked(response, 200);
    if (!data || typeof data.token !== "string" || data.token.length < 1 || data.token.length > 4096) throw new UiError("Не удалось получить токен безопасности.");
    return data.token;
  }
  async function post(path, body, status, expectedBinding, expectedState) {
    const token = await csrf();
    let response;
    try {
      response = await fetch(path, {
        ...requestOptions, method: "POST",
        headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": token }, body
      });
    } catch { throw new UiError(unknown); }
    let raw;
    try { raw = await checked(response, status); }
    catch (error) {
      if (response.status === status) throw new UiError(unknown);
      throw error;
    }
    try {
      const safe = normalizeMetadata(raw);
      if (safe.state !== expectedState || bindingKeys.some(key => safe[key] !== expectedBinding[key])) throw new UiError(unknown);
      return safe;
    } catch { throw new UiError(unknown); }
  }

  byId("generateId").addEventListener("click", event => {
    event.preventDefault();
    if (busy) return;
    try { byId("credentialId").value = crypto.randomUUID(); }
    catch { message("Не удалось создать ID в этом браузере."); }
  });
  byId("importForm").addEventListener("submit", async event => {
    event.preventDefault();
    if (busy) { clearSecrets(); return; }
    setBusy(true);
    clearSelection();
    let payload = null;
    let body = null;
    try {
      payload = {};
      for (const key of bindingKeys) payload[key] = byId(key).value.trim();
      for (const key of secretIds) payload[key] = byId(key).value;
      // Clear DOM before validation, token requests, or any await/network operation.
      clearSecrets();
      for (const key of bindingKeys) payload[key] = guid(payload[key]);
      for (const key of secretIds) {
        if (!payload[key] || payload[key].length > 4096 || /[\u0000-\u001f\u007f-\u009f]/.test(payload[key]) || !wellFormed(payload[key])) throw new UiError("Заполните четыре секретных поля (до 4096 символов, без управляющих символов).");
      }
      body = JSON.stringify(payload);
      if (new TextEncoder().encode(body).byteLength > 16 * 1024) throw new UiError("Размер запроса превышает 16 КиБ UTF-8.");
      const expectedBinding = {};
      for (const key of bindingKeys) expectedBinding[key] = payload[key];
      payload = null;
      message("Импорт выполняется… Не повторяйте запрос автоматически.");
      render(await post(api, body, 201, expectedBinding, 0));
      message("Импорт завершён. Показаны только метаданные; выполнение операций не разрешено.");
    } catch (error) { message(error instanceof UiError ? error.message : unknown); }
    finally {
      clearSecrets();
      payload = null;
      body = null;
      // JS strings are immutable: dropping references does not guarantee zeroing memory.
      setBusy(false);
    }
  });
  function wellFormed(value) {
    for (let i = 0; i < value.length; i++) {
      const unit = value.charCodeAt(i);
      if (unit >= 0xd800 && unit <= 0xdbff) {
        const next = value.charCodeAt(++i);
        if (!(next >= 0xdc00 && next <= 0xdfff)) return false;
      } else if (unit >= 0xdc00 && unit <= 0xdfff) return false;
    }
    return true;
  }
  byId("lookupForm").addEventListener("submit", async event => {
    event.preventDefault();
    if (busy) return;
    setBusy(true);
    clearSelection();
    try {
      const id = guid(byId("lookupId").value.trim());
      const response = await fetch(`${api}/${id}`, { ...requestOptions });
      const safe = normalizeMetadata(await checked(response, 200));
      if (safe.credentialId !== id) throw new UiError("Недопустимый формат метаданных.");
      render(safe);
      message("Метаданные загружены. 0 — сохранено, 1 — отозвано; это не разрешение на выполнение.");
    } catch { message("Не удалось получить метаданные. Проверьте ID, права и доступность хранилища."); }
    finally { setBusy(false); }
  });
  byId("revoke").addEventListener("click", async event => {
    event.preventDefault();
    if (busy || !selected || selected.state !== 0 || selected.revision !== 1) return;
    if (!window.confirm("Отозвать выбранную запись? Отзыв не удаляет зашифрованные данные и не запускает операции.")) return;
    setBusy(true);
    const id = selected.credentialId;
    const expectedRevision = selected.revision;
    const expectedBinding = selected;
    clearSelection();
    try {
      render(await post(`${api}/${id}/revoke`, JSON.stringify({ expectedRevision }), 200, expectedBinding, 1));
      message("Запись отозвана. Показаны только метаданные.");
    } catch (error) { message(error instanceof UiError ? error.message : unknown); }
    finally { setBusy(false); }
  });
  // No fetch on load, no persistence, and no secret prefilling.
  clearSecrets();
  setBusy(false);
})();