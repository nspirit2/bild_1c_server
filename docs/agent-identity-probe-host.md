# Explicit Windows API identity probe host

## Command and configuration

```
BuildSystem.Agent probe-agent-api-identity
```

Only this exact single argument is accepted. Extra flags/config overrides are rejected before configuration or dependencies. The branch returns before workers, journal access, RAC/SQL, grants or creation transports.

The probe is independently default-off. Configure the existing Agent:ApiOrigin (trusted HTTPS root) and Agent:DataDirectory (existing protected absolute service directory), plus the flat Agent:ReadinessIdentityProbe section:

```
Enabled = false
ExpectedAgentId = <independently assigned nonempty agent GUID>
HttpTimeoutSeconds = 30
```

Enabled=true is a deliberate operational opt-in, NOT a requirement for ordinary worker startup or the offline readiness command. No operational values are installed by this change. Disabled binding ignores all other fields and does not inspect token paths. Enabled binding rejects unknown/nested probe members, malformed IDs, non-root/non-HTTPS origins and timeout outside1..60seconds. The expected identity is never derived from the server response.

Enabled configuration checks token-file existence and metadata length1..16384bytes, without reading/decrypting content. Host execution then checks cancellation and Windows before composing the production transport. It loads the DPAPI CurrentUser token through the existing TokenStore under the process's actual account and sends only the authenticated identity GET. It does NOT select or impersonate a service account, use administrator credentials, change ACLs, initialize journal or contact RAC/SQL. Invoke under the intended account explicitly. No broad credential/transport injection seam is exposed by the host.

## Reports and exit codes

One fixed JSON output attempt follows owned-client cleanup. No IDs, endpoint values, paths, provider errors or bearer are output. Fields: status, apiIdentityObserved, executable, freshAuthority, readyForExecution, operationalVerificationRequired, scheduledJobsMustRemainBlocked, retryAllowed.

- 3: AgentReadinessIdentityDisabled.
- 2: AgentReadinessIdentityObservedRequiresOperationalVerification (apiIdentityObserved=true only after exchange and cleanup).
- 5: AgentReadinessIdentityUnavailable or output failure.
- 130: AgentReadinessIdentityCancelled for caller cancellation.
- 64: unsupported argument shape, rejected before configuration.

There is never a success0. All reports keep executable=false, freshAuthority=false, readyForExecution=false, operationalVerificationRequired=true, scheduledJobsMustRemainBlocked=true and retryAllowed=false. A failure clears identity-observed metadata rather than reusing a partly completed observation. Output failure never triggers a fallback write or second request.

## Limits of the evidence

The identity transport retains ordinary certificate validation, no redirects/cookies/retries, exact expected ID correlation, strict bounded JSON, late asynchronous cleanup and a whole-exchange elapsed guard. Synchronous DPAPI/provider/disposal code cannot be forcibly interrupted; overdue completion is rejected rather than represented as fresh. The metadata token-length precheck is not atomic with TokenStore.Load and does not fence replacement/reparse races or impose an atomic byte bound on the existing token loader. The directory and token must already be protected by operator-managed ACLs. No ACL, service SID, publisher/signature or reparse-integrity policy is invented or attested.

TLS/token identity does not prove SQL TLS/integrated identity, physical RAC/SQL mapping, local journal integrity, server schema readiness, permits, Admission, namespace absence/hold or execution authority. Approved-token state may change after the observation. Default OS trust may include organizational TLS proxies; the report does not attest end-to-end physical host identity beyond that trust configuration.

## Validation boundary

Host tests use disabled, invalid or pre-cancelled inputs only; no enabled uncancelled operational composition is exercised on Windows. CLI subprocess tests use clean environments, isolated directories and disabled/invalid configuration exclusively. Enabled transport behavior is tested separately with fake handlers. No live probe, DPAPI decryption, CREATE, migration or rights change is performed. Targeted validation passed39options cases,8CLI cases and7host cases. Final full local Release2372passed/0failed,264SQLexplicitlyskipped (integration320total). Independent final production/options/host/CLI tests review found no blockers. The precancelled test plus explicit source ordering verifies the intended dependency gate; a130 receipt alone would not independently attest absence of provider calls if a different implementation normalized failures after cancellation. CIafterpush recorded separately. All actual enabled exchange behavior remains fake-handler-only; no real account/TLS/DPAPI deployment validation was performed.