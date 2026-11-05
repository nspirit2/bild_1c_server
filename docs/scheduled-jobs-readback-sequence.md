# Полный набор scheduled-jobs-deny readbacks

После успешного observation-only smoke следующий локальный safety-срез не создаёт полномочие CREATE/restore. Он проверяет согласованность трёх уже разобранных readbacks: BeforeRestore, AfterRestore, AfterServiceRestart. Каждый readback разобран существующим parser при `scheduled-jobs-deny : on`; получение подлинного полного RAC ответа с нормальным завершением процесса — отдельная обязанность trusted adapter. Public parser не аутентифицирует вызывающего и не доказывает происхождение stdout.

`ScheduledJobsReadbackSequence.RequireComplete` требует:

- exact соответствие OperationId, FencingToken, TargetIncarnationId, ClusterId, InfobaseId во всех трёх scope;
- точные checkpoint значения на соответствующих позициях;
- три разных ObservationId;
- одинаковый ServiceEpoch для BeforeRestore/AfterRestore, другой для AfterServiceRestart;
- все три readback присутствуют.

Return type void. Нет Ready, grant, issuer, transport, IO, часов, команды, restore или restart; нет регистрации в Worker/DI/CLI. `Scope` остаётся caller-supplied correlation. Новый epoch НЕ доказывает реальный restart, а порядок аргументов НЕ доказывает временной порядок наблюдений. Даже успешно проверенный набор не является разрешением запуска и не подтверждает сохранение блокировки между проверками. Future trusted coordinator обязан выдавать/потреблять observation IDs, отслеживать restart epochs и реальную последовательность durable transitions, получать свежие readbacks на каждом этапе. Проверка всего набора не заменяет проверку BeforeRestore перед side effect.

Сбой проверки даёт фиксированный redacted code без target identifiers/raw output. Публичный parser не меняется, existing callers совместимы. SQL/SQLite schema и existing receipts не меняются. Security profile не добавляется как mandatory gate; запрет scheduled jobs остаётся обязательным до/после restore и после restart.

## Следующие prerequisites реального исполнения

Историческое BothObservedAbsent и StageCompleted не выдаёт fresh authority. Нужны отдельные server-issued executable operation/lease/fence, каноническое physical exclusion, проверяемое создание/владение пустой целью с durable reconciliation после неизвестного исхода, artifact verification и отсутствие сессий. ObserveArchiveStage и его retained intent не должны автоматически повышаться до ProvisionArchive или executable lease. Версионированный Designer recipe и фактические process/restore verification также ещё не подключены. Перед реальными target changes требуется конкретное согласование владельца.

На стенде этот срез не запускается и ничего не изменяет. Existing observation receipt/holds/quota сохранены.

## Проверки

Добавлены pure Pipeline tests на полный набор, null/partial inputs, wrong checkpoints, общие pins, повторные observation IDs и неправильные epochs. Локально locked restore и полный Release: 1240 passed, 0 failed; 166 SQL tests explicitly skipped без opt-in (не SQL validation). Pipeline158passed. Независимый review не нашёл blocking code issue; замечание о различии parser/adapter guarantees исправлено. Diff check passed. Windows/Linux и SQL CI должны подтвердиться отдельно после push.
