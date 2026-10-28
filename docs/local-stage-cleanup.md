# Локальный staged artifact cleanup — без automatic worker/quota release

Предыдущий1ddbf01 прошёл [Build Windows/Ubuntu40708188584](https://github.com/nspirit2/bild_1c_server/actions/runs/40708188584) и [SQL40708188318](https://github.com/nspirit2/bild_1c_server/actions/runs/40708188318) success. СтарыйWindowsfailure35c013b не объяснён логами403, но больше не воспроизводится последующимfullCI; не переписываем историю какsuccess.

## Граница

Локальный executor удаляет только exact stagingartifact по сохранённой авторизации и локальному подтверждённомуstagejournal. Не подключён к worker/HTTP/autostart, нельзя вызвать изinspectionplan. No production deployment или удаление реального smokeartifact. Sourcearchive никогданеудаляется. Нет serverreceipt/resultoutbox/quota release/environmentdeletion.

Файловая безопасность предполагает заранее установленный service-only ACL наstaging/data и отсутствие враждебного writer в этих каталогах. Shared OS .stage.lock защищает cooperatingstage/cleanup процессы, но не злонамеренногоAdmin и не произвольный недоверенный процесс, игнорирующийlock. Anti-reparse и exactmanifest/hashcheck обязательны; это не полная handle-based защита от всехfilesystemTOCTOU/hardlink атак. Не использовать наuser-writable/UNCroots безотдельнойприёмкиfilesystemсемантики.

## Permanent retirement identity

До первогоunlink executor проверяетdeterministicartifactidentity, reservation/version/root/sourceNameHash/size/format/SHA, exactmanifestиrealdatahash. Никаких arbitrarypathfromserver. В директории допустимы толькоdata иmanifest.json, никакойrecursiveDelete. Extra/nested/reparse/missing доintent ->failclosed.

Permanent immutable sibling intent .retired-ARTIFACT.json публикуется сflush+atomicrename до первогоудаления. StageCore под темжеlock отказывает в повторномсоздании retiring/retired identity, даже если dataещёесть. Поэтому ранее принятыйgrant не удалит позднее легально созданныйартефакт тойжеidentity — такуюреинкарнациюзапрещаем.

Послеintent crash-replayпроверяетоставшиесяmanagedfiles иудаляеттолькоподтверждённыепути; unknownfuturefiles не трогает. Completionmarker послеemptydirectoryremoval подтверждаетlocalstate; recreateddirectory послеcompletedmarker ->conflict, noforcedcleanup. Markers не удаляются, продолжают занимать небольшой localspace; serverstagingquota не пересчитывается автоматически.

ДолговечностьFlush(true)+same-volumeatomicrename проверяется contracttests и simulatedcrash, но не powerloss proof directoryfsync/NTFShardware guarantee. Тестовыеfault checkpoints — исключениямежду шагами, не реальноеотключениепитания.

## Journal/coordinator

Preparedauthorization дополняетсяdurablelocalresult Deleting/DeletedLocal в отдельнойтаблице, совместимойспрежнимCHECKPrepared. Beginсначалаповторяетsettledstage/ackedartifact/completion/globalquietguards и сохраняетDeleting; затемфайловыйengine; толькопослеуспехаDeletedLocal. Сбойпосередине оставляетDeleting, replayпоtombstone. DeletedLocal не serverack и не разрешениеquota release; repeatedinvocation опятьпроверяетmarkers/absence, не слеповозвращаетsuccessприrecreatedfiles.

Authorizationnon-expiring иscopeirreversible изпредыдущегопротокола; tokenrevocationнеотменяетранее доставленныйgrant. Доautomaticexecutor вproduction нужно отдельноутвердить revocation/executionpolicy. Сейчас автоматическойдоставкиилиисполнения нет.

## Проверки

На окончательном полном локальном прогоне прошли403.NETtests (Domain39,API92,Agent180,Archive44,Process22,Pipeline26),0failed,79SQLskipped;30Nodepassed. Новые21archive+10coordinatorcases. НаWindowsportableVSTest18.0.1 окончательно прошли **180Agent+44Archive**,0failed/0skipped, толькоtempfixtures, результатывTestRuns\\LocalCleanupFinal-20261028. **Два symlinkcase наWindows делаютраннийreturn из-заполитикиprivilege**, поэтому44passedнеозначаетреальнуюWindowsjunction/reparseприёмку; обаLinuxsymlinkcaseисполнены. SQLсервернаялогика/миграции не менялись, новыйSQLпрогон в этом пакете не заявлен.

Read-onlyreview проверилfalseDeletedLocal/identity/crash/nesteddelete: приtrustedACL+serializedcaller серьёзныхошибокобычногопути неустановлено; дополнительныйdanglingsymlinkedge закрыт: directFile.GetAttributes безExistsgate, отдельныйLinuxслучайпослеintentотклоняетdanglingdirectory и не создаётcompletedmarker. Обафинальныхпрогонапослеисправлениязелёные. Reviewнеявляетсяpowerlosstest илиmaliciousAdminraceproof. Требуютсяsuccess/reopen/crashatsteps/wrongauth/fingerprint/sourceuntouched/nointentmissing/extra/nested/reparse/permanentnostageresurrection. RealWindowscontracttests временныеfiles, неapplicationdata. Restore/IB/SQLtargetsнеучаствуют.
