using System.Text;
using BuildSystem.Agent.Execution;

namespace BuildSystem.Agent.Tools;

/// <summary>Operator-owned RAC settings; never constructed from pipeline data.</summary>
public sealed class ScheduledJobsObserverOptions
{
    public required string TrustedRacToolId { get; init; }
    public required string Endpoint { get; init; }
    public required string WorkingDirectory { get; init; }
    public required TimeSpan Timeout { get; init; }
    public override string ToString() => "ScheduledJobsObserverOptions [details omitted]";
}

/// <summary>Explicit invocation credentials; diagnostic representations must never reveal secrets.</summary>
public sealed record ScheduledJobsObserverCredentials(string ClusterUser, string ClusterPassword)
{
    public override string ToString() => "ScheduledJobsObserverCredentials [details omitted]";
}

/// <summary>
/// Read-only RAC observation. A successful result is a parsed statement, not freshness, authority,
/// a lock, a grant or proof that the state remains unchanged.
/// </summary>
public sealed class ScheduledJobsObserver
{
    private readonly IProcessRunner runner;
    private readonly string toolId;
    private readonly string endpoint;
    private readonly string workingDirectory;
    private readonly TimeSpan timeout;

    public ScheduledJobsObserver(IProcessRunner runner, ScheduledJobsObserverOptions options)
    {
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.TrustedRacToolId) ||
            options.TrustedRacToolId.Length > 128 || options.TrustedRacToolId.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(options.WorkingDirectory) ||
            options.WorkingDirectory.Length > 4096 || options.WorkingDirectory.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(options.Endpoint) || options.Endpoint.Length > 255 ||
            options.Endpoint.StartsWith('-') ||
            options.Endpoint.Any(char.IsWhiteSpace) ||
            options.Endpoint.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(options.WorkingDirectory) ||
            options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentException("invalid_observer_options", nameof(options));
        // Copy the validated operator policy into immutable per-observer values.
        toolId = options.TrustedRacToolId;
        endpoint = options.Endpoint;
        workingDirectory = options.WorkingDirectory;
        timeout = options.Timeout;
    }

    public async Task<ScheduledJobsBlockedReadback> ObserveAsync(ScheduledJobsReadbackScope scope,
        ScheduledJobsObserverCredentials credentials, CancellationToken cancellationToken = default)
    {
        // Validate caller correlation before executing a command; this is not ownership evidence.
        if (scope is null || scope.FencingToken <= 0 ||
            new[] { scope.OperationId, scope.TargetIncarnationId, scope.ClusterId, scope.InfobaseId,
                scope.ServiceEpoch, scope.ObservationId }.Any(id => id == Guid.Empty) ||
            !Enum.IsDefined(scope.Checkpoint))
            throw new ScheduledJobsReadbackException("InvalidScheduledJobsScope");
        if (credentials is null || string.IsNullOrEmpty(credentials.ClusterUser) ||
            string.IsNullOrEmpty(credentials.ClusterPassword) ||
            credentials.ClusterUser.Length > 4096 || credentials.ClusterPassword.Length > 4096 ||
            credentials.ClusterUser.Any(char.IsControl) || credentials.ClusterPassword.Any(char.IsControl))
            throw new ScheduledJobsReadbackException("InvalidScheduledJobsCredentials");
        if (cancellationToken.IsCancellationRequested)
            throw new ScheduledJobsReadbackException("ScheduledJobsReadbackUnavailable");

        var command = new ProcessCommand(toolId, workingDirectory,
            [new("infobase"), new("info"), new("--cluster=" + scope.ClusterId.ToString("D")),
             new("--infobase=" + scope.InfobaseId.ToString("D")),
             new("--cluster-user=" + credentials.ClusterUser, Sensitive: true),
             new("--cluster-pwd=" + credentials.ClusterPassword, Sensitive: true),
             new(endpoint)], timeout, [credentials.ClusterUser, credentials.ClusterPassword]);
        var stdout = new StringBuilder();
        var stderrSeen = false;
        var overflow = false;
        var gate = new object(); // Do not rely on a fake runner serializing callbacks.
        Task Capture(ProcessOutput item)
        {
            lock (gate)
            {
                if (item is null || item.Text is null)
                    throw new InvalidOperationException("invalid_process_output");
                if (item.Stream == ProcessOutputStream.StandardError)
                    stderrSeen |= item.Text.Length != 0;
                else if (item.Stream == ProcessOutputStream.StandardOutput)
                {
                    if (item.Text.Length > ScheduledJobsReadback.MaxOutputCharacters - stdout.Length)
                        overflow = true;
                    else if (!overflow) stdout.Append(item.Text);
                }
                else throw new InvalidOperationException("invalid_process_output");
            }
            return Task.CompletedTask;
        }

        ProcessResult result;
        try { result = await runner.RunAsync(command, Capture, cancellationToken).ConfigureAwait(false); }
        catch { throw new ScheduledJobsReadbackException("ScheduledJobsReadbackUnavailable"); }
        lock (gate)
        {
            if (cancellationToken.IsCancellationRequested || result is null ||
                result.Completion != ProcessCompletion.Exited || result.ExitCode != 0 ||
                result.OutputTruncated || overflow || stderrSeen)
                throw new ScheduledJobsReadbackException("ScheduledJobsReadbackUnavailable");
            return ScheduledJobsReadback.Parse(scope, result.ExitCode.Value, false, stdout.ToString());
        }
    }
}
