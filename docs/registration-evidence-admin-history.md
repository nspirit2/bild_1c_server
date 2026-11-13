# Administrative historical registration evidence

## Read-only diagnostic surface

Current administrator может прочитать сохранённый historical registration receipt через ITargetRegistrationEvidenceReads.GetAsync(Actor, operationId). Возвращается view EvidenceRecorded=false/HistoricalEvidence=null, если operation существует, но report ещё не принят. Это не подтверждение отсутствия physical invocation или target.

SQL service использует existing default-off TargetCreationOperationOptions и current DB administrator check до раскрытия существования operation. Cookie/JWT role claim или Actor.IsAdministrator сами по себе недостаточны. Unknown operation → existing404; corrupted retained linkage/shape → existing409; provider fault → fixed redacted503. Только immutable claim/operation/intent linkage и bounded stored metadata проверяются; current Admission, credential state, agent state или lease expiry не используются как способ стереть историю после revocation.

История содержит retained server pins, reported infobase UUID, RAC/SQL observation IDs и mappings, version-specific profile, exact names/SQL literal и SQL fingerprint с original canonical DateTime O. Offset-bearing +03:00 metadata сохраняется без нормализации через local time zone сервера. AcceptedAt означает server acceptance time, не observation freshness. Reported observation mappings прямо не считаются attested; creation credential-pair mapping хранится отдельно.

Endpoint GET /api/admin/target-creation-operations/{operationId}/registration-evidence существует только при independent existing TargetCreationDiagnosticsOptions.Enabled=true. Default false не меняется. Canonical lowercase nonempty Guid D проверяется в handler; Administrator policy и actual DB admin check остаются независимыми уровнями. Handler responses выставляют Cache-Control:no-store, Pragma:no-cache до read service, включая его failures. Unauthorized middleware может отвергнуть запрос до handler.

Нет POST, agent token delivery, outbox, worker integration или нового executable authority. History/view flags: Executable/OwnershipEstablished/RestoreAllowed/RetryAllowed/FreshAuthority=false; RequiresIndependentVerification/ScheduledJobsMustRemainBlocked=true. Presence и absence не дают права на CREATE, restore, adoption, retry, release, drop или unblocking scheduled jobs.

## Operational boundaries

Read service не пишет audit, intent/operation state, leases, holds, reservations или credential metadata и не загружает credential envelope. Только read port зарегистрирован в DI; default-off receipt ingestion port не включается автоматически. Нет migration нового read slice и нет live configuration/deployment.

Снимок immutable metadata остаётся агентским утверждением, а не server physical verification. Readback не доказывает SQL incarnation continuity или trusted endpoint identity и не конструирует execution claim заново.

## Validation

SQL tests проверяют current admin before existence, known-operation absence, полный inert payload, history после agent/credential revocation, readonly snapshots, malformed metadata rejection и отсутствие credential-table queries. API tests проверяют diagnostics flag-off, authorization, canonical IDs, cache headers и fixed failure propagation. FocusedAPI15passed/0failed; offlineSQLread suite1passed/6SQLexplicitlyskipped. Полный локальный Release2780passed/0failed,289SQLexplicitlyskipped; Node84passed/0failed. Final independent static review source/tests/docs — без блокеров. SQL execution after revocation и trigger-disabled disposable-test corruption ещё требуют real SQL CI; skip не доказывает SQL behavior. Новая API fixture проверяет serialization envelope через stub; полнота historical26storedfields проверяется SQL suite, а не nullable stub. CI после push фиксируется отдельно.