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
