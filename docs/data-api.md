# SQL Server и API

## Таблицы
Projects(Id, Name, RepositoryId, EdtVersion, PlatformVersion)
Repositories(Id, Url, CredentialRef)
Clusters(Id, Name, RasAddress, CredentialRef)
SqlServers(Id, Name, CredentialRef)
Infobases(Id, ProjectId, ClusterId, SqlServerId, Name, DbName, State)
Agents(Id, Name, Host, Status, LastHeartbeat)
AgentCapabilities(AgentId, Tool, Version)
PipelineDefinitions(Id, Name, Version, Yaml, Hash)
Jobs(Id, PipelineId, ProjectId, InfobaseId, Branch, CommitSha, PlanHash, Status)
JobSteps(Id, JobId, StepId, Type, Attempt, Status, ErrorCode)
JobLeases(JobId, AgentId, FencingToken, ExpiresAt)
InfobaseLocks(InfobaseId, JobId, FencingToken)
Artifacts(Id, JobId, Type, StorageUri, Sha256)
Backups(Id, InfobaseId, JobId, StorageUri, Verified, Status)
Deployments(Id, JobId, InfobaseId, CommitSha, ArtifactId, Status)
AuditEvents(Id, ActorId, Action, TargetId, CreatedAt)

Уникальная активная блокировка на одну базу; rowversion для конкурентных переходов; время UTC; секреты только по CredentialRef.

## Публичное API
```http
GET /api/projects
GET /api/projects/{id}/branches
GET /api/infobases
POST /api/infobases
GET /api/pipelines
POST /api/pipelines/validate
POST /api/jobs
GET /api/jobs/{id}
GET /api/jobs/{id}/steps
POST /api/jobs/{id}/cancel
GET /api/agents
GET /api/artifacts/{id}
```

Агентский API: register/capabilities, poll/lease, heartbeat, step events, log streaming, complete. В UI — SignalR. Сервер резолвит ветку в SHA и проверяет доступ до запуска.

## TestEnvironments: данные и API
Подробное поведение — [test-environments.md](test-environments.md).

Дополнительные таблицы:
```text
EnvironmentPools(Id, Name, ClusterId, SqlServerId, Enabled, CapacityBytes, IsolationPolicyId)
EnvironmentTemplates(Id, ProjectId, Name, Enabled)
EnvironmentTemplateVersions(Id, TemplateId, Version, BackupArtifactId, PlatformVersion, ConfigVersion, IsolationPolicyId, EstimatedSizeBytes, PublishedAt)
EnvironmentRequests(Id, OwnerId, ProjectId, Mode, TemplateVersionId, ArtifactId, CommitSha, RequestedTtl, IdempotencyKey, PayloadHash, State, EnvironmentId)
TestEnvironments(Id, RequestId, OwnerId, ProjectId, InfobaseId, PoolId, ReservationId, State, ExpiresAt, DeleteAfter, RowVersion)
EnvironmentReservations(Id, OwnerId, PoolId, SqlDbName, InfobaseName, ReservedBytes, State)
EnvironmentResources(Id, EnvironmentId, ReservationId, Kind, ExternalId, OperationState, OwnershipMarker)
EnvironmentQuotas(SubjectId, MaxActiveEnvironments, MaxConcurrentProvisioning, MaxStorageBytes, MaxTtl)
```

Уникальны (OwnerId, IdempotencyKey), TestEnvironments.RequestId, TestEnvironments.InfobaseId при ненулевом значении, (SqlServerId, DbName) и (ClusterId, Name) технических ресурсов. Reservation разрешает SqlServerId/ClusterId через неизменяемое размещение пула. Версии опубликованных шаблонов неизменяемы; их источники удерживаются на время активных заявок. Ресурсы Failed/Deleting/NeedsAttention продолжают занимать квоту до подтверждённой очистки. Квоты и резервирование проверяются транзакционно с защитой от write skew; одного rowversion отдельных заявок недостаточно.

```http
GET /api/environment-templates?projectId={id}
POST /api/environment-requests
GET /api/environment-requests/{id}
POST /api/environment-requests/{id}/cancel
GET /api/test-environments?owner=me
GET /api/test-environments/{id}
POST /api/test-environments/{id}/extend
POST /api/test-environments/{id}/delete
GET /api/test-environments/{id}/connection
```

POST заказа принимает Idempotency-Key и JSON: projectId, displayName, mode (empty/artifact/branch/template), ровно один соответствующий source (кроме empty), ttlDays. Source содержит artifactId, branch либо templateVersionId. Ветка разрешается сервером в SHA до постановки задания; выбранный источник фиксируется в заявке. Клиент не задаёт ownerId, сервер, техническое имя БД или путь backup.

Успех: HTTP 202 с requestId, environmentId, jobId и Location. Повтор ключа с тем же payload возвращает ту же заявку, с другим — 409. Превышение квот — 409 с кодом QuotaExceeded; ошибки контракта — 400; запрет доступа — 403. Авторизация проверяется на каждом объекте, включая source, логи и connection. 202 означает принятие заказа, а не готовую базу. Валидация источника и резервирование должны завершиться до 202; тяжёлые проверки выполняются заданием.

Продление и удаление требуют ожидаемую версию окружения (If-Match/ETag); конфликт версии — 412. Удаление возвращает 202 и идентификатор задания. Повтор удаления возвращает существующее задание или итог Deleted. Connection доступен владельцу/разрешённым участникам только в Ready и возвращает адрес подключения и способ получения учётной записи без сервисных паролей.

POST /api/infobases остаётся административным API регистрации технического ресурса; разработчик заказывает базу через EnvironmentRequests.
