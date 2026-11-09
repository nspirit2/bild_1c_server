# Administrative credential HTTP boundary

## Routes and feature gate

Default-off `TargetCredentials:Enabled` включает только административный импорт, metadata-read и revoke:

- `POST /api/admin/target-credentials` — новые credentials, ответ201metadata;
- `GET /api/admin/target-credentials/{credentialId}` — только metadata;
- `POST /api/admin/target-credentials/{credentialId}/revoke` — terminal revision-checked revoke, ответmetadata.

Cookie policy требует роль Administrator, storage дополнительно проверяет текущий ADMINISTRATOR role membership в БД под serializable/global lock. Stale cookie недостаточно. ВсеPOST подпадают под существующую CSRF validation; machine exemption отсутствует. Никаких публичных secret-read/agent-delivery endpoints. Metadata ответ не содержит credential values/ciphertext; default-off отказ до чтения JSON body самим handler; предшествующий глобальный CSRF middleware сохраняет свою обработку non-JSON form requests и может читать form body. No-store включён и для ранних redirect/auth/body-limit failures.

## Import contract

Только JSON body с восемью уникальными case-sensitive полями: `credentialId`, `canonicalClusterId`, `canonicalSqlServerId`, `endpointMappingId`, `clusterUser`, `clusterPassword`, `sqlUser`, `sqlPassword`. GUIDs — ненулевые canonical D. Secrets — строки1..4096 без controls; общая граница тела16384bytes, поэтому независимо допустимые поля не гарантируют допустимость их общей суммы. Не передавать через query/path/URL, CLIargv или логи. BindingIDs immutable, существующийcredentialId всегда conflict; rotation использует новыйID. Revoke JSON содержит только положительное int64 `expectedRevision`.

Manual bounded parsing не принимает duplicate/unknown/missing/null/wrong-type fields, malformed UTF8/JSON/trailing content. Предел действует и при отсутствии Content-Length/chunked body. Ошибки нейтральны без JsonException/rawinput/secret DTO echo. Plaintext используется лишь в памяти в ходе запроса; собственный byte buffer очищается best effort, managed strings/library scratch не гарантированно обнуляемы. Запрещено включать request-body logging/reverse-proxy body capture на маршрутахcredentials. Показатьилиполучитьсохранённыепаролиобратно черезэтотAPIневозможно.

## Boundary

DI использует существующий server Data Protection provider/keyring, не ephemeral provider. Политика key protection/backup описана в target-credentials. Enabled=false по умолчанию; фактические config/schema/secrets стенда не изменяются. Admin UI ещё не подключён. Нет назначений credential ID к operation/permit, secret delivery агенту или real CREATE/restore/drop. Stored/revoked metadata не external execution authority и не возможность отменить уже доставленный пароль.

## Verification

4offlineHTTPDevexceptiontestsпрошли: unexpectedfailureGET/Store/Revoke->neutral503, defaultoffбезportcalls. 5opt-inSQLfactsпроверяютrealIdentitycookie/CSRF/currentDBrole/removal/no-secretmetadata+DBciphertext/defaultoff,malformedUTF8/escapedlonesurrogates/strictGUID/missingfields/secretslimits/revisionbounds/trailingJSON, known/chunkedoversize413 иvalidbodyровно16384bytes. Независимыйfrozen-source reviewбезblockers. Дляbe40421 Build42588539161Windows/Linux и SQL42588539205 success. ЛокальныйRelease1546passed/0failed,223SQLexplicitlyskipped; integration256. Disposable metadata SQL tests не создают target SQL DB или 1Сinfobase.