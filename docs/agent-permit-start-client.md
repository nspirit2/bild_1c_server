# Isolated assigned-agent permit-start client

## Purpose

Default-off HTTPS adapter records assigned-agent start metadata via `/api/agents/target-creation-permit-starts`. It does not issue permits, persist local intent, claim a lease, resolve credentials or invoke RAC. Caller supplies previously verified local TargetCreationIntent and bound TargetCreationPermitBinding; the client validates correlation, not that these objects were actually persisted/received from a trusted source.

## Strict metadata correlation

One POST contains permitId, expectedPermitRevision=1 and attemptId. The response must contain exactly the server's sixteen permit fields and twenty nested operation fields, with no extra secret/data fields. Every available local target/permit/attempt/credential/mapping/restore/artifact/assigned-agent pin must match. Deterministic names derive only from the reservation; canonical IDs are not physical discovery. Server does not return FencingToken, so matching local job ID does not certify remote lease freshness.

Only StartRecorded/revision2 and AttemptRecorded/revision2 are accepted, startExpectedRevision/attemptExpectedRevision=1, quarantine=null, nested newlyRecorded=false. Outer newlyRecorded may be true or false. Both are historical nonexecuting metadata, never fresh authority; no retry of CREATE can be inferred. A server may legitimately return Revoked historical metadata on exact replay, but this narrower client rejects it rather than passing it to a future eligible-start flow. IssuedAt/StartBefore must match bound values and define the same UTC five-minute window; StartedAt must lie in that window. No client-clock check turns a receipt into freshness evidence. Credential resolution still rechecks server time and revocation independently.

## Transport and failure

Trusted HTTPS root URI only; production sockets disable redirect, cookies, decompression and tracing propagation. One fresh token-provider invocation per explicit call, no automatic retries and no error-body reads. HTTP200, application/json UTF8 without encoding and Cache-Control:no-store required. Response limited to 32 KiB plus one detection byte, actual bounded stream read and strict UTF8/JSON parsing. Whole exchange deadline covers asynchronous send/open/read and parsing checks; late results/resources are observed/disposed. Trusted synchronous provider/disposal cannot be preempted, nor can arbitrary injected handlers be treated as secure transports.

Fixed exceptions exclude input, URL, bearer, response and inner diagnostics. Caller cancellation is rebuilt without raw provider/network exception details. Returned receipt preserves verified immutable input objects and fixed flags: Executable=false, FreshAuthority=false, ScheduledJobsMustRemainBlocked=true, RetryAllowed=false. It is not a type capable of unblocking scheduled jobs or releasing one-attempt slots.

## Pure local preflight

`TargetCreationLocalPreflight.ValidateInitial/ValidateInFlight` separately validates caller-supplied local records and bound pins, not journal persistence. Initial is exactly IntentRecorded/revision1 with no attempt; in-flight is exactly CreationInFlight/revision2 with the bound attempt and expected revision1. Quarantined/legacy/unbound/malformed/cross-stage metadata fails. No clock freshness, external lookup, storage mutation or authority can result. The transport calls initial-shape validation on synthetic caller-provided metadata before network I/O; this does not pretend the intent is durable. Future orchestration must read the actual journal and perform its own durable transition.

## Deliberately absent

No Worker/AgentHostedService wiring, journal mutation, permit-delivery channel, namespace/absence checks, process invocation or stand deployment. A lost HTTP response means unknown metadata outcome, not rollback; a subsequent explicit start request remains subject to fresh server authentication/admission and may only return an inert replay. Physical uncertainty must still quarantine without repeating CREATE.

## Verification

101 expanded cases passed (97 client +4 pure preflight): fake HTTP only, no network/process/SQL targets. Strict nested pin/state/shape, request/gate/security, body limit/deadline, cancellation and replay, initial/in-flight/quarantine distinctions. Independent final source/test review found no logic blockers; timestamp definite-assignment issue fixed before first build. Full local Release:1879 passed/0 failed,244 SQL explicitly skipped; integration282. CI after push separately. No worker/stand/target execution.