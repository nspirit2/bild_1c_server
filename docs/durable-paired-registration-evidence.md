# Durable paired registration evidence: local historical metadata

## Scope

После exact CREATE ACK и paired RAC registration/SQL observation добавляется append-only локальная запись исторического evidence. Это сохранение проверенных correlation metadata, **не ownership acceptance**, не свежая authority, не разрешение restore и не свидетельство continuing SQL incarnation. Snapshot может устареть до записи; SQL fingerprint не становится durable target identity из-за persistence.

## Exact saved-ACK binding

RecordTargetRegistrationEvidence читает именно journal.GetTargetCreationAcknowledgement для operation внутри одной SQLite transaction с append. До insert проверяются saved intent/binding/claim/reported infobase UUID, quarantine ReconciliationRequired/revision3/OutcomeUnknown и полное совпадение assessment operation/fence/incarnation/reservation/canonical cluster/SQL/attempt/claim/UUID/generated names.

Assessment должен относиться к observed RAC profile8.3.22.2557, содержать distinct RAC/SQL observation IDs, exact SQL database name, positive database_id, nondefault create_date, ONLINE. Новые observation mappings и SQL server literal сохраняются как caller-trusted configuration assertions: сам journal не делает endpoint discovery, не проверяет binary/hash и не удостоверяет физическое соответствие RAC/SQL servers.

Отсутствие actual durable ACK — отказ. Public acknowledgement record, обычный quarantine без ACK, CLI bool или совпадение имени не являются основанием записи. Исходные permit/claim timestamps сохраняют исторический смысл и не возобновляют deadline. Не записываются executable grant, password, permission-code, stdout/stderr или JSON blob неизвестной схемы.

## Append-only storage

Одна запись на operation. Попытка повторной записи, в том числе с иными observation IDs, отвергается; old evidence не заменяется и не интерпретируется как новый успешный observation. Оба observation ID уникальны во всём наборе, включая cross-role RAC↔SQL reuse. SQL triggers блокируют INSERT OR REPLACE через любой конфликт, UPDATE и DELETE, включая ordinary direct SQL mutation paths. Это не защита от администратора, изменяющего schema/файлы; строгий getter остаётся отдельной проверкой.

Append, exact ACK verification и commit выполняются в одной local transaction. Record возвращается только после commit/disposal; storage failure не даёт receipt и не запускает fallback/retry. Existing quarantine/intent/binding/ACK не изменяются. Getter валидирует canonical GUIDs, числа/длины, profile, scopes, fingerprint и точное связывание с saved ACK; malformed/corrupt state выдаёт fixed redacted failure без исходного текста/inner exception.

Все20columns хранят allowlisted canonical TEXT; fence/database ID дополнительно проверяются как canonical positive decimal без знаков, пробелов и leading zeros, без SQLite numeric coercion. SQL create_date сохраняется в round-trip O representation с kind, полученным от provider. Local-kind representation зависит от timezone: перенос между timezone не обещается, canonical roundtrip mismatch отвергается fail-closed. Это не UTC observation time, не server timestamp authority и не подтверждение порядка CREATE/restore. Нет newly observed At, clock freshness или anti-replay lease. Get возвращает исторические данные, не executable authority; отсутствие записи не доказывает отсутствие физической попытки или observations.

## Operational boundaries

AgentJournal initialization добавляет локальную SQLite table/triggers; это writable schema initialization, не forensic-read-only mode и не stand migration. CLI/worker/DI, delivery outbox, server ownership acceptance, observation invocation или restore wiring не добавляются. Новых live probes, CREATE/restore/drop/reset/release/enablement нет.

Executable, FreshAuthority, OwnershipEstablished, RestoreAllowed, RetryAllowed=false; RequiresIndependentVerification=true; ScheduledJobsMustRemainBlocked=true. Restart не отменяет quarantine и не разрешает retry. Historical evidence не доказывает provenance вызвавшего process, atomic cross-system snapshot, external administrator fencing или физическую подлинность targets.

Следующие gates отдельно: trusted endpoint/mapping attestation, policy observation lifecycle и freshness, stronger SQL incarnation reasoning, server-side exact append-only evidence acceptance и отдельная default-off restore authority. Unambiguous ACK + persisted point-in-time observations пока НЕ означают, что все ownership prerequisites выполнены.

## Validation

Локальные SQLite fixtures и actual pure assessment построены через bounded RAC parser и SqlDatabaseObserver с fake DbConnection. Никакие SQL/1C targets не используются. Проверяются reopen metadata, inert flags/state unchanged, noACK rejection, exact scope drift, same/cross-role observation conflicts, immutable SQL paths, injected insert failure/rollback и strict getter corruption/redaction. Добавлены30cases: DateTimeKind3, missing/noACK, exact ACK pin drift, raw four cross-role collisions с trigger-specific1811 и unchanged count, API four-role collisions двух saved ACK, immutable SQL paths, ABORT/IGNORE inserts, metadata corruption, nested ACK/quarantine corruption и disposed journal. Полный локальный Release2733passed/0failed,277SQLexplicitlyskipped; Node84passed/0failed. Независимый final source/tests/docs review — без блокеров; test-only reason corruption использует разрешённый CHECK value TargetConflict, чтобы проверить именно strict saved ACK gate. Первый CI6ae2f79: SQL metadata integration success; Build and test отменён (Cancelled, annotation «The operation was canceled»), не считается ни успешным, ни доказанным test failure. Повторный CI запускается documentation-only commit без изменения implementation/tests; результат фиксируется отдельно.