# Read-only SQL database presence observer

## Отсутствие требует полноты видимости

Пустой sys.databases результат сам по себе не означает отсутствие базы: SQL Server скрывает часть metadata от недостаточно привилегированного login. Для этого начального fail-closed observer выбрана консервативная предпосылка sysadmin=1 при каждом наблюдении. Это **не** рекомендация выдавать Worker sysadmin: feature не подключён к Worker, provider/factory operator-owned, production identity/least-privilege дизайн требует отдельного решения. Observer не выдаёт прав и не выполняет GRANT.

Подключение должно быть master. Первым фиксированным SELECT проверяются SERVERPROPERTY MachineName/InstanceName (default instance нормализуется MSSQLSERVER) и IS_SRVROLEMEMBER(sysadmin). Ожидаемые machine/instance и canonical server/endpoint mapping GUID задаёт оператор; это декларация mapping, не discovery aliases/SQL AG/physical ownership. Неизвестная/несогласованная identity или неполная видимость дают отказ, не Absent.

Второй фиксированный SELECT обращается к sys.databases с параметром @databaseName nvarchar(128), без конкатенации SQL. Имя строго reservation-derived bs_test_<GUID N>. Сопоставление выполняет SQL Server collation: любой возвращённый row означает Present, даже если фактическое имя отличается регистром/коллационной эквивалентностью. Raw identity не переписывается в ожидаемое имя. Более одного row или неизвестные/null/type/size/state значения отвергаются.

DatabaseId/createDate/state/name — только наблюдаемый fingerprint. Они не доказывают владение, durable incarnation или пустоту. Между двумя SELECT и будущим CREATE остаётся TOCTOU: отсутствующее имя не резервируется, роль/состояние могут измениться. Нужны отдельные authority/fence и creation history; повтор той же scope parser не предотвращает.

## Границы выполнения

DbConnection factory доверенная локальная зависимость. Пользователь не задаёт connection string, provider, SQL text или parameters schema. Единственные операции observer — Open/SELECT/Dispose. Timeout ограничен1..60sec, используется cancellation и CommandTimeout; ошибки/отмена/неполные результаты/несовпавшие пины дают fixed safe exception без SQL text, connection string и inner diagnostic leaks. Никаких CREATE/DROP/restore, journal transitions, Ready/Worker registration.

Review обнаружил дополнительный fail-closed случай: пустой первый resultset не должен пропускать следующий resultset. Исправлено: проверяются FieldCount и отсутствие NextResultAsync для identity и обоих Presence/Absent путей. Регрессия empty-first/nonempty-second закреплена. Отмена проверяется также после async disposal; provider cleanup ошибки не раскрываются.

## Проверки

Fake DbConnection tests проверяют exact queries/typed parameter, scope/mapping до IO, sysadmin visibility gate, identity/master mismatch, Present/Absent/offline/case-equivalent response, malformed/multiple rows, cancellation и безопасные diagnostics. Отдельные SQL opt-in tests используют существующий dedicated host/CI и только принадлежащие fixture bs_ci_metadata_* lifecycle; имена bs_test не создаются. Поэтому настоящий Present нового target не заявляется: реальный SQL subset проверяет read-only absence и отказы.

Окончательная проверка: filtered SQL fake31/31passed; полный локальный Release **812passed/0failed**, SQL138explicitly skipped без opt-in. Windows Server2019 + SQL2019 **138/138passed**,0failed/0skipped, TRX `E:\BuildSystem\TestRuns\sql-observer-20261103\results\ustiantsev.is_SK-CICD-1C-01_2026-11-03_13_14_01.trx`. Locked restore и diff check successful. Portable win-x64 publish RID-only lock sections удалены; Integration lock обновлён только для Tools project reference и существующего YamlDotNet transitive dependency, versions не менялись. Production deployment/metadata и smoke IB не трогались.

Scheduled-jobs-deny=on остаётся обязательным для будущего restore до/после и после restart; SQL presence не заменяет эту политику. Security profile не является обязательным gate.
