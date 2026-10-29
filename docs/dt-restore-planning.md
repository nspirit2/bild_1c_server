# План восстановления DT: review-only, без исполнения

## Назначение

Следующий шаг после staging/cleanup — связать конкретный контент архива с конкретной целью восстановления. Библиотека Execution формирует immutable pathless review descriptor. Она **не запускает 1С**, не создаёт IB/SQL DB и не меняет environment state.

Вход содержит pinned GUIDs environment/job/reservation/artifact/archive version/target/cluster/SQL-server, положительный fence, размер, SHA256, format `dt` и preparation profile. Operator-owned snapshot сопоставляет target GUID с cluster/SQL IDs и именами IB/DB; pipeline не передаёт connection string, executable, paths, flags или credentials. Для v1 имена строго `bs_test_{reservation:N}`. Имя — ограничение соглашения, **не доказательство владения**.

План перечисляет обязательные будущие фазы: проверка артефакта, свежего полномочия, принадлежащей операции пустой цели, блокировки scheduled jobs, restore, изоляционной подготовки, verification и publish. Наличие этих пунктов не означает, что они выполнены. План/его hash не является grant или свидетельством готовности базы. `RecipeSha256` — identity плана; `Request.Sha256` — fingerprint архива. Это разные значения. Mapping snapshot ограничен 1024 целями.

В public API нет `ProcessCommand`, аргументов запуска, converter-to-command, filesystem IO, runner/CLI/worker/DI регистрации. Код не расширяет stage-only lease: он не даёт права восстановления. Stage completion и cleanup receipt не превращаются в Ready.

## Перед реальным restore

По документации [1Ci](https://kb.1ci.com/1C_Enterprise_Platform/1C_Enterprise_Platform_Overview/Administration_tools/Dumping_and_restoring_infobases/) восстановление **полностью заменяет текущую IB**. Поэтому следующий исполняющий адаптер потребует fresh server-owned authority, target incarnation/ownership journal, доказательство пустой цели, отсутствие конфликтующей операции/сессий, disabled scheduled jobs и изоляцию внешних интеграций. Результат процесса с exit code 0 не заменяет verification.

Пока не зафиксирован CLI recipe под проверенную версию платформы. [Designer batch mode](https://1c-dn.com/1c_enterprise/designer_batch_mode/) подтверждает наличие пакетного режима, но этот пакет не обещает валидность конкретного набора CLI flags. BAK/SQL RESTORE в scope не входят.

## Стенд и доказательства

Readonly-проверка 29.10.2026: Agent service Stopped/Manual; стандартные каталоги `C:\Program Files\1cv8`, `C:\Program Files (x86)\1cv8`, `E:\Program Files\1cv8` не найдены. Это не полная инвентаризация и не доказательство отсутствия 1С в нестандартном месте. Установка ПО, запуск службы, IIS/SQL mutations не выполнялись.

Предыдущий f8c6915: [Build40734773090](https://github.com/nspirit2/bild_1c_server/actions/runs/40734773090) и [SQL40734773065](https://github.com/nspirit2/bild_1c_server/actions/runs/40734773065) success.

Проверено: **538 .NET passed, 0 failed, 114 SQL explicitly skipped** локально; **38 Node passed**. Pipeline suite — **56 passed**, включая 30 новых planner cases. **Windows 56/56 passed**, 0 failed/skipped, 1s, TRX TestRuns\\DtRestorePlanning-20261029. Locked restore успешно, dependencies и EF schema не менялись. Независимый review не нашёл исполнения или authority bypass; его замечания устранены: bounded mappings и недвусмысленное RecipeSha256. Новое CI выполняется после push; предыдущий 114 SQL suite не объявляется новым standalone SQL-прогоном этого pure-library пакета.
