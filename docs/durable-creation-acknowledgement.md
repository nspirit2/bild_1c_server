# Долговечное RAC ACK без подтверждения владения

Владелец принял conservative policy: подтверждение владения требует сохранённого однозначного результата нашей попытки и независимой проверки точной RAC/SQL цели с reservation. Потерянный/неоднозначный результат остаётся в карантине: без усыновления, retry CREATE или restore. Restore authority — отдельный default-off механизм. Принятие этой политики не разрешает стендовые операции.

## Что реализует этот срез

Ранее strict RAC classifier возвращал ReportedInfobaseId, но coordinator сокращал его до эфемерного bool CreationAcknowledged. Теперь allowlisted UUID и исторические claim pins сохраняются в локальном durable journal вместе с quarantine. Это первый необходимый компонент протокола, НЕ признание цели нашей или готовой.

Новая запись TargetCreationAcknowledgement содержит Intent, Binding, ClaimId, ReportedInfobaseId, ClaimIssuedAt, ClaimExpiresAt. Fixed inert flags: Executable=false, FreshAuthority=false, RetryAllowed=false, RequiresIndependentVerification=true, ScheduledJobsMustRemainBlocked=true, OwnershipEstablished=false, RestoreAllowed=false. Claim grant type с Executable=true не сериализуется и не сохраняется: копируются только allowlisted исторические ID/time scalars. Raw stdout/stderr, PID, credentials и command lines не журналируются.

## Однозначность ACK

Сохраняется только существующий strict classifier result: процесс нормально завершён с exit0 и положительным PID, capture полностью завершён/не truncated, stderr пуст, отмена не замечена, одна непустая строка infobase:<nonempty canonical UUID>. Coordinator дополнительно проверяет exact invocation scope, reason и inert flags. Это однозначный разобранный ответ вызова, НЕ доказательство внешней SQL БД, непрерывной идентичности или отсутствия interference.

## Atomic quarantine

После успешного cleanup namespace hold/credential preparation и финальной cooperative cancellation проверки atomic journal method выполняет в одной SQLite transaction:

1. Проверяет точное состояние CreationInFlight/revision2/attemptExpectedRevision1, immutable Intent и credential-bound Binding.
2. Добавляет одну ACK запись с canonical GUID/O UTC timestamps и исходным claim window (expires=Binding.StartBefore, issued>=Binding.IssuedAt, положительная длительность <=5мин).
3. CAS переводит ту же попытку в ReconciliationRequired/revision3/OutcomeUnknown.
4. Commit завершает обе записи вместе; лишь затем возвращается CreationAcknowledged=true.

Failure вставки/CAS/commit не разрешает fallback, который бы сообщил saved ACK. Возвращается существующий fixed reconciliation-unconfirmed error; исходный in-flight barrier при rollback сохраняется. Неопределённость commit также не даёт права повторить физическую операцию. Остальные пути используют прежний quarantine без ACK. Cleanup failure или отмена не сохраняют подтверждённый ACK.

Никаких новых await/журнальных операций в критическом промежутке между финальным reread и invocation entry; исходный monotonic launch budget не продлевается. Имя и слоты остаются удержаны. Нового success/owned/reset/release state нет.

## Хранение и восстановление

Аддитивная локальная таблица target_creation_acknowledgements и triggers запрещают UPDATE/DELETE/REPLACE существующей записи через обычный SQL path. Это не защита от администратора/повреждения самого файла: strict bounded read заново проверяет row canonical shape и корреляцию с intent/binding/quarantine. Single-operation read не является серверным доказательством или atomic cross-system truth. Обычное открытие AgentJournal writable/инициализирующее, не обещается forensic read-only.

Старые журналы открываются без backfill. Отсутствие ACK не доказывает отсутствие запуска; старые CLI bool/output не повышаются до saved evidence. После restart AlreadyAttempted по-прежнему не возвращает fresh ACK authority и никогда не повторяет CREATE. Хранимые timestamps — исходные исторические сведения, не новое окно выполнения.

## Следующий gate

Независимое подтверждение точной цели ещё не реализовано. Текущий RAC summary даёт UUID/name, но не проверяет связь registration → конкретный SQL server/db; SQL observer даёт name/database_id/create_date/state, не долговечный GUID владения. Нужен отдельный доверенный readback этой связи и корреляция с saved ACK/reservation до рассмотрения ownership acceptance. Никакой ACK, имя, claim или exit0 отдельно не разрешает restore. Стендовые чтения/создание/restore этим изменением не выполнялись.

## Проверки

Локальные SQLite/fake-port tests проверяют reopen, exact pins, atomic rollback, append-only, malformed persisted data, cleanup/cancellation, ambiguous/invalid output и отсутствие replay. Полный локальный Release2482passed/0failed,277SQLexplicitlyskipped; Node84passed/0failed. Добавлено33 Agent tests (25journal cases +8coordinator cases), включая regression cross-operation unique-claim INSERT OR REPLACE и rollback ACK при нулевом quarantine CAS. Независимый review без блокеров после исправления guard обоих unique conflicts. CI после push фиксируется отдельно. Production host flags/default-off wiring и CLI shape не расширяются.