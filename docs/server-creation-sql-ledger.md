# SQL creation operation ledger: nonexecutable

## Согласованная политика

Владелец явно согласовал admin-only внутренний service без публичного endpoint/worker. Register и BeginAttempt, включая exact replay, требуют текущую admin authorization и актуальные retained bindings. Quarantine разрешён текущему administrator даже после отключения project/pool: только консервативно сохранить запрет, без смены pins, reset, release или исполнения. Unknown outcome не разрешает новую попытку. Реальный SQL/1C target этим service не изменяется.

Domain foundation fdeffd9 passed BuildWindows/Linux и SQL CI. SQL слой связывает единственную creation operation с существующим immutable RestoreIntent, не дублируя его permanent canonical hold. Новая OperationId генерируется сервером отдельно от observation operation; historical receipt не обязателен и не считается fresh grant. Только ProvisionArchive допускается к записи ledger; ObserveArchiveStage не повышается автоматически, даже для nonexecutable operation.

Состояния Registered → AttemptRecorded → ReconciliationRequired либо Registered → ReconciliationRequired. AttemptId и его original ExpectedRevision сохраняются; exact replay после quarantine возвращает NewlyRecorded=false, сохраняя terminal state. Quarantine повтор не идемпотентный begin: stale/current terminal отклоняется. Сама запись NewlyRecorded=true не даёт права внешнего вызова. Executable=false и ScheduledJobsMustRemainBlocked=true обязательны в storage/receipt.

## Реализация и ограничения

Отдельная additive metadata table с Restrict RestoreIntent FK, unique RestoreIntent anchor и attempt identity, revision CAS, checks state/reason/attempt pairing и immutable identity guard. Никаких обновлений существующих snapshots/holds/env lifecycle, новых grants или target commands. Register/current validation в той же Serializable transaction, global agent-control → relational root → pool locks. Existing RestoreIntent сохраняет canonical uniqueness. Это координация server ledger, не physical target lock и не внешнее ownership evidence.

Current admin проверяется по actualIdentityrole до replay. Register/Begin проверяют enabled project/root/pool, assigned approvedagent/scope/membership, stagecompleted/leasecompleted/exact artifact/retained immutable snapshot/canonicalmapping, отсутствие activelease/retirement/cleanup/release. Quarantine требует admin identity и существующую корректную operation, но не повторяет отменённый admission. Все изменения audit атомарны с row transitions.

Feature default-off; application/internal DI только. API host не получает routes, worker не получает dispatch. Нет deployment или metadata migrations на стенде в этом этапе. Миграция применяется только к disposable SQL fixtures в tests/CI. Перед любым production migration отдельный review/backup; Down destructive и не normalrollback.

Этот ledger не fresh executable authority: нет expiry/lease issuance/reassignment, physical alias identity/exclusion, trusted owned-empty-target assertion или actual creation/restore. Будущий executable срез требует отдельного согласования и тестов crash boundaries. Scheduled-jobs deny before/after/afterrestart mandatory; security profile не mandatory.

## Проверки

SQL tests должны проверять current authorization до replay, exact lostACK replay после quarantine, CAS concurrency, запрет ObserveArchiveStage, quarantine после disabling bindings, immutable anchor/deleteguards и отсутствие lifecycle promotion. Локальный полный Release прошёл: 1268 passed, 0 failed; 178 SQL cases явно skipped без opt-in (всего integration180). Подготовлено 12 новых SQL cases и один default-off contract case. Независимый source review обнаружил отсутствие owner membership revalidation и quarantine validation после commit; оба исправлены с отдельными regression cases. EF has-pending-model-changes: no changes, Up inspected additive only. Реальную проверку SQL assertions выполняет opt-in SQL CI после push; local skipped не считаются SQL validation.
