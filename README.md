# 1C BuildSystem

Система сборки 1С:EDT из Git и развёртывания клиент-серверных информационных баз 1С на Microsoft SQL Server.

## Зафиксированные решения
- Backend ASP.NET Core, Agent .NET Worker Service, UI React + TypeScript.
- Исходники фиксируются конкретным Git commit SHA.
- Сборка CF — Vanessa Runner / EDT CLI; обновление существующей базы — полная загрузка CF.
- Pipeline — декларативный YAML с готовыми версионированными шагами.
- Агент получает задания через API и отправляет heartbeat, логи и результаты.

## Документы
- [Архитектура](docs/architecture.md)
- [Агент и инструменты](docs/agent.md)
- [Pipeline Engine](docs/pipeline.md)
- [База данных и API](docs/data-api.md)
- [Безопасность и ошибки](docs/safety.md)
- [Этапы реализации](docs/roadmap.md)
- [Пример YAML](examples/deploy-existing.yaml)

Документация — проектная спецификация. Точный синтаксис Vanessa Runner и EDT CLI требуется проверить на целевых версиях до реализации.
