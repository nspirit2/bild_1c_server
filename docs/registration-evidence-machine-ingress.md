# Bounded machine ingress for historical registration evidence

## Explicit opt-ins

POST /api/agent/target-creation/registration-evidence доставляет только existing immutable historical report. Независимые API TargetRegistrationEvidenceEndpoint:Enabled и SQL-port TargetRegistrationEvidenceReceipts:Enabled выключены по умолчанию; настройки не включаются этим срезом. Оба switches проверяются до чтения body и вызова SQL port. HTTPS обязателен; machine path не превращается в redirect с credential-bearing request.

Это не agent worker/outbox, не automatic delivery, не executable claim и не ownership promotion. Существующий service требует current Approved agent token, retained claim/assignment и current credential metadata даже для exact replay. Stable ClaimId сохраняет idempotency при потерянном HTTP ACK. Отсутствие ответа никогда не разрешает повторить CREATE/restore.

## Strict transport shape

Принимается один exact Bearer token, не browser session. Origin/query/Cookie requests отвергаются; forwarded scheme доверяется только existing trusted-proxy configuration, не arbitrary X-Forwarded-Proto. JSON media type — application/json, optional UTF-8 charset, без suffix aliases/прочих параметров. Body bounded8192bytes, включая chunked requests, с awaited cancellation/cleanup без detached reads.

Report содержит ровно13 camelCase properties: claimId, reportedInfobaseId, racObservationId, sqlObservationId, racEndpointMappingId, sqlEndpointMappingId, registrationProfileId, infobaseName, sqlServer, databaseName, sqlDatabaseId, sqlCreateDate, sqlState. Duplicate/unknown/case-aliased fields, nested values, noncanonical/nonempty UUID, нецелые/неположительные SQL IDs и invalid strings отвергаются. Authority booleans, credentials, stdout/stderr и permission-code не входят в payload.

SQL date — original canonical27/28/33 DateTime O metadata, включая foreign +03:00 без local-zone conversion. Profile exact rac-registration-8.3.22.2557-v1, state ONLINE, target name/mapping coherence проверяется retained SQL service. Reported observation mappings по-прежнему не physical endpoint attestation и не creation credential pair mapping.

## Output and failure safety

Success200 — explicit bounded inert receipt: ClaimId/OperationId/Recorded/AcceptedAt, no execution grant. Endpoint проверяет receipt identity/status/UTC shape перед сериализацией. Unknown provider, serializer, DI или arbitrary RequestRejected errors становятся fixed sanitized unavailable response, без inner/message/code leakage. Known failures проходят только explicit allowlist. Cache-Control:no-store, Pragma:no-cache и nosniff выставляются на machine responses; started/aborted response не заменяется вторым success/error payload.

Request cancellation может наступить после durable SQL commit. Transport не отменяет историю и не пытается rollback/retry physical operations. Следующая explicit доставка same ClaimId/full payload проходит existing current auth + exact immutable replay, а не regenerates IDs.

## Validation

API fake-port tests проверяют gate order, exact auth/browser exclusion, HTTPS, bounded media/body/parser, duplicate/unknown fields, canonical UUID/date/integer checks, current service failure codes, malicious secret code sanitization, receipt shape и inert flags. Existing SQL CI отдельно проверяет настоящую authentication/claim/replay/persistence; fake-port HTTP tests не подменяют SQL proof. Новые45expanded HTTP cases проходят в полном локальном Release:2825passed/0failed,289SQLexplicitlyskipped; Node84passed/0failed. Parent завершил Program/options/DI wiring после незавершённого source delegation и исправил ValidName: ведущие/замыкающие whitespace отвергаются без trimming. Первичный focused прогон44pass/1fail обнаружил именно этот guard; полный Release после исправления зелёный. SQL service/replay ранее проверен real SQL CI7abb3e5; Real SQL CI b7222b1 success (43820927634). Build run43820927622 отменён (Cancelled), не считается зелёным или test failure; documentation-only follow-up запускает повторную проверку без изменения implementation/tests.