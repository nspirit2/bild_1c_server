# Explicit read-only observation coordinator

Новый coordinator вызывается явно по ObservationRequestId. Он сначала получает assignment через transport (current server auth/bindings), сохраняет immutable tuple в local journal и проверяет исторический report, включая ACKed. Существующий report возвращается неизменённым: нет нового observer invocation, IDs или timestamp. Offline fallback для history не используется; revoked token не обходит серверную проверку через journal.

Если report ещё нет, concrete executor мапит assignment в ровно восемь correlation pins TargetPresenceRequest и вызывает sequential RAC→SQL TargetPresenceObserver. Expected AgentId проверяется до credential provider/adapter IO. Только успешно завершённая и согласованная пара преобразуется в один ReportTargetObservation с новым receipt ID и UTC timestamp. Outcome мапится явным four-value switch; partial/error результат не публикуется.

Coordinator проверяет report assignment и IDs/outcome/timestamp, затем durable enqueue. ReportAsync/ACK выполняет отдельный explicit outbox delivery. Этот срез не вызывает его автоматически, не подключает Worker/DI/timer/poll/restore. На ошибке assignment может остаться сохранённым; повторное явно разрешённое наблюдение после незавершённой пары может получить новые observation IDs. Сохранённый полный report всегда подавляет повторное наблюдение.

## Concurrency boundary

Fail-fast gate защищает один coordinator instance. Host обязан сериализовать journal/observation across instances/processes; это не durable cross-process lease или физическое исключение target. Crash после завершения read-only пары, но до durable enqueue допускает повторное наблюдение при retry. Никакие CREATE/restore/lease/ownership/grant из этого не следуют.

Agent получает compile-time reference на Tools (и существующий Execution/Yaml graph), без новых package versions/SqlClient dependency. Force-evaluate restore обновил Agent/AgentTests/IntegrationTests lock graphs: AgentTests также убрал package assets, предоставляемые уже существующим Microsoft.AspNetCore.App FrameworkReference (NuGet pruning), а не новые version upgrades. Последующий locked restore успешен. Это не включает инструменты автоматически и не настраивает RAC/SQL endpoints или credentials.

Executable=false/ScheduledJobsMustRemainBlocked=true остаются hard policy. Scheduled-jobs-deny=on mandatory до/после restore и после restart; security profile не mandatory gate. Production/config/schema/существующие DB не менялись.

Независимый read-only review не обнаружил concrete defects: auth-before-history, durable assignment-before-observe, ACKed history no-reobserve, complete paired result only, exact pin mapping и per-instance concurrency boundary подтверждены. Crash window до enqueue явно допускает повтор read-only observation, не exactly-once execution.

Окончательный полный Release **1067passed/0failed**, SQL161explicitly skipped без opt-in; Agent tests **462passed** (55новых coordinator/executor/history cases). Locked restore/diff check успешны. Core pipeline использует fake transport/process/SQL adapters и real SQLite journal; live targets не вызывались. Предыдущий transport7bba569 CI Windows/Ubuntu и SQL163 полностью зелёный; Windows нового coordinator будет проверен обычным CI после push.

## Проверки

Coordinator tests используют real SQLite journal + trusted fake transport/executor, проверяют durable order/history/ACK/no-reobserve/current server auth/conflicts/cancel/concurrency. Executor tests используют concrete paired RAC/SQL observers с fake runner/DbConnection, не live targets. Это не live network/1C E2E и не автоматический restore.
