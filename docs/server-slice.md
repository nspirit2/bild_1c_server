# Серверный срез: приём заказа архива

## Границы

API принимает и сохраняет заказ, но не исполняет его. Агент, enrollment, каталогизация реальных файлов, staging, engine и восстановление 1С не входят в этот срез. Запись задания со статусом Pending не означает готовый executable pipeline. До следующих этапов задания остаются в очереди, окружение — Requested, TTL ещё не отсчитывается.

Ветка `feat/archive-order-api` зависит от `feat/domain-foundation`, который ещё не принят в main. Автоматического слияния нет.

## Конфигурация

.NET SDK закреплён в global.json. SQL — Microsoft SQL Server (не SQLite/EF InMemory). Для Development/Testing ключи Data Protection сохраняются в настроенном каталоге; в Windows Production они дополнительно защищаются DPAPI в контексте службы. Каталог должен иметь ACL только сервисной учётной записи и администратора. Production на Linux намеренно отклоняется, пока не согласован другой механизм защиты. Сохраняйте ключи при обновлениях: их потеря инвалидирует cookie.

Задайте через защищённую конфигурацию:

- `ASPNETCORE_ENVIRONMENT` — `Development` только для локальной разработки, не для рабочей службы.
- `ConnectionStrings__BuildSystem` — подключение к отдельной БД метаданных; предпочтительно Windows Integrated Authentication. Секретные строки не коммитить.
- `Security__DataProtectionDirectory` — постоянный защищённый каталог ключей вне репозитория.
- HTTPS endpoint Kestrel и сертификат через стандартную конфигурацию ASP.NET Core. В Development можно `dotnet dev-certs https --trust` на машине разработчика; доверие не отключать в рабочей конфигурации.

API не включает HTTP body/request logging и EF sensitive data logging. Не включайте их для login/секретов. Под reverse proxy дополнительно настройте доверенные forwarded headers; по умолчанию приложение не доверяет произвольному X-Forwarded-Proto.

## Установка схемы и первого администратора

После сборки отдельный оператор миграций запускает из каталога опубликованного приложения:

```text
dotnet BuildSystem.Api.dll migrate
dotnet BuildSystem.Api.dll bootstrap-admin admin@example.invalid
```

Миграции не применяются автоматически при старте API. Команда bootstrap читает пароль скрыто в терминале либо одной строкой из защищённого stdin; не передавать пароль аргументом, не хранить в shell history/скрипте/репозитории. Пароль минимум 12 символов со стандартными требованиями Identity. Bootstrap не изменяет существующую учётную запись и отказывается работать, если администратор уже существует; повторная административная выдача прав — не bootstrap.

Bootstrap и API запускайте под одной сервисной OS-учётной записью для DPAPI. Оператор миграций может быть другим SQL principal, но не должен создавать Data Protection keys под чужим Windows-пользователем в общем каталоге.

SQL DDL-права нужны только оператору миграций; повседневной API-службе — права чтения/изменения таблиц метаданных и transaction-owned sp_getapplock в этой БД. API не требует доступа к рабочим ИБ, SQL RESTORE или sysadmin. Проверка конкретного least-privilege SQL principal остаётся задачей стенда.

## HTTP протокол

Один HTTPS origin; CORS для сторонних сайтов не включён. Сессия cookie `__Host-BuildSystem`, Secure/HttpOnly/SameSite=Strict. Публичной регистрации нет. Identity lockout: пять ошибок, 15 минут.

1. `GET /api/auth/csrf` возвращает `{ token }` и устанавливает antiforgery cookie.
2. `POST /api/auth/login` с JSON `{ userName, password }` и `X-CSRF-TOKEN`.
3. После входа запросить новый CSRF token: он связан с текущим пользователем.
4. Для всех POST `/api/*` обязателен `X-CSRF-TOKEN` и cookie.
5. `POST /api/auth/logout`, `GET /api/auth/me`.

Пример тела заказа (все UUID — реальные разрешённые серверные идентификаторы):

```json
{
  "projectId": "11111111-1111-1111-1111-111111111111",
  "displayName": "Проверка задачи",
  "mode": "archive",
  "source": { "archiveVersionId": "22222222-2222-2222-2222-222222222222" },
  "ttlDays": 7
}
```

`POST /api/environment-requests` требует `Idempotency-Key`. Неизвестные поля JSON отклоняются: ownerId, пул, пути, SQL и имена ресурсов клиент не задаёт. Успех — 202, Location заявки и requestId/environmentId/jobId. Совпадающий повтор возвращает те же идентификаторы, несовпадающий payload — 409. Повтор не обходит актуальные проверки авторизации. Ошибки возвращаются кодом без путей/SQL/секретов.

Администрирование: `POST /api/admin/users` создаёт только Developer (`{ email, password }`); `POST /api/admin/project-memberships` выдаёт членство (`{ userId, projectId }`). Оба требуют роли Administrator и CSRF; аудируются. Проекты, пулы, квоты, ACL архивов и версии в этом срезе настраиваются оператором через контролируемую SQL-процедуру развёртывания/EF, не пользовательский endpoint. Полного административного UI и API конфигурации пока нет. Автоматического создания разрешённых пулов или Ready-архивов нет.

Чтение: `GET /api/environment-requests/{id}`, `GET /api/test-environments`, `GET /api/test-environments/{id}`. Доступ по владельцу/администратору и project membership; DTO не содержит UNC, CredentialRef, SQL-сервера или cookie/паролей. Продление/удаление/connection будут отдельным срезом: выдачи подключения Requested-окружению нет.

## Данные и безопасность

Никаких seed production-пулов, архивов или учётных записей с паролями по умолчанию. Пул/формат не активируется без подтверждённого паспорта, профиль/изоляция/совместимость и численные бюджеты обязательны. Искусственный seed только в SQL integration tests и только в отдельной генерируемой БД.

Транзакция приёма включает заявку, окружение, reservation, Pending job и аудит. Защита квот должна сериализовать всех конкурентов по владельцу и пулу; уникальные ограничения дополнительно защищают idempotency и технические имена. Все дальнейшие writers/reconciliation обязаны соблюдать тот же протокол блокировок.

## Проверки

Обычные команды build/test — в development.md. API-тесты с подменой application port проверяют HTTP auth/CSRF/контракт, но не SQL и не реальный Identity password flow. SQL-тесты используют настоящий SQL Server и явно пропускаются без opt-in; не выдавать зелёные unit/HTTP тесты за доказательство транзакционной корректности.

Workflow `.github/workflows/sql-integration.yml` запускается вручную и поднимает одноразовый SQL Server 2022 service container без доступа к корпоративной сети. Указанный в workflow пароль относится только к этому job-local контейнеру, не к инфраструктуре; TrustServerCertificate разрешён только для него. Образ закреплён CU-тегом; доступность manifest закреплённого образа проверена (HTTP 200), фактический запуск контейнера/workflow здесь не проверен.

Для интеграционных тестов предоставить подключение к отдельному тестовому SQL instance через `BUILDSYSTEM_TEST_SQL` и явно `BUILDSYSTEM_TEST_SQL_ALLOW_CREATE=true`. Harness создаёт БД с собственным `bs_ci_metadata_` UUID, применяет миграции и удаляет только эту БД. Пользователь должен иметь разрешение CREATE/DROP DATABASE на этом тестовом instance; не указывать рабочий сервер. Внешние ИБ и архивы не используются.

Результаты локального выполнения, ревью и непроверенные случаи — в [server-slice-review.md](server-slice-review.md).

Для изменения модели выполните `dotnet tool restore`: локальный manifest `.config/dotnet-tools.json` закрепляет `dotnet-ef` 10.0.13, совпадающий с EF packages. Используйте `--project src/Server/BuildSystem.Infrastructure` и конфигурацию Release. Design-time factory требует `BUILDSYSTEM_DESIGN_CONNECTION`; генерация миграции не подключается к SQL, но не должна использовать рабочие секреты. Проверка `migrations has-pending-model-changes` и генерация idempotent script выполняются до SQL-прогона. EF runtime/Design/Identity закреплены на 10.0.13, NuGet audit включён.
