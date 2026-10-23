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

EF `has-pending-model-changes`: изменений нет. NuGet audit всех 15 проектов с transitive dependencies: известных уязвимостей в текущем источнике не найдено. `git diff --check` проходит. Общий CI содержит Linux/Windows matrix и solution-wide tests. Ветка отправлена; подтверждённый remote результат приведён ниже.

## Исправления по ревью

- Хеш плана учитывает значения параметров, схемы/версии и отсортированные input maps; внутренние конструкторы не позволяют собрать plan в обход compiler через публичный API.
- Lease повторно проверяется после handler до принятия outputs; false/exception не превращаются в Succeeded.
- После lease guard повторно проверяется caller cancellation до новой операции; отдельный regression test.
- Mutating handler при in-flight cancellation/timeout/исключении возвращает NeedsAttention без retry.
- Synchronous prefix output callback вынесен в Task.Run: он не блокирует установку наблюдения за process timeout. Некооперативный callback всё ещё может продолжиться в фоне после возврата runner; это не изолированный код.
- Вывод продолжает дренироваться после cap; секреты маскируются до разбиения на строки/chunks публикации.
- Detached/reparented descendant escape остаётся ограничением .NET tree kill, а не скрытой гарантией OS containment.

## Подтверждённый Linux/Windows CI

Пакет отправлен коммитом `a866481`. Первый прогон выявил дефект CRLF-нормализации в тесте порядка вывода на Windows: пустая строка stderr от fixture удалялась до очистки CR. Исправлен только тест, коммит `37cec5bcb582f258f08d2edcd64bbd78b4832b8c`.

Повторный [Build and test 39800065762](https://github.com/nspirit2/bild_1c_server/actions/runs/39800065762) завершился success. Предоставленные пользователем ZIP/TRX обеих ОС прочитаны непосредственно; в каждой платформе 6 TRX:

| Платформа | Всего | Выполнено | Passed | Failed | NotExecuted по UnitTestResult |
|---|---:|---:|---:|---:|---:|
| Windows | 140 | 124 | 124 | 0 | 16 SQL |
| Ubuntu | 140 | 124 | 124 | 0 | 16 SQL |

В обеих платформах: domain39, API26, Worker/SQLite11, ProcessRunner22, Pipeline26. Error/timeout/aborted = 0. У SQL TRX Counters.notExecuted=0 при executed=0, но все 16 UnitTestResult имеют outcome NotExecuted; пропуски определены по фактическим результатам тестов, а не этому счётчику.

SHA-256 исходных архивов:

- Windows `d3bfe0f37ff7237b1fe05ce768fb5efcf4ba7ddf8a9a3e7050f33d0fa759cdeb`;
- Ubuntu `1155804f1df66d34ad18f0a2ee1cbc215a4fd248bdc3fdb09f449ae21dd17af6`.

Это подтверждает process/pipeline tests под Windows и Ubuntu, но не установку Windows-службы, DPAPI roundtrip, service account ACL или реальные providers 1С/SQL. SQL отдельно подтверждён 16/16 для предыдущего пакета, новый SQL-прогон здесь не заявляется.

Отчёт предыдущего SQL-прогона `6b63e84` также отправлен в агентскую ветку. Слияний в main нет.
