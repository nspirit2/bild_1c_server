# Архитектура

## Сценарий
Пользователь выбирает проект, Git-ветку, существующую базу либо создание новой. API фиксирует SHA, создаёт задание, агент собирает CF, создаёт/обновляет базу, проверяет результат. UI показывает логи и статус.

```text
React UI -> ASP.NET Core API -> SQL Server (метаданные)
                      |               ^
                      v               |
                Agent Worker ---------+
                  |   |   |   |
                 Git EDT Vanessa SQL / rac
                           |
                     Кластер 1С + SQL Server (ИБ)
```

## Структура solution
```text
src/Server/BuildSystem.Api
src/Server/BuildSystem.Application
src/Server/BuildSystem.Domain
src/Server/BuildSystem.Infrastructure
src/Agent/BuildSystem.Agent.Worker
src/Agent/BuildSystem.Agent.Execution
src/Agent/BuildSystem.Agent.Tools
src/Contracts/BuildSystem.Contracts
src/Web/build-system-ui
tests/BuildSystem.UnitTests
tests/BuildSystem.IntegrationTests
tests/BuildSystem.AgentTests
```

## Модули
Projects, Repositories, Builds, Infobases, Deployments, Clusters, Backups, Jobs, Agents, Artifacts, Audit. Модульный монолит без микросервисов в MVP. Сборка и развёртывание независимы: один CF может развёртываться в несколько баз. Ключ кэша: Git SHA + версии EDT/платформы/инструмента + параметры сборки.

## Технологии
ASP.NET Core, EF Core, Microsoft SQL Server, .NET Worker, SignalR, Serilog, React + TypeScript, Git CLI, Vanessa Runner, EDT CLI, rac/ras, Microsoft.Data.SqlClient.

## Самостоятельный заказ тестовых баз
Отдельный пользовательский сценарий и модуль TestEnvironments: каталог шаблонов, заявки, личные базы, квоты и срок использования. Подробный контракт — [Заказ тестовых баз](test-environments.md).

API принимает заказ, атомарно резервирует квоту и имена ресурсов, фиксирует версию шаблона/сборку/SHA и создаёт задание Pipeline Engine. Агент создаёт новую ИБ в разрешённом тестовом пуле. Доступ выдаётся после подготовки и проверки. Сборка из Git нужна только при выборе ветки; заказ готового шаблона не запускает EDT.

TestEnvironment — пользовательское окружение с владельцем и сроком использования; Infobase — технический ресурс. Заявка, окружение и задания создания/удаления имеют отдельные состояния. Фоновый lifecycle worker обрабатывает сроки и reconciliation, используя ту же очередь и блокировки. Производственные серверы не входят в пулы заказа.
