# Проекты и архивы в операторском UI

В `/stage` добавлена отдельная read-only панель выбора проекта и просмотра Ready archive versions. Загрузка только по явной кнопке, без фонового polling и без создания заказов. Версии отображаются безопасными IDs/format/bytes, без source path/hash/agent identity. Restore/Ready окружения не добавлены.

## API

Новый GET `/api/projects/browse?limit=25&cursor=...`: page Items(ProjectId,Name),NextCursor,ObservedAt. Enabled projects и current membership либо admin, name SQL-side bounded256 UTF16units, exact2field allowlist. Limit1..100, cursor512 canonicalactorrolebound, lowerGuidBIN2ASC, read-onlySerializable+SQLUTC projectionTakeLimit+1. Name bounding через DATALENGTH/BIN2 SUBSTRING учитывает trailing spaces/SC collation, но не гарантирует сохранения целого surrogate pair при усечении; UI не интерпретирует текст как HTML.

Существующий GET `/api/projects` с legacy array сохранён: первоначальный новый duplicate route обнаружен HTTP tests (AmbiguousMatch), исправлен отдельным `/browse`, без молчаливой смены wire legacy shape. Archive route прежний `/api/projects/{id}/archive-versions`.

## UI

Lazy explicit browse action, project select с validated GUID, отдельная кнопка загрузки archive page. Projects и archives имеют независимые cursor histories. Отмена+generation guards, logout/filter/main context очищают и скрывают panel, смена проекта не принимает late archive response. Все names/values — DOM textContent/createElement, без storage/unsafeHTML. Нет submit order POST; любые будущие заказы потребуют CSRF и stable idempotency identity.

## Проверки

Локально **321 .NET passed,0 failed,63 SQL skipped**, **15 Node behavior passed** (предыдущие9+новые6UI cases). API76, domain39, Agent135, Archive23, Process22, Pipeline26. Новые4 SQL facts и6 API/application cases. SQL expression translation дополнительно проверена ToQueryString. SQL guard63.

### Реальный Windows SQL прогон

27.10.2026 на согласованном dedicated BuildSystem host, SQL Server2019 Standard15.0.2000.5, .NET10.0.14: **63 passed,0 failed,0 skipped**,16s. TRX counters повторно прочитаны. Test connection: Initial Catalog=master, integrated auth, Encrypt=True/TrustServerCertificate=False, explicit ALLOW_CREATE=true. Каждый fixture создаёт уникальную bs_ci_metadata_GUID DB, удаляет только собственную после успешного CREATE. После suite: remaining pattern DBs0, основная BuildSystem существует. На application DB тесты не направлялись.

Первый test runner из Linux SDK дал BadImageFormatException до выполнения тестов. Исправлено использованием официального Microsoft.TestPlatform.Portable18.0.1 NuGet tools/net8.0, не установкой SDK на production. Второй runner завершил exit0. Windows test directory `E:\BuildSystem\TestRuns\ProjectBrowse-20261027` и uploaded installer archives оставлены для evidence/repeat; test processes завершены, DB leftovers отсутствуют. Main API binaries/config/pool/schema не обновлялись этим прогоном. Real domain browser E2E по-прежнему не выполнен.

Raw TRX содержит environment data — хранится на сервере и временной локальной копии, не публикуется как открытый артефакт. No passwords/tokens in connection. Режим разрешён владельцем до первого полного релиза; после релиза scope пересогласовать.

Следующий шаг: форма заказа с идемпотентным POST, а не восстановление архива. Deployment новой UI версии — отдельный проверенный шаг; текущий host остаётся на c3a665a.
