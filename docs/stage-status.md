# Read-only stage status

GET `/api/test-environments/{environmentId}/stage-status` — human authenticated API, не machine endpoint. Доступен владельцу окружения с текущим членством в enabled project либо Administrator. Foreign/missing/probe/nonmember возвращают одинаковый404; admin может инспектировать disabled project. Source root ACL не является правом на historical read: просмотр собственных безопасных metadata не выдаёт новой execution authority.

DTO содержит environment/request/job IDs и состояния, reservation state/staging bytes, unsettled flag, artifact ID/state, lease state/expiry, ReconciliationCode и ObservedAt (SQL UTC). Нет agent identity, token, snapshot, исходного пути, fingerprint/hash, SQL targets и free-text diagnostics.

## Значения ReconciliationCode

- AwaitingStage — Pending job без lease/artifact.
- StageActive — Leased/Active до deadline, server artifact отсутствует.
- ArtifactReported — ContentVerified зафиксирован при действующем stage lease; это не local artifact ack и не stage completion.
- LeaseExpired — Active в БД, но deadline прошёл. GET не выполняет sweep и не меняет state.
- NeedsAttention — control plane уже записал соответствующий lease/job/environment state.
- StageCompleted — точная server metadata комбинация StageCompleted job, Completed lease, ContentVerified artifact. Не означает Ready, validation или наличие файла на диске.
- Inconsistent — комбинация состояний не соответствует поддерживаемому stage lifecycle; автоматических corrective actions нет.

Структурно повреждённые reciprocal bindings (request/job/environment/reservation/artifact/lease/version) дают409 StageStatusInconsistent без раскрытия чужих metadata. Это не разрешение на reset. Наблюдение coherent в read-only Serializable SQL transaction с execution strategy retry; AsNoTracking, без SaveChanges, expiry sweep, audit mutation и scheduling applocks. SQL read locks кратковременно могут конкурировать с writers; read-only не означает zero-lock.

Статус показывает серверные metadata, не агентский SQLite outbox, не фактическое наличие/формат артефакта, не доказательство остановки процесса или актуальное подключение агента. ObservedAt — время наблюдения, не health timestamp. Не использовать один GET как permission на cleanup/reassign/restore.

## Reconciliation вручную

1. Получить status с authorized human account.
2. Для LeaseExpired/NeedsAttention/409/Inconsistent сохранить безопасные IDs/state и проверить audit/agent journal в защищённом канале.
3. Сверить lease/fence, receipt/completion outcome и staged bytes, прежде чем принимать решение.
4. Ничего не удалять/не перепривязывать автоматически. Recovery/reset endpoint и автоматическая выдача нового fence отсутствуют.

Stage flags остаются defaultfalse; GET работает независимо от enable flag, чтобы видеть исторические операции. Restore/Ready/quota release/GC не добавлены. SQL schema не изменена.

## Проверки

Локально locked restore успешен, Release build0 warnings/0 errors. Итог: **292 passed, 0 failed, 47 SQL skipped** (domain39, API47, Agent135, Archive23, Process22, Pipeline26). Новые3 HTTP tests проверяют human auth/machine denial, actor forwarding, GET без CSRF и404 недоступного объекта. SQL5 facts скомпилированы/обнаружены, но runtime SQL proof локально отсутствует. Статическое read-only review не выявило дополнительных серьёзных authorization/leak/coherence/classification ошибок; read-only означает отсутствие writes/sweep, не отсутствие SQL locks. Первый общий build был остановлен timeout20sec, повтор с60sec завершился успешно; ошибок компиляции на повторе нет. Тесты SQL нельзя считать пройденными до SQL CI без skips. SQL suite расширен5 facts (guard47): owner/admin isolation/currentmembership, actual stage->receipt->completion, expired read без sweep, DTO allowlist и отсутствие persistent side effects. HTTP tests используют stub application port и не доказывают реальный SQL/Identity login.
