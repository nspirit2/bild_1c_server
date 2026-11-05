# Server creation operation: domain foundation

## Этот инкремент

Pure immutable domain model TargetCreationOperation, не SQL ledger и не executable grant. Отдельный OperationId не переиспользует observation OperationId. Pins включают RestoreIntentId/environment/stage job/reservation/artifact/agent/incarnation/canonical cluster+SQL IDs; имена выводятся только как bs_test_{reservation:N}. Stage fence в этом aggregate вообще не представлен и не выдаётся за свежий creation fence. Различие creation/observation OperationId семантическое: доменная модель не видит observation ledger и не может проверить отсутствие GUID collision; server issuer обязан генерировать и проверять отдельную identity.

Registered(revision1) → AttemptRecorded(revision2) → ReconciliationRequired, либо Registered → ReconciliationRequired. BeginAttempt требует expectedRevision и отдельный AttemptId. Exact повтор original attempt+expectedRevision после записи возвращает NewlyRecorded=false; это persistence semantics proposal, не повторный сигнал side effect. Другой attempt, stale CAS и новая попытка из terminal state отклоняются. Exact replay исходных attempt+expectedRevision после quarantine возвращает NewlyRecorded=false и сохраняет ReconciliationRequired без нового begin-сигнала. Quarantine хранит pins и attempt, не выпускает reset/release/owned/success transition. Причина — bounded enum, диагностика redacted. Executable всегдаfalse, ScheduledJobsMustRemainBlocked всегдаtrue.

Это immutable value transitions в памяти. Объект можно заново Register в другом процессе; отсутствует общая БД, поэтому нельзя заявлять server-wide exclusion, durability, uniqueness across operations или глобальную одноразовость. Никакого публичного endpoint/DI/worker/CLI/transport/EF migration и стенд changes. Не создаёт реальный permanent barrier и не меняет старые holds, квоту, stage lease, lifecycle или observation history.

## Следующий SQL срез — ещё не реализован

Нужны транзакционный rehydrate/CAS и server-issued IDs, unique RestoreIntent anchor, immutable pins, Restrict FKs/CHECK null pairing, audit атомарно с переходом, strict current actor authorization до replay, locks в существующем порядке agent-control/root/pool/canonical slots. Нельзя вызывать RestoreIntents.RequestAsync с commit вне транзакции и затем доверять его устаревшему результату; нельзя фабриковать observation assignment ради reuse validation. Existing RestoreIntent уже удерживает canonical slots; второй independently releasable hold не нужен.

Наблюдение BothObservedAbsent остаётся историей, не fresh permission. Для записи barrier historical receipt не должен молча становиться доказательством freshness или owned empty target. ObserveArchiveStage автоматически не становится ProvisionArchive. Общую проверку retained bindings нужно выделять явно с regression coverage.

Перед реализацией записывающего SQL service требуется зафиксировать operator policy: кто инициирует/записывает попытку (admin-only internal service или assigned-agent), возможность safety quarantine после отзыва project/pool authorization и какие постоянные holds потребует реальное применение. Inert domain не принимает это решение за владельца.

## Позже: executable authority

Expiry/revocation/fresh fence/lease и authentic physical target exclusion ещё отсутствуют. BeginAttempt не разрешает внешний вызов. Новый coordinator должен отдельно получить fresh authority, сверить artifact и physical ownership/incarnation, исключить конфликты/сессии, фиксировать каждый externalstep до вызова и reconciliation после неизвестного исхода, не делать слепой retry.

Scheduled-jobs deny обязателен до/после restore и после restart, с отдельными readbacks и epoch correlation. Security profile не добавляется как mandatory gate. Реальный CREATE/restore потребует отдельного согласования владельца.

## Проверки

Unit tests проверяют immutable pins, Register/Begin/Quarantine, CAS/replay, invalid IDs/revisions/enums, фиксированные diagnostics и отсутствие execution/Ready/success transitions. Локальный locked restore/full Release:1267passed/0failed (Unit71,26новыхdomaincases;166SQLexplicitlyskippedбезopt-in, неSQLvalidation). Независимый review выявил replay-after-quarantine mismatch; исправлено хранением AttemptExpectedRevision и exact replay terminal false-return, testsпрошли. CIпослеpushотдельно. No live changes.
