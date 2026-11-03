# Server acceptance historical observation receipts

Сервер принимает только полный paired receipt для существующего immutable observation assignment. Feature TargetObservationOptions.Enabled остаётся default-off; HTTP routes/dispatch/machine claim в этом срезе отсутствуют. Report не создаёт assignment и не переиспользует active stage lease.

AgentToken должен принадлежать текущему Approved assigned agent. Текущие owner/project/root/pool/profile/mapping и retained intent/stage/hash/accounting/cleanup pins проверяются до deduplication. Это консервативный исторический приём: revoked credentials не могут replay, потерянный ACK не обходит отзыв. Полный assignment DTO должен совпасть с server record; operation/fence/IDs не принимаются как authority.

На один request сохраняется один immutable receipt. Receipt ID и независимые RAC/SQL observation IDs не переиспользуются между receipts; cross-role collisions тоже требуют отказа на application boundary. Timestamp наблюдения — label агента, не server-clock freshness. AcceptedAt задаёт сервер. Эквивалентный DateTimeOffset instant нормализуется без смены payload. Exact replay возвращает первоначальный ACK Recorded без дополнительного audit, любые изменённые payloads конфликтуют.

ACK означает лишь сохранение historical evidence. Executable=false/jobsblocked=true закреплены contract и DB CHECK. Ни BothObservedAbsent, ни State=Recorded не дают CREATE/restore/Ready. EF sync/async guard запрещает update/delete receipt; assignment и retained holds не меняются. SQL metadata migration применяется только к disposable fixture DB при тестах, production не обновляется.

Следующий шаг — dedicated HTTP authentication surfaces и observation dispatch; локальная outbox доставка пока не подключена автоматически. Требование scheduled-jobs-deny=on до/после restore и после restart сохраняется. Security profile не mandatory gate.

Независимое review не обнаружило ACL/replay bug. После замечания добавлена консервативная проверка AcceptedAt!=default для replay повреждённого persistence row; это не crypto integrity гарантия. Первичный compile обнаружил EF1002 в rawSQL constraint tests: исправлено построением SQL только из фиксированного whitelist identifiers/clauses, все GUID values parameterized, без глобального suppression.

Окончательный полный локальный Release **882passed/0failed**, SQL153explicitly skipped без opt-in (integration suite содержит ещё1providerlessFact, который прошёл локально). Windows SQL2019 integration **154/154passed**,0failed/0skipped, включая153SQL-backed +1providerless validation. TRX `E:\BuildSystem\TestRuns\observation-receipts-20261103\results\ustiantsev.is_SK-CICD-1C-01_2026-11-03_13_58_15.trx`. EF model parity no changes; locked restore/diff check successful. RID-only lock updates portablepublish исключены. Новая metadata миграция20261103105308_TargetObservationReceipt production не применялась.

Commitb517a50: [SQL41623807809](https://github.com/nspirit2/bild_1c_server/actions/runs/41623807809) success154/154; [Build41623807621](https://github.com/nspirit2/bild_1c_server/actions/runs/41623807621) success Windows/Ubuntu.

## Проверки

SQL regressions покрывают immutable replay/ACK, token revocation/foreign agent, каждый assignment pin, payload/outcome/time/observationID conflicts, current bindings/ACL, concurrent report, EF/SQL constraints. Model tests проверяют migration parity, default-off no-DB, sync/async guards и JSON отсутствие injected execution authority.
