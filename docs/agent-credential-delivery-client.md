# Isolated agent HTTPS credential delivery client

## Scope and replay

Default-off `TargetCreationCredentialHttpClient` явно запрашивает уже закреплённые credential/mapping IDs для одной permit/attempt. Клиент не зарегистрирован в worker/DI, не получает permit самостоятельно, не начинает local attempt и не запускает RAC. Он не проверяет server DB admission локально — каждый явный Resolve вызывает existing server endpoint с fresh current token. Повторный explicit Resolve разрешён согласованной policy; автоматических retries/redirects/продления deadline нет. Получение credentials не даёт права повторить физический CREATE.

## Transport

Trusted configuration содержит HTTPS base URI root без userinfo/query/fragment и ограниченный timeout. Production handler не следует redirects, не использует cookies, не выполняет automatic decompression. Test handler — trusted dependency, не security sandbox: произвольный handler может изменить destination/log secret, поэтому caller не должен передавать attacker-owned handler. Token читается из provider заново один раз на explicit request; credentials не помещаются в query, URL, diagnostics, local journal или options.

Клиент отправляет единственный POST с пятью exact correlation fields. При non-200 он не читает error body и не пересылает bearer на другой адрес. Принимается только application/json UTF8, без content encoding, с Cache-Control:no-store; это требование контракта, не доказательство TLS или свежести metadata. Сертификаты проверяются стандартным handler, bypass callbacks не добавляются. Bounded 128КиБ response читается под общим deadline, включая stream phase и parsing. Oversized/chunked/malformed response отвергается. Late send response disposal ограничивает lifetime после timeout; произвольный uncooperative injected handler невозможно принудительно остановить. Synchronous credential provider и блокирующий synchronous Dispose также не preemptable: они принадлежат trusted локальной конфигурации, а deadline проверяется после их возврата.

## Strict response and plaintext

Ровно 12 уникальных известных полей, exact matching всех пяти request pins, revision 2, nonzero GUID-D и safety flags: executable=false, scheduledJobsMustRemainBlocked=true, retryAllowed=false. Secret fields 1..4096 characters, no controls, well-formed Unicode. Нельзя принять только часть ответа, подменить mapping или выбрать latest credential ID. Отклонение pin/shape не запускает fallback.

Результат — disposable sealed holder с private secret references, redacted diagnostics и rejecting System.Text.Json converter. Только явный ReadSecrets даёт отдельный secret value record; его уже можно сериализовать, поэтому вызвавший код обязан ограничить lifetime и не логировать. Dispose обнуляет ссылки holder, а не гарантирует erasure managed strings, serializer/TLS/OS/client buffers. Owned byte buffers очищаются best effort; это не secure-memory implementation. Секреты по-прежнему могут быть видны OS/process monitoring при дальнейшей owner-approved передаче RAC argv.

Все transport errors нейтральны, без URL/token/body/inner exception. Caller cancellation сохраняет смысл отмены, но не повторяет diagnostics provider/network. Timeout/response mismatch не говорят, был ли CREATE: этот клиент вообще не выполняет mutation. `RetryAllowed=false` относится к физической попытке, не к повторному explicit credential delivery request.

## Deliberately absent

Нет auto-wire в AgentHostedService, credential cache, HTTP permit issuance/start adapters, local barrier orchestration, physical observations/creation, стендовых операций или deployment. Future caller обязан сопоставить pins с authoritative permit и local journal и выполнить fresh admission/revocation/heldnamespace/absence checks перед мутацией.

## Verification

81fake-handlercaseпрошёл, network/RAC/SQLнеиспользуются: gate/URI/tokenrotation, exactrequestpins, strictschema/flags/Unicode4096, JSONdeny/dispose/redaction, header/non200beforebody, bounded/chunkedresponses, caller-cancel/bodytimeout иlate-responsecleanup. ReviewисправилUnicodecontractmismatchдоcommit; frozenfinalsource+testreviewбезblockers. Nonblockingfuturecoverage: exact128КиБacceptance, counted+1read, uncooperativeOpen/Read latecleanup; implementationпроверенsource. ПолныйlocalRelease1768passed/0failed,239SQLexplicitlyskipped; integration277. CIafterpushотдельно.