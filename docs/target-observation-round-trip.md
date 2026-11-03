# Metadata-only observation round-trip regression

Этот срез проверяет соединённую protocol chain, не подключая production Worker и не выполняя restore.

Real Identity cookie + CSRF создаёт immutable assignment в disposable metadata SQL. TargetObservationHttpClient использует trusted HTTPS TestServer handler, выполняет адресный AgentOnly GET. Coordinator сохраняет assignment в real SQLite journal перед fake paired executor. Полный report попадает в durable outbox. Explicit delivery POST использует реальные production authentication/services/SQL persistence, сверяет exact ACK и помечает journal record ACKed.

После reopen journal coordinator снова проходит current server auth, но возвращает ACKed исторический report без повторного executor call. Server assignment/receipt/audit остаются единственными; correlation tuple неизменён. Lease/lifecycle/held reservation/квоты не переводятся и не освобождаются.

## Lost ACK

Trusted test handler сначала пропускает receipt POST до полного server commit, затем скрывает response транспортной ошибкой. Pending report сохраняет исходные receipt/observation IDs/payload. Повтор coordinator не запускает observer, explicit retry POST получает прежний Recorded ACK; нового receipt/audit нет. Это проверка historical idempotency, не exactly-once observation в crash window до enqueue.

## Revocation

После durable local report отозванный credential блокирует как addressed GET history, так и outbox POST replay. Pending data не превращается в локальное разрешение на повторное observation или выполнение; offline bypass отсутствует.

## Граница доказательства

Real SQL services/auth + SQLite + HTTP serialization/middleware используются вместе, но network/TLS sockets, IIS, live RAC/SQL adapter и 1C не запускаются. Fake executor — единственная замена observation side; его read-only contract отдельно покрыт concrete adapter unit tests. Custom TestServer handler является trusted dependency, не production redirect/cookie guarantee.

Production/config/schema/существующие target DB не менялись. Scheduled jobs deny mandatory до/после restore и после restart, security profile не mandatory gate.

Read-only review подтвердил real auth/services/SQL persistence и post-commit ACK loss. Замечание failure diagnostics устранено: cookie атрибуты проверяются boolean assertions с фиксированными сообщениями, без вывода session cookie. В этих трёх facts CSRF exercised positively; negative missing/invalid token проверки остаются в существующих API/real-cookie suites, не заявляются новой round-trip проверкой.

Окончательный полный локальный Release **1067passed/0failed**, SQL164explicitly skipped без opt-in (2providerlessFacts прошли). Windows SQL2019 **166/166passed**,0failed/0skipped; TRX `E:\BuildSystem\TestRuns\observation-roundtrip-final-20261103\results\ustiantsev.is_SK-CICD-1C-01_2026-11-03_14_39_29.trx`. Locked restore/diff check успешны; RID-only publish graph исключён. Ни production API, ни Agent service не обновлялись.

## Проверки

Три opt-in SQL facts: complete round-trip/reopen, post-commit lost ACK, revoke-before-replay. SQL CI gate повышен до166 без skipped.
