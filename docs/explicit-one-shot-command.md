# Explicit known-permit one-shot command boundary

TargetCreationOneShotCommand implements only `run-target-creation-once <permit-guid-D>` parsing and an injected command boundary. It is NOT yet wired into Program/host, so this increment cannot enable execution. Default-off command options gate before cancellation, local journal, dependencies or transports. No operation/mapping discovery, configuration override, secret arguments or automatic worker pickup.

This is an alternative INITIAL path. The older metadata-only record-target-creation-start consumes the attempt; a later command MUST NOT continue/adopt that attempt. Existing in-flight or quarantined local state returns AlreadyAttempted without Prepared GET, start POST, credential delivery, claim, namespace or invocation. Original slots remain reserved.

For a persisted exact initial record/binding: validate permit/local pins, sole Prepared GET as inert cross-check (not authority), then invoke the existing fresh one-shot coordinator. The coordinator alone durably begins before start POST, resolves ephemeral secrets, acquires real namespace hold, reads fresh paired absence, obtains server-current one-shot claim and calls one deadline-preserving invocation port. No command-level physical retry or local reset exists.

## Inert output and exit codes

Output is one fixed-schema JSON line, without input IDs/secrets/provider diagnostics: status, executable=false, freshAuthority=false, scheduledJobsMustRemainBlocked=true, retryAllowed=false, physicalInvocationAttempted and creationAcknowledged metadata. One external write attempt; output failure returns5 without reentering any lifecycle code. Secrets and acquired hold are disposed by coordinator before normal output.

- 3 TargetCreationOneShotDisabled
- 2 TargetCreationOneShotAlreadyAttempted
- 4 TargetCreationOneShotReconciliationRequired, even when RAC acknowledgement was observed
- 5 TargetCreationOneShotUnavailable or external output failure
- 6 TargetCreationOneShotReconciliationUnconfirmed (quarantine persistence could not be confirmed)
- 130 TargetCreationOneShotCancelled for prebegin cancellation

On error or unconfirmed exits, false `physicalInvocationAttempted` / `creationAcknowledged` means no confirmed receipt metadata is available, NOT proof that no RAC process started. There is NEVER a success0 from a physical acknowledgement. Ack is not successful creation, ownership, usable target, cleared slot or permission to unblock scheduled jobs. Postbegin cancellation/failure remains coordinator-owned reconciliation; cancellation is not a reason to retry. Unknown local/server start or CREATE result must be independently reconciled.

Future host must remain independently default-off, Windows/DPAPI protected, strict physical/mapping/agent/operation pins, exclusive existing journal ownership and trusted runner/provider composition. Actual enablement, grants, live invocation or migration requires separate operational authorization. No target IO/stand/CREATE is performed in tests.

## Verification

Fake-only tests cover parsing, disabled/pre-cancel behavior, initial exact pins, malformed Prepared cross-check, existing-attempt no IO, credential failure quarantine/replay, and output failure without lifecycle reentry. 16 targeted cases passed; independent final source/tests review found no blockers after a stray source artifact was removed before compilation. Full local Release2182passed/0failed,264SQLexplicitlyskipped; integration320total. CIafterpush separately. Optional future coverage: acknowledged command result still exit4 and command-level postbegin cancellation (lower layers already covered). No Program registration, target IO or stand operations.