# Offline one-shot readiness assessment

This assessment is deliberately non-authorizing and read-only. It does not enable execution, construct AgentJournal, acquire a local lock, read/decrypt a token, contact an endpoint, run RAC/SQL/processes, change rights or apply migrations. No remote stand inspection is performed by this increment.

TargetCreationOneShotReadiness.Evaluate uses the actual TargetCreationOneShotHostOptions.Bind without cloning configuration or overriding Enabled. Disabled configuration yields DisabledNotAssessed and does not inspect the remaining execution configuration or local artifacts. It does NOT validate a fabricated enabled candidate or claim disabled configuration is complete.

For enabled configuration, existing strict host binding validates physical/canonical/mapping pins, exclusive namespace policy confirmation, endpoints/timeouts and executable/workdir presence without invoking any adapter. Assessment additionally checks a trusted HTTPS API origin (no userinfo/path/query/fragment), absolute existing data directory, and presence of agent-journal.sqlite and agent-token.dpapi. File presence is NOT content validity, database integrity or DPAPI decryptability. Malformed dummy SQLite/token bytes may satisfy presence checks intentionally: no content is read. The journal must never be opened for this assessment because its normal constructor initializes writable SQLite and lock artifacts.

The report carries only fixed status and booleans, no paths, IDs, endpoint strings, credentials or provider diagnostics. Malformed configuration returns InvalidConfiguration. Static success is named StaticChecksPassedRequiresOperationalVerification, never Ready. ReadyForExecution=false, Executable=false, FreshAuthority=false, RetryAllowed=false, ScheduledJobsMustRemainBlocked=true and OperationalVerificationRequired=true on EVERY path, including Windows with all static checks passed. PlatformSupported is an independent Windows fact, not execution permission.

## Unverified operational conditions

- Actual service account identity, protected directory/OS rights, effective ACLs, reparse integrity and trusted executable signature/publisher.
- Token origin and DPAPI CurrentUser decryptability under the intended service account.
- API TLS chain/hostname, availability and authentication; SQL TLS chain/hostname and integrated identity/admin visibility.
- Physical RAC UUID and SQL machine/instance correspondence to canonical identities and mappings.
- Existing journal content/immutable pins/attempt state and applied server schema migrations.
- Current permit/admission, fresh absence, held namespace and one-shot execution claim.

All remain mandatory operational/runtime prerequisites. Configuring Encrypt=Mandatory and TrustServerCertificate=false is not proof that TLS negotiation succeeded. A static report must not be used to retry a lost attempt, release slots, unblock scheduled jobs or infer ownership/success. Any future explicit operational probe requires its own constrained read-only implementation; it cannot promote this metadata to execution authority.

## Verification

Tests use isolated temporary dummy artifacts only. They verify disabled precedence, fixed failure redaction, strict HTTPS origins, missing artifacts, unchanged file contents/directory entries and invariant non-readiness. 17 targeted cases passed; independent final source/tests review found no blockers. Full local Release2254passed/0failed,264SQLexplicitlyskipped; integration320total. Tests confirm malformed artifact bytes and exact directory entries unchanged with no lock/WAL/SHM creation. Optional future coverage: explicit config snapshot unchanged and fragment-origin rejection. CIafterpush recorded separately. The API is also exposed by the explicit read-only CLI below; it is not an operational network probe.

## Read-only CLI

```
BuildSystem.Agent check-target-creation-readiness
```

Exactly one command argument is accepted; extra flags are rejected before configuration. It returns before any worker or execution-host entry. JSON is fixed metadata in camelCase. Exit3 means DisabledNotAssessed, exit4 InvalidConfiguration, exit2 StaticChecksPassedRequiresOperationalVerification, exit5 output failure. There is no success0 and no permission to execute. Malformed host configuration produces the same redacted invalid report. The original execution Enabled value is never changed. Even enabled valid configuration invokes only offline evaluation, never the execution host. Eight targeted CLI cases passed, including actual-agent subprocess with Enabled=true dummy configuration and unchanged malformed SQLite/DPAPI artifact bytes. Independent final routing/command/tests review found no blockers. Full local Release2262passed/0failed,264SQLexplicitlyskipped; integration320total. No remote target probes, credentials or SQLite opens. Initial cce69cf/7bbcb7d SQL CI passed, but Windows Build CI exposed a pre-existing one-shot CLI subprocess test's 10-second harness timeout (TaskCanceledException in WaitForExitAsync; artifact confirmed no receipt assertion failure). Both actual-agent CLI test classes now share a nonparallel collection with a bounded 60-second test-only startup/exit budget and existing kill-tree/output cap cleanup. Production execution deadlines and retry policy are unchanged. Corrective Release/CI recorded separately.