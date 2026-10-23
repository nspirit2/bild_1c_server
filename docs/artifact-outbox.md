# Durable доставка artifact receipts

## Назначение

SQLite outbox сохраняет metadata отчёта до HTTP. Повтор после потери ответа использует тот же ArtifactId и pinned payload. AgentJournal по-прежнему единственный владелец data directory; работа с одним экземпляром journal сериализована, concurrent вызовы не поддерживаются.

Shared wire records ReportStagedArtifact / StagedArtifactReceipt перенесены из Application в BuildSystem.Contracts без изменения JSON. Серверный API/SQL validation сохранены. Это source-level namespace change для .NET callers, не wire schema change.

## Сохранение и replay

Отдельная таблица artifacts содержит только job/fence/reservation/version/artifact, hash/size/format, delivery state и закрытый failure code. Не содержит token, исходный путь, snapshot или CLI output. New enqueue разрешён только для незавершённого живого ProvisionArchive lease. Одинаковый canonical report идемпотентен; изменённый payload или попытка перепривязать artifact/job/reservation/version отклоняются, включая ранее Acked записи.

Lease kind добавлен совместимой локальной schema migration. Старые записи получают Unknown, не выдуманный ProvisionArchive: artifact enqueue и probe recovery для Unknown закрыты. Существующие event/lease rows не удаляются.

Dispatcher обрабатывает не более одного отчёта за FlushAsync. Сначала persisted Pending, затем HTTP, затем Acked. Потеря ответа оставляет Pending и разрешает exact replay. Artifact ack НЕ завершает lease и НЕ освобождает reservation; после рестарта worker не отправляет probe completion/unknown для provisioning lease.

## Безопасные отказы

- Истёкший локально lease: durable Blocked/LeaseExpired без HTTP.
- Уже завершённый локально lease: Blocked/LeaseRejected.
- 401/403: AuthRejected; конфликт/неподходящее задание и другие постоянные ошибки: LeaseRejected.
- Некорректный ArtifactId/state в receipt: ReceiptMismatch.
- Сетевой сбой, server timeout,429/5xx: RetryLater с сохранением Pending.
- Caller cancellation не считается ack. При неизвестном ответе повторяется тот же отчёт. Неизвестные HTTP статусы (включая нераспознанные4xx/redirect) сохраняют Pending с RetryLater, но не подтверждаются; до expiry либо вмешательства оператора повтор возможен. Production transport redirect не выполняет.

Blocked не очищается автоматически. Это сигнал к отдельному reconciliation, а не разрешение создать новый ArtifactId, удалить копию или повторить restore. Нет автоматического endpoint разблокировки/GC. Сервер authoritative: accepted receipt может прийти уже после локального deadline, тогда корректный ack сохраняет установленный сервером факт, но не продлевает lease.

## HTTP и Worker

Новый transport использует существующий HTTPS origin/token, не следует redirects, не использует cookie и не отключает TLS validation. Ответ bounded16KiB; ошибки не публикуют body/token. Custom HttpMessageHandler доступен только internal test constructor, не production config.

ProbeWorker сначала обслуживает persisted artifact outbox и не poll-ит новые задания при незавершённой доставке. После ack provisioning lease остаётся unsettled и блокирует poll. Без artifact report и без существующего lease поведение probe прежнее. Задания ProvisionArchive всё ещё не выдаются Poll: capabilities остаются только ControlPlaneProbe.

Этот пакет не создаёт отчёт после stage автоматически: для этого нужен полноценный lease-aware stage handler, сверка локального artifact manifest и серверного immutable plan. Crash между публикацией копии и enqueue, heartbeat provisioning jobs, continuous lease monitoring и server-side stage completion ещё не закрыты. Нельзя считать durable доставку полным archive pipeline.

## Приёмка

Тесты используют реальные SQLite файлы и fake HTTP; они не доказывают TLS/SCM/DPAPI configuration на стенде. Фактический локальный результат: locked restore успешен, Release build0 warnings/0 errors; **211 passed, 0 failed, 26 SQL skipped**. Разбивка: domain39, API40, Agent61, Archive23, Process22, Pipeline26. В Agent61 входят прежние11 и новые50: journal10, HTTPS transport28, delivery/worker integration12. Проверены real SQLite reopen/replay, loss response, блокировки401/403/409/410, transient429/5xx, expiry, mismatch, cancellation, запрет poll/probe recovery после provisioning artifact ack, старый journal kindUnknown. Fake HTTP tests не доказывают реальный TLS.

Read-only ревью не выявило дополнительных серьёзных false-ack/crash/idempotency ошибок после исправлений format validation/canonical hash и stream IO retry. Обнаруженный на промежуточном запуске неверный format predicate исправлен и полный прогон повторён успешно.

SQL модель не меняется, has-pending-model-changes drift не обнаружил; предыдущий receipt commit473e44a имеет successful Windows/Linux и SQL CI, но это не проверка нового agent кода. Новый пакет пока локальный, без commit/push/merge. Windows/SQL CI для текущих изменений ещё не запускались.
