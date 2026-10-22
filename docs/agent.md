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
