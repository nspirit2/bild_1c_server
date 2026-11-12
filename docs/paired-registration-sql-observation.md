# Paired registration/SQL observation: evidence-only gate

## Purpose

Следующий компонент conservative ownership policy сопоставляет parsed RAC registration и независимый SQL database observation для одной exact operation/fence/incarnation/reservation. Успех означает только, что два отдельных чтения согласованно сообщают нужную регистрацию и ONLINE SQL-базу. Он **не устанавливает ownership**, не создаёт restore authority и не сохраняет server-side evidence acceptance.

## Exact scope composition

TargetRegistrationAssessmentScope включает полный InfobaseRegistrationReadbackScope, отдельный SqlObservationId и ExpectedProfileId. Все15RAC scope fields сохраняются без изменения. SQL observation ID должен быть непустым и отличным от RAC observation ID. Это correlation, не защита от replay и не доказательство момента наблюдения.

Для gate допустим только observed profile `rac-registration-8.3.22.2557-v1`; synthetic six-field contract не принимается. Explicit RequireMatch связывает полный RAC scope и profile. SQL scope строится из тех же operation, fence, target incarnation, canonical SQL server, SQL endpoint mapping, reservation, exact generated database name и отдельного SQL observation ID.

Нужен SQL Present с непустым database fingerprint: точное имя сравнивается Ordinal, database_id положительный, create_date не default, state ровно ONLINE. Это исключает случайный case-insensitive catalog match и unusable/offline observation. Однако SQL database_id/create_date могут быть переиспользованы/изменены; они **не durable incarnation ID** и не доказательство нашего CREATE. Нет сравнения с несуществующим историческим baseline fingerprint. Требование ONLINE — условие этого readback, не Ready/health guarantee.

RAC SQL-server literal берётся из trusted profile scope. SQL observer отдельно проверяет operator-pinned machine/instance identity и mapping перед catalog query. Assessment не разрешает DNS/SQL aliases и не доказывает физическую эквивалентность literal и SQL endpoint самостоятельно: связь этих trusted mappings по-прежнему утверждение оператора.

## Sequential coordinator

TargetRegistrationObserver выключен по умолчанию. Он принимает только expected correlation scope и credentials для ранее настроенных concrete adapters; invocation не может заменить connection factory или выбрать произвольный executable/endpoint. Все scope checks выполняются до первого I/O. После RAC read проверяется exact profile/scope **до SQL I/O**. Затем выполняется отдельный SQL read и pure assessment.

Общий monotonic budget1..120sec охватывает ожидания обоих adapters, их cleanup и assessment; проверки до/после awaited stages и перед возвратом. Clock exceptions/negative elapsed, отмена, timeout, partial outcome, SQL absence/offline и несовпадения дают fixed redacted failure. При некооперативном runner/provider это не hard preemption: coordinator ждёт завершения, не отсоединяет задачи и не возвращает ложный success после deadline. Отдельные adapter timeouts не возобновляют общий budget.

Нет transaction/snapshot между RAC и SQL и нет namespace lock/внешнего fence. Даже успешная пара может устареть сразу после чтения. Whole-budget bound не превращается в freshness authority. Provider errors/raw RAC stdout/stderr не возвращаются; runner обязан не логировать потенциально секретный вывод.

## Integration and non-authority

Existing saved-ACK bridge может подготовить registration scope из реально записанного ACK; сам public observer request не доказывает происхождение от journal. No host/CLI/DI/worker wiring добавлено, никакие operational switches не включены. Journal, permit, claim и quarantine не меняются. Нет live calls/CREATE/restore/drop/reset/adoption, SQL permission changes или migration в этом implementation slice.

Result хранит минимальный exact scope, reported infobase UUID/profile и SQL point-in-time fingerprint. Executable, FreshAuthority, OwnershipEstablished, RestoreAllowed, RetryAllowed=false; ScheduledJobsMustRemainBlocked=true; RequiresIndependentVerification=true. Это не persisted proof bundle и не server grant. Следующие отдельные gates: trusted mapping attestation, observation lifecycle/anti-replay policy, durable evidence acceptance с exact saved CREATE ACK и SQL target/incarnation validation. При неоднозначности сохраняется карантин, без ручного adoption или retry.

## Validation

Pure assessment и default-off sequential observer проверяются с parser fixtures, fake process runner и fake DbConnection. Реальные SQL/1C targets не используются. Проверяются all-pin drift, synthetic profile rejection, distinct observation IDs, exact SQL name, absent/offline database, fixed-error redaction, cancellation/clock/deadline и отсутствие SQL I/O при невалидном RAC result. Focused37newcasespassed (несколько facts дополнительно перебирают все scope pins); полный локальный Release2703passed/0failed,277SQLexplicitlyskipped; Node84passed/0failed. Независимый final source/tests/docs review — без блокеров. При review скорректированы test-only unreachable exception expectations: invalid fingerprints проверяются на отказ actual SQL adapter, valid changed reservation/name pairs — на mismatch assessment; добавлен недостающий fake provider без reflection result fabrication. CI после push фиксируется отдельно.