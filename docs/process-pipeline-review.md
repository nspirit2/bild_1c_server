# ProcessRunner / Pipeline: границы приёмки

Ветка `feat/process-pipeline-foundation` создана от `6b63e84` (подтверждённый отчёт SQL CI предыдущего пакета). Новый пакет не требует изменения SQL-модели или открытия ProvisionArchive.

## Проверяемые свойства

- CLI вызывается без shell, только trusted tool, с отдельным ArgumentList и ограниченным workspace/environment.
- stdout/stderr дренируются параллельно даже после исчерпания лимита лога.
- timeout/cancellation/callback failure не оставляют ненаблюдаемый исполняющийся локальный процесс.
- redaction известных секретов не зависит от границ прочитанных chunks.
- YAML compiler fail closed для unknown fields/anchors/aliases/tags/дублирующихся ключей/некорректных ссылок.
- Типы параметров и declared inputs/outputs согласованы; registry задаёт retry safety и capabilities.
- План нельзя подменить через изменяемый словарь или публичную сборку небезопасных шагов в обход compiler.
- Хеш плана учитывает YAML, pinned values и definition fingerprints, а не один текст YAML.
- Executor не начинает следующий шаг после loss of lease/cancellation/неизвестного исхода.

## Ограничения

Проверка нового пакета должна включать console fixture и реальные дочерние процессы, но не 1С/SQL restore. Полная SQL-регрессия предыдущего пакета имеет отдельный подтверждённый прогон; текущие локальные SQL tests могут быть skipped без стенда и не считаются новым runtime SQL-прогоном.

Managed-handler timeout кооперативный; нет гарантии остановить arbitrary .NET-код. Pipeline engine не подключён к API persistence/worker dispatcher. finally/when и durable step state store ещё не реализованы. Restore providers и resource reconciliation отсутствуют.

## Фактический локальный результат

.NET SDK 10.0.402 / Linux Debian12. Locked restore успешен, Release build 0 warnings / 0 errors. Полный прогон: **124 passed, 0 failed, 16 SQL skipped**:

- domain39;
- API26;
- Worker/SQLite11;
- ProcessRunner22;
- Pipeline26.

Новые 48 тестов выполнялись реально, ProcessRunner использовал отдельные процессы .NET fixture. SQL skipped не подменяются успехом: предыдущий коммит `22715d7` имеет отдельно подтверждённые 16/16 SQL passed, новый remote SQL-прогон не запускался.

EF `has-pending-model-changes`: изменений нет. NuGet audit всех 15 проектов с transitive dependencies: известных уязвимостей в текущем источнике не найдено. `git diff --check` проходит. Общий CI уже содержит Linux/Windows matrix и solution-wide tests; текущая ветка не отправлялась, Windows runtime verification нового пакета ещё нет.

## Исправления по ревью

- Хеш плана учитывает значения параметров, схемы/версии и отсортированные input maps; внутренние конструкторы не позволяют собрать plan в обход compiler через публичный API.
- Lease повторно проверяется после handler до принятия outputs; false/exception не превращаются в Succeeded.
- После lease guard повторно проверяется caller cancellation до новой операции; отдельный regression test.
- Mutating handler при in-flight cancellation/timeout/исключении возвращает NeedsAttention без retry.
- Synchronous prefix output callback вынесен в Task.Run: он не блокирует установку наблюдения за process timeout. Некооперативный callback всё ещё может продолжиться в фоне после возврата runner; это не изолированный код.
- Вывод продолжает дренироваться после cap; секреты маскируются до разбиения на строки/chunks публикации.
- Detached/reparented descendant escape остаётся ограничением .NET tree kill, а не скрытой гарантией OS containment.

Изменения пакета пока локальные и не закоммичены. Отчёт предыдущего SQL-прогона закоммичен отдельно как `6b63e84`, не отправлен. Слияний в main нет.
