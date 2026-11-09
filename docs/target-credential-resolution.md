# Internal assigned-agent target credential resolution

## Scope

Разрешение уже закреплённой пары credential ID / endpoint mapping ID для текущего назначенного агента. Инфраструктурный default-off primitive не зарегистрирован в DI, не доступен по HTTP и не подключён к worker/RAC. Resolve возвращает временную secret-bearing оболочку только после SQL transaction commit. Она не является credential-delivery contract, permit, execution authorization или доказательством существования/отсутствия цели.

## Fresh checks

Все default-off gates (`TargetCreationPermitOptions.Enabled`, `AgentStartEnabled`, `CredentialBindingEnabled`, `TargetCreationOperationOptions.Enabled`, `TargetCredentialOptions.Enabled`, отдельный resolution Enabled) должны быть включены оператором. Публичных routes и automatic activation нет. В каждом serializable strategy attempt: current Approved agent token проверяется **до поиска permit** под global `BuildSystem:agent-control` lock; затем exact assigned AgentId, current trusted admission (root/pool), existing operation/intent/permit structural receipts. Требуются `StartRecorded`/revision 2 и `AttemptRecorded`/revision 2; exact permit revision, attempt, credential ID и mapping ID. Legacy unbound permits отвергаются. SQL UTC NOW должен оставаться в `[IssuedAt, StartBefore)`, в том числе при повторном чтении после start.

Только после этого credential row должен оставаться Stored/revision1/nonrevoked и точно совпадать по canonical cluster, SQL server и выбранному mapping. Защищённый envelope расшифровывается с exact purpose binding. Следующий strategy retry вновь проходит аутентификацию и admission; внешних callbacks/process/HTTP внутри него нет. Секреты не попадают в audit, exceptions или metadata receipts. Нейтральные коды ошибок не пересказывают токен, ciphertext или plaintext.

## Plaintext boundary

Оболочка — sealed non-record с redacted ToString, без публичных строковых secret properties и с rejecting System.Text.Json converter. Только явный `ReadSecrets()` даёт `TargetCredentialSecrets`; этот record сам по себе сериализуем, поэтому caller обязан не логировать/не возвращать его наружу. Dispose оболочки освобождает ссылку, но не стирает managed strings; keyring Data Protection остаётся отдельно от SQL. Если commit/retry fails, созданная оболочка уничтожается до выхода.

Internal Resolve можно повторить при каждой fresh online проверке; это **не политика повторной выдачи** агенту. Отзыв после Resolve не может вернуть уже прочитанный plaintext и не делает start rollback. `StartRecorded` не хранит происхождение admin vs agent вызова: теперь проверяется текущий назначенный агент, а не provenance исторического start. Future HTTP delivery, replay/ACK/timeout policy, concurrent use, client authentication over network, endpoint trust и physical execution требуют отдельной реализации/решения. Даже после resolution receipts remain `Executable=false`, scheduled jobs blocked.

## Verification

6SQLfacts+3offlinefacts добавлены: gate/inputbeforeSQL; auth/tokenrotation/assignedagent; currentadmission/exactpins/revocation; prepared/revoked/expired/future/quarantinestatesbeforedecrypt; ciphertextpurpose/providererrorredaction; explicitread/repeat/JSONreject/dispose иunchangedmetadata/audit. Localtargeted3passed/6SQLskipped. Независимыйfinalsource reviewбезblockingfindings; общийReceipt validatorпереиспользованчерезinternalvisibility. ПолныйlocalRelease1671passed/0failed,235SQLexplicitlyskipped; integration273. CIafterpushотдельно. Нет live/stand изменений.