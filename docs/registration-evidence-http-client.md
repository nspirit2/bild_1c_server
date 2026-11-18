# Historical registration evidence HTTP transport

## Manual, independent default-off client

TargetRegistrationEvidenceHttpClient реализует уже существующий ITargetRegistrationEvidenceTransport. Он сам не открывает journal, не запускает observation/CREATE/restore и не создаёт delivery worker. Enabled=false по умолчанию; URI/timeout валидируются при enabled snapshot. Null dependencies отвергаются fixed error независимо от switches. Библиотека не регистрирует worker или DI и автоматически не повторяет отправку. Разовая команда описана ниже; на стенде ничего не включается.

Назначение — fixed POST /api/agent/target-creation/registration-evidence от HTTPS root BaseUri без userinfo/path/query/fragment. Production handler отключает redirects, cookies, decompression и tracing propagation; TLS использует стандартную platform certificate validation, не bypass. Trusted test handler должен сохранять destination/headers и cooperative cancellation, не перехватывать credentials; это явная доверительная граница.

## Strict wire protocol

Request ровно13 полей соответствует existing machine ingress. Payload проходит shape validation до credential lookup, ограничен8192bytes и содержит те же immutable claim/observation IDs. SQL create-date O27/28/33, включая foreign +03:00, остаётся исходной metadata без timezone conversion. Credential provider вызывается один раз; bearer только printable ASCII с bounded length, без whitespace/comma/prefix ambiguity. HTTP transport не выдаёт новых observation IDs.

Response принимается только200 от того же destination. Error bodies не читаются. Redirects не вызывают повторную credential-bearing отправку. Требуются bounded uncompressed JSON и no-store/no-cache; тело максимум2048bytes, включая chunked unknown-length stream, strict UTF8. Принимаются ровно11 case-sensitive полей без duplicates/unknown/missing aliases. Claimecho exact, operation UUID canonical/nonempty; status Recorded, AcceptedAt nondefault UTC, authority5false/safety2true должны явно присутствовать и иметь JSON boolean types.

Computed receipt flags нельзя десериализовать с игнорированием присланных malicious значений: client проверяет каждый wire flag перед построением inert receipt. Delivery coordinator дополнительно сверяет operationId с addressed local operation; сам report не содержит operationId, поэтому client не выдумывает отсутствующий pin.

## Deadline and privacy

Whole1..120sec TimeProvider budget охватывает report validation/serialization, credential provider, send, stream read, parser и disposal. Backward/failed clock, cancellation или disposed client отвергаются fixed TargetRegistrationEvidenceTransportUnavailable без URL, token, remote body, diagnostics, stack/inner exception. Request/response byte buffers очищаются при завершении.

Await кооперативный: нет detached send/stream tasks или hard-preempt обещания. Synchronous provider/dispose и trusted handler, игнорирующий cancellation, могут вернуться позже deadline; после возврата success не выдаётся и cleanup ожидается. Производственная сеть получает linked cancellation token. Caller-side cancellation не позволяет вывести sensitive messages из чужих exceptions.

## Acceptance is historical only

AcceptedAt не сравнивается с host now как freshness/lease proof. Даже потерянный или malformed HTTP ответ может следовать за server commit; следующий explicit delivery проход берёт неизменённый report из journal и использует exact server replay. Нет автоматического physical retry, adoption, release или unblocking scheduled jobs.

## Разовая команда агента

`deliver-target-registration-evidence <operation-id>` принимает только один явно заданный ненулевой UUID в нижнем регистре, в формате `D`. Команда не запускает worker, не перечисляет операции и не повторяет отправку автоматически. `Agent:TargetRegistrationEvidenceDelivery:Enabled` по умолчанию выключен. При включении применяется `TotalTimeoutSeconds` (1–120, по умолчанию 30); нужны Windows, существующий каталог агента на локальном диске с ограниченными ACL, существующий SQLite-журнал, HTTPS origin и штатный DPAPI-токен. При уже записанном ACK токен не читается. Журнал открывается со штатной инициализацией схемы; это не read-only команда. Команда использует эксклюзивную блокировку журнала; запускать её следует при остановленном worker. Настройки стенда этим изменением не меняются.

Вывод — одна строка JSON без токенов, имён, идентификаторов и дат. Коды завершения: `0` — отчёт доставлен или локальный ACK уже записан; `3` — команда отключена; `5` — доставка недоступна, запись ACK не подтверждена или не удалось вывести статус; `64` — неверные аргументы. `acknowledgementMayHaveCompleted` касается только локальной записи: `false` не доказывает, что сервер не принял отчёт. Все результаты остаются историческими и не разрешают CREATE, restore или разблокировку заданий.

## Сквозная проверка

[Тест доставки](../tests/BuildSystem.ApiTests/TargetRegistrationEvidenceDeliveryInteropTests.cs) проводит сохранённый SQLite report через настоящий клиент и ASP.NET TestServer. Первый ответ `200 Recorded` намеренно теряется после обработки API. После повторного открытия журнала следующий явный вызов отправляет побайтово тот же payload и сохраняет исходный `AcceptedAt`. После ещё одного открытия журнала HTTP не вызывается. Карантин и CREATE ACK не меняются. Проверяются `Unspecified` и `Utc` SQL create-date.

Тест заменяет SQL receipt port, RAC и SQL-наблюдения локальными fixtures; он не проверяет сетевой TLS, SQL-аутентификацию или SQL-идемпотентность. SQL-аутентификация и SQL-идемпотентность проверяются отдельно в [SQL receipt tests](../tests/BuildSystem.IntegrationTests/TargetRegistrationEvidenceReceiptTests.cs).

## Validation

Fake-handler tests проверяют exact request/receipt protocol, input validation before credentials, independent default-off, URI restrictions, response size/media/cache/encoding, malformed/duplicate/safety fields, error-body exclusion, no redirect/retry, cancellation/clock/disposal privacy и сохранение provider date offset. Эти tests не считаются real TLS certification или server SQL authentication proof; existing server/SQL CI покрывает отдельные backend gates. Полный локальный Release2907passed/0failed,289SQLexplicitlyskipped; Node84passed/0failed. Добавлены43expanded client cases; final independent source/tests review — без блокеров. Исправлены review findings: rollback clock, final deadline после timer disposal, required no-cache и endpoint-equivalent SQL offset date; strict STJ receipt timestamp отвергает bare fractional dot. Test-only ReadStream double counting исправлен без увеличения response bound. Первый Windows Build достиг общего лимита. Повторный прогон с console artifact показал, что durable SQLite tests продолжали проходить, но медленно. После изменения бюджета CI-прогона, без ослабления production timeouts или SQLite durability, [Build and test](https://github.com/nspirit2/bild_1c_server/actions/runs/43843452950) и [SQL integration](https://github.com/nspirit2/bild_1c_server/actions/runs/43843453096) прошли на `6970693`.