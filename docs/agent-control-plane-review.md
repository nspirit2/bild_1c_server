# Агентский пакет: результаты проверки

Ветка `feat/agent-control-plane`; предыдущий серверный пакет сохранён отдельным локальным коммитом `2a627fa`. Текущие агентские изменения не закоммичены и не отправлены. Main не менялся.

## Проверено локально

.NET SDK 10.0.402, EF/SQLite/Hosting packages 10.0.13. Locked restore успешен. Release build: 0 warnings, 0 errors.

Полный тестовый прогон:

- 39 domain passed;
- 26 HTTP/application/offline-model passed;
- 11 Worker/реальный SQLite passed;
- 16 SQL integration tests явно skipped без SQL opt-in, включая 6 новых агентских сценариев;
- всего 76 passed, 0 failed, 16 skipped.

`ef migrations has-pending-model-changes`: изменений нет. Добавлена фактически сгенерированная вторая миграция AgentControlPlane; первая миграция не редактировалась. NuGet audit известных уязвимостей в текущем feed не обнаружил. `git diff --check` проходит.

## Исправления по ревью

1. Потеря ответа Poll после COMMIT: теперь тот же агент получает повторно тот же живой lease без продления срока/смены fence/повторного allocation; Worker/journal не исполняют незавершённое действие повторно после рестарта.
2. NeedsAttention lease блокирует агенту новые задания до reconciliation, независимо от heartbeat/токена.
3. Credential import через временный файл, flush и атомарный move без перезаписи вместо записи прямо в конечный файл.
4. Lifetime lock журнала исключает два Worker с одним каталогом; этот случай проверен тестом.

Read-only ревью API/server control-plane и Worker/journal выполнены отдельно. Это статический анализ, не доказательство SQL конкурентности или правильности настройки Windows.

## Открытые проверки и ограничения

- SQL Server отсутствует: реальное применение второй миграции, applock/serializable конкурентность, token revocation races и SQL-authoritative lease time не проверены runtime.
- Windows-служба/DPAPI/service account ACL/TLS end-to-end не запускались. Linux-сессия выполняет SQLite tests, а production Worker на Linux отказывается загружать credential.
- Only ControlPlaneProbe: archive jobs не выдаются, providers/ProcessRunner/pipeline отсутствуют; ни одна реальная ИБ не создана/изменена.
- Нет автоматического reconciliation/reissue/background expiry sweeper. Expiry фиксируется лениво при следующем mutation; stale heartbeat/report отклоняются сразу по времени SQL. Fence=1 для единственной выдачи per job, не сбрасывается.
- Событие после expiry не подтверждается даже при прошлой успешной доставке; журнал удерживается для разбирательства. Автоматической очистки или quota release по lease expiry нет.

Следующее обязательное действие: SQL workflow/стенд и ревью миграций. После него можно развивать ProcessRunner, pipeline compiler и каталог/staging, не подменяя ими техническую приёмку restore.
