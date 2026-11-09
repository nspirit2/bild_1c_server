# Repeatable assigned-agent credential delivery over HTTPS

## Approved replay policy

Владелец принял повторную доставку credentials для той же попытки, exact immutable IDs и оставшегося окна permit. Каждая выдача повторно проверяет current token, assigned agent, admission, state/revision, credential binding, revocation и SQL UTC deadline через scoped resolver. Нет одноразового consumption, active/default credential lookup, cached plaintext или продления permit. Повторная доставка не разрешает повторить CREATE, не снимает local one-attempt/quarantine и не делает полученный ACK fresh execution authority.

## Dedicated machine endpoint

POST `/api/agents/target-creation-credentials/resolve` — отдельный bearer-auth machine endpoint. Ambient administrator/user cookie не является доступом. CSRF exception применён только к machine route через existing marker, не ко всем credentials/admin routes. Origin запрещён: API не предназначен browser clients. TLS обязателен по `Request.IsHttps`; HTTP отклоняется **до redirect**, `X-Forwarded-Proto` не считается доказательством TLS. Reverse proxy должен обеспечивать корректную trusted HTTPS интеграцию отдельно; не включать implicit forwarded-header trust. Сервер не может заставить клиента не посылать bearer по HTTP изначально — agent должен выбирать HTTPS до отправки.

Строгий JSON body максимум 4КиБ содержит ровно `permitId`, `expectedPermitRevision` (2), `attemptId`, `credentialId`, `endpointMappingId`; GUID — exact nonzero D. Duplicate/unknown/missing/wrong-case fields и malformed UTF8 отвергаются без input echo. Metadata не берутся из URL/query и не выбираются по последней credential row. Successful response явно содержит четыре secret strings и correlation pins, `executable=false`, `scheduledJobsMustRemainBlocked=true`, `retryAllowed=false`. Последний flag относится к physical CREATE, не к разрешённой fresh повторной доставке.

## Gates and plaintext boundary

Delivery и resolution имеют отдельные default-off `TargetCreationCredentialDelivery:Enabled` / `TargetCreationCredentialResolution:Enabled`. Store `TargetCredentials:Enabled`, `TargetCreationOperation:Enabled`, `TargetCreationPermit:{Enabled,AgentStartEnabled,CredentialBindingEnabled}` также default false. Creation canonical mappings используются из existing trusted `RestoreIntentOptions.CanonicalMappings`, без inferred physical aliases. Включение HTTP route не включает worker/executor и не даёт unbound legacy rows права на получение secrets.

Явное чтение wrapper разрешено только для написания этого ответа. Не сериализуется ambient `TargetCreationResolvedCredentials` (его converter продолжает deny-all). Буфер JSON освобождается/очищается best effort после записи; immutable managed strings, serializer buffers, TLS/OS/client memory не гарантированно стираются. Ответы/ошибки/redirect alternatives на prefix получают no-store/nosniff; no response body logging/middleware должен добавляться upstream. Credentials доступны назначенному клиенту и его OS/monitoring согласно owner-approved exposure. TLS защищает сеть, не ОС получателя.

Ошибки — fixed allowlisted codes; неизвестные service/parser/provider/activation failures не попадают в Development exception page. При partially written response соединение abort, не append error к plaintext. Потеря ответа допускает только fresh повторный resolve, не retry physical mutation. Server cannot recall delivered plaintext after revoke; дальнейшая online execution boundary должна повторно проверять revocation перед фазой.

## Deliberately absent

Нет agent HTTP client/credential cache, worker, actual RAC/SQL invocation, target CREATE/restore/drop, automatic permit issuance UI или live/stand deployment. HTTP delivery tests используют disposable metadata SQL и in-memory web server, не target endpoints.

## Verification

16offlineHTTPcasesпрошли: defaultoff/HTTPSbeforeRedirect/forwardedreject, noCSRFmachine-onlyvs cookie, Origin, bearer, strictbody+chunkedlimit, Developmentexceptions/unknowncodes/DIactivationfixederrors. 4SQLfactsдобавлены: repeatdeliveryexact12fields+pins+jobsblocked, tokenrotation/wrongagent/cookienoauthority, revokedcredential/permit/expired/admissionwithdrawal, wrongpins/legacy/noauto-selection. Independentfinalsource/testreviewбезblockingfindings; knownlimitationпосле-startwritefailureпроверенsourceAbortboundary, ненастоящимpartial-networkтестом. LocalfullRelease1687passed/0failed,239SQLexplicitlyskipped; integration277. Finaltargetedrerun+SQLcompileпослеassertionhardening; CIafterpushотдельно.