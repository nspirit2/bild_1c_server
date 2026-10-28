# Durable cleanup result: agent outbox → server receipt

Предыдущий10a96c2 прошёл [BuildWindows/Ubuntu40711012570](https://github.com/nspirit2/bild_1c_server/actions/runs/40711012570) и [SQL40711013029](https://github.com/nspirit2/bild_1c_server/actions/runs/40711013029) success.

## Граница подтверждения

Это запись **утверждения агента** о локальном удалении, не серверная проверка наличия файла. ReceiptState=DeletedReported. No worker dispatch/HTTPdeliveryloop включено, no automatic invocation oflocalexecutor. Productionconfig/DB/service и существующийsyntheticartifact не изменяются. **ReservationHeld,environmentRequested/unsettled,requestPending и квоты остаются прежними**. Очистка stagingallocation — отдельный будущий accountingtransition.

## Wire/API

POST `/api/agent/stage/cleanup-result`, HTTPSAgentOnly dedicatedmachineCSRFnoforgerymetadata. Body ровно ResultId/AuthorizationId/RetirementId/JobId/ArtifactId/FencingToken. Никакихpaths/source/credentials/SHA/body arbitrarylogs. Receipt ResultId/AuthorizationId/ArtifactId/State/ReceivedAt. StageCleanupResult.Enabled defaultfalse;409CleanupResultDisabled beforeDB. Human cookie недостаточно; HTTP/invalidtoken401. UnknownJSONfieldsreject400.

ResultId генерируется одинраз локально в durableoutbox, не при каждой попытке. Сервер immutabletable сPKResultId иuniqueAuthorizationId/ArtifactId. Firstreport atomicallyresult+StageCleanupDeletedReportedaudit; exactreplayтотжеreceipt/time, no duplicateaudit. ДругойResultIdдлятогожеgrant или тотжеIDдляиногоtuple409CleanupResultMismatch. EmptyIDs/nonpositivefence400InvalidCleanupResult.

## Scope и исторический результат

Serializable retry, freshApprovedcredential/token underglobalagentcontrollock, затем historicalroot/pool locks. Exactimmutableauthorization PlanJson/retirement/pinnedartifact/agent/fence сверяются, нет права менять grant заднимчислом. Tokenrotationold401/newcurrentcanreport; revoked401. **Текущие disabledproject/root/pool и новая Activelease не блокируют исторический результат**, посколькуудаление моглобытьзавершено раньше иreportпотеряться. Это не новый executiongrant. Lifecyclecompletion/reservation/retirementintent не мутируются.

## Agent journal

После filesystemcompletion engineverifiespermanentmarkers/absence, однаSQLiteFULLsynctransaction сохраняет DeletedLocal и один Pending result. Crash между ними не оставляетновыйDeletedLocal безновогоresult. Reopen сохраняетID; localcleanupreplay не перегенерирует и не сбрасываетAcked/Blocked. СуществующиеDeletedLocal изпредыдущейверсии не получаютoutboxblindlyприstartup: следующий explicitcoordinatorreplay повторяетenginechecksи затемсоздаётmissingresult.

PendingCleanupResults — allowlistedmetadataсписок (пока без paging/bounded batchlimit) длябудущейявнойдоставки; HTTP здесьнет. Exactreceiptcheckresult/auth/artifact/state/time; Acked сохраняетUTCreceivedtime, changedreceipt/rebindingзапрещены. Blocked исключён изPending и не имеетautomaticreset; mismatch/conflict будущийtransport обязан оставитьдляразбора, неmintnewResultId. Acked не означаетquota release илиpermissionудалитьдругойfile.

## Проверки

Локально **421.NETpassed,0failed,84SQLskipped** (Domain39/API96/Agent194/Archive44/Process22/Pipeline26), **30Nodepassed**. Новые4API+14SQLite+5SQLfacts. НаWindowsportableVSTest18.0.1 **194Agentpassed** и **84SQLpassed**,0failed/0skipped, SQL26s; TRX сохраненывTestRuns\\CleanupResult-20261028. Остаточныхbs_ci_metadata_GUIDDB0; mainBuildSystem4миграции, не обновлялась. НоваяEFRelease миграция20261028155554_StageCleanupResult:1table/6columns/3RestrictFK/uniqueindexes; has-pending-model-changes Nochanges.

Atomicityproof SQLite trigger отклоняетresultinsert послеfilesystemcleanup: DeletedLocalнеcommits, остаётсяDeleting/noresult; послеreopen/storagefix enginecompletedmarkerпроверяетсяповторно исоздаётсяодинresult. LegacyDeletedLocalбезoutbox неbootstrapприconstructor. Readreviewзакрылmultirowcollisionedge: ORlookupпоResultId/Auth/Artifact теперьTake2+count>1->409, а неSingleOrDefault500; двухразныхvalidgrantcollisionfixture не добавлен, existingtestsпокрываютoccupiedresultchangedtuple иnewResultIdforreportedauthorization. API4cases/SQL5facts:concurrentexactreplay,collisions/foreignidentity,rotation/revoke,historicaldisabledscope/newlease,EFimmutableguard. SQLitecasesexecution/reopen/ack/rebind/block/failure иatomiclocalresultidentity. SQLfixtures толькоfreshdisposableDB; mainDBнеапгрейдится.
