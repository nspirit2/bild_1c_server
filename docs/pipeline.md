# Pipeline Engine

## Концепция
YAML -> Parser -> Schema Validator -> Semantic Validator -> Immutable Execution Plan -> Agent Execution. YAML не исполняет произвольный shell-код. `uses` ссылается только на зарегистрированные обработчики `name@version`.

## Интерфейсы
- IPipelineCompiler — разбор и проверка YAML.
- IStepRegistry — типы шагов, версии, схемы входов/выходов, требуемые capabilities.
- IPipelineExecutor — последовательное выполнение шагов.
- IPipelineStateStore — сохранение состояний и попыток в SQL.
- IPipelineEventSink — логи, метрики, события.

## Выражения
Поддерживаются только `${{ parameters.name }}` и `${{ steps.id.outputs.name }}`. Ссылки на будущие шаги запрещены. Выражения не являются программным языком; значения типизированы. Секреты не помещаются в outputs.

## Состояния
Job: Pending, Running, Succeeded, Failed, Cancelled, NeedsAttention.
Step: Pending, Running, Succeeded, Failed, Skipped, Interrupted.

## Повторы и восстановление
Git checkout — ограниченный retry; компиляция — повтор после проверки workspace; backup — новая попытка с отдельным идентификатором; загрузка CF/обновление БД — без автоматического повтора при неопределённом исходе. Lease и fencing token, блокировка по базе. После потери агента обязателен reconciliation фактического состояния.

## finally
Cleanup запускается при обычном завершении и обработанной ошибке, но не гарантируется при падении агента. Отдельный reconciler проверяет незавершённые блокировки. Если состояние базы неопределённо, её нельзя автоматически открывать пользователям.

## Версионирование
Для каждого запуска сохраняются YAML snapshot, план, версии обработчиков, параметры, Git SHA и идентификаторы артефактов.

## Pipeline заказа тестовой базы
Заказ использует тот же engine, lease и registry; отдельный движок не создаётся. Четыре серверных сценария: пустая ИБ, ИБ из архива с диска (.dt/.bak), ИБ с CF (готовый артефакт либо предварительная сборка зафиксированного SHA), ИБ из опубликованного SQL-шаблона. В MVP обязательны пустая ИБ, архив с диска и SQL-шаблон; вариант из ветки подключается к существующей цепочке сборки.

План содержит неизменяемые RequestId, EnvironmentId, ReservationId, template version / ArchiveVersionId / ArtifactId / CommitSha. Эти идентификаторы серверные: пользователь не передаёт произвольные SQL-пути или параметры подключения. Типизированные параметры реестра расширяются типами reservation, templateVersion и archiveVersion.

Пример: [provision-test-base.yaml](../examples/provision-test-base.yaml). Ошибка или отмена оставляет окружение недоступным и создаёт задачу reconciliation; finally не удаляет БД без сверки журнала ресурсов. Обработчик publish атомарно проверяет результаты prepare/verify, актуальный lease и отсутствие запроса отмены. После публикации отмена создания отклоняется; удаление — отдельная операция.

## Pipeline дискового архива
[Пример](../examples/provision-from-disk.yaml): preflight → archive.stage → test.infobase.restore-archive → prepare → verify → publish. Stage фиксирует байты по archiveVersionId и возвращает неизменяемый artifactId; restore не открывает исходный изменяемый путь повторно. Распознавание .dt/.bak выполняет обработчик по проверенному типу артефакта. Полный контракт — [disk-archives.md](disk-archives.md). Восстановление не имеет автоматического retry при неизвестном исходе.

## Поэтапная поддержка синтаксиса
В первом archive-срезе обязательны типизированные параметры/expressions, последовательные steps, registry, timeout и retry; непонятные поля компилятор отклоняет. finally и when: safe-to-unlock обязательны перед включением deploy существующей ИБ, а не до первого заказа архива. Пример deploy-existing относится к последующему этапу. safe-to-unlock — встроенная политика закрытого реестра, а не свободное выражение. В каждом выпуске явно указывается поддерживаемая матрица полей; см. вопрос 30 в [implementation-decisions.md](implementation-decisions.md).
