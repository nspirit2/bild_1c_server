# BuildSystem.Agent

Агент — Windows-служба .NET Worker. Регистрируется на API, публикует capabilities (версии EDT, 1С, Vanessa Runner), запрашивает задания, обновляет lease, исполняет шаги, передаёт журналы и результаты.

## Компоненты
- Worker: регистрация, polling, heartbeat, shutdown.
- Execution: PipelineExecutor, StepRegistry, JobExecutionContext, StateReporter.
- Tools: ProcessRunner, GitProvider, VanessaRunnerProvider, ClusterProvider, SqlBackupProvider, ArtifactStorage.

## Контракты
```csharp
public interface IProcessRunner {
    Task<ProcessResult> RunAsync(ProcessCommand command,
        Func<string, Task> onOutput, CancellationToken ct);
}
public interface IPipelineStepHandler {
    string Type { get; }
    int Version { get; }
    Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context, CancellationToken ct);
}
public interface IVanessaRunnerClient {
    Task<CompileResult> CompileCfAsync(CompileRequest request, CancellationToken ct);
    Task<DeployResult> UpdateInfobaseAsync(DeployRequest request, CancellationToken ct);
}
```

## Исполнение CLI
ProcessStartInfo.ArgumentList без cmd.exe, чтение stdout/stderr, таймауты, cancellation с завершением дерева процессов, маскирование секретов, запись exit code и PID. Отдельные Git checkout и EDT workspace для каждого запуска. Не считать exit code достаточным: проверить CF и состояние базы.

## Интеграции
Git checkout по зафиксированному SHA; Vanessa Runner для компиляции CF и обновления ИБ; rac/ras для регистрации ИБ и управления сеансами; SQL client для BACKUP DATABASE и RESTORE VERIFYONLY. Проверить реальный синтаксис Vanessa Runner 3.x в техническом spike. Новая серверная ИБ создаётся отдельным шагом, а не предполагается результатом init.

## Provisioning тестовых баз
Добавить TestEnvironmentProvider, TemplateRestoreProvider и TestIsolationProvider. Входы — только идентификаторы серверных резервирований, версий дисковых архивов и опубликованных версий шаблонов; сервер разрешает пути и CredentialRef. Контракты шагов описаны в [test-environments.md](test-environments.md).

Создание пустой ИБ, восстановление SQL в новую БД, регистрация существующей БД в кластере 1С и загрузка CF — разные операции. Проверить их точные параметры на целевых версиях. RESTORE выполняется без REPLACE с уникальными именами БД и файлов; пути относятся к SQL Server. После восстановления ИБ не доступна разработчику до применения политики изоляции и проверки.

Каждая внешняя операция записывает intent и полученные идентификаторы в журнал ресурсов. При потере lease агент останавливает дальнейшие операции; новый агент сначала сверяет фактическое состояние и оставшиеся процессы. Проверка fencing token в API сама по себе не останавливает уже выполняющийся RESTORE.

## Дисковые архивы
Добавить ArchiveCatalogProvider и ArchiveStagingProvider, поддержку .dt в TestEnvironmentProvider и .bak через TemplateRestoreProvider. Агент объявляет capabilities чтения конкретных archive root и восстановления каждого формата. Root привязан к источнику/агенту либо группе с эквивалентным доступом; наличие одинакового пути на другом хосте не означает тот же источник.

Сканирование выполняет агент с доступом к root; stage читает выбранную версию в контролируемую копию с SHA-256. .dt передаётся процессу платформы 1С для загрузки в новую ИБ; .bak размещается там, где его может прочитать служба SQL Server. Строки запуска и поведение запрета регламентных заданий во время загрузки проверить на целевых версиях; наличие имени шага в документации не подтверждает готовность CLI-интеграции. Подробности — [disk-archives.md](disk-archives.md).
