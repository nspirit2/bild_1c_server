# One-shot server execution claim

Owner approved a DISTINCT one-shot physical-CREATE authorization protocol. Existing creation permit remains nonexecutable in domain, DTO and SQL CHECK. This increment adds server issuance only: no agent claim consumer, namespace observation/orchestrator, process invocation, default configuration enablement, deployment or live CREATE.

## Boundary

POST `/api/agents/target-creation-execution-claims`, exact JSON keys permitId, expectedPermitRevision (literal2), attemptId, credentialId, endpointMappingId. Required nonzero GUID-D pins, exact one Bearer, HTTPS before redirect, no Origin/query, strict UTF8 JSON, no unknown/duplicate/case-alias fields; actual+declared body max4096. Response is no-store/no-cache/nosniff. Only this exact POST has machine CSRF exemption; unmatched POST remains CSRF-protected.

Independent gates TargetCreationExecutionClaimHttp:Enabled and TargetCreationExecutionClaim:Enabled default false. SQL service also requires creation enabled, permit enabled, agent-start enabled, credential-binding enabled and credential store enabled. HTTP gate does not enable lower gates or configure trusted mappings.

## Atomic authorization

Serializable transaction -> global agent-control lock -> authenticate CURRENT Approved assigned machine token -> existing ledger Admission root/pool locks and current retained staging/fencing/membership/capability/mapping checks -> exact started permit rev2 + operation attempt rev2 -> SQL UTC within ORIGINAL permit five-minute window -> currently Stored rev1/nonrevoked credential metadata matching canonical cluster/SQL/mapping -> no existing claim for permit OR operation OR attempt -> append one server-generated claim ID -> save -> commit -> dispose transaction -> fresh receipt.

Admission validates retained COMPLETED staging lease, not a current physical execution lease; do not infer physical fencing from copied stage fencing token. Credential query projects metadata only, never ciphertext or plaintext and never invokes a protector.

The separately returned 13-field grant contains claimId/permitId/operationId/attemptId/agentId/credentialId/endpointMappingId/issuedAt/expiresAt, newlyIssued=true, executable=true, scheduledJobsMustRemainBlocked=true, retryAllowed=false. ExpiresAt is exactly original StartBefore, NOT claim time+5min. API validates receipt shape/UTC/pins, not API wall-clock freshness: SQL clock is authoritative.

## Lost response, repetition and revocation

Claim row is permanent issuance evidence, not a replayable grant. Individually unique permit, operation and attempt IDs; append-only EF guards; restrictive permit/operation/credential FKs; nonempty/UTC/window SQL constraints. No read endpoint recreates authority, no reset/delete/reissue transition. Privileged direct SQL bypass is outside these service/EF append-only guarantees.

Provider execution strategy is required for explicit transactions but its second delegate invocation is blocked BEFORE SQL by a per-request counter; it can never repeat transaction issuance or return an old row as newly issued. Commit ACK or transaction-disposal failure returns fixed unavailable even when the row committed. A later request performs current checks and rejects (AlreadyIssued conflict while still current; authorization/admission/revocation failures may reject earlier) instead of returning a historical executable grant. No verifySucceeded/historical recovery. Lost HTTP response consumes authority: do NOT automatically resend claim or CREATE; reconciliation only.

Permit/credential revocation shares global lock. Revoke-first blocks issuance; claim-first remains issued and later revoke cannot recall an already authorized/running RAC process. Holds/attempts/slots remain; grant is not success, reset, release or job unblocking.

## Agent obligations before any future invocation

Trusted agent must durably consume its sole local attempt, hold approved cooperative namespace lock, perform fresh paired RAC+SQL absence checks and accept only both absent, obtain this claim within original window, then retain/check the namespace and exact local pins through a SINGLE bounded RAC CREATE. Other service agents must cooperate; a SQL applock cannot physically fence RAC or an external administrator. Existing target remains conflict, never adopt/drop/recreate. Fixed ru_RU and --scheduled-jobs-deny=on remain mandatory. No physical retry after lost/unknown claim or invocation outcome. This server cannot verify lock possession or atomic/continuing physical absence and makes no such claim.

## Schema and evidence

Generated migration creates only TargetCreationExecutionClaims table/indexes/FKs/checks and updates snapshot, leaving existing metadata nonexecutable constraint unchanged. Migration generated for offline review, not applied to any stand. Unit/offline API/model/gates tests and SQL CI cover separate grant authority, immutable row, replay/revoke/clock/pin checks and commit ambiguity. Local Release2045passed/0failed,264SQLexplicitlyskipped; integration320total. New4domain+13offlinegates/model+22APIcases passed locally;15newSQLfacts require disposable SQL CI. CI SQL minimum raised320 and requires no skips. Independent final source/model/API/migration/tests review found no blockers; API clock comparison removed so SQL remains authoritative, unmatched POST test corrected to preserve CSRF, unique-index assertion counts only unique indexes. CIafterpush recorded separately.