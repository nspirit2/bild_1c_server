# Серверный учёт staged artifacts

## Граница среза

Этот пакет связывает metadata подтверждённой локальной копии с серверным provisioning intent. Это не restore и не включение archive dispatcher. Worker по-прежнему рекламирует только ControlPlaneProbe; Poll не выдаёт ProvisionArchive. Новая библиотека staging не подключается к Worker автоматически.

Архивный пакет `efa1dec` прошёл отдельные Windows/Linux и SQL CI. Этот следующий пакет ведётся в зависимой ветке `feat/artifact-receipts`; предыдущая ветка и новый receipt не считаются слитыми в main.

## Контракт

`POST /api/agent/artifacts/staged`, HTTPS + AgentToken. Payload: JobId, FencingToken, ReservationId, ArchiveVersionId, ArtifactId, Sha256, SizeBytes, Format (dt/bak). Токены и абсолютные пути в metadata не сохраняются. API принимает только закрытый JSON schema; arbitrary location/path не предусмотрен.

Сервис повторно аутентифицирует действующий токен Approved агента в serializable SQL transaction под общей agent-control блокировкой с revoke/rotate. Lease проверяется по SQL UTC: Active, тот же agent/job/fence, срок ещё не истёк. Проверка действует и при повторной доставке: старый receipt не даёт обходить expiry или revoke.

Проверяются provisioning intent, цепочка request/environment/reservation/version/archive/root/pool и неизменяемый SnapshotJson. Размер/формат/hash должны совпадать с выбранной версией и snapshot; reservation Held, staging allowance достаточен; агент и root/pool согласованы. Запрос не может указать чужой reservation или подменить archiveVersion.

## Metadata и состояния

Запись StagedArtifact содержит ArtifactId, job/agent/fence, reservation/archiveVersion, hash/size/format и время SQL. Уникальность: один artifact на job и на reservation/version. Повтор exact payload возвращает существующий receipt без повторного audit; конфликт payload/идентификаторов отклоняется. Metadata защищены от обычных update/delete через DbContext; прямые SQL/admin-действия не считаются защищёнными этим guard.

Состояние `ContentVerified` означает принятый отчёт доверенного агента о совпадении байтов, а не независимое чтение сервером файла. Оно не доказывает наличия файла в данный момент, форматную корректность, SQL VERIFYONLY или совместимость 1С. Нельзя использовать это состояние как замену ArchiveVersion.Ready или Environment.Ready.

При receipt не завершаются job/lease, не освобождаются reservation/quota, не публикуется окружение. Retention/GC/reconciliation ресурсов не реализованы. Перед будущим restore агент должен повторно проверить существование и fingerprint локальной копии; серверный metadata receipt сам файл не удерживает.

## Дормантный протокол и тестирование

Receipt принимается только для ProvisionArchive job в состоянии Leased. Текущий probe scheduler создаёт Running ControlPlaneProbe и не подходит под этот контракт. Для SQL-тестов fixture явно создаёт ProvisionArchive lease, не симулирует публичную выдачу и не меняет production scheduler. Отказ штатному probe — обязательная проверка.

Это контракт подготовленного следующего dispatcher, а не готовый сквозной stage pipeline. Для подключения нужны согласованные состояния выдачи/heartbeat/events archive job, durable outbox artifact report, server plan/execution store и lease-aware handler. Нельзя просто добавить capability и начать restore.

## Проверки

Фактический локальный прогон: locked restore успешен; Release build **0 warnings / 0 errors**; **161 passed, 0 failed, 26 SQL skipped** (domain39, API40, Worker11, Archive23, Process22, Pipeline26). Четыре новые API/model проверки покрывают machine-only endpoint, HTTPS отказ, input validation до SQL и запрет update/delete receipt. Шесть новых SQL facts скомпилированы, но без SQL opt-in явно skipped: exact replay/no Ready, stale/expired replay, rotate/revoke, payload/identity conflicts, probe/inactive lease, snapshot/root/pool binding.

Сгенерирована четвёртая миграция `20261023090512_StagedArtifactReceipts`: PK ArtifactId, unique JobId и reservation/version, Restrict FK. `has-pending-model-changes` изменений не обнаружил. Первую попытку генерации прервал concurrent write/build, причина перепроверена обычной успешной build, повторная генерация по завершённой assembly успешна.

Read-only ревью: исправлены OR-identity query с двумя найденными строками (теперь structured409), недостающие request.JobId/owner/project проверки и AgentScopeId в snapshot. Raw token, fence/expiry до replay и отсутствие Ready/quota-release подтверждены статически. Это не замена runtime SQL proof.

Пакет отправлен как `473e44a518d8830ffc6b0250e9d6bb00ecd1dff5`. [SQL CI39935503904](https://github.com/nspirit2/bild_1c_server/actions/runs/39935503904) и [Windows/Linux CI39935503630](https://github.com/nspirit2/bild_1c_server/actions/runs/39935503630) завершились success, статус перепроверен через GitHub API. SQL TRX guard требует не менее26 результатов и все Passed; значит новый receipt suite не пропущен. Применение миграций проверено disposable SQL integration test. Точные remote количества Windows/Ubuntu без их TRX не утверждаются. Это не проверка существующей корпоративной БД или реального archive dispatch. В main пакет ещё не слит. Секреты и доступы к рабочим базам для этих тестов не нужны.
