# План восстановления DT: review-only, без исполнения

## Назначение

Следующий шаг после staging/cleanup — связать конкретный контент архива с конкретной целью восстановления. Библиотека Execution формирует immutable pathless review descriptor. Она **не запускает 1С**, не создаёт IB/SQL DB и не меняет environment state.

Вход содержит pinned GUIDs environment/job/reservation/artifact/archive version/target/cluster/SQL-server, положительный fence, размер, SHA256, format `dt` и preparation profile. Operator-owned snapshot сопоставляет target GUID с cluster/SQL IDs и именами IB/DB; pipeline не передаёт connection string, executable, paths, flags или credentials. Для v1 имена строго `bs_test_{reservation:N}`. Имя — ограничение соглашения, **не доказательство владения**.

План перечисляет обязательные будущие фазы: проверка артефакта, свежего полномочия, принадлежащей операции пустой цели, блокировки scheduled jobs, restore, изоляционной подготовки, verification и publish. Наличие этих пунктов не означает, что они выполнены. План/его hash не является grant или свидетельством готовности базы. `RecipeSha256` — identity плана; `Request.Sha256` — fingerprint архива. Это разные значения. Mapping snapshot ограничен 1024 целями.

В public API нет `ProcessCommand`, аргументов запуска, converter-to-command, filesystem IO, runner/CLI/worker/DI регистрации. Код не расширяет stage-only lease: он не даёт права восстановления. Stage completion и cleanup receipt не превращаются в Ready.

## Проверка запрета регламентных заданий

Добавлен отдельный pure parser `ScheduledJobsReadback` для полного stdout RAC `infobase info`. Принимает только exit0, непрерванный/неусечённый вывод до16384characters, точный GUID IB и единственное `scheduled-jobs-deny : on`. CRLF/LF поддержаны; дубликаты полей, missing/off/unknown, повреждённый вывод и чужая IB отклоняются. Диагностика не включает содержимое вывода.

Scope содержит operation/fence/incarnation/cluster/IB/service epoch/observation/checkpoint. `RequireMatch` проверяет точное совпадение, включая отдельные BeforeRestore, AfterRestore и AfterServiceRestart. **Это caller-supplied correlation, не доказательство свежести, подлинности, владения или полномочий**: RAC info не возвращает operation/cluster/epoch. Новый observation ID/epoch должен выдавать доверенный будущий coordinator; повторное использование того же scope само по себе парсер не предотвращает. Нет clock/IO/runner/worker/DI, реального исполнения restore и обязательного порядка checkpoint пока нет. Результат не является Ready или restore grant.

Правило владельца: scheduled jobs deny включать до восстановления, проверять после него и после рестарта; не снимать без явного разрешения. Security-profile assignment не является обязательным этапом. Замороженный review-v1 не меняется: `PrepareIsolation`/preparation profile — общая подготовка приложения, не обязательное назначение security profile. Ручной успешный smoke не превращает stage-only authority в разрешение автоматического restore.

## Перед реальным restore

По документации [1Ci](https://kb.1ci.com/1C_Enterprise_Platform/1C_Enterprise_Platform_Overview/Administration_tools/Dumping_and_restoring_infobases/) восстановление **полностью заменяет текущую IB**. Поэтому следующий исполняющий адаптер потребует fresh server-owned authority, target incarnation/ownership journal, доказательство пустой цели, отсутствие конфликтующей операции/сессий и disabled scheduled jobs с readback до/после restore и после restart. По решению владельца дополнительный security profile/egress isolation не является обязательным gate; наличие внешних интеграций не скрывается и запрет scheduled jobs не объявляется их блокировкой. Результат процесса с exit code 0 не заменяет verification.

Для автоматического исполнителя пока не зафиксирован versioned CLI recipe; ручной restore и Designer dump на 8.3.22.2557 x64 уже прошли отдельный smoke (см. актуальный паспорт). [Designer batch mode](https://1c-dn.com/1c_enterprise/designer_batch_mode/) подтверждает наличие пакетного режима, но этот пакет не обещает валидность конкретного набора CLI flags. BAK/SQL RESTORE в scope не входят.

## Стенд и доказательства

Readonly-проверка 29.10.2026: Agent service Stopped/Manual; стандартные каталоги `C:\Program Files\1cv8`, `C:\Program Files (x86)\1cv8`, `E:\Program Files\1cv8` не найдены. Это не полная инвентаризация и не доказательство отсутствия 1С в нестандартном месте. Установка ПО, запуск службы, IIS/SQL mutations не выполнялись.

Предыдущий f8c6915: [Build40734773090](https://github.com/nspirit2/bild_1c_server/actions/runs/40734773090) и [SQL40734773065](https://github.com/nspirit2/bild_1c_server/actions/runs/40734773065) success.

Проверено: **538 .NET passed, 0 failed, 114 SQL explicitly skipped** локально; **38 Node passed**. Pipeline suite — **56 passed**, включая 30 новых planner cases. **Windows 56/56 passed**, 0 failed/skipped, 1s, TRX TestRuns\\DtRestorePlanning-20261029. Locked restore успешно, dependencies и EF schema не менялись. Независимый review не нашёл исполнения или authority bypass; его замечания устранены: bounded mappings и недвусмысленное RecipeSha256. [Build 40833736845](https://github.com/nspirit2/bild_1c_server/actions/runs/40833736845) для 694bea5 — success Windows/Ubuntu; [SQL 40833736755](https://github.com/nspirit2/bild_1c_server/actions/runs/40833736755) — success. Предыдущий portable 114 SQL suite не объявляется новым standalone Windows SQL-прогоном этого pure-library пакета.

Дополнительный contract-пакет закрепляет независимый golden vector (Python SHA256 fixed ordered JSON), неизменную публичную review-only поверхность без execution entrypoints и culture invariance Invariant/tr-TR. После него **541 .NET passed, 114 SQL skipped**, **38 Node passed**, **Windows Pipeline 59/59 passed**, 0 failed/skipped, TestRuns\\DtPlanContract-20261029. Изменён только test contract; никаких исполнителей/прав восстановления не добавлено. Golden hash фиксирует recipe v1; намеренное изменение canonical encoding требует нового version/review, а не незаметного обновления expected value.

Финальные code gates ff4be82: [Build 40834134296](https://github.com/nspirit2/bild_1c_server/actions/runs/40834134296) success Windows/Ubuntu, [SQL 40834134316](https://github.com/nspirit2/bild_1c_server/actions/runs/40834134316) success. Следующий docs-only cea849d имеет отдельные CI-прогоны, которые не подменяются этими результатами.

Дополнительно повторены 7 process timeout/cancellation/callback contracts: passed. Предположение review об unbounded ожидании после успешного OS Kill не воспроизведено; callback уже ограничен WaitAsync(stop.Token). OS-level termination failure/невыходящий процесс остаются отдельной темой hardening перед реальным executor, не объявляются установленной причиной прежнего CI-сбоя.
