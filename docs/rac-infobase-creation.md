# RAC infobase creation command recipe

## Approved choice

Владелец явно выбрал создание новой server infobase через RAC с `--create-database`: SQL DB создаётся платформой1С, отдельногоSQLCREATEпередregistrationнет. `--scheduled-jobs-deny=on` обязательно непосредственно вcreate. ЗатемfutureexecutorобязаннезависимопроверитьSQLDB, registration, exactmappingиscheduledjobsdeny; exit0/stdoutнеownership/emptytargetproof. Unknownoutcome/timeouts/потеряACK/processfailure — reconciliationбезавтоповтораcreate, DROP/recreate/adoption.

Exclusive `bs_test_*` namespace уже подтверждён. Любая существующаяцельконфликт. Длякаждойactualmutationбудутнужны currentserverauthority, durable soleattemptвобоихjournal иудерживаемыйcooperativeSQLnamespaceguard соfreshabsenceSQL+RAC. Отсутствие, записанноеранее, не creation grant.

## Scope of this increment

Только bounded command recipe; **нет `IProcessRunner.RunAsync`, CLI/HTTP/worker регистраций, targetmutation или liveexecution**. ProcessCommand содержит executableargv, но он неauthorizinggrant; recipe.Executable=false означает отсутствиеauthority, а не неспособностьпроизвольногоcallerзапуститькоманду. Callerнесётобязанностьвыполнитьвсеguards; текущиеproductionflowsнепотребляютrecipe.

Operator-onlyoptions: trustedtoolID, endpoint, endpointMappingID, actualRacClusterId, canonicalcluster/server IDs, actualSQLservername, absoluteCWD, timeout, Locale (`ru_RU` по подтверждённой owner policy; default `ru_RU`, любое отличающееся значение отклоняется), default-offEnabled и default-falseExclusiveNamespaceConfirmed. ActualRACclusterGUIDневыводитсяизcanonicalGUID, SQLservernameнеприходитизrequest. Запрещеныarbitraryswitches, pathsилисозданныеcallerимена. Scope содержитoperation/permit/attempt/reservation/incarnation/canonicalcluster/server/mapping, всеnonempty иmatchingtrustedmapping. Именаinfobase/SQLDBвыводятсятолькоизreservation: `bs_test_{reservation:N}`.

Fixed recipe: `infobase create`, cluster+clustercredentials, `--create-database`, derivedname, `--dbms=MSSQLServer`, trusteddbserver, deriveddbname, SQLcredentials, fixed `--locale=ru_RU`, mandatoryjobsdeny, endpointlast. Нетbackup/restore/drop/update/overwrite, maintenance/securityprofile/licenseconfiguration. Securityprofileнеобязателен, каксогласовано. ДополнительныеSQLstorageparametersневыбираютсяскрыто — nativeplatformdefaultsиoperatorSQLpolicyостаютсявнеэтогобилдера.

Четыреcredentialvaluesненулевые/nonemptybounded/nocontrol, всеcredentialargvSensitive=true иKnownSecrets; логированиеProcessCommand/recipe/options/credentials/exceptionredacted. Arguments иKnownSecretsreadonlysnapshots. Это незащищаетargvотлокальногопривилегированногoprocessinspection: trustedtool/processenvironmentсекретызащищаютсяоператором. Не сохранятьcommand/credentialsвdurablejournal/audit.

## Verification boundary

Unit/fake testsпроверяютfixedargv,mandatorydeny,derivedname,option/scope/credentialnegatives,defaultoff/namespacegate,immutability/redaction. Это не real RACversion compatibility, SQL/1Сcreate илиserverfailurevalidation. Futureactualadapterобязаноднократновызватьrunner, boundedcaptureparseиunknownoutcomepersist; вэтотсрезоннеподключён. Стенднеизменяется,liveCREATE/restoreнеразрешеныэтимcommit. Targetedrecipe86expandedcasespassed; независимыйsource reviewбезblockingfindings. ПолныйlocalRelease1530passed/0failed,212SQLexplicitlyskippedбезopt-in; integration215. CIafterpushотдельнопроверяетWindows/LinuxиSQLmetadata, нонеRAC/liveCREATE.