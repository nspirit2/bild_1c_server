# Observation API upgrade — 03.11.2026

Владелец разрешил обновление действующего BuildSystemApi (выбор «Обновить действующий API стенда») перед одним observation-only smoke. Не разрешались target CREATE/restore/drop, снятие scheduled jobs deny, смена service identities/ACL. Live observation ещё не запускался.

## Preflight

SSH по FQDN теперь разрешился в62.122.170.171,22No route/443refused. Внутренний10.21.0.149 доступен22/443; подключение по ранее pinned ED25519 ключу в ci_1c_known_hosts, без изменения knownhosts/trust bypass. Identity RKF\ustiantsev.is. IIS BuildSystemApi Started, physicalPath E:\BuildSystem\Api\releases\0ca1567. BuildSystem.Agent Stopped/Manual LocalService;1C Agent/RAS Running/Manual. Runtime config517bytes, содержимое/секреты не публиковались.

SQL metadata BuildSystem ONLINE/FULL,4migrations: InitialOrders,AgentControlPlane,ArchivePathIdentity,StagedArtifactReceipts. Deployment identity sysadmin1. IIS principal существует, прямые CONNECT/EXECUTE; effective table permissions/role memberships ещё требуют проверки. Нельзя делать вывод об отсутствии DML только из direct permission rows. На E свободно~528GB.

Review7pending migrations: только новые empty tables/indexes/checks/Restrict FKs, без rewrite существующих данных. New tables: StageRetirementIntents,StageCleanupAuthorizations,StageCleanupResults,StagingAllocationReleases,RestoreIntents,TargetObservations,TargetObservationReceipts. Runtime SELECT+INSERT, без UPDATE/DELETE на этих ledgers. Existing AuditEvents write и прочие grants сохраняются. Ordinary admission читает release accounting даже при выключенных новых feature flags — новые SELECT нужны сразу.

## Backup выполнен

COPY_ONLY/CHECKSUM/COMPRESSION metadata backup:
C:\Program Files\Microsoft SQL Server\MSSQL15.MSSQLSERVER\MSSQL\Backup\BuildSystem-pre-9cd2901-20261103-222254.bak
519680bytes, SHA25695296C2FCF8C8C9ED45F7BCE4CD29F22DDDA2CD636075CA9DCCDE858EC87B2CA.
RESTORE VERIFYONLY WITH CHECKSUM успешно. Это проверка backup, не фактический test restore и не гарантия всей recovery chain. Целевые1C/SQLDB не восстанавливались.

Evidence E:\BuildSystem\ApiState\Upgrade-9cd2901-20261103-222254: backup.json, site-before.json, копии appsettings.Production.json/web.config. DataProtectionKeys не копировались/не менялись. Старый release сохранён. ApplicationHost full IIS backup ещё не сделан.

## Release подготовлен локально

Проверенный main9cd29012975d5de0ff017995a06dbcc9c6d2256d, Build/SQL CI success. Portable API publish Release --no-restore успешен. ZIP /tmp/bs-api-9cd2901-upgrade.zip,27694744bytes,SHA256CB900EBD7164DBB49270D2FE8DA381D07C1E6FA45CFA0D722D4F5FAF1E04F447. Передан в evidence каталог, SHA256 проверен на Windows до распаковки в E:\BuildSystem\Api\releases\9cd2901.

## Deployment выполнен

IIS ApplicationHost backup BuildSystem-pre-9cd2901-20261103 создан. Runtime membership db_datareader/db_datawriter уже существовал; db_owner0/CreateTable0, grants/ACL/identities не менялись. Старые protected appsettings.Production.json/web.config скопированы, feature flags не включались.

Site/pool остановлены для metadata migrations. Первый Start-Process wrapper ошибочно прочитал null ExitCode после WaitForExit и остановил дальнейшее переключение; фактическая проверка обнаружила все11migrationhistoryrows, stderr0bytes, no fail markers и отсутствие миграционного процесса. Никаких Down/delete/retryrestore не выполнялось. Повтор exact migrate через Diagnostics.Process с корректным handle был no-op up-to-date и подтвердил exit0. После этого IIS переключён на9cd2901 и Started; deployment.json и оба migration logs сохранены в evidence.

Postdeploy effective runtime TargetObservations SELECT1 INSERT1,TargetObservationReceipts SELECT1,CreateTable0. HTTP проверки с Linux explicit internalIP resolve и exported public CA: /stage200,/stage/app.js200,/api/auth/me401,newmachineGETбезcredential401. Certificate hostname/chain проверены curl --cacert, без --insecure; WindowsHttpClient SSHcontext handshake failed аналогично прежнему deployment, причина не установлена. Системный trust/DNS не менялся.

Live observation/assignment ещё не создан, agentслужба остаётсяостановленной; authenticatedcookie/DB-backedhumanroundtripещёнепроверен.

## Live smoke: согласования и canonical preflight

Владелец отдельно разрешил one-off под RKF\\ustiantsev.is (уже SQLsysadmin), отдельный закрытый DataDirectory и protected копии agent token/RAC credentials; service LocalService не получаетsysadmin/сменуidentity. Также разрешён retained intent/assignment с постоянным hold. Эти операции ещё не выполнены.

Повторный read-only preflight после паузы подтвердил IIS9cd2901Started, RestoreIntents0. Обнаружено, что старый synthetic pool02935e0d-a1bc-4619-b715-a5d7b921b8df иreservation1cb5b120-fde7-4243-aeba-5527f98364a3 содержат canonical ClusterId=SqlServerId=ae8ea6ab-50f6-4248-a11a-7e6bc3c80edb, а ранее проверенный реальный RAC cluster3ca4be26-0f43-4910-956e-b43c6aebdaa4. Нельзя переопределить immutable snapshot/retainedstagepins ради smoke. Ни hold, ни assignment для synthetic цели не создавались.

Владелец согласовал новый real-bound stage-only проход: отдельные metadata pool/agent mappings иorder, копирование/hashverification предоставленного DT, затем hold/observation. Это не CREATE/restoreDB; synthetic история остаётся неизменной. Следующие шаги — подготовить этот отдельный real-bound admission/staging через штатные services, проверив credentials/TLS иtargetidentity; не переписывать snapshot/reservation вручную.

## Stage-only admission review — 04.11.2026

DT source проверен16687989bytes/hashDF76D7...0D3. Создан только новый закрытый E:\BuildSystem\ObservationSmoke-20261104 с Data/Ops/Source/Staging, ACL SYSTEM/Administrators/RKF\\ustiantsev.is FullControl; существующие ACL не менялись. Credentials/DT в него ещё не импортировались.

Владелец разрешил owner quota2environments/2GiB, затем отдельно ownerConcurrency2 (poolConcurrency1/TTL1 без изменений). Изменение квоты ещё НЕ выполнено. Причина concurrency: старый StageCompleted остаётся Requested/unsettled и штатно продолжает учитываться; flags/state не освобождались.

Review выявил design issue: SqlEnvironmentOrders требует IsolationVerified/ResourceLimitVerified даже для фактически stage-only заказа. Draft ops/ObservationSmoke содержит target flags true, что не является доказанной verification реального pool. Поэтому draft НЕ собран/НЕ перенесён/НЕ выполнен и его Run закрыт безусловным RejectUnreviewedDraft. Ни newmetadata graph/agent/order, ни hold/assignment не создавались, quotaнеизменна. No bypass/falseverification.

Владелец согласовал отдельный следующий кодовый этап: honest nonexecutable stage-only admission с tests/CI, без fake target-readiness flags, без ослабления futureCREATE/restoregate и без возврата securityprofileкакmandatory. После этого вернуться к live smoke. Ops draft требует переработки и review до исполнения.

## Real-bound stage выполнен — 04.11.2026

После codefix0d0bf49 BuildWindows/Linux иSQL168/no skips success, reviewed opt-in utilityeb91588 собран/published. Пакеты SHAverified наWindows: API075C68E8314150F4C98989C0320FE877429CE02B73AEFECF79EF65977F99D13B; utility3C58F84F81E0DF12ACAD93709BB23FE84D807A338FA1D993FAFBDC8DAF3E201C. До новых writes созданещёmetadataCOPY_ONLYCHECKSUMbackup/VERIFYONLY, receipt Ops\\pre-smoke-backup.json. API переключён с9cd2901 на0d0bf49Started; новых migrationsнет.

prepare/stage/intent/inspect exit0 через deploymentidentity, sourcecopy/hashverified. Ownerquotaизмененааудированно с1/16MiB/C1 до2/2GiB/C2 TTL1. Newpoola287e145-959c-47ed-b12c-68d72833f94b,agent1bd0382a-d163-47b1-a902-cb8b81655e8e; targetreadinessвсеfalse. Agenttokenновойidentity CurrentUserDPAPI, не копировался LocalService token. Oldsyntheticrowsнеизменны.

Environment54825511-35c9-4fe5-a51f-d46c00ea8251 Requested/unsettledtrue,job2a2e49da-05c5-44cf-93c4-de0ff594869a ObserveArchiveStage/StageCompleted. Localunsettled/pendingartifact/pendingcompletion0. Artifact412fd1fd-5483-8e8b-82e2-864888ed8356 (realDT16687989bytes/hashknown). Retainedintentab6b9adb-e550-47e9-b6c5-eea3a14590e7, reservationcfb65e37-b64d-4f5c-9006-dbbcb9042ab2. Newcanonicalcluster3ca4be26-0f43-4910-956e-b43c6aebdaa4, SQLmappingGUID4e1f3731-5220-4ecb-9fcb-ffea164eb016 expectedactualmachineSK-CICD-1C-01/MSSQLSERVER. Assignment0b07a394-275e-4354-91f3-c4e8514d2665, RACmapping1827db8f-bd77-455f-9eee-64c155120fd8,SQLmapping1c355b7b-da76-45bb-8222-66824041a486. Targetbs_test_cfb65e37b64d4f5c9006dbbcb9042ab2 NONEXECUTABLE/jobsmustremainblocked.

Это permanenthold/retainedquota, не physicalownership/restore/Ready. RACcredentialsimport иactualCLIobservation ещёнепроводились. Следом: защитныйimportсexistingLocalMachineenvelope→CurrentUser (безсекретов), hostTLS/trustidentitypreflight иAPIendpointmappingconfig толькодляэтогоpool, затемодинobserve-target. Applicationfeaturesпокаoff.

## Credentials/TLS и explicit CLI attempt — 04.11.2026

Новый Ops credentials mode проверил envelope ClusterId, импортировал ClusterUser/ClusterPassword existing LocalMachineadmin.dpapi в отдельный CurrentUser observation-credentials.dpapi. Секреты не выводились/не экспортировались. Ops importer/TLS additions пока локальноuncommitted; published helperDLL transferred в закрытыйOpsbin. AgentDPAPItoken отдельный.

DefaultWindowsDNS returns::1 +10.21.0.149; defaultHttpClient/legacyPS failedsocketconnection. .NET10 TLS diagnostic с exact IPv4ConnectCallback, normalFQDN/SNI/certificatechain validation иUseProxyfalse дал auth/me401 и authenticated assignmentGET200. Trustbypass/certificateacceptallcallback НЕ применялись. API TargetObservationEnabledtrue иmappingтолькодляnewpool, originalconfigcopyсохраненаOps/api-config-before-observation.json. HostpinsfileлокальноOpsbin/appsettings.Production.json.

Попыткаdotnet BuildSystem.Agent.dll изcombinedutilitypublish упала доstartup: hostingassemblypruned. Исправленныйlaunch через dotnetexec --runtimeconfigObservationSmoke.runtimeconfig.json --depsfileObservationSmoke.deps.json (какexistingCLItests) дошёл до TargetObservationBlocked/exit4. Rootcauseнеустановлена; defaulttransportDNS/IPv6подозревается, не доказано. TargetObservationReceiptscount0 после попытки. Нельзя заявлять smokecomplete. Нужен bounded diagnostic на границеproductionHTTPclient/coordinator без bypassTLS ибез повторного observer если durable report уже существует; сначала inspectlocaljournal. Единственная попытка не выдала receipt, выполнениеRAC/SQLнеподтверждено. TargetCREATE/restore/drop иscheduledjobsstatechangesнепроизводились.

## Успешный observation-only smoke — 05.11.2026 07:49 МСК

Read-only diagnose сначала подтвердил AssignmentRetained=false, ReportRetained=false, PendingReport=false, UnsettledStage0: предыдущий blocked вызов не дошёл до durableassignment/observer. DefaultDNS productiontransportReadFailed иdirectHttpSocketException. Только process-local DOTNET_SYSTEM_NET_DISABLEIPV6=1 (никакихsystemDNS/hosts/IIS/networkchanges) дал ProductionTransportAssignmentReadOk=True/directHTTP200 с неизменным FQDN, normalSNI/TLSchainvalidation иcanonicalpins. Это воспроизводимо устранило IPv6connectfailure; private/secretdiagnosticsневыводились.

ExplicitAgentCLI через sharedframeworkruntimeconfig/deps, Productionconfig иsameprocessIPv4switch вернул TargetObservationRecorded/exit0. OutcomeBothObservedAbsent: разрешённые read-only RAC/SQL observers не нашли зарезервированное имя на обеих целях. TimestampObserved2026-11-05T04:49:20.914656Z (=07:49:20МСК), Accepted04:49:21.0263496Z. Receipt287f09e1-2089-46a6-83bb-a68f6f46fba5.

Последующий diagnose НЕ создавал executor/coordinator/delivery: AssignmentRetainedtrue, ReportRetainedtrue, PendingReportfalse, localUnsettledStage0. ServerдляassignmentровноодинreceiptсsameGUID, Outcome0/BothObservedAbsent, Executablefalse, ScheduledJobsMustRemainBlockedtrue. RecordedAuditCount1. EnvironmentStateRequested/HasUnsettledProvisioningOperationtrue, НЕ Ready. NoCREATE/restore/drop/targetscheduledjobmutations. BothObservedAbsent — historicallookup, неfreshness/ownership/правонаCREATE/restore.

Permanentholds/reservations/artifactиownerquotaостаютсякакразрешено; старуюsynthetichistoryнеизменяли. API0d0bf49active, newpoolobservationmappingenabled; agentobservationoptin толькоизолированныйOpsbinconfig, штатныйworker/serviceнеподнимался. НеотключалиTLSverification/неизменялиidentityACL. Utilitycredentials/tls/diagnosemodes позволяют auditбезсекретов; noauto credential reissue or cleanup. ЕдинственныйполныйreportсACKполучен; повторныйobserverневызывался.

Down/DBrestore не являются обычным binary rollback: Down удаляет историю; старые binaries могут игнорировать retained holds/release accounting после новых writes. При migration failure остановиться и inspecthistoryobjects, не удалять tables/holds. Newfeature flagsoff до validation, никакого автоматического Ready/CREATE/restore.
