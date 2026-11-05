# Admin-only creation ledger diagnostics

Владелец явно выбрал read-only операторскую страницу вместо перехода к executable CREATE/restore. Это metadata diagnosis, не grant, не physical target observation, не success/rollback proof.

## Поверхность

- `/admin/creation-diagnostics` — защищённый cookie-auth Administrator shell и его ресурсы; CSP без inline scripts/styles, no-store.
- `GET /api/admin/target-creation-operations/{operationId}` — единственный read endpoint, строгий nonempty canonical D-format GUID. Cookie Administrator authorization плюс actual current DB role внутри ledger reads; agent bearer credentials не подходят. State/reason явные строки в web DTO, без изменения enum serialization других API.
- Поиск по конкретному OperationId; нет list/enumeration, create/begin/quarantine/reset/release кнопок или HTTP mutations.
- Показываются retained pins, state/revision/attempt/reason и hard nonexecuting/jobsblocked. `newlyRecorded=false` означает только lookup, не отсутствие ранее committed записи.

`TargetCreationDiagnostics:Enabled` defaultfalse. Host включает только ITargetCreationOperationReads через отдельную factory с read options, не включает ITargetCreationOperations или worker. Для чтения current canonical mappings не требуются: после отключения project/pool/root/agent или отзыва owner membership действующий DB Administrator должен видеть durable metadata. Повреждённые returned pins/state/names отклоняются fail closed.

## Ограничения

Нет автоматической reconciliation/repair, fresh authority/ownership/alias exclusion и actual target commands. Для реального исполнения всё ещё необходимы отдельные согласования lease/expiry/revocation/unknown outcome. Scheduled-jobs deny обязателен до/после restore и после restart; security profile не mandatory.

В этом срезе нет audit timeline endpoint/UI: существующий ledger read не выдаёт audit events, а придумывать timeline по одному текущему состоянию нельзя. Audit rows продолжают храниться сервером; отдельный bounded audit read — следующий кодовый этап при необходимости. На этой странице никакой исторический observation receipt не превращается в CREATE grant.

UI очищает предыдущие данные при ошибке/новом поиске, не использует innerHTML для server input и отменяет/игнорирует stale responses. При401/403 данные не остаются видимыми как актуальный результат. GET не требует CSRF mutation token, потому что не пишет в БД. Полный session logout/rotation — существующая auth flow, не новый endpoint здесь.

## Проверки и rollout

Нужны HTTP tests401/403/malformed/valid/disabled/current-role propagation/no-store/no mutation routes; UI tests invalid input/errors/stale responses/text safety; existing SQL readcases проверяют actual DBrole и immutable snapshots. Локальные mocked HTTP tests не доказывают production DB identity; это покрывают opt-in SQL cases ранее.

Новая migration не требуется. Не выполняются publish/deployment, IIS/config mutation, migration на стенде, CREATE/restore/drop. Production feature remains off до отдельного rollout. Локальный полный Release1298passed/0failed;190SQLявноskippedбезopt-in. HTTP25новыхcases прошли, включая protectedembeddedresources, real-cookiebearer401, configread-enabled/mutationdisabled и early413no-store. Node49/49 (7новыхdiagnosticcases,42existingStage): invalidGUID/uppercasecanonicalization, hostiletextsafe, errors/abort/stalefetch, clearingoninput/tabhide/BFCache. Исправлены реальные дефекты review/build: malformedadminprincipal403 вместо500, explicitLogicalName для embedded hyphen-folderresources, lowercase request canonicalization. Отдельный source review API/config и JS завершён без оставшихся concretefindings. Это mockedHTTP/DOMtests, не browservisualreview и не standvalidation. CIпослеpush.