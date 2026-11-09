# Current assigned-agent prepared-permit read

## Known-ID metadata delivery only

GET `/api/agents/target-creation-permits/{permitId}` returns a bound, unstarted Prepared/revision1 receipt and nested Registered/revision1 operation with no attempt recorded in the operation. It does not discover IDs, list work, choose latest credentials, issue/reissue, consume, start, renew or replace a permit. Caller must already know the server-generated permit ID from an explicitly selected trusted source. Response is nonexecuting metadata with NewlyRecorded=false and scheduled jobs blocked.

The immutable permit attempt ID is present even though operation attempt metadata remains null before start. Credential/mapping IDs are retained, never credentials/ciphertext or decryption. The stored credential mapping pin and canonical identities are checked with the existing RequireCurrentCredential barrier; canonical pool mapping asserts only canonical cluster/SQL IDs and is not physical endpoint discovery. Actual trusted endpoint validation remains an agent-side prerequisite.

## Current checks on every read

Independent default-off `TargetCreationPermit:AgentReadEnabled` plus HTTP, permit, credential-binding and creation gates. HTTP configuration rejection uses generic TargetCreationPermitUnavailable503; direct service feature rejection uses TargetCreationPermitAgentReadDisabled503. Exact Bearer token, current Approved agent, assigned identity, current admission/canonical mapping and current nonrevoked stored credential are checked within the same SQL transaction and established lock order. No administrator-cookie substitution. SQL server time must lie in the original five-minute half-open issuance window; Prepared/unstarted state and revision pins must still match. Started/revoked/quarantined/expired/unbound rows are not deliverable through this narrow read, even though administrator diagnostic reads retain historical metadata.

Repeated reads recheck everything and neither consume nor renew the row. Revocation/admission/token withdrawal between reads can deny the next call. Lost response does not authorize a fresh issuance or a new attempt. This method performs no SaveChanges or audit transition; transactional read locks are not execution ownership.

## HTTP boundary

Known nonzero GUID-D route only, no query selectors or request body, no Origin. HTTPS is checked before redirects, without trusting forwarded headers. Manual Bearer authentication is independent of cookie CSRF; exact route metadata does not exempt admin POSTs. Prefix-wide no-store/Pragma/nosniff, protected DI/serialization and fixed allowlisted errors match existing permit start. JSON receipt retains all immutable target/restore/artifact/agent/canonical/credential pins, never fresh authority or remote fencing proof.

## Not included

No agent prepared-receipt parser, permit work queue, operator UI, automatic polling/dispatch, worker or CREATE. The existing agent start transport validates already-selected local pins and cannot adopt arbitrary delivered IDs. Local durable binding/attempt ordering and physical exclusion must remain separate.

## Verification

6offlineHTTPfacts passed, including successful numeric-state DTO serialization/repeated exact-pin200, all gates, HTTPS/auth/query/body/Origin, fixed errors and no-store. POST to GET-only route remains CSRF400, not a broadened machine exemption. Five disposable-SQL facts added: full receipt equality/repeat no-write snapshots, current approval/token+unknownID/assignment, all gates, SQL-window/started/revoked/quarantined readiness, credential/canonical-mapping/admission withdrawal. Expiry fixture shifts both original timestamps equally, preserving exact5min CHECK. Independent source/test review resolved compile enum naming and strengthened successful200/auth-before-existence coverage. Full local Release1915passed/0failed,249SQLexplicitlyskipped; integration292 total. SQL CI gate increased287; no stand/target operations. CIafterpush separately.