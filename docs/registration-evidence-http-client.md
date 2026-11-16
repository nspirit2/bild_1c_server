# Historical registration evidence HTTP transport

## Manual, independent default-off client

TargetRegistrationEvidenceHttpClient реализует уже существующий ITargetRegistrationEvidenceTransport. Он сам не открывает journal, не запускает observation/CREATE/restore и не создаёт delivery worker. Enabled=false по умолчанию; URI/timeout валидируются при enabled snapshot. Null dependencies отвергаются fixed error независимо от switches. Нет host/CLI/DI enablement или автоматического retry.

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

## Validation

Fake-handler tests проверяют exact request/receipt protocol, input validation before credentials, independent default-off, URI restrictions, response size/media/cache/encoding, malformed/duplicate/safety fields, error-body exclusion, no redirect/retry, cancellation/clock/disposal privacy и сохранение provider date offset. Эти tests не считаются real TLS certification или server SQL authentication proof; existing server/SQL CI покрывает отдельные backend gates. Полный локальный Release2907passed/0failed,289SQLexplicitlyskipped; Node84passed/0failed. Добавлены43expanded client cases; final independent source/tests review — без блокеров. Исправлены review findings: rollback clock, final deadline после timer disposal, required no-cache и endpoint-equivalent SQL offset date; strict STJ receipt timestamp отвергает bare fractional dot. Test-only ReadStream double counting исправлен без увеличения response bound. Real SQL CI3121be9 success(43835238394). Windows Build job133190099692 достиг15min job timeout; TRX artifacts в run43835238363 недоступны, поэтому test failure не установлен и Build не считается зелёным. Documentation-only follow-up повторяет проверку без изменения implementation/tests; лимиты не увеличиваются.