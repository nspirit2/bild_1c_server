# Explicit credential pin for a creation attempt

## Approved policy

Владелец подтвердил: администратор явно выбирает credential ID для попытки создания, ID закрепляется неизменно. Не выбирать последнюю запись, default active credential или изменяемую ссылку во время выполнения. Ротация создаёт новый ID, но не переназначает уже выданную попытку. Отзыв блокирует последующий допуск; исторические pins сохраняются. Отзыв не отменяет уже выполненную мутацию и не обнуляет ранее выданные credentials.

## Metadata-only implementation

Новый внутренний `IssueWithCredentialAsync` фиксирует credential ID и endpoint mapping ID на existing permit/attempt. `CredentialBindingEnabled=false` по умолчанию, дополнительно к existing permit/operation flags. Serializable transaction и global agent-control lock охватывают current DB admin, admission, credential checks, immutable insert/replay. Credential Stored/revision1 должен точно соответствовать canonical cluster/SQL server intent и выбранному endpoint mapping. Это связывание metadata с выбранной записью; не резолвинг физических endpoints и не выдача секрета.

Проверка текущего состояния credentials выполняется **до возврата replay** bound issue/start. Admin и assigned-agent start разделяют проверку. Revoked/missing/mismatched credentials дают нейтральный отказ; новая версия с другим credential ID не может заменить pin существующей попытки. `Get`/`Revoke` permit остаются historical metadata operations после отзыва credentials — не grant. Присутствие pin не подтверждает пароль, доступ, свежую физическую identity, absence или scheduled-jobs deny.

Legacy `IssueAsync` остаётся только для существующих nonexecuting metadata flows: nullable pins отсутствуют парой. Он не может replay bound permit как unbound. Никакой legacy/unbound receipt нельзя использовать в будущем physical executor; future execution/delivery потребуют immutable non-null credential pin и новую online проверку. Все receipts по-прежнему `Executable=false`, `ScheduledJobsMustRemainBlocked=true`.

EF guard запрещает редактирование/удаление anchors, включая пару pins. SQL CHECK требует обаNULL либо обаnonNULL/ненулевые, FK restrict сохраняет ссылку на credential row. Старые записи остаются unbound; migration не придумывает credential mapping и не применялась к стенду. Privileged SQL writers остаются вне cooperative application boundary.

## Deliberately absent

Нет HTTP выдачи permit или секрета, UI выбора credential для запуска, worker wiring, смены external password, target CREATE/restore/drop, расшифровки ciphertext или live-конфигурации. Existing credential import/admin metadata UI не предоставляет разрешение на создание. Endpoint mapping проверяется как выбранный immutable GUID; actual RAC/SQL adapter должен независимо сопоставить trusted mapping и physical identity перед фазой.

## Verification

6opt-inSQLfactsдобавлены: exactreplay+rotation/mapping/legacyconflicts; currentadmin/admission/canonicalchecks; revocationbeforeadmin/agentstartиreplay; historicalGet/Revoke; legacyunbound; EFpairimmutability+SQLpair/FKconstraints. 2offlineflag/emptyIDcasesпрошли. Existingmodelcontracttestобновлённа2RestrictFK. Независимыйfinalsource reviewбезfindings. ПолныйlocalRelease1559passed/0failed,229SQLexplicitlyskipped; integration264. Migrationgeneratedсnullablepairбезbackfill,неприменяласькстенду. CIafterpushотдельно.