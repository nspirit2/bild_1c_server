# Агентский пакет: результаты проверки

Ветка `feat/agent-control-plane`; предыдущий серверный пакет — коммит `2a627fa`, агентский пакет — `22715d7`. Обе ветки отправлены в origin. Main не менялся.

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

## Подтверждённый SQL-прогон GitHub Actions

[Run 39792665034](https://github.com/nspirit2/bild_1c_server/actions/runs/39792665034), коммит `22715d7d581dc2dbeca5baab3d54a58a75c6196c`, SQL Server 2022 CU20 в одноразовом CI-контейнере. Workflow завершён успешно; предоставленный пользователем артефакт `sql-integration-results.zip` прочитан непосредственно как ZIP/TRX. SHA-256 ZIP: `c88b0cd915e50179614cbf896fe260121ae303fe736df099b0cf4ca3db483743`.

TRX `_runnervmxehtw_2026-10-22_14_06_06_net10.0.trx`: **total 16, executed 16, passed 16, failed 0, error 0, timeout 0, aborted 0, notExecuted 0**. Все UnitTestResult имеют outcome Passed. Пропущенных SQL-тестов в этом CI-прогоне нет; локальные 16 skipped выше остаются историческим результатом Linux-среды без SQL.

На настоящем SQL выполнены миграции, идемпотентность и конфликт payload, атомарность при отказе квот, конкурентные owner/pool квоты, сохранение данных через новый DbContext, изоляция владельцев и ACL/replay. Агентские тесты подтвердили enrollment expiry/reuse, approve/rotate/revoke и invalidation, probe-only выдачу, конкурентный poll/повторную доставку, stale/expired heartbeat без reissue, event replay и удержание reservation. Это подтверждает конкретные проверенные сценарии, не исчерпывающую корректность всех отказов/нагрузок.

## Открытые проверки и ограничения

- Корпоративный SQL-стенд, реальные service principals/least-privilege permissions и нагрузочные/длительные отказные сценарии не проверены. Одноразовый SQL CI не заменяет целевую инфраструктурную приёмку.
- Windows-служба/DPAPI/service account ACL/TLS end-to-end не запускались. Linux-сессия выполняет SQLite tests, а production Worker на Linux отказывается загружать credential.
- Only ControlPlaneProbe: archive jobs не выдаются, providers/ProcessRunner/pipeline отсутствуют; ни одна реальная ИБ не создана/изменена.
- Нет автоматического reconciliation/reissue/background expiry sweeper. Expiry фиксируется лениво при следующем mutation; stale heartbeat/report отклоняются сразу по времени SQL. Fence=1 для единственной выдачи per job, не сбрасывается.
- Событие после expiry не подтверждается даже при прошлой успешной доставке; журнал удерживается для разбирательства. Автоматической очистки или quota release по lease expiry нет.

SQL workflow выполнен, его результаты проверены по TRX. Следующие действия: ревью/слияние пакетов в согласованном порядке, проверка Windows-службы/DPAPI на целевом стенде. Можно развивать ProcessRunner, pipeline compiler и каталог/staging, не подменяя ими техническую приёмку restore.
