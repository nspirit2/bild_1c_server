# Isolated start-to-credentials preparation (NONEXECUTABLE)

This opt-in library links the existing durable local-first start coordinator to one credential resolution for the exact bound permit/attempt. It is not registered in worker, CLI or DI and does not create, observe, restore or release a target. The existing explicit metadata CLI remains unchanged and does not read credentials. This is an alternative initial orchestration helper, not a continuation to call after that CLI has already consumed the local attempt; such a call returns AlreadyAttempted without credentials.

## Order

The caller must already have selected and persisted the exact local intent and credential-bound permit binding; this helper does not fetch Prepared metadata, create bindings or adopt IDs. The start coordinator consumes the sole local attempt before its one start request. Only its freshly consumed local StartMetadataRecorded outcome can proceed to one credential.ResolveAsync request: same permit, revision2, attempt, credential and endpoint mapping. A historical NewlyRecorded=false server acknowledgement remains inert metadata; it cannot permit CREATE or bypass independent current server resolution checks.

Local AlreadyAttempted or existing reconciliation never resolves credentials. No automatic retry, refresh, rearm or reset exists. A second helper call cannot obtain a fresh credential set for the already-consumed attempt through this helper, even though the lower-level repeatable delivery endpoint independently allows explicit reads inside its original window.

## Secret ownership

Success returns a disposable receipt with private owned delivery holder. No public holder property or secret fields. Explicit ReadSecrets escapes caller-owned managed strings; disposing the receipt drops its references, but cannot erase strings already escaped. Validation transiently reads the holder to detect disposed/invalid trusted fake responses and verify the server's UTF16/Unicode bounds; it retains no validation values. Managed/parser scratch and escaped strings are not guaranteed erased.

Receipt and holder deny System.Text.Json read/write including null via explicit converters; ToString is fixed/redacted. Metadata contains Record/Binding/Outcome and immutable inert flags only. It is not an authority token. Caller must dispose a successful receipt promptly and never log or serialize escaped values. Helpers do not persist secrets, ciphertext or process argv to journal.

## Failure

After durable start, credential exceptions, cancellation, null/disposed/invalid delivery quarantine the exact in-flight revision2 as OutcomeUnknown or Cancelled; attempt/binding/namespace slots remain occupied. Any acquired holder is disposed on failure. Quarantine is confirmed only after its journal transition succeeds; otherwise a fixed reconciliation-unconfirmed exception is returned, never fabricated confirmation. Start uncertainty retains the existing coordinator's conservative handling without a credential call.

Enabled defaults false before journal/provider I/O. Same-instance gate serializes the whole helper; trusted owner must serialize all other use of the shared journal. Injected start/credential ports are trusted and cooperative: production HTTP exchanges are bounded, but this wrapper adds no independent network deadline. No output, raw exception, token, URL or secret is logged.

## Pure scope bridge

TargetCreationScopeBridge validates only the exact in-flight revision2/bound attempt and independently expected agent/cluster/SQL/creation-mapping pins, then projects RAC creation, SQL namespace and paired-presence requests. Observation RAC and SQL mapping IDs are separate trusted configuration inputs, not inferred from the credential pair mapping; their physical relationship is the trusted adapter/configuration owner's responsibility. They need not be unequal. The namespace guard's mapping must match the creation pair mapping. The copied local fencing token is correlation only, not active server lease authority. This pure helper does not read the journal, make observations, hold a lock, construct argv or resolve secrets. No worker/host wiring.

## Still absent

No physical namespace lock/absence verification, fresh executable authority, RAC invocation, success/reset/unblock, host deployment or stand mutation. Physical one-shot creation needs all of those guards separately; receiving credentials is not execution authorization.

## Verification

Local temporary SQLite and fake transports only. Credential holders are produced through the real bounded credential HTTP parser over fake HTTP, not public-secret test constructors. 25 targeted cases passed (21 preparation +4 scope bridge), including both server NewlyRecorded values, lifetime/JSON denial, cancellation/quarantine/slot retention and failed SQLite quarantine with acquired-holder disposal. Independent final static review found no blocking issues. Optional coverage gaps: no directly injected start-coordinator quarantine failure through the wrapper; malformed secret shape cannot be materialized by the real sealed-holder HTTP fixture (strict transport/parser regressions cover it). Full local Release:2006passed/0failed,249SQLskipped; integration292total. CIafterpush separately. No live target commands or stand mutation.