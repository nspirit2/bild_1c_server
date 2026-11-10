# Isolated one-shot CREATE coordinator

Default-off library orchestration over TRUSTED injected ports. No production adapters, worker/CLI/DI registration, configuration enablement, SQL target connection or RAC process are added by this increment. Tests fake physical operations; this is not a stand deployment or authorization to execute live CREATE.

## Sole attempt and ordering

Only this call's fresh CredentialsPrepared can continue. Existing metadata CLI attempt/in-flight/quarantine returns AlreadyAttempted and cannot resume, reacquire, reclaim or invoke. Credential preparation durably consumes the sole local attempt before one start POST and one pinned credential read.

Coordinator checks independently trusted configured agent/canonical cluster/SQL/creation pair mapping and observation submapping IDs, exact persisted in-flight revision2/attempt/binding, then projects scopes. Acquire matching namespace -> CheckHeld -> fresh paired assessment using coordinator-generated distinct RAC/SQL observation IDs -> exact expected scope and BothObservedAbsent only -> CheckHeld -> record monotonic t0 BEFORE one claim request -> independently correlate fresh grant -> CheckHeld -> reread exact persisted journal -> cancellation plus final monotonic budget check -> single awaited trusted invocation port call while lease held.

No physical callbacks occur for prior attempts, present/unknown targets, mismatched scope/claim, failed lock check, elapsed budget or cancellation before invocation. Lost/unknown claim is never retried. No detached invocation/timeout task: trusted ports must cooperate and implement bounded operations; this wrapper does not preempt a hung physical provider.

## Conservative deadline

Let d=grant.ExpiresAt-grant.IssuedAt, using authoritative SQL issuance fields. Require d positive and <=5minutes and original binding deadline unchanged. Require monotonic elapsed since BEFORE claim request to be strictly less than d immediately before invocation. Request/commit/network/response/lease/journal checking latency consumes budget; no new window begins at response delivery and no local UTC skew assumptions are used.

With continuous normal-rate SQL/monotonic clocks, request-start t0 precedes SQL issuance ti, so t0+d <= ti+d=original expiry. This is deliberately conservative. SQL wall-clock jumps, rate differences and abnormal suspend/reset behavior are outside that proof. The check guards the TRUSTED PORT CALL boundary, not an atomic OS Process.Start across arbitrary thread descheduling. A future physical adapter must carry/check the deadline at its trusted launch boundary rather than silently adding unbounded setup after this check. No claim of mathematical check-to-side-effect atomicity.

## Unresolved outcome and lifetime

Every owned post-begin failure/cancellation and every invocation outcome, including valid RAC acknowledgement, ends in retained quarantine/reconciliation. Receipt reports only local metadata, whether the invocation boundary was attempted, and correlated acknowledgement metadata. It never reports successful provisioning, readiness, restored data or permission to unblock jobs. Failed journal quarantine is a fixed reconciliation-unconfirmed error, never a fabricated state confirmation. PhysicalInvocationAttempted means port was called, not proof an OS process started.

Lease disposal is awaited, no retries/reacquire. Ephemeral credential receipt is disposed, and secrets/grant are never serialized, logged or written to the journal. Explicit secret strings passed to trusted ports cannot be guaranteed erased after escape. All returned orchestration receipts remain executable=false, freshAuthority=false, retryAllowed=false, scheduledJobsMustRemainBlocked=true.

## Future adapters: mandatory unresolved engineering guards

- Namespace SQL factory must create unpooled master sessions with explicit ConnectRetryCount=0; existing observation factory is not reusable unchanged.
- Canonical cluster ID is correlation, not necessarily RAC's physical cluster UUID. Existing observation path passes canonical ID as RAC --cluster, whereas create recipe has separately configured RacClusterId. Resolve through a trusted mapping adapter or reject mismatch; never assume equality from matching metadata IDs.
- All observation/namespace/create mappings must target the same trusted physical RAC/SQL pair. This pure orchestration cannot discover or certify that relationship.
- Cooperating service agents share namespace applock; it does not fence external administrators or guarantee a SQL session cannot fail after its last held check.
- Physical adapter must use fixed ru_RU, --create-database and --scheduled-jobs-deny=on through existing validated recipe/no-shell bounded runner. No adopt/drop/recreate, no physical retry, no reset/success/release.

## Verification

Temporary SQLite, actual credential/claim parsers over fake HTTP and fake physical ports only. Deterministic monotonic TimeProvider enables budget-boundary tests without sleeps. 25 targeted cases passed, including ack/unknown, no replay, unsafe hold/assessment/grant, strict ten-second budget edge, final held-check delay and journal mutation, credential lifetime/slots, cancellation during claim and cleanup, disposal failure and unconfirmed quarantine. Independent final source/tests review found no blockers. Final journal reread follows the final awaited held check (no extra await before port entry); post-cleanup cancellation clears acknowledgement. Optional future coverage: null hold/assessment, wrong-scope outcome and delayed invocation cancellation with retained hold. Full local Release2113passed/0failed,264SQLexplicitlyskipped; integration320total. CIafterpush recorded separately. No stand/physical invocation.