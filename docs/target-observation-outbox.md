# Durable outbox historical target observations

Последующее локальное усиление: полный Release **1076passed/0failed**, Agent471cases (9новых cross-role/ACK/reopen/legacy collision checks), SQL164skipped без opt-in. Locked restore/diff check и независимый read-only review успешны; schema/Worker/production не менялись. Локальная проверка теперь симметрична серверному evidence-ID conflict guard.

## Назначение

Observation control plane начинает с bounded protocol и immutable durable outbox. Assignment — будущий server-assigned request со строгими intent/agent/operation/environment/stageJob/fence/reservation/incarnation/canonical targets и endpoint mapping pins. Сам по себе полученный объект не доказывает authentic server issuance: серверная issuance/claim/authentication ещё отдельный этап. Нет stage-lease promotion.

Успешный полный paired outcome сохраняется до HTTP доставки. Receipt имеет собственный ID, immutable assignment, отдельные RAC/SQL observation IDs, один из четырёх occupancy outcomes и timestamp наблюдения. Credentials, stdout, SQL diagnostic, connection strings, paths и executable instructions не хранятся. Исполнение всегда false, scheduled-jobs mandatory всегда true. Timestamp — label агента, не server-attested freshness.

После lost ACK/restart отправляется тот же receipt, не новый timestamp и не повторное наблюдение. Exact enqueue replay idempotent даже после ACK; receipt ID/request/payload конфликт отвергается. Любой RAC/SQL observation ID сохраняет идентичность независимо от роли и остаётся зарезервированным между requests и после ACK: journal API откажет до enqueue, включая cross-role collisions. Legacy/raw SQL corruption не считается доказуемо обнаруженной глобально: это application-API guard, а не server-level UNIQUE constraint для union двух столбцов. На один observation request допускается ровно один outcome/receipt навсегда. ACK только отмечает receipt delivered, не удаляет историю и не освобождает slots/artifact. Локальный journal single-owner SQLite/FULL synchronous не является distributed lock; observations не переводят creation intent в CreationInFlight и не меняют leases/quotas.

Pending читает только bounded первую страницу; это delivery queue, не полнота всех historical observations и не очередной permit. Assignment с отсутствующим report может требовать нового read-only наблюдения по будущему orchestration policy, но этот срез не запускает execution/retry/reconciliation автоматически. Partial/failure result не маскируется под absence.

## Доверительные границы

App validation проверяет все nonempty GUIDs/положительный fence, distinct observation IDs, enum/timestamp, exact known assignment и canonical replay. Storage constraints дополняют протокол. Изменение файла/копия journal вне singleowner assumption не предотвращается как атака: read corruption fail-closed где возможно, но DB не crypto evidence. Server acceptance обязана отдельно проверить actor/assignment/fence/current bindings; локальный outbox не делает payload authentic.

Никакого Worker/CLI/API route/HTTP delivery implementation, production migration/config, CREATE/DROP/restore, Ready, ownership adoption, release или security-profile gate в этом срезе. Scheduled-jobs-deny=on до/после restore и после restart остаётся mandatory.

Pending отбирает acked=0: искусственно испорченный вне CHECK значение acked=2 не попадает в pending выборку, но явный replay/ACK такого receipt отклоняется полным reader validation. Это не обещание exhaustive integrity scan всего SQLite; trusted local single-owner file assumption остаётся.

Полный локальный Release **874passed/0failed**, SQL138explicitly skipped без opt-in. AgentTests310passed включая26новыхcases. Locked restore и diff check successful; пакеты/SQL EF migrations не менялись. Предыдущий coordinator commitd5a065c BuildWindows/Ubuntu и SQL138 CI прошёл; новый outbox не меняет SQL server metadata и не требует rollout.

Commit3ce3245: [Build41620650868](https://github.com/nspirit2/bild_1c_server/actions/runs/41620650868) success Windows/Ubuntu; [SQL41620650869](https://github.com/nspirit2/bild_1c_server/actions/runs/41620650869) success138/138.

## Проверки

Review учёл SQLite dynamic typing: GetInt64 может нормализовать повреждённый REAL payload в integer; новый reader требует actual long storage type для fence/outcome/ack, а GUID и timestamp — canonical representation. ACK сначала валидирует полный retained report, не blindly принимает malformed row.

Независимое review нормализованных tables/readers не обнаружило дополнительных transaction/replay/ACK bugs. Filtered Release26/26passed. Tests проверяют reopen assignment/pending/ack history, exact replay после lost ACK и ACK, conflicts каждого pin/outcome/time/receiptID, неизвестные assignment/report, bounds/malformed raw storage, additive init старого journal, отсутствие изменения stage/creation lifecycle.
