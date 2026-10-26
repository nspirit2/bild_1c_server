# Явное stage-only completion

## Назначение

После ContentVerified artifact receipt агент отправляет отдельный POST `/api/agent/stage/complete` с job/fence/artifact identity. Endpoint HTTPS/AgentOnly, не cookie API. Серверный `ArchiveStage:Enabled` и агентский stage mode по-прежнему выключены по умолчанию. Полный provisioning snapshot не передаётся.

Предыдущий renewal `4fba48b` прошёл [Windows/Linux40316303368](https://github.com/nspirit2/bild_1c_server/actions/runs/40316303368) и [SQL40316303251](https://github.com/nspirit2/bild_1c_server/actions/runs/40316303251), оба success по GitHub API. Это не проверка текущего completion пакета.

## Серверный commit

Повторно проверяется текущий token внутри global scheduling transaction. Lock order global -> root -> pool; далее exact artifact/job/agent/fence, ContentVerified, immutable fingerprint/snapshot/path и текущие project/root/pool/reservation permissions. При первом completion нужен живой Active lease и ProvisionArchive/Leased job; после ожидания locks используется свежий SQL UTC.

Атомарно меняются только `job.State=StageCompleted`, `lease.State=Completed` и добавляется один audit `StageCompleted`. Artifact остаётся immutable ContentVerified. Request остаётся Pending, environment Requested/Provisioning с HasUnsettledProvisioningOperation=true, reservation Held и staging quota заняты. Не вызываются restore/VERIFYONLY/1С, не возникает Ready.

Версия source не обязана оставаться Ready: уже подтверждённая pinned копия полезна и после изменения доступности исходника. Но текущий root/pool/project доступ и совпадение metadata/snapshot остаются обязательны; completion не повышает права после deactivation.

## Идемпотентность и expiry

Только точная пара server StageCompleted+Completed с тем же immutable artifact/job/agent/fence доказывает уже committed completion. Такой replay разрешён после прежнего deadline, но с текущим токеном и текущими bindings. First completion expired Active/NeedsAttention не превращается в success. Expiry sweep до action savepoint сохраняется даже при отказе первого stale запроса. Revoke/rotation старого token запрещает и terminal replay.

Потерянный completion response не приводит к новому artifact/fence. Агент повторяет тот же durable CompleteStage payload. Сервер не создаёт повторный audit. Несовпадающая identity/state receipt не может завершить локальный lease.

## Агентский журнал и доставка

Artifact Acked означает только приём ContentVerified сервером. Затем completion outbox сохраняется в SQLite **до HTTP**. Только точный StageCompletionReceipt со state StageCompleted атомарно ставит completion Acked и lease settled. Artifact/event acknowledgements не подменяются; ProbeCompleted не создаётся.

Completion outbox имеет Pending/Blocked/Acked. Первое enqueue после локального expiry сохраняет Blocked/LeaseExpired без HTTP. Но уже существующий Pending разрешено повторить после expiry: сервер мог committed completion до потери ответа. HTTP401/403 и постоянные4xx требуют reconciliation; timeout/network/429/5xx сохраняют Pending. Unknown/mismatched receipt не является ack. Caller cancellation сохраняет неизвестный исход для replay.

MarkSettled вне подтверждённого completion не должен автоматически ack completion: pending запись блокируется. Exact enqueue не стирает Blocked/Acked. Метаданные completion содержат только job/fence/artifact/state/failure code, без путей/token/snapshot.

StageWorker последовательно обслуживает artifact outbox, completion outbox и текущее задание. После completion ack локальный stage lease settled; следующий iteration может poll-ить новое stage-only задание. Сервер никогда не переиздаёт StageCompleted job. Старое окружение всё ещё unsettled и занимает reservation: следующий stage не означает освобождения ресурсов.

## Что остаётся нерешённым

Это успешное завершение **stage-only**, не универсальный reconciliation API. Blocked/revoked/истёкший первый completion требует operator investigation; reset/reassign/GC и automatic cleanup не добавлены. Потеря artifact response после expiry может остаться Blocked ещё до completion, даже если ContentVerified уже есть на сервере. Байты не удаляются и права не восстанавливаются молча.

Следующий provisioning/validation этап должен явно принять StageCompleted artifact и проверить его наличие/hash, а не считать ContentVerified доказательством формата или сохранности. Пока такого перехода нет. Конфигурация Windows/UNC/DPAPI/SCM проверяется на стенде отдельно, не fake HTTP tests.

## Проверки

Локально locked restore успешен, Release build0 warnings/0 errors; **289 passed, 0 failed, 42 SQL skipped** (domain39, API44, Agent135, Archive23, Process22, Pipeline26). Новые19 локальных проверок: Agent18 (journal/delivery/transport/worker), API1 плюс расширен defaultdisabled test. Новые8 SQL facts компилируются и обнаружены, но локально явно skipped без SQL opt-in; реального SQL proof пока нет.

Проверены SQLite reopen/exact replay после expiry, подтверждение полного receipt tuple, auth/conflict/temporary HTTP failures, caller cancel, operator MarkSettled без ложного ack, dedicated HTTPS route/bearer и следующий poll только после completion. Read-only security review не выявило серьёзных auth/idempotency/false-settlement ошибок; актуальный raw token и currentbindings остаются обязательны даже для terminal replay. Это статическое ревью, не runtime Windows/SQL стенд. SQL guard повышен до42:34 прежних +8 completion facts, все должны реально выполняться без skips в SQL CI. SQL/wire EF schema не меняется; локальная SQLite schema добавляет отдельную таблицу completion outbox совместимо.
