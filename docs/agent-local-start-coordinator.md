# Local-first start metadata coordinator

## Isolated opt-in

TargetCreationStartCoordinator is not wired into worker, CLI, DI or physical execution. Enabled defaults false. It owns neither AgentJournal nor ITargetCreationPermitStartTransport; its semaphore serializes calls to this coordinator instance only, not arbitrary other journal users/processes. Trusted host must respect journal ownership. The injected transport must be trusted/cooperative: the coordinator adds no independent request deadline. The production HTTPS transport has a bounded exchange; an arbitrary fake/custom port that never completes can retain the semaphore and in-flight slot, never grant execution.

## Durable order

Caller-supplied exact intent and bound credential/mapping pins must already exist in SQLite. Neither missing intent nor missing binding is created/adopted by this coordinator. Pure preflight validates shapes, while actual journal reads establish the local row being used. Mismatches fail before mutation/network.

Only actual IntentRecorded/revision1 can begin. TryBeginTargetCreation consumes the sole local attempt first, retaining both namespace slots. A fresh successful receipt with CreationInFlight/revision2 and the same bound attempt is required before one transport call. A local replay or already in-flight/quarantined record causes no POST, reset, rearm, credential read or CREATE. Same-operation retry cannot replace any IDs or reopen a slot.

A successful server metadata reply is independently correlated to the immutable input objects and half-open bound start window. The local record remains in-flight, not successful or released. NewlyRecorded=false from the server is still metadata, not execution authority. No step authorizes target mutation or unblocks scheduled jobs.

## Unknown outcome

After a fresh local begin, any error, caller cancellation, invalid response or lost acknowledgement causes quarantine at local revision2: Cancelled if caller cancellation is observed, otherwise OutcomeUnknown. The sole attempt, binding and slots remain retained. No automatic start/CREATE retry follows. Quarantine is reported as confirmed only after the journal update succeeds. If quarantine cannot be confirmed, a fixed reconciliation-unconfirmed exception is returned with no transport diagnostic; no success/quarantine claim is fabricated.

Precancel is rejected before journal mutation. If local begin itself throws, no POST occurs; its result may be uncertain, so the caller cannot infer rollback or safely recreate/rearm an attempt. A later read may show consumed slots. Persisted local metadata is not a lease or proof of external namespace exclusion.

## Not included

No credential resolution, namespace acquisition/absence observation, RAC invocation, server quarantine delivery, recovery or slot release. A future coordinator that mutates targets must add all current fencing, time, namespace and secret guards independently; these inert receipts cannot satisfy them.

## Verification

25 expanded cases passed using local temporary SQLite and fake start transport only. Separate SQLite connection observes committed revision2 before transport; success/replay, malformed/missing/legacy/cross-stage pins, errors/cancellation/null or mismatched receipt, same-instance concurrency, trigger-injected begin/quarantine failures and retained namespace slots covered. Quarantine failure asserts the exact distinct reconciliation-unconfirmed code. Independent final source/tests review approved, no blockers. Full local Release1904passed/0failed,244SQLexplicitlyskipped; integration282. CIafterpush separately. No physical/network/stand operations.

Additional offline interop: 5 ordinary theory cases serialize the actual Application/Domain receipt types with API-equivalent JsonSerializerDefaults.Web, feed the real HTTP client and coordinator, and verify actual SQLite state. Both newlyRecorded variants succeed inertly; historical revoked, changed mapping and nested canonical SQL pin produce quarantine. A response-produced marker prevents swallowed handler assertions from falsely passing negative tests. Full local Release1909passed/0failed; SQL count unchanged, integration287 total (282 prior +5offline). No production references or runtime wiring changed.