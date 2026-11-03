# Адресная read-only выдача observation assignment

GET /api/agents/target-observations/{observationRequestId} выдаёт только существующее immutable assignment текущему Approved назначенному агенту. Нужен HTTPS и wire Authorization: Bearer <token> через существующую AgentOnly policy. Human cookie не заменяет machine token.

Это не poll, claim и не executable issuance. Сервер не выбирает очередь, не назначает нового агента и не выдаёт новый fence/operation/observation ID. Никаких lease/lifecycle/receipt/audit/assignment updates. Serializable transaction и global agent-control→relational root→pool applocks лишь обеспечивают согласованное чтение существующего назначения и актуальных bindings.

Каждое чтение повторно проверяет current approved token, назначенного агента, endpoint mappings, retained intent/source/stage/hash/accounting/cleanup history, owner project membership, root/pool/project/profile/current ACL. Общий внутренний validator используется также receipt acceptance, чтобы политика replay/read не расходилась. Default-off, invalid request ID/credentials и отозванные bindings отклоняются. Unknown assignment не создаёт новое назначение.

Сохранённый historical receipt не меняет assignment. GET после ACK возвращает тот же correlation tuple, не свежий readback и не разрешение на повторное observation. Два агента не могут получить назначение друг друга; caller-supplied ID — selector, не authority.

Mapping GUIDs — operator assertions, не проверка физической identity endpoint. Canonical отсутствие не даёт reservation/CREATE; потребуется отдельный executable lease/fence/physical exclusion/ownership reconciliation. Scheduled-jobs-deny=on mandatory до/после restore и после restart, security profile не mandatory gate.

Независимый read-only review extraction подтвердил сохранение current owner membership, полного набора retained/binding predicates и lock order; no concrete ACL/replay/read-only defect found. Предикаты receipt до dedup не ослаблены. SQL row snapshots tests охватывают явно загруженные lifecycle/lease/assignment/receipt/audit/agent tables со scalar/rowversion, не заявляются snapshot всего metadata database.

Первый full Release обнаружил ошибку самого providerless test: финальный вызов ChangeTracker.Entries() требует EF provider и бросал уже после корректных отказов service. Удалена только эта лишняя проверка; SQL-free доказательство остаётся providerless context: любое DB access из проверяемых service веток упало бы раньше. Повторный полный Release **915passed/0failed**, SQL161explicitly skipped без opt-in (2providerlessFacts прошли). API delivery cases **6/6passed**. Locked restore/diff check успешны; portablepublish RID-only locks исключены.

Windows SQL2019 integration **163/163passed**,0failed/0skipped; TRX `E:\BuildSystem\TestRuns\observation-delivery-20261103\results\ustiantsev.is_SK-CICD-1C-01_2026-11-03_14_16_58.trx`. Никаких live target SQL/IB CREATE/restore; fixtures создают/удаляют лишь собственные disposable metadata DB. Schema не менялась.

## Проверки

Прямые SQL tests используют только disposable metadata DB. Проверяются exact pins/immutable replay, authentication/revocation, current policies, historical receipt/snapshot drift, конкурентные чтения и отсутствие изменений lifecycle/lease/audit/receipts. Fake HTTP auth tests проверяют AgentOnly routing/wiring; это не real cookie/network E2E. Poll/dispatch/Worker и автоматический restore не подключены. Production deployment/config/schema не менялись.
