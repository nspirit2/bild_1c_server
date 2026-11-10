# Offline trusted creation adapter bundle

TargetCreationTrustedAdapters.Create accepts a trusted immutable operator configuration, trusted IProcessRunner and trusted Func<string,DbConnection> provider seam. It constructs only; no callbacks, credentials, target connections, processes or migrations run during factory construction. Enabled and ExclusiveNamespaceConfirmed are independently false by default. Missing gates or invalid config fail with fixed TargetCreationTrustedAdapterUnavailable without provider/runner diagnostics.

The bundle returns NamespacePort, AbsencePort, InvocationPort and OneShotOptions derived from the SAME snapshotted pins. It is not a claim, permission, lease or dry-run flag. With all layers enabled and real provider/runner it CAN contact real targets and create; this increment does not register it with any CLI, worker, DI host or live environment.

## One physical pair

One explicit canonical cluster ID + physical RAC cluster UUID + RAC endpoint/workdir/tool ID configures inventory and creation recipe. One SqlServer string configures both RAC --db-server and SQL Data Source; the same canonical SQL ID and expected machine/instance configure namespace guard and SQL readback. Separate pair/namespace/create mapping and RAC/SQL observation mappings remain pinned. Mandatory encrypted integrated master connection, certificate validation, Pooling=false and ConnectRetryCount=0 apply to fresh provider-created connections. No connection strings or mapping IDs originate from invocation requests.

All constructors validate eagerly; invalid physical config fails before local attempt consumption when the trusted caller constructs this bundle first. Provider and runner are TRUSTED seams and can lie/ignore pins; the type system cannot prove physical identity, ACLs, DNS stability or external administrator exclusion. No arbitrary prebuilt observers are accepted because their private configured physical pins cannot be inspected by a wrapper.

## Port behavior

Namespace port acquires the actual SqlTargetNamespaceGuard lease and privately wraps it. Scope, CheckHeldAsync and awaited DisposeAsync delegate to that real lease; no synthetic held Boolean. Separate connections used by SQL absence reads do not replace or reconnect the retained namespace session.

Absence port rejects wrong canonical cluster/SQL and either observation mapping before either reader starts, then invokes the bounded named expected-scope observer with the caller's fresh distinct IDs. Each call performs both actual reads once and assesses the exact scope; no cache/reissued IDs. SQL readback uses trusted integrated credentials; delivered transient SQL user/password are for RAC creation only. Cluster secrets are passed transiently to RAC inventory.

Invocation uses the existing independently default-off deadline adapter enabled only inside this explicit factory opt-in. It prepares fixed ru_RU/create-database/jobs-deny=on recipe and preserves the SAME preclaim launch deadline into the runner. No renewed budget, retry, success/ownership promotion, target adoption, slot release or job unblock is provided. Every coordinator-owned path ends reconciliation/quarantine even on ack.

The trusted caller must still use the fresh initial coordinator orchestration with server-current claim and durable no-retry semantics. Calling bundle ports directly bypasses those prerequisites and is forbidden by composition contract; these lower-level ports are not standalone authorization APIs. Sequential absence plus cooperative lock is not an atomic physical fence. The finite final Process.Start guard is not mathematically atomic across arbitrary thread suspension.

## Verification

Fake-only tests exercise no-IO construction/default-off/bad config, shared physical argv and SQL source, exact caller observation IDs/no cache, pre-IO pin mismatch, actual guard/lease delegation and preserved invocation deadline. No process or live SQL server is used. Six targeted fake-only cases passed; independent final source/tests review found no blockers, including typed SQL policy helper re-review. Full local Release2165passed/0failed,264SQLexplicitlyskipped; integration320total. Initial three fake-provider failures were test settings bugs (Encrypt Mandatory is not bool; generic keyword aliases differ); fixed with typed SqlConnectionStringBuilder assertions including explicit retry0. No production relaxation. CIafterpush separately. No target IO/stand.