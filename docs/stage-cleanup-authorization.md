# Cleanup authorization и durable preparation — без удаления

Этот пакет отличает inspection plan от явного server claim и локальной подготовки. **Executor/File.Delete/tombstone/resultoutbox/quota release ещё нет.** Нельзя подключать Prepared records к genericdelete worker. Production flags/DB/service не обновляются этим кодом автоматически.

## Server claim

POST `/api/agent/stage/cleanup-authorization`, HTTPSAgentOnly, JSON ExpectedRetirementId/ExpectedArtifactId. StageCleanup.Enabled=false default. Receipt AuthorizationId/Plan/AuthorizedAt; Plan — те же10pinnedmetadatafields. Human cookie недостаточно, machine CSRF exemption толькоendpointmetadata.

Отдельная durableimmutable metadata record одинк одному сretirement/artifact. CurrenttokenApproved/rotation/revocation, root/pool/projectbindings, fullsnapshot, exactCompletedstage и отсутствиеActive/NeedsAttentionlease проверяются внутриSerializableglobal/root/pooltransaction. При первомcommit добавляется authorization+audit, repeatвозвращаеттотжеID/plan/time. Никакого sweep/postcompletionleaseextension/reassignment/clearingunsettled.

### Семантика срока и отзыва

Authorization — durable, non-expiring, single-pinned-artifact preparation grant, не временнаяcleanuplease. Уже полученные байты ответа нельзя "отозвать" одной tokenrotation; currenttokenrevocation блокирует новыйclaim/replay, но не стирает локальныйgrant. **Протокол отзыва execution, lease renewal/clock и удаление по этомуgrant не реализованы.** До executor нужно явно выбрать irreversible scoped authority либо добавить runtime revalidation/revocationhandshake. Нельзя обещать что disabledserverflag остановит уже начатоеудаление. Пока это безопасно потому, что удаление отсутствует.

## Agent preparation journal

SQLiteFULLsync сохраняет immutableauthorization в statePrepared. Перед записью и exactreplay нужны settledArchiveStageOnlylease, exactlocalStageIntent, Ackedartifact и Ackedmatchingcompletion. OperatorMarkSettled сам по себе не доказательство. Любойunsettledlease, nonackedartifact/completion/event блокирует preparation. Authorization/retirement/artifactidentity нельзя перепривязать. UTCtimestampcanonicalization, fullpayloadexactreplay.

Reopen сохраняетpreparedidentity. Локальный SQLitepreparedrecord не доказывает владение текущим файлом или разрешение delete на момент будущегоexecute. СписокPendingCleanupAuthorizations только reconciliation, не исполняемаяочередь; новые stage grants существующийloop не блокируетнаоснованииPrepared, поэтому будущийexecutor обязан повторить exclusivejournal/stagingconcurrencychecks. No credentials/paths/server snapshots вjournal.

## Следующий destructive шаг

Нужны bounded executor с точнымmanifestidentity/hash/size и anti-reparse/readonlyACL, общийstagingOSlock, durabledeleteintent/tombstone до firstdelete, identityнепереиспользуется; crash before/after eachunlink, unknown/missingfile неclean. Затемdurable resultoutbox, exactserverack и толькодоказанныйstagingallocationrelease. ИсторияArtifactContentVerified остаётсяисторией, environmentcount/estimatedDB/unsettled нельзяснять попутно. Этотпакет ничегоизэтого не заявляет.

## Проверки

Локально **372.NETpassed,0failed,79SQLskipped**, **30Nodepassed** (API92,Agent170,Domain39,Archive23,Process22,Pipeline26). Новые4HTTP+13SQLitecases;5SQLfacts содержат6конкурентныхclaims сoneaudit/onegrant. EFRelease миграция20261028143627_StageCleanupAuthorization сnewtable/3restrictFK/uniqueindexes; has-pending-model-changes nochanges. InitialstaleDebug scaffold былпустым, удалён доapply ипересозданRelease; existing/applicationDBнеизменялась.

**28.10.2026 realWindows:79SQLpassed,170Agentpassed,22Processpassed**,0failed/0skipped во всехтрёхsuites; portableVSTest18.0.1, logs/TRX вTestRuns\\CleanupPreparation-20261028. ОстаточныхdisposableDB0; mainBuildSystem4миграции. Нетproductionstageflag/service/configизменений. Предыдущий35c013b: SQLCI40703311311success,UbuntuBuildsuccess,WindowsBuild40703311078failure наteststep; publiclogs403, поэтомуточнаяпричинапокаНЕустановлена. ЭтотWindowsstandagent/processpass не объясняет весьпредыдущийCIfailure; текущийпакетпотребует отдельнойпроверкиCI. Нельзязаявлятьпредыдущийallgreen. Новые SQLfacts проверяют exactoneauthorization/audit/concurrentconstraints/retainedstates,scope/tokenrotation/bindings иimmutableguard; localSQLitecasesreopen/falseMarkSettled/missingack/mismatch/rebind/overlap. API проверяетHTTPSAgentOnly/defaultoff/strictJSON. Настоящийcleanupfaultproof отсутствует, таккакdeleteкодотсутствует.
