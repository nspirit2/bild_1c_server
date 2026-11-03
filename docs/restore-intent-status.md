# Read-only статус retained restore hold

Созданный restore intent не должен оставаться невидимым за историческим StageCompleted. Проекция добавляет к существующему stage-status nullable `restoreIntent`, не меняя смысл `reconciliationCode`: завершение staging не равно выполненному restore.

Минимальные поля: restoreIntentId, targetIncarnation, requestedAt, restoreIntentCode=AwaitingTrustedTarget, executable=false, artifactRetained=true. Incarnation — корреляционный ожидаемый GUID, не доказательство внешней IB и не ownership. Пути, credentials, canonical command recipe и archive bytes не передаются.

Cleanup-status отдельно показывает `restoreIntentHold`. Это дополнительное retained hold поверх staging lifecycle; quota/reservation удерживаются, retirement/cleanup/release блокируются сервером независимо от отображения UI. Проекция не делает recovery/reset/release и не выдаёт grant.

Оба SQL reader используют существующую авторизацию и coherent serializable read-only transaction. Они ищут hold по environment/job/reservation/artifact tuple, проверяют согласованность immutable source/snapshot/canonical pins, а не молча скрывают повреждённый row. Inconsistent metadata fail closed. Нет scheduler expiry sweep, audit write, обновления policy или target mutation.

Операторский UI выводит hold и понятное объяснение: restore пока не разрешён, артефакт сохраняется, автоматического повторного восстановления/очистки нет. Новых кнопок для запуска restore/снятия hold нет; отображение не включает feature. Динамический текст вставляется безопасно существующими renderer helpers.

Независимое review выявило отдельный accounting pin: snapshot stagingBytes должен точно совпадать с reservation.StagingBytes, а не только покрывать размер artifact. Проверка добавлена; SQL regression специально меняет staged accounting в disposable metadata и требует409 от обоих reader без repair/audit.

## Проверки

Отдельные SQL tests проверяют до/после intent, owner/admin/foreign/revoked membership, неизменность stage reconciliation и всех lifecycle/quota/audit scalar fields, fail-closed на повреждённых metadata pins. Node renderer tests проверяют текст и отсутствие новых операций/HTML injection. Это наблюдение metadata, не подтверждение состояния 1С или SQL target. Локальный Release657passed/0failed (SQL134 explicitly skipped без opt-in), Node42/42passed. Locked restore success, diff check success. Первый Windows SQL134 прогон:132passed/2failed — старые exact serialization allowlists ещё не включали новые RestoreIntent/restoreIntentHold поля. Allowlists обновлены целевым образом; это не разрешает пути/секреты/произвольные поля. Окончательный Windows SQL2019 прогон: **134passed/0failed/0skipped**, включая повреждение source hash и staging accounting без repair. Итого791 .NET cases плюс42 Node; EF has-pending-model-changes — no changes. TRX отдельно TestRuns/restore-intent-status-fixed-20261103; рабочий IIS/Agent не обновлялся.
