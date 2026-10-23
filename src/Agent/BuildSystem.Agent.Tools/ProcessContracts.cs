namespace BuildSystem.Agent.Tools;

// Values are deliberately absent from diagnostic representations.
public sealed record ProcessArgument(string Value, bool Sensitive = false)
{
    public override string ToString() => Sensitive ? "[sensitive]" : "[argument]";
}
public sealed record ProcessCommand(string TrustedToolId, string WorkingDirectory,
    IReadOnlyList<ProcessArgument> Arguments, TimeSpan Timeout, IReadOnlyList<string>? KnownSecrets = null)
{
    public override string ToString() => "ProcessCommand [details omitted]";
}
public enum ProcessCompletion { Exited, TimedOut, Cancelled }
public enum ProcessOutputStream { StandardOutput, StandardError }
public sealed record ProcessOutput(ProcessOutputStream Stream, string Text);
public sealed record ProcessResult(int? ProcessId, int? ExitCode, ProcessCompletion Completion, bool OutputTruncated);
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessCommand command, Func<ProcessOutput, Task>? onOutput = null,
        CancellationToken cancellationToken = default);
}
public sealed class ProcessRunnerException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
/// <summary>Operator-owned configuration, never populated from pipeline input.</summary>
public sealed class ProcessRunnerOptions
{
    public required IReadOnlyDictionary<string, string> TrustedTools { get; init; }
    public required IReadOnlyList<string> AllowedWorkspaceRoots { get; init; }
    public IReadOnlyList<string> InheritedEnvironmentVariables { get; init; } = [];
    public int MaxLineCharacters { get; init; } = 4096;
    public int MaxOutputCharacters { get; init; } = 65536;
}