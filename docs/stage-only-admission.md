# Explicit nonexecutable stage-only admission

Mode `archive-stage-only` отличается от прежнего `archive`. DTO не принимает executable/готовность цели. Существующий archive payload hash остаётся byte-compatible; новый mode включён в payload hash и same-key cross-mode replay даёт IdempotencyConflict.

Новый job kind ObserveArchiveStage никогда не является ProvisionArchive. Его snapshot содержит admissionMode=archive-stage-only,stageOnly=true,executable=false,scheduledJobsMustRemainBlocked=true. Stage compiler, artifact acceptance, completion, retained-intent и observation проверяют StageAdmission helper. Missing/duplicate/alternate-case/invalid flags отвергаются. Если только Kind поменять на ProvisionArchive, stageOnlytrue/mode mismatch отвергаются. Legacy ProvisionArchive snapshots без всех новых markers сохраняют совместимость; partial markers не legacy fallback.

Для stage-only admission target IsolationVerified/ResourceLimitVerified/ScheduledJobsSuppressionVerified не требуются и НЕ выставляются. Сохраняются owner/project/source ACL, enabled pool/root, policy/membership/mapping consistency, форматы, source availability/bytes/hash, quotas/concurrency/storage/staging limits, canonical IDs, immutable snapshot/current authorization. Это не target restore permission и не ослабление CREATE gate: новые jobs имеют отличный kind и hard nonexecutable snapshot, не execution lease. Обычный archive по-прежнему требует все три verified flags.

RestoreIntent для такого stage — permanent nonexecutable hold/correlation только. Он не подтверждает реальный scheduled-jobs state; safety boolean означает требование сохранять блокировку, не выполненную проверку. Observation-only чтение допустимо при pool scheduled verificationfalse, но готовность restore/Ready не заявляется. Canonical IDs остаются operator-owned pins, RACcluster actualUUID/SQLstablemappingGUID.

Существующие stage UI/retirement/cleanup paths, которые принимают только ProvisionArchive, пока НЕ поддерживают ObserveArchiveStage и fail closed/не отображают его. Автоматического cleanup/release нового stage нет. Artifact/reservation остаютсяheld; это согласованный smoke limitation, не скрытый расход.

No migration: existingKindnvarchar допускает discriminator, snapshotJSON содержитверсионируемыемаркеры. No existingdatarewrites; oldsyntheticrowsнеизменны. Draft ops/ObservationSmoke заблокирован RejectUnreviewedDraft до переработки всех false-ready flags и независимого review, на сервер не переносился.

## Validation

Новые parser/hash tests и SQLintegration проверяют newallfalseadmission, oldarchivePoolUnavailable, snapshotflags/newkind/crossmodeconflict. Helpertests проверяютmissing/tamper/case/duplicate/promotionrejection. FullRelease локально1214passed/0failed (API202,Agent580;SQL166skippedбезopt-in), locked restore/diffcheckpassed. Добавлены SQL admission и полный serverstage→hold→cookieassignment→journal→GET/POSTreceipt newmode/allfalse roundtrip (fake target executor, без RAC/SQLtarget). SQLsuitegate168results/no skips; эти SQLcases пока локально не исполнялись, требуется CI доdeployment. Независимый source review не нашёл demonstrated defect; persistedtamper SQL негативные cases на каждомboundary остаются дополнительным покрытием. Smoke CREATE/restore и прочие target changes не выполняются.
