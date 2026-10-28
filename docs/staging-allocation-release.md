# Отдельное освобождение staging allocation

## Что освобождается

Только логически учтённые staging bytes конкретной reservation после принятого authenticated cleanupresult. **Не ReservedBytes БД, не environmentcount/ownerquota, не HasUnsettledProvisioningOperation, не requestPending.** Reservation.State остаётсяHeld, историческоеReservation.StagingBytes не обнуляется. ArtifactContentVerified — историческая запись, не существующий файл. Никакого нового restore/IB/deleteDB исполнения.

Новая immutable ledger запись одинк одному сResultId/ReservationId — доказательствоaccountingперехода. Admission исключаетполностьюreleasedreservation из**обоих**pool/root stagingSUM, но сохраняет еёвDBstorage/envquota расчётах. Partialrelease не поддерживается: ReleasedBytesдолжныравнятьсяполнойallocation иpinnedartifactSize. Отдельныйledger избегаетложнойinconsistency встарыхStageStatus/StageRetirement/Completion checks, ожидающихStagingBytes>=historicalartifactSize.

## Admin операция

POST `/api/admin/stage-cleanup-results/{resultId}/release-staging`, normalhumanCSRF иAdministrator role. BodyExpectedAuthorizationId/ExpectedReservationId; receiptReleaseId/ResultId/ReservationId/ReleasedBytes/ReleasedAt. FeatureStagingAllocationRelease.Enabled=false default,409StagingReleaseDisabled beforeDB. Service повторнопроверяетactor.IsAdministrator; empty400InvalidStagingRelease, wrong/missingimmutablechain409StagingReleaseMismatch. MachineAgentToken недостаточно, specialCSRFinjectionexemptionнет.

Serializable retry, globalagent-control→root→poollocks, exactacceptedcleanupresult/auth/intent/artifact/job/Completedlease/reservation pins; currentenabledbindings не нужныдляhistoricalcleanupaccounting. Firstrelease atomicallyledger+StagingAllocationReleasedaudit; concurrent/exactreplay одинreceipt/time/audit. Uniqueindexes иEFmodified/deletedguard. Не вычитаем числовойсчётчикповторно — admissionотфильтровываетодинledgerfact.

## Ограничение доверия

Servercleanupreceipt — утверждениеаутентифицированногоагента, не независимыйfilesystemproof. Release — явноеadminрешение учёта послеэтогоутверждения. Ошибочный/злонамеренныйагентможетсоврать;ledgerнепроверяетdisk. Physicalmarkerbytes остаютсянаagentdisk и учитываютсяlocalstagingquota, serverreleaseвернёттольколимитлогическойallocation. НеобещаемреальнуюсвободнуюFSёмкость. Нельзяотменитьledgerdelete/перепривязатьartifact радиповторногоstaging:localpermanenttombstoneзапрещаетidentityreuse.

## Совместимость status/UI

Существующие humanstatus и list wirefields пока **показывают исторические reservationStagingBytes/StateHeld**, а не netremainingstaging. Они не обновляютсявэтомпакете: поле НЕдоказательствотекущегоquotaaccounting. Отдельный UIrelease/receiptview иnetstagingmetadataнужныследующимшагом; не заявляем чтооператорвидитосвобождениевстаромэкране. Releaseendpointвозвращаетточныйreceiptдляявноговызывающегоadmin. Нетautomaticworker/releaseнаreport.

## Проверки

Локально **427.NETpassed,0failed,90SQLskipped**, **30Nodepassed** (API102/Agent194/Archive44/Domain39/Process22/Pipeline26). ПервоначальнаяAPIbuildошибка missingContractsusing исправлена. Новые6HTTPcasespassed. **28.10.2026 Windows90SQLpassed,0failed,0skipped,32s**, portableVSTest18.0.1, TRXTestRuns\\StagingReleaseV3-20261028. ДвапредыдущихSQLпрогона89/90 выявили ошибкупорядкаwireallowlist в тесте (culture-sensitiveOrder vs Ordinal, неaccountingbug); исправленexplicitStringComparer.Ordinal+правильныйexpectedorder, весьsuiteповторён. ОстаточныхdisposableDB0, mainBuildSystem4миграции неизменны. НоваяEFRelease миграция20261028160343_StagingAllocationRelease1table/9columns/6RestrictFK; EFpendingmodelNochanges.

В root-only и pool-onlyсценариях первыйstagingorder блокируетследующийприлимите10, послеreleaseновыйзаказпроходит, третийсноваблокируется. ОтдельнопровереныownerMaxActive/DBstorage/poolDBcapacity — stagingreleaseихнеосвобождает. Physicalproductionartifactнеудалялся. Сервернаясхема/config/serviceнеразвёрнуты.6SQLfacts:concurrentexactoneledger/audit;403/wrong/noacceptedresult;root-only/pool-onlylimitдоrelease409/послеaccepted/следующий409;ownerenv/storage/poolDBquotastillblock;disabledhistoricalscope;immutability.6HTTPcasesrole+CSRF+defaultoff+strictJSON. ProductionDB/config/service неизменяются; реальныеSQLtests толькоdisposablebs_ci_metadata_GUID.
