# Isolated deadline-preserving RAC invocation adapter

Default-off trusted composition, NOT a dry run when enabled with a real runner. No worker, CLI, DI/host registration, target connection or live CREATE is enabled by this increment. Tests use fake IProcessRunner only.

The one-shot invocation port now receives ProcessLaunchDeadline constructed from the SAME TimeProvider, timestamp captured BEFORE the sole claim request and server ExpiresAt-IssuedAt budget. It is never recreated with a fresh start timestamp after delivery or recipe preparation. Coordinator retains its own final elapsed guard and passes this deadline to the port.

TargetCreationDeadlineInvocationAdapter requires its independent Enabled opt-in, existing validated recipe factory and invocation adapter. It constructs only the existing credential-bound scope recipe, then calls named RunWithLaunchDeadlineAsync so existing RunAsync(recipe,ct) is unchanged and has no ambiguous overload with default literals. The named path attaches the EXACT same guard object to a copied ProcessCommand; existing runner enforces it immediately before Process.Start. No authority is inferred from enabled options, recipe executable=false, credential possession or the guard itself. Claim/namespace/absence/current-pins checks belong to the coordinator, not this lower-level adapter. Direct callers must not bypass them.

RAC recipe remains fixed ru_RU, --create-database, --scheduled-jobs-deny=on; no shell, no target adoption/recreation, no retry. Managed credential strings and argv remain transient, redacted by existing runner policy, not persisted/logged; no guarantee of managed memory erasure. Unknown runner result or exception remains reconciliation metadata only. Every coordinator-owned outcome retains quarantine/slots and blocks scheduled jobs.

Final guard narrows setup-delay exposure by surviving through recipe, capture and runner setup. It remains a finite launch-boundary check, not a mathematical atomic check+OS side effect across arbitrary scheduling or a physical fence. Runtime timeout after process start is independent from authorization launch deadline.

Physical namespace/observation adapters remain absent. Canonical->physical RAC UUID mapping and explicit nonreconnecting SQL namespace session remain required before any future real host can enable this composition. No stand/migration/process execution is performed here.

## Verification

Fake runner verifies guard identity, original budget expiry rather than TTL renewal, single call/ack/unknown behavior and independent default-off/expired gates. Existing one-shot tests verify passed guard originates before the claim and still quarantines every outcome. 34 targeted fake-only cases passed; independent final review found no blockers. Full local Release2135passed/0failed,264SQLexplicitlyskipped; integration320total. CIafterpush separately. Optional future coverage: disabled named invocation precedence and cancellation through enabled adapter. No RAC/target process or stand deployment.