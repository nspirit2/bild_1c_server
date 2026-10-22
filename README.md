# 1C BuildSystem

Система сборки 1С:EDT из Git и развёртывания клиент-серверных информационных баз 1С на Microsoft SQL Server. Разработчики могут самостоятельно заказывать личные тестовые базы через веб-интерфейс.

## Зафиксированные решения
- Backend ASP.NET Core, Agent .NET Worker Service, UI React + TypeScript.
- Исходники фиксируются конкретным Git commit SHA.
- Сборка CF — Vanessa Runner / EDT CLI; обновление существующей базы — полная загрузка CF.
- Pipeline — декларативный YAML с готовыми версионированными шагами.
- Агент получает задания через API и отправляет heartbeat, логи и результаты.

- Заказ тестовых баз: пустая база, архив с диска (.dt/.bak), готовая сборка/ветка Git или разрешённый SQL-шаблон; владелец, квоты и срок использования.

## Документы
- [Архитектура](docs/architecture.md)
- [Агент и инструменты](docs/agent.md)
- [Pipeline Engine](docs/pipeline.md)
- [База данных и API](docs/data-api.md)
- [Безопасность и ошибки](docs/safety.md)
- [Заказ тестовых баз](docs/test-environments.md)
- [Развёртывание архивов с диска — обязательный MVP](docs/disk-archives.md)
- [Пример развёртывания архива](examples/provision-from-disk.yaml)
- [Пример заказа базы из шаблона](examples/provision-test-base.yaml)
- [Этапы реализации](docs/roadmap.md)
- [Пример YAML](examples/deploy-existing.yaml)

Документация — проектная спецификация. Точный синтаксис Vanessa Runner и EDT CLI требуется проверить на целевых версиях до реализации.
