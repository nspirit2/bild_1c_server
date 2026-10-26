# Stage-only control plane

## Разрешение, а не полный provisioning

Отдельные POST `/api/agent/stage/poll` и `/api/agent/stage/heartbeat` используют HTTPS AgentToken/AgentOnly и текущий raw bearer повторно проверяется внутри SQL transaction. Cookie/CSRF human API не является разрешением для этих endpoints. Общий `/api/agent/poll` и capabilities остаются probe-only, общий heartbeat теперь явно принимает только probe job.

Сервер выключен по умолчанию: `ArchiveStage__Enabled=true` — отдельное operator opt-in. Agent также выключен: `Agent__ArchiveStageEnabled=true` выбирает отдельный StageHostedWorker **вместо** ProbeWorker, не параллельно. Нельзя включать два цикла на одном AgentJournal.

StageLeaseGrant содержит только job/fence/expiry, Kind `ArchiveStageOnly` и Plan (reservation/version/root/relativePath/hash/size/format). SQL/cluster/infobase/preparation settings и полный snapshot не передаются. Agent принимает только `ArchiveStageOnly`; старые локальные `ProvisionArchive` lease не повышаются автоматически до этого разрешения и требуют reconciliation. Формат SQLite таблиц совместим, но семантика local kind намеренно ужесточена.

## Серверные состояния

Durable job остаётся immutable `ProvisionArchive`, но выделенный stage endpoint переводит `Pending -> Leased`, создавая Active lease fence1. Это не разрешение restore. До выдачи проверяются текущие agent/root/pool/project bindings, разрешение root для project, reservation Held, staging capacity, Ready source version, fingerprint и immutable snapshot. Path из snapshot точно сравнивается с catalog path, без подстановки новой версии/пути.

Lock order: global agent-control -> root -> pool, serializable SQL transaction и retry. Истёкшие lease помечаются NeedsAttention до savepoint action: отказ stale heartbeat не откатывает expiry sweep. После ожидания source locks срок перепроверяется по SQL UTC. Existing active probe или unresolved NeedsAttention блокируют новую stage выдачу; история lease запрещает automatic reassignment. Lost poll возвращает тот же fence/expiry/plan, не renew. Первый недоступный/некорректный candidate отклоняется, а не пропускается молча; нужен операторский разбор, возможна блокировка очереди до reconciliation.

Stage heartbeat проверяет действующие права и immutable plan, продлевая lease на две минуты. Source/root/pool deactivation или mismatch отказывают; неизвестный исход не даёт разрешения копирования. Никакой optimistic local renewal.

## Агент

Нужны защищённые Windows service configuration и directories, действующий DPAPI token, стандартный `Agent__ApiOrigin`. Для stage mode дополнительно:

- `Agent__StagingDirectory` — абсолютный непересекающийся с sources каталог;
- `Agent__MaxArchiveBytes`, `Agent__MaxStagingBytes` — положительные byte limits;
- `Agent__ArchiveRoots__0__Id` — approved root GUID;
- `Agent__ArchiveRoots__0__AbsolutePath` — локальный/UNC путь;
- `Agent__ArchiveRoots__0__AtomicPublication=true` — явное операторское утверждение политики публикации.

RootId из wire не является произвольным путём: разрешается только локальной approved root mapping. ACL source/staging/journal и consistency mapping между рестартами — обязанность deployment. Не записывать относительные пути или bearer в логи.

StageWorker сериализует работу: сначала artifact outbox, затем существующий stage intent, затем dedicated stage poll. Если crash произошёл после сохранения lease, но до intent, lost grant повторно запрашивается и сверяется по идентичности/expiry. Malformed plan отбрасывается до StoreGrant. Existing wrong kind, expired lease, failed intent, Blocked artifact не разрешают новую работу. После Acked lease остаётся unresolved, новый poll не выполняется.

Coordinator выполняет heartbeat до/после копирования и ограничивает копирование текущим deadline. Во время copy renewal не производится: большие архивы, не укладывающиеся в две минуты, блокируются. Это безопасная граница текущего пакета, а не production throughput claim. Непрерывный revoke monitoring и безопасный serialized periodic renewal — последующая работа.

## Receipt и завершение

ContentVerified receipt сохраняется существующим серверным API после проверки active lease. Он не ставит Ready, не завершает stage/provisioning lease/job, не освобождает quota/reservation и не доказывает форматную корректность. После receipt нет бесконечного renewal: сохраняется блокирующий unresolved lease; expiry sweep/reconciliation должны разобрать операцию. Нет restore, SQL VERIFYONLY, вызова 1С или удаления staged bytes.

Нельзя включать stage flag в рабочем окружении до Windows+SQL CI и управляемого стендового прогона. Новый SQL schema migration не нужен. Тестовые fake guards/HTTP не являются proof реального TLS/UNC/DPAPI/SCM.

## Приёмка

Локально locked restore успешен, Release build0 warnings/0 errors; **257 passed, 0 failed, 34 SQL skipped** (domain39, API43, Agent104, Archive23, Process22, Pipeline26). Новые29 локальных проверок: Agent26, API3. SQL8 новых facts скомпилированы и обнаружены, но без SQL opt-in явно skipped — runtime SQL proof ещё нет. Статическое read-only review не выявило дополнительных серьёзных authorization/stalelease/crash дефектов; исправлено использование свежего SQL UTC после ожидания root/pool locks. Промежуточные build errors от незавершённой записи helper/test footer устранены, полный прогон повторён успешно. Stage/probe general heartbeat разделены; API tests проверяют HTTPS+AgentOnly+raw bearer forwarding и defaultdisabled до доступа к БД. Реальный сквозной стендовый тест ещё не выполнен. SQL suite получил восемь stage-only facts, guard минимум34 результатов без skips. Предыдущий coordinator `f917cac` прошёл [Windows/Linux](https://github.com/nspirit2/bild_1c_server/actions/runs/40288629185) и [SQL](https://github.com/nspirit2/bild_1c_server/actions/runs/40288629273), оба success. Это не проверка нового protocol пакета.
