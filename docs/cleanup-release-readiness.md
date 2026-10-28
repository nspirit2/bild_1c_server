# Готовность cleanup-этапа к review

## Итог реализации

Cleanup — отдельная цепочка исторических подтверждений, а не восстановление или удаление базы 1С:

1. Человек создаёт retirement intent для точного staged artifact.
2. Одобренный агент получает immutable cleanup authorization.
3. Локальный executor проверяет identity, manifest и hash, использует lock и crash-safe tombstones.
4. Успешное локальное удаление атомарно создаёт запись result outbox в SQLite.
5. Явная одноразовая команда доставляет результат по HTTPS; точный receipt завершает доставку.
6. Администратор отдельно освобождает staging allocation по принятому результату.
7. Read-only API/UI показывают цепочку и net staging bytes, не предоставляя автоматического удаления.

`ContentVerified`, `DeletedLocal`, `DeletedReported` и `StagingReleased` — разные события. Server receipt не является доказательством текущего наличия файла, пригодности архива или готовности базы.

## Границы accounting

Release исключает allocation из staging-сумм **и root, и pool**. Исторический размер staging сохраняется. DB `ReservedBytes`, reservation `Held`, environment quota и unsettled-состояние не освобождаются. Полный lifecycle SQL/IB deletion не реализован этим этапом.

## Доказательства проверки

| Уровень | Результат |
| --- | --- |
| Локальные .NET suites | 508 passed, 0 failed; 114 SQL cases явно skipped без opt-in |
| UI behavioral tests | 38 passed |
| Dedicated Windows, настоящий SQL Server | 114 passed, 0 failed/skipped; disposable DB leftovers 0 |
| Windows agent contracts | 272 passed, включая 3 настоящих loopback TLS cases |
| Windows API contracts | 105 passed предыдущим portable-прогоном |
| Dependency graph | `dotnet restore BuildSystem.slnx --locked-mode` успешно |
| EF model | `has-pending-model-changes`: изменений после последней миграции нет |

SQL + file/SQLite cycle, API pipeline, TLS и human cookie проверены разными слоями:

- [File/SQLite/SQL result delivery](cleanup-result-delivery.md): потерянный ответ, reopen и exact replay.
- [Реальный loopback TLS](cleanup-real-tls.md): точный certificate pin, Bearer/body, wrong pin, запрет redirect.
- [Настоящий API pipeline + SQL](cleanup-api-pipeline.md): реальные authentication/authorization, rotation/revocation и default-off.
- [Identity cookie и CSRF](cleanup-admin-cookie.md): реальный login и admin release, границы human/machine authority.
- [HTTP request boundaries](cleanup-request-boundaries.md): malformed input и известный Content-Length cap без побочных записей.
- [HTTP concurrency](cleanup-http-concurrency.md): шесть параллельных повторов создают по одному result/release и audit.

TestServer HTTPS scheme не доказывает TLS. CookieContainer не доказывает browser SameSite. Content-Length тест не доказывает Kestrel chunked-body cap. Windows archive tests могут пропускать отдельные reparse-сценарии при отсутствии symlink privilege; общий passed count не доказывает их выполнение.

## CI

Последний полностью завершившийся пакет перед этой сводкой: `5c48125`, [Build 40733202927](https://github.com/nspirit2/bild_1c_server/actions/runs/40733202927) и [SQL 40733202755](https://github.com/nspirit2/bild_1c_server/actions/runs/40733202755) — success.

Последний code-пакет `2a5a5ce`: [Build 40733874692](https://github.com/nspirit2/bild_1c_server/actions/runs/40733874692) — **success на Windows и Ubuntu**, [SQL 40733874657](https://github.com/nspirit2/bild_1c_server/actions/runs/40733874657) — **success**. CI gate текущего cleanup-этапа закрыт. Повторный локальный TRX-анализ подтвердил ровно 508 Passed, 114 NotExecuted, 0 Failed; Node — 38 Passed.

Исторический Windows test-step failure `33b18d0` не воспроизвёлся в следующих двух пакетах. Причина не установлена; добавлена публикация имён failed tests из TRX без exception bodies. Не заявляется исправление неизвестной причины.

## Что не менялось на рабочем стенде

Рабочий IIS/API остаётся на ранее развёрнутом `0ca1567`. Новые cleanup migrations, endpoints и UI туда не выкатывались; основная BuildSystem DB по-прежнему имеет 4 миграции. Автоматический executor/worker не включён. Agent service не запускался ради этих тестов. Существующий synthetic stage artifact и занятая им DB/environment quota не освобождались.

Все тестовые SQL-БД создавались и удалялись только под fixture-owned именами `bs_ci_metadata_GUID`. Обновление cert trust, IIS bindings, firewall, service identity и production feature flags не выполнялось. Слияния в `main` нет.

## Предпосылки безопасного enablement

По умолчанию выключены `ArchiveStage:Enabled`, `StageRetirement:Enabled`, `StageCleanup:Enabled`, `StageCleanupResult:Enabled` и `StagingAllocationRelease:Enabled`. `StageRetirement` управляет выдачей плана агенту, не является общим выключателем human intent. Флаги необходимо включать отдельно и только согласованным change package.

Executor требует service-only ACL для source/staging/journal, single-owner journal и согласованного stage lock. Уже выданная cleanup authorization durable и не истекает: отключение функции или ротация/отзыв токена запрещают будущие claims, но **не отзывают доставленное полномочие** и не возвращают удалённые байты. Это должно быть явно учтено в backup/rollback-плане, а не скрыто за переключателем.

## Следующий релизный gate

Этот этап можно завершить как реализацию и проверку cleanup-протокола после зелёного последнего CI. Это **не первый полный релиз продукта**. Перед ним остаются:

- review и согласованное merge/deploy решение, backup/rollback миграций;
- explicit smoke нового API/agent с protected service credentials и реальным IIS TLS;
- реальный архив 1С → личная тестовая база и проверка результата восстановления;
- отдельное domain-решение для DB/IB lifecycle и освобождения environment quota;
- политика automatic deletion и эксплуатационные security/patching вопросы — без скрытого включения.

Автоматическое удаление, DB quota release и production rollout требуют отдельного решения, а не следуют из прохождения тестов.
