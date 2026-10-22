# Агентский пакет: доверие, lease и durable журнал

## Границы

Этот пакет — безопасный control-plane и Windows Worker skeleton. Исполняется только **ControlPlaneProbe** без внешних действий и без интерпретации snapshot. `ProvisionArchive` не выдаётся ни одному агенту: компилятор pipeline, providers и технический spike отсутствуют. Успех probe не переводит окружение в Ready и не освобождает квоты/резервирование.

Ветка `feat/agent-control-plane` основана на локальном серверном коммите `2a627fa`; пакеты не слиты в main и не отправлены автоматически.

## Доверие и HTTP

Пользовательские admin endpoints используют обычную Identity cookie, Administrator и CSRF:

- POST `/api/admin/agents/enrollments`: `{ expectedAgentId, expectedName, poolId }`, создаёт одноразовый 256-bit token на 15 минут.
- POST `/api/admin/agents/{id}/approve`: Pending → Approved, возвращает agent token один раз.
- POST `/api/admin/agents/{id}/rotate`: Approved → новый token, прежний сразу недействителен.
- POST `/api/admin/agents/{id}/revoke`: отзыв и NeedsAttention для активных заданий.
- GET `/api/admin/agents/`: диагностический список без credential hashes.

Регистрация службы: POST `/api/agent/register` по HTTPS, `{ agentId, enrollmentId, token, name }`. Token одноразовый, привязан к ожидаемым id/name/pool. До approve агент Pending и не имеет рабочего credential. Регистрация ограничена rate limiter по remote IP; за reverse proxy использовать только доверенную конфигурацию forwarded headers, не произвольный клиентский заголовок.

После approve администратор передаёт выданный token отдельным защищённым каналом оператору агента. Это не автоматическая выдача Pending-агенту. Потерянный token не раскрывается повторно: rotate для Approved. Потерянный enrollment token создать заново до регистрации. В SQL хранятся только хеши.

Машинные endpoints используют **только** `Authorization: Bearer <agent-token>` и HTTPS, без cookie:

- POST `/api/agent/capabilities`: `{ supportedJobKinds: ["ControlPlaneProbe"] }`.
- POST `/api/agent/poll`: без тела; 204 либо LeaseGrant.
- POST `/api/agent/heartbeat`: `{ jobId, fencingToken }`.
- POST `/api/agent/events`: `{ eventId, jobId, fencingToken, code }`.

CSRF-исключение задано metadata конкретных machine endpoints, не по префиксу URL или наличию Bearer. Cookie не даёт доступа к агентским операциям; агентский token не даёт роли Administrator или доступа к пользовательскому API. Сервер повторно проверяет текущее поколение credential под транзакционной блокировкой, не только при аутентификации HTTP. `AuthenticateAsync` и операция используют один scoped IAgentControl.

## Выдача и истечение lease

SQL serializable transaction и общий transaction-owned applock сериализуют выдачу/отзыв/отчёты. Время берётся из SQL SYSUTCDATETIME, не с компьютера агента. Lease — 2 минуты, heartbeat обновляет его только пока действуют credential, agent/job/fence и время lease.

Агент привязан администратором к конкретному пулу. Capabilities — не права. Только Pending ControlPlaneProbe с принадлежащим пулу окружением и Held reservation подходит для выдачи. В этом пакете нет публичного endpoint постановки probe или изменения типа существующего ProvisionArchive; диагностические probe задаются только контролируемой тестовой fixture.

Повтор Poll после потери ответа возвращает **тот же** собственный активный lease: job/fence/expiry неизменны, срок не продлевается. Другому агенту он не выдаётся. Worker дедуплицирует grant по durable job/fence, не повторяет действие после рестарта.

Lease на одно задание выдаётся только один раз: fence=1, строка не удаляется и не перераспределяется. Новый агент не получает истёкшее задание. Автоматического reconciliation/reissue нет; будущая реализация обязана увеличить fencing generation и сначала установить фактический исход.

Истечение обрабатывается **лениво при следующей транзакционной операции control-plane**: Job/Environment/Request → NeedsAttention; квоты и reservation удерживаются. Background sweeper пока отсутствует. Даже до записи перехода сервер не принимает heartbeat/report с истёкшим временем. Наличие NeedsAttention lease блокирует этому агенту выдачу новых заданий до будущего reconciliation.

Event codes закрыты: ProbeCompleted, WorkerUnsupported, LeaseOutcomeUnknown. EventId идемпотентен при том же payload/agent/job/fence; конфликт — 409. Replay принимается лишь с ещё не истёкшим matching lease; после expiry даже ранее принятому event нужен разбор, а не фиктивный ack.

## Windows Worker

Проект `src/Agent/BuildSystem.Agent`, .NET10, AddWindowsService. Администратор заранее создаёт постоянный абсолютный `Agent__DataDirectory` с ACL только сервисной учётной записи/администратору. Программа проверяет путь/наличие, но **не удостоверяет автоматически корректность Windows ACL** — это обязательная установка и проверка администратора.

Конфигурация:

- `Agent__ApiOrigin`: только HTTPS origin без userinfo/path/query/fragment.
- `Agent__DataDirectory`: постоянный защищённый каталог вне checkout.

Импорт под той же Windows service account, которая запускает службу:

```text
dotnet BuildSystem.Agent.dll import-token
```

Ввод token скрыт в терминале либо читается из защищённого stdin; не помещать token в аргументы, environment, shell history и репозиторий. DPAPI CurrentUser защищает `agent-token.dpapi`; существующий файл не перезаписывается. Для ротации остановить службу, получить новый token, контролируемо заменить локальный файл и запустить службу. Потеря доступа к профилю service account требует повторного provisioning credential. На Linux token store/служба fail closed; SQLite tests кроссплатформенные.

Импорт публикует зашифрованный token через flush временного файла и атомарный move без перезаписи. Для Worker требуется единственный владелец каталога/журнала: lifetime lock исключает конкурентный запуск двух экземпляров с одним DataDirectory.

HttpClient не следует redirect и не использует cookie, стандартная проверка TLS-сертификата не отключается. Не включать body/header logging. Worker логирует только тип ошибки, не exception message, URL, token или snapshot.

## SQLite

`agent-journal.sqlite`: lease identity/expiry/settled и закрытые structured outbox events, synchronous FULL. В SQLite нет токенов, snapshot, аргументов процессов или произвольного текста. Grant/event фиксируются до отправки, ack фиксируется после matching server receipt. ProbeCompleted ack атомарно помечает lease settled; unknown/unsupported ack сохраняет unsettled.

После рестарта незавершённая запись не исполняется заново: попытка LeaseOutcomeUnknown с текущим fence, либо удержание до разбирательства при истечении/отказе. Старый outbox не удаляется по таймауту. MarkSettled — библиотечная операторская операция, не штатная CLI-команда и не доказательство серверного reconciliation.

## Проверки и ограничения

Сборка и тесты — обычный BuildSystem.slnx. Реальные SQLite unit tests проверяют reopen, ack и дедупликацию. HTTP tests подменяют application port и подтверждают разделение схем/CSRF, но не SQL auth. SQL tests используют существующий opt-in harness, без SQL явно skipped. Windows service installation, DPAPI под реальной service account, HTTPS end-to-end с SQL и корпоративные ACL не проверены в Linux-сессии.

Это не готовый deploy-agent и не доказательство безопасного выполнения RESTORE. Следующий пакет: ProcessRunner + pipeline compiler/registry и каталог/staging; перед любым restore — реальный стенд и подтверждённая изоляция.
