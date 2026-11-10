# Explicit one-shot execution host (dormant composition)

## No operational enablement

This code adds strict host options and a host command. The follow-up Program route accepts only `run-target-creation-once <permit-guid-D>`; it returns before worker/service registration and remains independently default-off. Positional arguments never become configuration overrides. It does not enable config, grant SQL/OS rights, import secrets, apply migrations, connect to a stand or run CREATE. With explicit enablement and valid real dependencies this host CAN create a target; it is not a dry run. Actual enablement/live invocation remains separately authorized.

Agent:TargetCreationOneShot:Enabled defaults false and disabled binding/host output wins before cancellation, Windows checks, credentials or journal. Disabled config ignores other members. Enabled section is flat/closed, rejects unknown/nested members, requires ExclusiveNamespaceConfirmed=true, nonempty operation/agent/credential/canonical/physical/pair/observation mapping IDs, bounded independent timeouts, existing absolute operator-owned rac.exe and workdir, fixed ru_RU. Filesystem existence is not ACL, symlink or executable-signature attestation.

## Required enabled fields

```
Agent:TargetCreationOneShot:Enabled=false
Agent:TargetCreationOneShot:ExclusiveNamespaceConfirmed=false
Agent:TargetCreationOneShot:OperationId
Agent:TargetCreationOneShot:AgentId
Agent:TargetCreationOneShot:CredentialId
Agent:TargetCreationOneShot:EndpointMappingId
Agent:TargetCreationOneShot:RacObservationMappingId
Agent:TargetCreationOneShot:SqlObservationMappingId
Agent:TargetCreationOneShot:CanonicalClusterId
Agent:TargetCreationOneShot:RacClusterId
Agent:TargetCreationOneShot:CanonicalSqlServerId
Agent:TargetCreationOneShot:RacExecutable
Agent:TargetCreationOneShot:RacWorkingDirectory
Agent:TargetCreationOneShot:RacEndpoint
Agent:TargetCreationOneShot:SqlServer
Agent:TargetCreationOneShot:ExpectedSqlMachineName
Agent:TargetCreationOneShot:ExpectedSqlInstanceName=MSSQLSERVER
Agent:TargetCreationOneShot:HttpTimeoutSeconds=30
Agent:TargetCreationOneShot:RacTimeoutSeconds=30
Agent:TargetCreationOneShot:SqlTimeoutSeconds=30
Agent:TargetCreationOneShot:TotalObservationTimeoutSeconds=60
Agent:TargetCreationOneShot:CreationTimeoutSeconds=120
```

Existing Agent:DataDirectory and Agent:ApiOrigin remain external top-level pins. No credentials, bearer, connection strings, endpoint overrides or arbitrary process argv come from CLI arguments. API origin is trusted HTTPS root; redirects/retries remain disabled by existing bounded transports.

## Runtime composition

Windows-only host requires existing protected data directory/journal and exclusive local journal ownership. It correlates selected local operation/permit, assigned agent, canonical IDs, credential and pair mapping before HTTP/target IO. CREATE secrets are delivered ephemerally from the server via strict credential transport, never observation credential store or CLI/env password selectors. DPAPI token store supplies fresh bearer per explicit HTTP request. Namespace/absence SQL uses integrated master connection with identity/admin gate, mandatory TLS, no pooling/reconnect; creation SQL login is delivered separately.

One ProcessRunner uses fixed trusted tool ID target-creation-rac, pinned executable/workdir and explicit benign inherited environment. Trusted bundle binds physical RAC/SQL pair and derived coordinator pins. OneShotCommand is an alternative INITIAL route, never continuation after record-target-creation-start consumed the attempt. Existing local attempts yield AlreadyAttempted without GET/POST/claim/physical execution. No automatic worker registration or success/slot-release transition.

Normal output is buffered until command returns and clients/journal cleanup completes, then externally written once. Host failures emit only fixed inert JSON/exit codes; output failure does not repeat lifecycle work. False attempt/ack on errors is absent confirmed metadata, not proof no physical invocation happened. Even acknowledged RAC CREATE ends reconciliation with jobs blocked.

## Verification boundaries

Host tests never execute a real process/SQL connection on any OS: enabled cases reject empty permit/missing journal before target dependencies, while full real coordinator/bundle behavior is covered through fake HTTP/runner/provider in separate composition tests. Windows-specific gates cannot be fully observed on Linux; real DPAPI/endpoint execution is deliberately not tested here. 47 targeted host/options cases passed after strict 1..65535 endpoint validation was added (initial server:0 test exposed opaque lower-level endpoint acceptance). Independent final source/tests review found no blockers. Full local Release2229passed/0failed,264SQLexplicitlyskipped; integration320total. Null configuration is fixed-redacted, late cleanup cancellation after command completion becomes Unavailable5. CIafterpush separately. Optional future coverage: IPv6/max-port acceptance and Windows existing quarantine replay. Follow-up CLI route verified by8actual-agent subprocess cases with disabled/invalid input only, clean environment, isolated cwd, bounded output/10sec timeout and kill-tree cleanup. Full local Release2237passed/0failed,264SQLexplicitlyskipped. Independent final Program/tests/fixture-copy review found no blockers. Program registration is explicit-only and returns before workers; no target IO or operational enablement.