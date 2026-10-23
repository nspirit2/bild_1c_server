# Отчёт серверного среза

Ветка: `feat/archive-order-api`, база — `feat/domain-foundation` (коммит 6ad8279). Код в main не изменяется, автоматического merge нет.

## Реализовано

- ASP.NET Core API, отдельные Application и Infrastructure, EF Core SQL Server 10.0.13, Identity Guid.
- Реально сгенерированная InitialOrders migration; офлайн-проверка совпадения модели и генерация SQL-скрипта.
- Cookie-сессии, CSRF, lockout, bootstrap первого администратора, создание Developer и выдача project membership администратором, аудит.
- Archive-only заказ с каноническим payload hash и бинарным ключом идемпотентности.
- Serializable транзакция: текущие ACL, source readiness, подтверждённые capability/изоляция/профиль, квоты, staging; атомарное сохранение request/environment/reservation/job/audit.
- Transaction-owned applocks owner → root → pool; отрицательные lock results останавливают запись, deadlock/timeout имеют ограниченный retry всей транзакции.
- Повтор существующего заказа не требует всё ещё доступного архива, но проверяет текущие project/root ACL.
- Чтение собственных данных и административный доступ без раскрытия путей/секретов.

## Ревью

Отдельные read-only ревью проведены для API auth/CSRF/bootstrap и для SQL admission/model. Подтверждённых критических ошибок по результатам статического просмотра не найдено. На замечания добавлены role/CSRF HTTP-тесты; уточнены DPAPI principal и retry applock.

## Что не доказано

В локальной среде отсутствуют SQL Server и Docker. Никаких реальных БД, 1С, служб, UNC или firewall-правил не изменялось. Реальное применение миграции, параллельный SQL admission, Identity password login и bootstrap с SQL пока не проверены. HTTP tests используют test-only authentication и application stub — это не end-to-end Identity.

SQL integration tests opt-in, используют только собственные `bs_ci_metadata_` БД. CI workflow для одноразового SQL Server подготовлен, но удалённо не запускался. Офлайн миграционная проверка не заменяет применение миграции к настоящему SQL Server.

Pending job — только durable intent, не compiled pipeline; executor отсутствует. Доступ к созданной ИБ не выдаётся, TTL Requested не отсчитывается. Нет UI, agent enrollment/polling/lease, реального каталога/staging/restore, lifecycle endpoints и полного административного конфигурирования.

## Воспроизведение

Сборка — development.md. Запуск, безопасная конфигурация, команды миграции/bootstrap и SQL opt-in — server-slice.md. NuGet audit не отключён; vulnerable transitive зависимости обновлены исправленными фиксированными версиями.

Локально: .NET SDK 10.0.402, Release build 0 warnings/0 errors; 39 domain и 17 HTTP/application/offline-model tests проходят. `dotnet publish` API в локальный каталог успешно; это не запуск службы и не публикация в интернет. NuGet vulnerable audit не выявил известных уязвимостей по текущему feed. Итоговый полный прогон: 56 passed (39 domain + 17 HTTP/application/offline-model), 0 failed; 10 SQL integration tests явно skipped без стенда. Эти 10 тестов скомпилированы, но их runtime-поведение не подтверждено. `restore --locked-mode`, `git diff --check` и `ef migrations has-pending-model-changes` проходят.
