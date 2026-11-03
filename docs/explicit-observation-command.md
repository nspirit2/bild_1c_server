# Explicit observation command — bounded foundation

Команда observe-target <ObservationRequestId> принимает ровно один nonempty canonical D-format GUID, без произвольных адресов/SQL/имён баз/дополнительных flags. Command primitive вызывает explicit coordinator, затем адресную delivery только для указанного request. Более ранние pending receipts других requests не отправляются и не ACKаются. Выходные сообщения фиксированы, без target IDs/credentials/provider diagnostics.

GetPendingTargetObservationReport читает ровно один request без сканирования первых1000pending. DeliverRequestAsync использует тот же fail-fast delivery gate и exact ACK validation, что batch delivery. Unknown/missing report — отказ, ACKed historical report —0новых доставок, сохранённый pending —один неизменяемый POST. Успех означает historical Recorded, не Ready/freshness/CREATE/restore.

Это foundation command API, ещё не production Program wiring. Default-off host configuration, protected credentials и operator-approved endpoint allowlist подключаются отдельным следующим срезом. Worker/poll/timer/restore не запускаются этим API. Scheduled jobs deny mandatory до/после restore и после restart; security profile не mandatory gate.

## Windows CI investigation

Основной [main c4caabd прогон41631165436](https://github.com/nspirit2/bild_1c_server/actions/runs/41631165436) прошёл Windows и Ubuntu. Попытка скачать failed Windows artifact через публичный GitHub API вернула401; секреты других систем для доступа не использовались. Предыдущее падение CancellationTerminatesDescendantTree на том же SHA не объявляется доказанно объяснённым без failed TRX. Выявлена конкретная fixture race: File.Exists мог увидеть PID path до закрытия File.WriteAllText; чтение могло конфликтовать с Windows writer sharing или получить неполный numeric prefix. Fixture теперь пишет и закрывает adjacent unique temp, затем публикует через same-directory File.Move. Тест всегда отменяет и bounded-await runner в finally при readiness failure, сохраняя исходные root/descendant death assertions и timeout budgets.

Production ProcessRunner не менялся; это fixture publication/cleanup hardening, не Windows Job Object implementation и не ослабление проверки kill tree.

Полный локальный Release **1084passed/0failed**, Agent479cases, SQL164explicitly skipped без opt-in. ProcessRunner filtered22/22passed. Locked restore/diff check успешны. Независимый review addressed delivery/strict journal validation и fixture публикации не нашёл actionable defects. Command primitive проверен на fixed parse/observe-deliver/historical replay/error/cancel; отсутствие unrelated sends покрыто отдельными addressed delivery тестами, а не заявляется каждым command test.

## Проверки

Real SQLite + fake transport/executor проверяют адресную delivery, unrelated pending preservation, lostACK exact retry, history no-reobserve, fixed statuses/cancellation/argument rejection. Полный Release и CI evidence записываются после проверки. Live RAC/SQL targets и production не вызывались.
