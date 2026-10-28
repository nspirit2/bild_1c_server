# Durable stage retirement intent — первый серверный шаг

POST `/api/test-environments/{id}/stage-retirement-intents` с cookie human auth и CSRF. JSON только `expectedJobId` и `expectedArtifactId`. Ожидаемые IDs — защита от устаревшего UI-контекста, не полномочие удалить файл. Ответ200 содержит RetirementId/EnvironmentId/JobId/ArtifactId/State/CreatedAt. State Requested — сохранённое намерение, **не cleanup queued/completed**, не обещание освобождения ресурсов.

## Граница полномочий

Только exact StageCompleted+Completed+ContentVerified и coherent environment/request/reservation/source/agent identity. Owner+currentmembership+enabledproject либоadmin, но иadmin должен иметь текущие enabledproject/root/pool и разрешённые root/project/poolbindings. Повтор снова проверяет права и metadata. Availability Ready исходной версии и live lease deadline не нужны: stage исторически завершён, pinned identity остаётся обязательной. Токен агента и fullserver snapshot не возвращаются.

Новая immutable таблица StageRetirementIntents: уникальностьEnvironmentId иJobId, capturedartifact/reservation/agent/fence/requestedBy/requestedAt. Foreignkeys restrictdelete, SaveChanges guard не допускает изменения/deletion intent. SQLSerializable+executionstrategy, applockglobalagentcontrol->root->pool; SQLUTCпослеlocks. Никакого expiry sweep для unrelatedleases. Audit StageRetirementRequested только при первом commit.

Отдельный Idempotency-Key не нужен: один intent на environment/job, exact повтор ожидаемой identity возвращает прежний receipt, конфликт409. Owner иadmin видят одну и ту же запись после текущей авторизации, первоначальный RequestedBy не переписывается. Requested здесь не state machine cleanup: переходы в этом пакете не добавлены.

## Не реализовано

Нет UIкнопки retirement, dispatch/poll cleanup, удаления файлов, tombstone/outbox, agentack, quota release, смены job/environment/request/reservation/lease/artifact state. Существующие status15fields и классификация не меняются. После intent stage/status по-прежнему StageCompleted, окружениеunsettled, reservationHeld. Нет automatic retry worker/cleanup/restore/reassignment.

Это миграция metadata, не основание менять production SQL автоматически. Новый API не развёртывается до CI и отдельно согласованного deployment. Smoke synthetic reservation не освобождается. Следующий пакет — bounded agent cleanupauthority и crash-safe execution после согласования domain/accounting отдельно для stagedbytes и envcount.

## Проверки

Локально **330 .NET passed,0 failed,69 SQL skipped**, **30 Node passed**. API85,Domain39,Agent135,Archive23,Process22,Pipeline26. Новые5HTTPcases проверяют human401/CSRF400/allowlistedreceipt/unknownauthorityfield400/emptyidentity400. Новые6SQLfacts компилируются, безSQLopt-in явноskipped: exactreplayпослеhistoricalexpiry+oneaudit+retainedstates; ACLforeign/admin/membership/project/rootrevocation; empty/mismatch; noncompletedstates; immutablemutation/deletion;6concurrentrequestsoneintent. CIguard69. При review admindisabledproject visibility исправлена на404 как уowner, до второй проверкиtuple.

EF10.0.13 сгенерировал миграцию20261028134036_StageRetirementIntent, has-pending-model-changes сообщает nochanges; SQLscript проверен как newtable+indexes+FK+history, без updates существующих данных. Миграция на applicationDB не применялась. В текущем runtime сначала отсутствовали ssh/scp: передача завершилась127доremoteизменений, gitpush128. Установлен openssh-client только локально, push выполнен. После этого **28.10.2026 реальный Windows SQL прогон:69passed,0failed,0skipped**,24s, официальный portable runner18.0.1. TRX counters повторно прочитаны (total/executed/passed69,notExecuted0). В TestRuns\\RetirementIntent-20261028 сохранены package/results. Disposablefixtures создавали только bs_ci_metadata_GUID DB при master+explicitoptin; проверка после suite: остаточныхDB0, mainBuildSystem по-прежнему4миграции, исходныйsmokejobStageCompleted. Новаямиграция применена только в одноразовыхDB, не applicationDB. RawTRX не публикуется, environment details остаются на сервере и временной локальной копии. Никакие agentservice/stageflags/productionrecords этим прогоном не менялись. CI запущен: Build40699101793 иSQL40699101879, на момент проверкиin_progress, не заявлены зелёными. HTTP application-port тесты не доказывают persistence; SQL opt-in facts требуют настоящую disposable SQL DB. NoSecret credential/CA изменения не нужны.
