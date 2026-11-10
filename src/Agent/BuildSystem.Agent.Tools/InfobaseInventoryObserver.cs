using System.Text;
using BuildSystem.Agent.Execution;

namespace BuildSystem.Agent.Tools;

/// <summary>Operator-owned RAC tool and endpoint mapping; never derived from pipeline data.</summary>
public sealed class InfobaseInventoryObserverOptions
{
    public required string TrustedRacToolId { get; init; }
    public required string Endpoint { get; init; }
    public required Guid EndpointMappingId { get; init; }
    // Set both to pin canonical correlation separately from RAC's physical cluster argument.
    // This operator assertion is not proof of the endpoint's physical identity. Both null retain legacy behavior.
    public Guid? CanonicalClusterId { get; init; }
    public Guid? RacClusterId { get; init; }
    public required string WorkingDirectory { get; init; }
    public required TimeSpan Timeout { get; init; }
    public override string ToString() => "InfobaseInventoryObserverOptions [details omitted]";
}

/// <summary>Explicit invocation credentials, excluded from diagnostic representations.</summary>
public sealed record InfobaseInventoryObserverCredentials(string ClusterUser, string ClusterPassword)
{
    public override string ToString() => "InfobaseInventoryObserverCredentials [details omitted]";
}

/// <summary>
/// A fixed read-only RAC inventory observation, not ownership, freshness, a lock, authority,
/// permission to create or restore, or proof that an observed state remains unchanged.
/// </summary>
public sealed class InfobaseInventoryObserver
{
    private readonly IProcessRunner runner;
    private readonly string toolId;
    private readonly string endpoint;
    private readonly Guid endpointMappingId;
    private readonly Guid? canonicalClusterId;
    private readonly Guid? racClusterId;
    private readonly string workingDirectory;
    private readonly TimeSpan timeout;

    public InfobaseInventoryObserver(IProcessRunner runner, InfobaseInventoryObserverOptions options)
    {
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.TrustedRacToolId) ||
            options.TrustedRacToolId.Length > 128 || options.TrustedRacToolId.StartsWith('-') ||
            options.TrustedRacToolId.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ||
            string.IsNullOrWhiteSpace(options.Endpoint) || options.Endpoint.Length > 255 ||
            options.Endpoint.StartsWith('-') ||
            options.Endpoint.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ||
            options.EndpointMappingId == Guid.Empty ||
            options.CanonicalClusterId.HasValue != options.RacClusterId.HasValue ||
            options.CanonicalClusterId == Guid.Empty || options.RacClusterId == Guid.Empty ||
            string.IsNullOrWhiteSpace(options.WorkingDirectory) ||
            options.WorkingDirectory.Length > 4096 || options.WorkingDirectory.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(options.WorkingDirectory) ||
            options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentException("invalid_observer_options", nameof(options));
        // Snapshot validated operator policy, independent of all invocation data.
        toolId = options.TrustedRacToolId;
        endpoint = options.Endpoint;
        endpointMappingId = options.EndpointMappingId;
        canonicalClusterId = options.CanonicalClusterId;
        racClusterId = options.RacClusterId;
        workingDirectory = options.WorkingDirectory;
        timeout = options.Timeout;
    }

    public async Task<InfobaseInventoryReadbackResult> ObserveAsync(InfobaseInventoryReadbackScope scope,
        InfobaseInventoryObserverCredentials credentials, CancellationToken cancellationToken = default)
    {
        if (scope is null || scope.FencingToken <= 0 ||
            new[] { scope.OperationId, scope.TargetIncarnationId, scope.ClusterId, scope.EndpointMappingId,
                scope.ReservationId, scope.ObservationId }.Any(id => id == Guid.Empty) ||
            !string.Equals(scope.ExpectedInfobaseName, $"bs_test_{scope.ReservationId:N}", StringComparison.Ordinal))
            throw new InfobaseInventoryReadbackException("InvalidInfobaseInventoryScope");
        if (scope.EndpointMappingId != endpointMappingId ||
            (canonicalClusterId.HasValue && scope.ClusterId != canonicalClusterId.Value))
            throw new InfobaseInventoryReadbackException("InfobaseInventoryScopeMismatch");
        if (credentials is null || string.IsNullOrEmpty(credentials.ClusterUser) ||
            string.IsNullOrEmpty(credentials.ClusterPassword) ||
            credentials.ClusterUser.Length > 4096 || credentials.ClusterPassword.Length > 4096 ||
            credentials.ClusterUser.Any(char.IsControl) || credentials.ClusterPassword.Any(char.IsControl))
            throw new InfobaseInventoryReadbackException("InvalidInfobaseInventoryCredentials");
        if (cancellationToken.IsCancellationRequested)
            throw new InfobaseInventoryReadbackException("InfobaseInventoryReadbackUnavailable");

        var command = new ProcessCommand(toolId, workingDirectory,
            [new("infobase"), new("summary"), new("list"), new("--cluster=" + (racClusterId ?? scope.ClusterId).ToString("D")),
             new("--cluster-user=" + credentials.ClusterUser, Sensitive: true),
             new("--cluster-pwd=" + credentials.ClusterPassword, Sensitive: true), new(endpoint)],
            timeout, [credentials.ClusterUser, credentials.ClusterPassword]);
        var stdout = new StringBuilder();
        var stderrSeen = false;
        var overflow = false;
        var callbackFailed = false;
        var gate = new object();
        Task Capture(ProcessOutput item)
        {
            lock (gate)
            {
                if (item is null || item.Text is null ||
                    item.Stream is not (ProcessOutputStream.StandardError or ProcessOutputStream.StandardOutput))
                {
                    // Also fail closed if a runner suppresses a callback exception.
                    callbackFailed = true;
                    throw new InvalidOperationException("invalid_process_output");
                }
                if (item.Stream == ProcessOutputStream.StandardError)
                    stderrSeen |= item.Text.Length != 0;
                else if (item.Text.Length > InfobaseInventoryReadback.MaxOutputCharacters - stdout.Length)
                    overflow = true;
                else if (!overflow) stdout.Append(item.Text);
            }
            return Task.CompletedTask;
        }

        ProcessResult result;
        try { result = await runner.RunAsync(command, Capture, cancellationToken).ConfigureAwait(false); }
        catch { throw new InfobaseInventoryReadbackException("InfobaseInventoryReadbackUnavailable"); }
        lock (gate)
        {
            if (cancellationToken.IsCancellationRequested || result is null ||
                result.Completion != ProcessCompletion.Exited || result.ExitCode != 0 ||
                result.OutputTruncated || overflow || stderrSeen || callbackFailed)
                throw new InfobaseInventoryReadbackException("InfobaseInventoryReadbackUnavailable");
            // ProcessRunner substitutes this marker when KnownSecrets occur in stdout. A substituted
            // target name could otherwise turn a real Present observation into a false Absent one.
            var output = stdout.ToString();
            if (output.Contains("[REDACTED]", StringComparison.Ordinal))
                throw new InfobaseInventoryReadbackException("InfobaseInventoryReadbackUnavailable");
            return InfobaseInventoryReadback.Parse(scope, result.ExitCode.Value, false, output);
        }
    }
}
