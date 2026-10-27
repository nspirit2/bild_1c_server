# Удержание ресурсов и следующий retirement protocol

## Реализованный шаг: объяснение, не освобождение

Карточка /stage использует существующий15-field stage-status ответ, тот же coherentSerializable read и ACL. Новых endpoint/DTO/migrations/worker нет. Панель объясняет StageCompleted/Held/unsettled и почему TTL/истечение lease не означают освобождение ресурсов. Только точный согласованный tuple позволяет объяснить успешный stage; malformed/conflicting tuple даёт неопределённость, не разрешение cleanup. Строки через textContent; никакой отправки cleanup/reset/release.

Это небольшой подготовительный lifecycle шаг, **не управление удалением**. Тестовые ресурсы реального smoke остаются на месте, stage off, agent stopped. Настоящий browser E2E этой панели не заявлен.

## Почему нельзя просто закрыть заказ

SqlStageCompletion меняет только jobStageCompleted/leaseCompleted/audit. EnvironmentRequested/Provisioning, requestPending/Provisioning, unsettled=true и reservationHeld остаются обязательными даже для terminalreplay. Agentcompletionack завершает locallease, но stagedbytes не удаляет. Serverreceipt не доказывает текущую сохранность файла/agenthealth/outboxack.

Квота многомерна: EnvironmentQuotaPolicy считает все env кромеDeleted и Requested/Provisioning/Preparing либоunsettled как provisioning; SqlEnvironmentOrders дополнительно считает staging всех reservationState!=Released в root/pool. Нельзя снять только флаг/reservation и объявить ёмкость свободной. Cancelled не существует в EnvironmentState, Enum.Parse сломает следующие admissions; добавлениеenumбезисправленияaccounting также недостаточно.

TTL в принятом запросе не подтверждает запущенный deadline; immutable admission snapshot automaticDeletionEnabled=false. Ни прошедший lease, ни UI clock не являются полномочием удалить или вернуть квоту.

## Предлагаемый протокол retirement — НЕ реализован

1. Human CSRF-authorized intent с currentowner/projectmembership либоadmin, explicitreason и immutable exactenvironment/job/fence/artifact/reservation/incarnation. Повтор intent не создаёт другую authority. StageCompleted admission для retirement отдельно от Pending cancel и fullIB deletion.
2. Durable server intent/CAS/audit; проверка currentbindings/owner/state под согласованными locks, без скрытого сброса HasUnsettled. Конкуренцию с poll/completion/configrevocation сериализовать. FreshSQLUTCпослеожиданий.
3. Отдельная ограниченная fenced cleanup authority агенту: только allowlisted stageartifact; никаких SQLDB/1C drop. Validated identity/pathownership/manifest/hash, anti-reparse, отсутствие liveоперации. Нельзя принимать server/client arbitrarypath или удалять поprefix/namealone.
4. Crash-safe localdeleteintent/tombstone/outbox, identityнепереиспользуется; отсутствие файла до начала не равно доказательству владения. Missing/mismatch/tamper->NeedsAttention, noforcedreset. Lostresponse повторяет exactreceipt, не удаляет новыйартефакт.
5. Matching authenticated serverack с currentcredentialgeneration и pinnedidentity. История/immutableartifactreceipt сохраняются; receiptContentVerified не становится "былвалидныйdt".
6. Serverallocationrelease только после достаточного подтверждения. Stagingbytesrelease **не означает** releaseestimatedDB/envcount. Нужно отдельное domainрешение для stage-only terminalrecord, которого нет в текущем enum/accounting; не подменять Deleted без предусмотренного доказательства SQL/IBabsence. SyntheticIDs не доказывают отсутствие внешнего ресурса.
7. End-to-end faulttests: concurrentpoll/delete/admission, revocation/rotation, wrongfence/identity, missingfile, tamper/reparse, crashbefore/afterdelete, lostack, exactreplayпослеexpiry, nofutureartifactdeletion, quotaonceonly acrossowner/pool/root. До этого destructive lifecycle и autodelete off.

Pending cancellation также отдельный protocol: noleaseever/noartifact/nogrant authority и атомарное accounting; не применять к нашему StageCompleted. Stage-only retirement не заменяет полный lifecycle TestEnvironment.BeginDeletion/ConfirmDeleted с ownershipproof/sessiondrain/jobidentity/SQLIBabsence.

## Проверки текущего пакета

Локально **325 .NET passed,0 failed,63 SQL skipped**, **30 Node cases passed**. API80,Domain39,Agent135,Archive23,Process22,Pipeline26. Продуктовая серверная логика/ACL/DTO не изменялись; новый HTTPcase проверяет embedded panel и отсутствие cleanupauthority. Шесть новых JS cases проверяют completedretainedquota/pending/expiredTTL/malformedtuple/noextraPOST/stalelogoutclear. Статическая проверка сопоставила классификацию с SqlStageStatus; NeedsAttention допускает requestNeedsAttention согласно expiry writer. Настоящий browserE2E и новый realSQL прогон этим UIпакетом не выполнены. Existing63SQL suite без schemachanges остаётся guard63; read-only пояснения не требуют новыхSQLwrites. Тесты должны покрывать consistentterminal tuple, неизвестные/противоречивые значения, удержаниеquota/TTL, stale/logoutclear и отсутствие extraPOST. HTTPпроверка запрещает cleanupmutation route в JS.
