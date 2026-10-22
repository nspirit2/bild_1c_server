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
