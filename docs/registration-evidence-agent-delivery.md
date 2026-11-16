# Explicit durable historical evidence delivery

## Bounded port composition, not a worker

Новый агентский срез связывает immutable local historical evidence с transport port и durable local acknowledgement. Concrete HTTP client, host/CLI wiring, фоновые retries, pending enumeration и worker не включаются. Опция Enabled=false по умолчанию; invocation адресуется ровно operationId. Это не повторное наблюдение RAC/SQL и не CREATE/restore.

PrepareTargetRegistrationEvidenceReport перечитывает actual saved evidence и strict CREATE ACK через journal. Report ровно13 полей соответствует machine ingress, но Agent не ссылается на Server.Application. Caller не подставляет произвольный assessment/evidence в delivery; claim/observation IDs и payload берутся из immutable записи. Повторная explicit доставка сохраняет тот же ClaimId и исходный payload, не генерирует IDs и не синтезирует ObservedAt.

SQL provider create-date форматируется O только после strict persisted getter validation. Нет ToUniversalTime/DateTimeOffset conversion. Как и существующий getter, Local-kind metadata после смены timezone может fail closed; оно не переписывается в другое значение ради replay. Portable timezone-independent raw-string projection потребует отдельного решения, не скрытой нормализации.

## Server receipt and local acknowledgement

Transport response обязан вернуть тот же ClaimId, exact operationId, status Recorded и nondefault UTC AcceptedAt. Все authority flags false; independent verification и scheduled-job blocking true. AcceptedAt сохраняется исходным server timestamp, не local now и не observation freshness.

Новая local ACK table хранит operation/claim/accepted_at с FK к immutable evidence, unique claim и append-only SQL guards, включая INSERT OR REPLACE. Record повторно проверяет actual saved ACK/evidence и expected report в той же transaction. Exact existing ACK idempotent; conflicting server acceptance metadata отвергается. Getter вновь валидирует retained evidence, а не выдаёт полномочия из одной ACK row.

Existing acknowledged operation возвращает DeliveredThisCall=false без transport I/O. Это historical delivery metadata, не fresh observation, не восстановленная execution claim и не доказательство physical target identity. Quarantine, revisions, claims, leases, reservation и credential state не меняются.

## Deadlines, ambiguity and concurrency

Общий1..120sec cooperative TimeProvider budget охватывает journal preflight, awaited transport и local ACK commit/disposal. Backward clock/clock exceptions/timeout/cancellation fail closed. Нет detached Task.WhenAny или обещания прервать synchronous SQLite мгновенно. Per-instance Busy guard отвергает overlap; все обращения к single-owner AgentJournal извне и между coordinator instances должны быть сериализованы caller.

До входа в local ACK persistence AcknowledgementMayHaveCompleted=false означает только, что coordinator ещё не вызывал local ACK write. **Remote server уже мог принять report**, даже если transport response потерян или malformed. Отсутствие local ACK не доказывает отсутствие server receipt; later explicit доставка повторяет immutable payload для server exact replay.

Сразу перед local ACK Record устанавливается storage uncertainty boundary. Любая ошибка после неё, включая ignored/failed insert, post-commit cancellation/timeout/clock fault, возвращает fixed AcknowledgementUnconfirmed с AcknowledgementMayHaveCompleted=true. Row может существовать или отсутствовать. Нет автоматического rollback, удаления metadata, physical retry или освобождения quarantine. Later explicit call сначала читает actual local ACK.

## Validation

Local SQLite/fake transport tests используют real saved ACK, actual RAC profile parser/SQL observer assessment и existing evidence journal. Проверяются stable13field projection, DateTime kinds, restart, lost/malformed ACK, exact/conflicting local replay, SQL immutability, cleanup/busy/deadlines, storage ABORT/IGNORE и post-commit uncertainty, unchanged quarantine и redaction. Concrete transport security и server authentication остаются существующими отдельными слоями; fake transport не считается HTTP proof.

Focused39expanded cases passed; полный локальный Release2864passed/0failed,289SQLexplicitlyskipped; Node84passed/0failed. Независимый final static review production/tests — без блокеров. Исправлена обнаруженная review ошибка: транспортная domain exception не может обойти текущую local ACK-entry классификацию; regression использует настоящую ранее полученную Unconfirmed exception. Initial test-only status/constructor/cancellation ожидания выровнены с Recorded/fixed-error контрактом. Malformed receipt проверяется journal API, а fake clock тестирует monotonic deadline checkpoints, не отдельную реализацию таймера. CI после scoped push фиксируется отдельно.