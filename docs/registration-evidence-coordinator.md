# Saved-ACK observation and historical evidence coordinator

## One explicit default-off pass

TargetRegistrationEvidenceCoordinator связывает existing strict journal ACK, TargetCreationRegistrationBridge, paired RAC/SQL observer и append-only evidence journal. Это локальная композиция read-only observations и inert metadata persistence, **не ownership lifecycle state machine**, server acceptance или restore authority.

Coordinator выключен по умолчанию, требует explicit operationId и заранее настроенные concrete adapters. Trusted configuration snapshot: expected agent/canonical cluster/SQL, creation endpoint pair mapping, отдельные RAC/SQL observation mappings, exact SQL server literal и общий budget1..120sec. Invocation не выбирает executable/SQL factory/endpoints и не передаёт observation IDs или готовый assessment. Все mappings и версия tool остаются operator assertions, не независимо удостоверенные physical endpoints.

Перед observations coordinator отвергает уже существующее evidence fixed AlreadyRecorded ошибкой. Старое evidence никогда не возвращается как результат нового прохода. Затем bridge строит scope только из реально saved ACK в quarantine. Два distinct nonempty observation IDs генерируются внутри прохода; они не гарантируют глобальную cryptographic freshness и не служат execution grant. Parsed RAC profile/SQL exact checks выполняются существующим observer; append снова сверяет actual saved ACK в local SQLite transaction.

## Budget and persistence uncertainty

Внешний monotonic budget охватывает journal preflight, bridge, awaited RAC/SQL adapters, assessment и запись evidence. Ошибка clock, negative elapsed, cancellation и истечение budget отвергаются. Adapter timeouts не обновляют внешний budget. Sync journal/некооперативные adapters могут не прерваться немедленно: нет Task.WhenAny detach или обещания hard-preempt, coordinator ожидает возврата/cleanup.

До входа в persistence failure означает, что coordinator ещё не вызывал append. Непосредственно перед RecordTargetRegistrationEvidence устанавливается PersistenceMayHaveCompleted boundary. Любая ошибка начиная с этого момента, включая storage error, отмену/clock failure/timeout после commit, возвращает fixed `TargetRegistrationEvidencePersistenceUnconfirmed`, PersistenceMayHaveCompleted=true. Даже если транзакция фактически откатилась, coordinator не преувеличивает определённость и не делает fallback.

При post-commit failure durable row может уже существовать. Она остаётся historical evidence; отдельный getter может прочитать её, но это не успешный новый observation или полномочие продолжать restore. Следующий вызов при наличии row отказывает до RAC/SQL I/O. Если storage outcome неизвестен, нельзя автоматически retry/overwrite/remove evidence или освобождать quarantine.

## Concurrency and restart

Per-instance Interlocked guard отвергает overlapping invocation как Busy до journal/adapter access. Он **не глобальный mutex**: все обращения к single-owner AgentJournal, включая другие coordinator instances и прямые journal callers, должны быть внешне сериализованы. Host/CLI/worker wiring и глобальная orchestration lock этим срезом не добавляются.

Нет новой durable observation-attempt issuance или server nonce. До append failure может не оставить observation metadata; явно инициированный последующий read-only проход может получить новые IDs, но автоматического retry loop нет. Restart с recorded evidence не вызывает новые probes. Это ограниченная local lifecycle composition, не anti-replay server protocol, не physical endpoint attestation, не SQL incarnation continuity proof и не atomic cross-system snapshot.

ACK claim timestamps остаются историческими: expired claim не становится новой CREATE authority и его deadline не возобновляется. Существующая smoke-ИБ не принимается через этот coordinator. Никакие live targets, CREATE/restore/drop/reset/release, schema стенда, worker switches или scheduled jobs не меняются.

## Result and next gates

Возвращается только existing TargetRegistrationEvidence с Executable/FreshAuthority/OwnershipEstablished/RestoreAllowed/RetryAllowed=false, RequiresIndependentVerification=true, ScheduledJobsMustRemainBlocked=true. Ошибки fixed-code/no-inner без credentials, provider diagnostics, stdout/stderr или permission-code.

Следующие отдельные gates: подтверждённая physical mapping policy, server-side lifecycle/anti-replay evidence acceptance и достаточное обоснование SQL incarnation identity. Текущий sys.databases fingerprint не становится доказательством ownership от дополнительного coordinator. Restore authority остаётся отдельной default-off системой.

## Validation

Local SQLite + real bridge/parser/SQL observer/assessment/persistence через fake process/database adapters. Проверяются новый internally generated pair, unchanged quarantine, existing-row no-I/O, missing ACK/config drift, default-off/Busy, cancellation/deadline до записи, injected storage failure и post-commit uncertainty без повторного probe. Focused30casespassed; полный локальный Release2763passed/0failed,277SQLexplicitlyskipped; Node84passed/0failed. Независимый final static source/tests/docs review — без блокеров. Initial test-fixture compilation исправлена использованием Binding.AgentId и nullable pair mapping.Value, production API не менялся. CI после push фиксируется отдельно.