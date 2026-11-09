# Isolated RAC creation invocation adapter

## Owner-approved credential exposure

Владелец разрешил передачу специально выделенных для тестовых агентов credentials в argv RAC. Это осознанная видимость для OS administrators и external process monitoring; encryption-at-rest и application redaction не скрывают argv от ОС. Dedicated identities не оправдывают raw output logging или вывод secret values в исключениях. Не хранить invocation credentials в options/local journal/audit.

## Scope

`RacInfobaseCreationInvocation` — отдельно default-off низкоуровневый adapter над existing `IProcessRunner`, НЕ server permit, НЕ новый execution authority, НЕ worker. Он не регистрируется в DI/HTTP/jobs и не вызывается автоматически. Когда его явно используют с real ProcessRunner, он действительно способен запустить RAC; это не pure parser и не dry run. Перед любым будущим production call caller обязан независимо обеспечить fresh online permit с immutable credential/mapping pins, local durable one-attempt journal, held SQL namespace session, trusted physical endpoints и fresh SQL/RAC absence в одной защищённой фазе. `recipe.Executable=false` не становится grant при передаче adapter.

Существующий `ProcessRunner` даёт trusted executable allowlist, workspace validation, shell=false, ArgumentList, environment allowlist, secret redaction, output/time limits и termination. Adapter не меняет эту реализацию. `IProcessRunner` — trusted dependency: произвольная fake implementation может нарушать timeout/process guarantees, adapter не может сделать её безопасной одним Task.WaitAsync. Process termination/timeout не означает rollback внешнего CREATE.

## Single invocation and fail-closed output

Enabled=false запрещает вызов до чтения recipe/runner. Cancellation, уже наблюдаемая до вызова, не начинает invocation. После входа в runner любые exceptions/cancellation/timeout/invalidresult/overflow трактуются как OutcomeUnknown / ReconciliationRequired, без повторного вызова и raw details. Это может консервативно требовать reconciliation даже если ошибка реально произошла до Process.Start: adapter не гадает о side effects.

Capture ограничен 4096 characters combined по stdout/stderr, serialized callbacks, no output forwarding/logging. Existing ProcessRunner callbacks — raw fragments, не logical lines: adapter не добавляет разделители и сохраняет chunking semantics. После возврата runner capture frozen; late callbacks не меняют result и не сохраняют новый output. Все stdout/stderr остаются внутри invocation; наружу только fixed-code outcome и correlation metadata. Clearing builder references — best effort, не гарантированное стирание managed strings/library buffers.

Только unambiguous zero-exit single `infobase : GUID-D` ACK с positive PID, без stderr/redaction/truncation/cancel даёт AcknowledgementObserved. Это CLI acknowledgement, не proof of ownership/readiness/SQL identity или permission на использование. Независимая проверка обязательна; jobs blocked, Executable=false, RetryAllowed=false сохраняются. Никогда не автоматически повторять ambiguous CREATE.

## Verification

51новыйfake-runnercaseпрошёл,119совместныхRACrecipe/outcome/invocationcasesпрошли. Тестыне стартуютrealprocesses: gating/pre-cancel, exactargv/tokenpassthrough, rawchunkACK,4096boundary/overflow, malformedcapture/result, exceptions/OCEunknown, post-returncancel, concurrentcallbacks/lateoutcomeimmutability. Finalsource/testreviewбезactionablefindings. Limitation: latecallbacktestдоказываетimmutableoutcome, аdiscardbufferподтверждёнsourceinspection; combinedlimitтакжепроверенsource. ПолныйlocalRelease1668passed/0failed,229SQLexplicitlyskipped. CIafterpushотдельно. Stand/RAC/SQL targets не изменяются.