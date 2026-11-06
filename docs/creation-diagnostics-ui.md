# Admin-only creation ledger diagnostics

Владелец явно выбрал read-only операторскую страницу вместо перехода к executable CREATE/restore. Это metadata diagnosis, не grant, не physical target observation, не success/rollback proof.

## Поверхность

- `/admin/creation-diagnostics` — защищённый cookie-auth Administrator shell и его ресурсы; CSP без inline scripts/styles, no-store.
- `GET /api/admin/target-creation-operations/{operationId}` — metadata read endpoint, строгий nonempty canonical D-format GUID. Cookie Administrator authorization плюс actual current DB role внутри ledger reads; agent bearer credentials не подходят. State/reason явные строки в web DTO, без изменения enum serialization других API.
- Поиск по конкретному OperationId; нет list/enumeration, create/begin/quarantine/reset/release кнопок или HTTP mutations.
- Показываются retained pins, state/revision/attempt/reason и hard nonexecuting/jobsblocked. `newlyRecorded=false` означает только lookup, не отсутствие ранее committed записи.

`TargetCreationDiagnostics:Enabled` defaultfalse. Host включает только ITargetCreationOperationReads через отдельную factory с read options, не включает ITargetCreationOperations или worker. Для чтения current canonical mappings не требуются: после отключения project/pool/root/agent или отзыва owner membership действующий DB Administrator должен видеть durable metadata. Повреждённые returned pins/state/names отклоняются fail closed.

## Ограничения

Нет автоматической reconciliation/repair, fresh authority/ownership/alias exclusion и actual target commands. Для реального исполнения всё ещё необходимы отдельные согласования lease/expiry/revocation/unknown outcome. Scheduled-jobs deny обязателен до/после restore и после restart; security profile не mandatory.

Добавлен `GET /api/admin/target-creation-operations/{operationId}/audit` и ограниченная timeline: максимум50 последних persisted events, `truncated=true` при наличии следующего. Порядок `CreatedAt DESC, Id DESC` определяется SQL; pagination/list по всем операциям не добавлены. Выборка только exact TargetId и binary ordinal allowlist `TargetCreationRegistered`, `TargetCreationAttemptRecorded`, `TargetCreationQuarantined`. DTO выдаёт только Id, ActorId, Action, CreatedAt; audit payload/details/пользовательские профили и секреты не читаются. Actual DBadmin проверяется до existence lookup; metadatapins/state валидируются, current admission не требуется. Read не создаёт audit и не меняет ledger. Timeline не придумывается по состоянию, не доказывает physical target outcome и не является исчерпывающим forensic export. Metadata и audit получены разными GET snapshots, могут не совпадать по времени. Никакой исторический observation receipt не превращается в CREATE grant.

UI очищает предыдущие данные при ошибке/новом поиске, не использует innerHTML для server input и отменяет/игнорирует stale responses. При401/403 данные не остаются видимыми как актуальный результат. GET не требует CSRF mutation token, потому что не пишет в БД. Полный session logout/rotation — существующая auth flow, не новый endpoint здесь.

## Проверки и rollout

Нужны HTTP tests401/403/malformed/valid/disabled/current-role propagation/no-store/no mutation routes; UI tests invalid input/errors/stale responses/text safety; existing SQL readcases проверяют actual DBrole и immutable snapshots. Локальные mocked HTTP tests не доказывают production DB identity; это покрывают opt-in SQL cases ранее.

Новая migration не требуется. Не выполняются publish/deployment, IIS/config mutation, migration на стенде, CREATE/restore/drop. Production feature remains off до отдельного rollout. Локальный полный Release1298passed/0failed;190SQLявноskippedбезopt-in. HTTP25новыхcases прошли, включая protectedembeddedresources, real-cookiebearer401, configread-enabled/mutationdisabled и early413no-store. Node49/49 (7новыхdiagnosticcases,42existingStage): invalidGUID/uppercasecanonicalization, hostiletextsafe, errors/abort/stalefetch, clearingoninput/tabhide/BFCache. Исправлены реальные дефекты review/build: malformedadminprincipal403 вместо500, explicitLogicalName для embedded hyphen-folderresources, lowercase request canonicalization. Отдельный source review API/config и JS завершён без оставшихся concretefindings. Это mockedHTTP/DOMtests, не browservisualreview и не standvalidation. Для9bb655d Build42053599693Windows/Ubuntu и SQL42053600080 success.

## Bounded audit increment

Добавлены10HTTPcases и6Nodecases (combinedNode55passed). SQLtests отдельно проверяют реальную DBроль, exact target/actionallowlist и boundary50/51 со стабильным SQLGUID tie-break. При SQLcomparison учитываются не только BIN2case, но и DATALENGTH: SQLдажеbinarycomparisonдополняет trailing spaces, поэтому простой equality недостаточен для exact allowlist. DataLength guard применяется до Take51, не post-filter после limit. Событие с padded/casevariant action не должно вытеснять настоящий audit из boundedwindow. No migrations/index changes в этом срезе; boundedresultне означаетconstantSQLscanстоимость. Ledger/audit consistency только в отдельномSerializableGET, metadata и timeline не atomicsnapshotвместе.

Локальный полный Release1308passed/0failed;194SQLexplicitlyskippedбезopt-in (integration196). Node55/55. Добавлены4SQLfacts: реальные3eventsread-onlyпослеpooldisable; disjointtarget/action/case/paddedexclude (в том числе60более новых paddedrowsдоlimit); overflow50/51 SQLGUIDtie-break; currentrole/disabled/invalid/missing. ПорядокGUIDсравнивается свежимSQLquery, не .NET Guid.CompareTo. Отдельный source review выявил padding issue и подтвердил исправление. HTTPmock/NodeDOMtests неbrowservisualreview. EFmodelchecknochanges. CIподтверждениепослеpush.