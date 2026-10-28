# Explicit bounded cleanup result delivery и integrated cleanup cycle

## Явная доставка одного результата

CleanupResultDelivery.DeliverOneAsync выбирает один старейший Pending через SQLiteORDERBYrowidLIMIT1. Никакойunboundedbatchзагрузки, startup/hostedloop/autodelete/claim/release нет. Caller сериализуетjournal и stage/cleanup coordinator. ExistingAgentTransport HTTPSorigin/Bearercredential/noRedirect/noCookies. POSTapi/agent/stage/cleanup-result6полей, exact200; ResponseHeadersRead+linked20stimeout+16KiBactualstreamlimit (недоверяемContent-Length). StrictJSONunknownfieldsreject, exactResult/Auth/Artifact/DeletedReported/nondefaultReceivedAt; никакие errorresponsebody не выводятся.

Успех даётjournalAck. Историческая семантика20ff10d:400/409/ReceiptMismatch ->Blocked+exception. **Исправлено5961581:** generic400/409, включаяCleanupResultDisabled, оставляютPending; Blocked только для allowlisted definitive error codes и ReceiptMismatch, см. [одноразовую команду](cleanup-delivery-command.md). Никакихautomaticreset/newResultId. 401/403/404/429/5xx/network/cancellation оставляютPending+exception для последующей явнойпопытки. Еслисерверcommitted иответпотерян, ResultIdсохраняетсячерезreopen, retryполучаеттотжеreceipt. Это библиотечныйпуть, не включённыйproductionservice.

## Интегрированная цепочка

ДваSQLfacts используютреальныеtempfiles, ArchiveStaging, SQLiteAgentJournal, SQLapplicationports инастоящийAgentTransport сfakeHttpMessageHandler, вызывающимauthenticatedSqlStageCleanupResults. Order/poll ->realstagebytes+manifest ->artifact/completion+localAck ->intent/auth ->localcleanup+permanentmarkers ->outbox ->delivery ->Acked ->adminstagingrelease ->CleanupStatusheld0. Sourcebytesunchanged, DBreservation100/Held/envRequested/unsettled/requestPendingсохранены. Второйfact бросаетnetworkerror послеservercommit, reopenпередretry, подтверждаетoneResultId/receipt/audit/release.

**Это не TLS/network/webhost/browserE2E**: URIHTTPS, ноhandlerin-processdispatch. Не1Сarchiveformat/restoreproof. Teststagebytes являютсясинтетическими. No productionartifact/config/DBschema/servicechange.

## Проверки

Локально **476.NETpassed,0failed,98SQLskipped**, **38Nodepassed**: Domain39/API105/Agent240/Archive44/Process22/Pipeline26. Новые46Agenttests=26transport+20delivery, includinginfinitechunkstreamreadsmax16385bytes despiteContentLengthlie, wrongreceipts,definitivevsretriablestatus,oldestof2Pendingonly. IntegrationAgentProjectReferenceдобавлен, ownedpackages.lockобновлён; lockedrestorepass, build0warnings/errors (ProtectedData9/10transitiveconflict устранён актуальнымlockgraph).

**Windows98SQLpassed,0failed,0skipped,30s**, portableVSTest18.0.1, TRXTestRuns\\CleanupDelivery-20261028. Этотполныйпрогонповторяет6cleanupstatusfacts послеусиленияsnapshotchecks иплюс2integratedcyclefacts. **WindowsAgent240passed,0failed,0skipped**,3s, тотжеTestRunsresults. ОстаточныхdisposableDB0, mainBuildSystem4миграции неизменны. НовыхEFмиграцийнет.
