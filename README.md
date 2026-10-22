# 1C BuildSystem

Система сборки 1С:EDT из Git и развёртывания клиент-серверных информационных баз 1С на Microsoft SQL Server. Разработчики могут самостоятельно заказывать личные тестовые базы через веб-интерфейс.

## Зафиксированные решения
- Backend ASP.NET Core, Agent .NET Worker Service, UI React + TypeScript.
- Исходники фиксируются конкретным Git commit SHA.
- Сборка CF — Vanessa Runner / EDT CLI; обновление существующей базы — полная загрузка CF.
- Pipeline — декларативный YAML с готовыми версионированными шагами.
- Агент получает задания через API и отправляет heartbeat, логи и результаты.

- Заказ тестовых баз: пустая база, архив с диска (.dt/.bak), готовая сборка/ветка Git или разрешённый SQL-шаблон; владелец, квоты и срок использования.

## Порядок реализации и приёмки
Первый приоритет — рабочий заказ из архива с диска, а не сборка EDT. Сценарии принимаются последовательно; итоговый MVP включает .dt, .bak, пустую базу и SQL-шаблон. Сведения о фактическом стенде пока не предоставлены. Базовые решения и ответы реализатору — в [implementation-decisions.md](docs/implementation-decisions.md), перечень нужных данных — в [stand-readiness.md](docs/stand-readiness.md).

## Текущая реализация
Доменная модель и серверный срез приёма заказа архива в `BuildSystem.slnx`: ASP.NET Core API, Identity, EF Core SQL Server, миграции и Pending-задания. .NET SDK 10.0.402, unit/HTTP tests и opt-in SQL integration tests. Добавлен [агентский control-plane и Worker skeleton](docs/agent-control-plane.md) только для безопасного probe. UI, исполнение archive pipeline и реальные восстановления ещё не реализованы; готовность SQL-интеграции подтверждается отдельным прогоном, не unit-тестами. Инструкции — [Разработка](docs/development.md) и [Серверный срез](docs/server-slice.md).

## Документы
- [Архитектура](docs/architecture.md)
- [Агент и инструменты](docs/agent.md)
- [Pipeline Engine](docs/pipeline.md)
- [ProcessRunner и минимальный Pipeline Engine: реализация и ограничения](docs/process-pipeline.md)
- [База данных и API](docs/data-api.md)
- [Безопасность и ошибки](docs/safety.md)
- [Заказ тестовых баз](docs/test-environments.md)
- [Развёртывание архивов с диска — обязательный MVP](docs/disk-archives.md)
- [Пример развёртывания архива](examples/provision-from-disk.yaml)
- [Пример заказа базы из шаблона](examples/provision-test-base.yaml)
- [Ответы реализатору: 39 решений и уточнений](docs/implementation-decisions.md)
- [Паспорт и готовность стенда](docs/stand-readiness.md)
- [Этапы реализации](docs/roadmap.md)
- [Пример YAML обновления существующей базы — последующий этап](examples/deploy-existing.yaml)

Документация — проектная спецификация. Точный синтаксис Vanessa Runner и EDT CLI требуется проверить на целевых версиях до реализации.
