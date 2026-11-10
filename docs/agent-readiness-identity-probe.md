# Read-only API identity probe

## Scope

The explicit identity probe complements offline readiness; it does not replace it and never authorizes execution. This increment adds a server GET and an isolated default-off client, tested with in-process API fixtures and fake HTTP handlers only. The transport-only increment does not register a worker/CLI or load DPAPI tokens. A separate explicit Windows host/CLI is documented in [identity probe host](agent-identity-probe-host.md); it is independently default-off and does not change the offline readiness path. No stand request or live operational probe is performed by implementation/tests.

GET `/api/agent/readiness-identity` is under the existing AgentOnly machine endpoint group. Existing HTTPS bearer authentication resolves a currently Approved agent using a no-tracking database read. The endpoint does not poll, obtain a lease, claim an attempt, update capabilities, send heartbeats, read creation credentials or open a journal. Its no-store JSON contains exactly agentId and three false flags: executable, freshAuthority, readinessComplete. It returns no grant or creation metadata.

## Client contract

AgentReadinessIdentityHttpClient is default-off and requires an independently assigned expected agent ID. Never derive that expectation from the returned identity. An explicit enabled call sends one GET to the fixed path with one fresh bearer-provider invocation. The ordinary production handler keeps platform certificate chain/hostname validation and disables redirect/cookie/decompression/tracing propagation. It installs no permissive certificate callback. Deployment proxy trust is an external assumption; trusted test handlers are not a production security boundary and must not redirect/retry/forward credentials.

Only HTTP200 with the same destination, Cache-Control:no-store, bounded unencoded JSON/UTF8 is accepted. Error and redirect bodies are not read. Strict JSON rejects unknown, duplicate, missing and case-variant properties, malformed/empty/mismatched IDs and true safety flags. A 4096+1 actual-byte bound applies independently of Content-Length. The whole-operation 1..60-second deadline includes credential-provider return, headers, body acquisition/read, parsing and cleanup; uncooperative asynchronous work is observed and late responses/streams disposed. A synchronous credential provider cannot be forcibly interrupted: overdue return is rejected. No retry, worker, journal, RAC or SQL port is present.

The returned report intentionally omits IDs, endpoints and credentials; it contains only inert metadata. Successful ApiIdentityObserved means the authenticated response matched the independently expected identity at the time of the exchange, assuming the configured transport and trust store. It is not a cached grant or continuing authentication guarantee. Executable=false, FreshAuthority=false and ReadyForExecution=false; OperationalVerificationRequired=true always. Normal HTTP server access logging may occur; 'read-only' here means no application lifecycle/resource state mutation, not zero infrastructure observability.

## What this does not prove

- Actual Windows service account selection, DPAPI CurrentUser decryptability and token-file ACLs (this client receives a trusted credential callback, not a token-store implementation).
- Service directory integrity, executable publisher trust or physical RAC/SQL canonical mapping.
- SQL integrated identity or TLS; API token identity cannot establish SQL access.
- Current permit, Admission, namespace absence/hold or execution authority.
- Absence of an intercepting trusted proxy or future token revocation; default platform TLS trusts configured OS roots.

Any operational host composition must be a separate explicit opt-in, load the token only under the intended account, avoid opening AgentJournal, compare a locally assigned expected ID, preserve no-store/deadline/error redaction, and return before all workers. Static readiness remains unchanged. No operational enablement or CREATE permission is implied by deploying this endpoint.

## Verification

Initial targeted validation passed50client cases and4API cases; final full Release2318passed/0failed,264SQLexplicitlyskipped (integration320total). Independent final source/tests review found no blockers. Optional future coverage includes uncooperative stream open/read cleanup, synchronous cleanup/provider overrun and authentication database-state snapshots. All tests use fake handlers/in-process fixtures; no live endpoint, token load, journal or tool execution. Initial e2b612c Ubuntu Build and SQL CI passed; Windows exposed a test harness race: the outer 3-second WaitAsync timeout expired before the scheduled internal 1-second timeout continuation (artifact reported System.TimeoutException rather than the expected sanitized client exception). The fake-handler test class now uses the existing nonparallel test collection and bounded 30-second harness waits for entry/result/disposal. Its actual configured client timeout remains1second; production code and deadlines are unchanged. Corrective Release/CI recorded separately.