# Server-side historical registration evidence receipts

## Scope

Independent default-off application port принимает explicit bounded report от currently Approved assigned agent. SQL transaction сохраняет только **authenticated agent assertion**: агент сообщает о своём saved CREATE ACK и paired RAC/SQL readback. Server не наблюдал ACK/process/RAC/SQL непосредственно. Это не server-certified physical target identity, successful CREATE, ownership, observation freshness или restore authority.

Нет HTTP endpoint, DI enablement, worker delivery/outbox, admin readback endpoint или live schema deployment в этом срезе. Новая EF migration проверяется автоматизированным SQL CI, не применяется к стенду. Existing server operation может оставаться AttemptRecorded rev2, пока local journal находится в quarantine: receipt не синхронизирует эти states неявно.

## Identity and replay

Stable key — existing durable ClaimId, а не новый случайный ReceiptId на каждую отправку. Server получает current claim/permit/operation/restore intent и фиксирует retained agent, attempt, credential, creation pair mapping, canonical IDs, fencing, reservation/incarnation и exact target names. Report содержит только claim reference, reported infobase UUID, distinct RAC/SQL observation IDs, reported observation mappings/profile/names/SQL literal/fingerprint. Ни caller authority flags, ни raw stdout/stderr, permission-code, credentials или envelope не принимаются.

SQLCreateDate передаётся canonical DateTime `O` string, а не DateTimeOffset: SQL provider Kind не объявляется UTC и дата не становится durable incarnation. AcceptedAt — отдельное server UTC acceptance time из SQL clock, не выдуманное observation time. В local evidence нет ObservedAt; сервер его не синтезирует.

Exact lost-ACK replay возвращает original AcceptedAt только после current authentication, retained admission и current credential metadata checks. Изменение любого payload/pin отвергается. ClaimId/operation/attempt и RAC/SQL IDs immutable; SQL guards отвергают UPDATE/DELETE и cross-role collisions, включая batch insert. Serializable transaction + existing agent-control lock сериализуют service writers. Ordinary SQL protections не защищают от privileged DBA, отключившего constraints/triggers.

## Current authorization, historical issuance

Перед любым replay: current Approved token authentication, claim.AgentId/intent.AgentId equality, retained admission checks и current credential metadata Stored/rev1/unrevoked. Metadata projection не загружает encrypted envelope. Wrong/rotated/revoked token и revoked credential не получают exception для historical replay.

Own receipt switch независим от executable issuance switches. Истёкший claim или retained revoked started permit не возобновляет authority, но сам по себе не запрещает historical report, если current authorization/admission/bindings всё ещё удовлетворяют проверкам. Original claim timestamps должны быть coherent с original started permit; future/corrupt issuance не принимается. Текущие admission restrictions не обходятся ради старых claims.

## Mapping trust boundary

CreationEndpointMappingId берётся из claim и credential metadata. Это **не** RacEndpointMappingId или SqlEndpointMappingId из report. Reported observation mappings и SQL server literal сохраняются как agent/operator assertions; они не превращаются в independently authenticated physical endpoints. Exact target names сравниваются с server-retained restore intent. Нельзя выводить canonical SQL GUID или непрерывность DB incarnation из name/database_id/create_date/ONLINE.

## Atomicity and output

Новая запись и audit event commit атомарно; нет изменений operation/intent/lifecycle, lease, holds, namespace/reservation или credential. Receipt конструируется только после commit и disposal. Historical execution-strategy replay допустим благодаря stable immutable key, в отличие от fresh executable grant issuance. Provider failures сворачиваются в fixed redacted error; ambiguous commit может означать, что row уже существует, поэтому later explicit same-payload delivery использует stable ClaimId. Это не разрешение повторить CREATE/restore.

Receipt всегда inert: Executable/OwnershipEstablished/RestoreAllowed/RetryAllowed/FreshAuthority=false; RequiresIndependentVerification/ScheduledJobsMustRemainBlocked=true. Полноценный transport/outbox и admin diagnostics остаются следующими отдельными срезами.

## Validation

Проверяются default-off/no SQL, malformed requests, current auth before replay, claim/permit/intent coherence, expired issuance history, immutable exact replay/changed payload, current credential revocation, cross-role observation-ID reuse, SQL append-only guards, concurrent delivery и отсутствие authority-state changes. Полный локальный Release2764passed/0failed,283SQLexplicitlyskipped (включая6новых SQL cases); Node84passed/0failed. Existing model-drift tests подтверждают snapshot/current model без SQL connection. Review выявил и исправил cross-host LocalO date normalization и disposed-context cleanup; SQL fixture использует retained реальные alternate FK records вместо invalid random FK. New suite:1offlineFact+6conditionalSqlServerFacts с loops. Реальная SQL execution, миграция disposable metadata DB и concurrency проверяются отдельным SQL CI после push, conditional skips не считаются их доказательством.

Первый real SQL CI ffc360d выполнил suite и выявил одну test-only expectation: crossed operation относится другому agent, поэтому current auth корректно возвращает401 AgentUnauthorized, а не409 structural mismatch. Expected401 теперь задан для agent и operation; остальные четыре chain cases остаются409. Production guard не изменялся; review подтвердил auth-first смысл. Повторный локальный Release/Node прошёл; clean SQL rerun фиксируется отдельно.

Перед ownership/restore promotion всё ещё нужно отдельное решение о physical endpoint trust и независимом SQL incarnation evidence; этот receipt gate не подменяет их.