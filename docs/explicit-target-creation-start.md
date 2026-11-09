# Explicit one-shot start metadata command (NONCREATE)

Owner selected explicit invocation with a known permit ID, not worker polling. This increment records start metadata only; **it does not create an information base**, resolve secrets, acquire physical namespace locks, restore, release slots or unblock scheduled jobs.

## Invocation

`BuildSystem.Agent.exe record-target-creation-start <permit-guid-D>`

Exactly two positional arguments, nonzero GUID-D, no extra flags or configuration overrides. Invalid commands are rejected before Host configuration and token/storage I/O. The explicit branch returns before Windows-service registration and worker construction.

`Agent:TargetCreationStartMetadata` is separately default-off. Enabled configuration is scalar-only and rejects unknown keys. Required nonzero IDs: OperationId, AgentId, CanonicalClusterId, CanonicalSqlServerId, CredentialId, EndpointMappingId. TimeoutSeconds defaults30 and accepts1..60. Existing Agent:DataDirectory must be an absolute protected directory with an existing agent-journal.sqlite; Agent:ApiOrigin is an HTTPS root without credentials/path/query. Enabled host is Windows-only, token loaded afresh through existing DPAPI store for each exchange. Disabled ignores other pins and does not access journal/token.

OperationId is an explicit trusted local selector, not server discovery. The persisted binding's PermitId must match the positional ID, and its agent/credential/mapping plus intent canonical cluster/SQL IDs must match independent host configuration. Remaining immutable full intent/binding pins come from trusted local journal storage; this is NOT an independent full-config allowlist or proof of current remote fencing. The host does not generate or adopt absent intent/binding rows from GET. The journal constructor still initializes/checks its existing schema and holds its existing exclusive lock; logical existing-record use is not a claim of physically read-only SQLite opening. Trusted operator must prevent concurrent worker/command use or deletion/replacement of the protected journal.

## Order and uncertainty

1. Read and match the existing local intent and bound pins. Existing in-flight/quarantined state returns AlreadyAttempted, with no GET/POST or state reset.
2. GET exactly `/api/agents/target-creation-permits/{permitId}`. Strict16/20-field parser accepts only matching Prepared1/Registered1, no start/operation attempt/quarantine, bound IDs, UTC original5min window and inert flags. GET is bodyless with fresh Bearer, bounded32KiB, no redirect/cookie/decompression/retries. No local-clock freshness authority is inferred.
3. Independently correlate prepared receipt, then existing local-first coordinator durably consumes the sole local attempt before one POST start. Every postbegin uncertainty/cancellation retains slots and quarantines without resend/CREATE.
4. Keep successful metadata in-flight. Repetition cannot create a new attempt, and server replay is never execution authority.

## Fixed output

One JSON line on stdout: status plus executable=false, scheduledJobsMustRemainBlocked=true, retryAllowed=false. No IDs, URL, target names, bearer, credential material or raw exception. Exit0 StartMetadataRecorded;2 TargetCreationStartAlreadyAttempted;3 TargetCreationStartDisabled;4 TargetCreationStartReconciliationRequired;5 TargetCreationStartUnavailable;130 TargetCreationStartCancelled (before durable begin). Runtime pin/config failures are5; unsupported command syntax64 before host startup. Output failure is5, with no second output attempt or rollback/retry.

Host buffers the command's fixed output until transport/journal disposal completes. Disposal failure discards that buffered response and emits one safe failure line rather than appending another JSON record. A failure after durable mutation is never evidence of rollback; inspect retained journal metadata, do not reset/rearm. Disabled mode emits only its fixed status.

## Not implemented or authorized here

No background permit pickup, journal bootstrapping, operator permit UI, automatic binding installation, physical CREATE or stand execution. A real physical one-shot mode still requires separate current authority/exclusion/absence/credential guards; this command's name intentionally states record-start rather than create.

## Verification

163 targeted cases passed:37 new prepared transport +18 new command +11 new host +97 existing start regressions. Independent final source/tests review approved after fixing missing-journal guard and disposal-triggered duplicate output; Program configuration-failure JSON aligned to stdout. Full local Release1981passed/0failed,249SQLexplicitlyskipped; integration292. Host missing-journal assertion exercises the file guard on Windows CI; on Linux the Windows-only gate exits earlier. Enabled-host injected disposal/output failure has no runtime fault seam coverage; source buffer/disposal ordering reviewed, disabled-write failure and command post-mutation write failure tested. CIafterpush separately. No stand changes or live target commands.