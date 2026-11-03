# Изолированные HTTP observation endpoints

## Human assignment

POST /api/test-environments/{id}/target-observations принимает только ExpectedRestoreIntentId. Нужна обычная Identity cookie и CSRF. Endpoint вызывает ITargetObservations: assignment только для current owner/admin при retained intent и согласованных source/agent/pool pins. AgentToken не заменяет human cookie и не даёт доступа к этому endpoint.

## Machine historical receipt

POST /api/agents/target-observation-receipts принимает полный ReportTargetObservation. Используется существующий internal authentication scheme AgentToken/AgentOnly policy и wire header Authorization: Bearer <token>. Схема имени AgentToken не означает wire prefix AgentToken. Cookie даже с CSRF не подменяет machine credentials. Metadata AgentMachineEndpoint сохраняет общую HTTPS/body/rate/CSRF изоляцию существующего middleware.

Receipt service проверяет current approved assigned agent и immutable assignment до replay; endpoint не авторизует execution. ACK Recorded означает сохранённую историю, не fresh absence proof/Ready/CREATE. Вызовы не создают новые IB/SQL DB и не отправляют команды Worker.

## Default-off configuration

TargetObservation:Enabled=false по умолчанию. TargetObservation:EndpointMappings имеет ключ pool GUID и два nonempty GUID значения RacEndpointMappingId/SqlEndpointMappingId. Конфигурация bindится из final IConfiguration через string-key dictionary, потому что .NET binder игнорирует Guid-key dictionaries; каждый ключ явно parse/validate,≤1024entries, неизвестные members/invalid GUID/нормализованные duplicate keys вызывают startup failure до traffic. Копирование mapping в сервисах не позволяет mutable dictionary изменить уже созданный scoped service.

Эта конфигурация лишь operator assertion, не canonical discovery physical endpoints и не автоматическое подключение provider/tool/credentials. Production feature flags/конфигурация не менялись.

JSON known getter-only Executable/ScheduledJobsMustRemainBlocked fields могут быть проигнорированы десериализатором, а не дать400; hard getters после parse остаются false/true. Неизвестные writable/несуществующие members отклоняются global unmapped-member policy. Это не аутентификация: service обязан заново проверить immutable pins.

Независимый static review options/endpoints/middleware не нашёл human↔machine auth/CSRF bypass; machine header slicing происходит после строгой scheme validation. Fake HTTP human auth не выдаётся за настоящий Identity login — это отдельно покрывается SQL TestServer suite.

Локальный полный Release **908passed/0failed**, SQL156explicitly skipped без opt-in; Windows SQL2019 integration **157/157passed**,0failed/0skipped, включая real-cookie3facts и providerless1fact. TRX `E:\BuildSystem\TestRuns\observation-http-20261103\results\ustiantsev.is_SK-CICD-1C-01_2026-11-03_14_09_46.trx`. Новые HTTP/config tests **26/26passed**. Locked restore/diff check успешны, portablepublish RID-only lock изменения исключены. Schema не менялась; production не обновлялся.

Commit8d29836: [SQL41625376962](https://github.com/nspirit2/bild_1c_server/actions/runs/41625376962) success157/157; [Build41625377074](https://github.com/nspirit2/bild_1c_server/actions/runs/41625377074) success Windows/Ubuntu.

## Проверки

HTTP fake-service tests проверяют route wiring, cookie/machine изоляцию, CSRF, body shape/unknown members/default-off и strict startup validation. Отдельные real Identity cookie + AgentToken + SQL services HTTPS TestServer tests проверяют сохранённые assignments/receipts, exact replay/one audit, отзыв membership/token и неизменность retained lifecycle. Это не IIS/network E2E и не real RAC+SQL adapter invocation.

Machine poll/claim, agent HTTP client/outbox delivery и Worker orchestration остаются отдельными шагами. Scheduled jobs deny mandatory до/после restore и после restart, security profile не mandatory gate.
