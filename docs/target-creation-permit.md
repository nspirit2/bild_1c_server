# Target creation permit: pure domain foundation

## Явно согласованная owner policy

Администратор инициирует единственную попытку для exact operation/attempt/agent/target pins через default-off feature. Existing target не принимается во владение; нет automatic DROP/recreate/reissue/reset/retry. Unknown outcome/timeout/потеря связи → reconciliation, не повтор внешней команды. Scheduled jobs запрещены до restore, после restore и после restart; security profile не mandatory.

Владелец отдельно согласовал5минутserver-timeна начало: перед первым physical side effect агент обязан online подтвердить current authority и durably записать sole attempt на сервере. Перед каждым следующим изменяющим этапом снова current online check. No offline start/automatic renew/reissue. После записи начала5минутныйdeadline не означает автоматического abort длительного restore и не разрешает повтор. Результат с потерянным ACK не доказывает rollback.

## Что реализует этот increment

Только pure immutable domain descriptor с отдельным PermitId, existing operation pins, pinned AttemptId, initial operation revision, IssuedAt/StartBefore и one-way start/revoke CAS. Он не выдаёт executable authority: Executable всегдаfalse, ScheduledJobsMustRemainBlocked всегдаtrue. НетSQLrows/issuedcredentials/delivery/API/worker/processrunner. State Prepare не Ready и неOwned; RecordStart фиксирует domain metadata, не начало внешней команды.

StartBefore = IssuedAt +5minutes, interval [IssuedAt,StartBefore). DateTimeOffset сравнивается как instant, не browser clock. Puremodel отклоняет время ранееIssuedAt, но не является монотоннымclockservice и не хранит lastSeenTime/revokedAt. За trustedcurrentSQLtime и detectionclockregression отвечает будущийservice; domainreplayнеприобретаетfreshness. Начало в точности наdeadlineотклоняется. Date overflow/invalidrevision/time/IDs отклоняются fixeddomaincodes безpinsвdiagnostics. Revocation доstart запрещаетfirststart. Revocation послеrecordedstart сохраняет attempt/time, неоткатываетphysicaltarget. Exact original start replay возвращает unchangedstate/NewlyRecorded=false, в том числе послеdeadline/revoke; это не новая попытка и не разрешение внешнего действия. ChangedAttempt/staleCAS не выдаётnewsignal. Нет reset/renew/reissue/success/release transitions.

Domain не знает trusted server clock/currentDBadmin/currentagenttoken/online transport/freshness/physicalidentity. Любой caller может создать в памяти descriptor; только будущий SQL issuer с atomic validation/uniqueness и trustedclock сможет сделать из него durable permit evidence. Existing creation ledger остаётся nonexecutable и не меняется в этом increment.

## Следующий интеграционный слой

Нужны отдельный SQLpermitledger с one-per-creation-operation uniqueness иimmutableFK/pins; admin-only issue/revoke; assigned-agent authorization под global/root/pool/canonical locks; trustedSQLtime; связанный atomic operation BeginAttempt + permit RecordStart; currentauthorization ДО replay; audit atomicity; ambiguous commit readback и lostACK не превращается вexec signal. Нельзя durablycommitpermitstart и operationattempt двумя независимыми запросами. Ни stage fencing token, ни historical absence observation не fresh creation fence.

До physical CREATE всё ещё требуются межагентное/physicalalias исключение и доказательство именно созданной нами empty target. Domain pins/GUID не являются таким доказательством. Необходимы boundary tests до/послеsideeffect и ownerpolicy для escalation unknownoutcome. Никакие actual commands или ограничения target ownership не подразумеваются готовыми.

## Проверки

29новыхexpanded domain unit cases прошли: intervalboundary/replay/revocation/CAS/immutablepins/hardflags/redaction, UTCequivalentoffsets/latestrepresentabledeadline/overflow. Полный localRelease1337passed/0failed;194SQLexplicitlyskippedбезopt-in, integration196. Source review не нашёлblockingfindings; это неSQLissuance/network/executorvalidation. CIevidenceпослеpush. Нетdeployment/standconfig/migration/CREATE/restore в этом срезе.