# План реализации

1. **Технический spike**: проверить Vanessa Runner 3.x, EDT CLI, 1С, сборку CF, загрузку CF в одноразовую тестовую ИБ и точные параметры команд.
2. **ProcessRunner и Tool Providers**: безопасный запуск CLI, логи, timeout, cancellation, тесты.
3. **Pipeline Engine**: YAML schema, registry, expressions, состояния, snapshot планов, unit tests.
4. **Agent Worker**: регистрация, capabilities, polling, lease, heartbeat, отчётность.
5. **Deploy**: создание ИБ, SQL backup, блокировка сеансов, LoadCfg, UpdateDBCfg, verify, recovery.
6. **Backend API + SQL schema**: проекты, базы, задания, блокировки, артефакты, аудит.
7. **React UI**: запуск задания, выбор проекта/ветки/базы, прогресс и логи.
8. **Интеграционные тесты**: повтор после сбоя, потеря агента, ошибка backup, ошибка UpdateDBCfg, concurrent deployment.

## Вопросы перед кодированием
- Точные версии EDT, 1С и Vanessa Runner.
- Доступность RAS и схема сервисных учётных записей.
- Где хранятся CF и SQL backups, retention policy.
- Шаблоны создания баз: пустая или восстановление из SQL backup.
- Политика завершения пользовательских сеансов.
- Нужна ли отдельная служба восстановления зависших блокировок.
