# Machine retirement plan — inspection-only protocol

Предыдущий intent d6e81b6 и295ba6d прошли Build/SQL CI success поGitHubAPI перед этим пакетом. Этот шаг **не cleanup execution**: выдаётся только описание pinned identity для проверки агентом, не новая lease/authority и не разрешение удалять файл. Намеренно не подключено к StageWorker.

## Endpoint и opt-in

POST `/api/agent/stage/retirement-plan`, HTTPS AgentOnly, dedicated machine metadata CSRF exemption. Human cookie недостаточно; invalidtoken401. `StageRetirement:Enabled` defaultfalse,409 RetirementDisabled доDB. Opt-in отдельно от ArchiveStage, поскольку это лишь чтение существующего intent. Nojob204, plan200.

План содержит ровно10полей RetirementId/EnvironmentId/JobId/ArtifactId/ReservationId/ArchiveVersionId/FencingToken/Sha256/SizeBytes/Format. Ни sourcepath/rootpath, ни targetSQL/agentidentity/credential/snapshot. SHA здесь нужен агенту для проверки pinnedbytes и доступен только machinecredentialscope, не humanstatus.

## SQL

Serializable retry/globalagentcontrol->root->pool. Freshsamecontextcredentialauthenticate иApproved/hashrecheck. Selectone matchingAgentId+Requested intent, RequestedAt/RetirementId orderedTake1. Последующее исправление66aa7c1 исключает intents с принятым cleanup-result; explicit claim проверяет ExpectedRetirementId напрямую, сохраняя прежний replay. См. [queue progress](retirement-queue-progress.md). Any Active/NeedsAttention lease блокирует выдачу(null), даже прошедшийdeadline: нет expiry sweep. Исторический Completedlease expiry допустим.

Current enabledproject/root/pool и bindings, snapshot fingerprint, exactintent/job/artifact/reservation/agent/fence проверяются перед ответом. SourceReadyavailability не требуется. Неверныеbinding/identity409 RetirementPlanMismatch. Повтор читает тотжеplan; никакого acknowledgment, audit или записей. Нет retirement claim/fence renewal/deadline: **старый полученный plan нельзя использовать для удаления после изменения прав**. Настоящая cleanupauthority потребует отдельного lease/CAS/invalidation protocol.

## Проверка агентом

Pure RetirementPlanValidation.Validate: непустые6GUID, положительныйfence/size,64ASCIIhexSHA,exactdt/bak, ArtifactId должен совпасть с archive-stage-v1 SHA256 UUIDv8 identity дляreservation/version. Неверный plan отвергается до любогоIO. Проверка формы и детерминированной identity не доказывает авторизацию, наличиеfile, manifestownership, localjournalsettlement или допустимость удаления. NoHTTPclient/worker/SQLiteoutbox/deletion добавлено.

## Дальше

Нужны fenced cleanupauthority, durablelocalintent+tombstone/outbox, perartifact ownershipmanifest и anti-reparse checks, failure/replay semantics. Перед execution граница инспекции должна быть заменена/дополнена актуальным проверяемым полномочием; нельзя просто подключить этот poll к File.Delete. Stagequota/envcount/estimatedDBrelease остаётся отдельным accounting решением. ApplicationDB/schema/service/stageflags не меняются этим пакетом.

## Проверки

Локально **355 .NET passed,0failed,74SQLskipped**, **30Nodepassed**. API88,Agent157,Domain39,Archive23,Process22,Pipeline26. Начальный независимый validatorcase упал из-за ошибочной длины mixedcase hash в тесте(68вместо64), исправлен testinput,22/22targetedpass; UUIDvector сверён независимымPythonSHA256.

**28.10.2026 реальный Windows SQL:74passed,0failed,0skipped**,24s, portableVSTest18.0.1, fresh test package RetirementPlan-20261028. После suite остаточныхbs_ci_metadata_GUID DB0, mainBuildSystem4миграции — не обновлялась. Config/agentservice/stageflags/applicationrecords не менялись этим прогоном. Testbinaries/TRX сохранены в isolatedTestRuns на сервере; rawenvironmentdata не публикуется. Это проверка SQL/protocol, не destructivecleanup/runtimeagentHTTPsmoke. Новые5SQLfacts покрывают exactread/nosideeffects,otheragent,rotation/revoke,currentbindings/historicalexpiry,unsettledleaseblocking. SQLguard74. API3cases HTTPSAgentOnly/defaultoff; Agent22cases shape/deterministicidentity. Тесты не заменяют destructive cleanup faulttesting или browserE2E.
