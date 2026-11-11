# Исторические сведения о выдаче execution claim

## Назначение

Admin-only диагностика reconciliation дополняет существующие метаданные операции и audit. GET `/api/admin/target-creation-operations/{operationId}/execution-evidence` показывает только сохранённый факт выдачи claim, исходные времена и immutable pins. Это НЕ повторный GET grant и не восстановление утраченного полномочия. DTO отличается от fresh execution-claim receipt; executable=false, freshAuthority=false, retryAllowed=false, scheduledJobsMustRemainBlocked=true, reconciliationRequired=true, physicalOutcomeKnown=false всегда.

Отсутствие claim означает только отсутствие соответствующей сохранённой строки в согласованном снимке. Оно НЕ доказывает, что физический процесс не запускался. Наличие claim НЕ доказывает получение ответа агентом, запуск/завершение RAC, создание или владение IB/SQL DB. Истечение времени не делает допустимым retry. Claim может оставаться исторически доступным после revoke/expiry; текущая авторизация администратора проверяется перед раскрытием существования операции.

## Чтение сервера

Новый read port использует одну serializable transaction и существующий cooperative agent-control lock, проверяет текущую database administrator role, структурную связь operation/restore-intent и исторические claim pins. Только AsNoTracking projections, без SaveChanges, audit append, Admission, credential-envelope selection/decryption, target I/O или lifecycle transition. Чтение метаданных SQL может брать блокировки; оно не является доказательством состояния внешнего RAC/SQL target.

Отключённая диагностика не публикует новый маршрут (404); старые диагностические маршруты сохраняют прежний контракт. Сам read service также требует включённого creation ledger, как существующие operation reads. Канонический lowercase GUID-D operation ID обязателен. Endpoint требует admin user session, не agent bearer, возвращает no-store/no-cache. Nested historicalClaim содержит только claimId, permitId, attemptId, agentId, credentialId, endpointMappingId, issuedAt, expiresAt. Ни секретов, ни запускаемых команд, ни свежего окна полномочия нет.

## Интерфейс оператора

Существующая страница creation diagnostics показывает отдельную карточку historical execution evidence. Отображаются исходные времена, без countdown/valid-now или Ready badge. Сбой/404/409 не подменяется сообщением «claim отсутствует». Изменение operation ID, навигация, скрытие вкладки, смена поколения запросов и утрата авторизации очищают результаты. Значения выводятся через textContent; browser storage и mutation actions отсутствуют.

Оператор всё ещё обязан отдельно разбирать локальные сведения и фактическое состояние целей. Обычный AgentJournal constructor не используется для якобы read-only local inspection: он инициализирует writable SQLite/lock. ACK остаётся эфемерным результатом команды, а не сохранённым серверным доказательством создания. Нового протокола доказательства владения, разрешения destructive DT restore, снятия quarantine, публикации Ready или освобождения слотов этот срез не вводит.

## Проверки

API/UI/fake fixtures и disposable SQL CI tests проверяют фиксированные inert flags, current-admin role, canonical IDs, отсутствие ложного physical outcome, expired/revoked history, отсутствие lifecycle/audit изменений и очистку устаревшего UI. Локальный SQL suite без отдельной test database явно skipped; реальный стенд не используется. Targeted API15passed; offline SQL gate1passed/6SQLexplicitlyskipped. Full local Release2407passed/0failed,270SQLexplicitlyskipped (integration327total). Node61passed/0failed including19creation-diagnostics cases (6new evidence scenarios). Independent final App/Infrastructure/DI/API/UI/SQL tests review found no blockers. Tests verify read-only state snapshots, expired/revoked history, absent-vs-unavailable distinction, strict timestamp precision, stale/auth clearing and inert flags. SQL-dependent assertions await disposable database CI; no local skip is reported as a pass. CIafterpush recorded separately.