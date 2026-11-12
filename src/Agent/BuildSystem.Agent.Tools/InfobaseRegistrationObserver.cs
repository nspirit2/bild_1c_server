using System.Text;
using BuildSystem.Agent.Execution;

namespace BuildSystem.Agent.Tools;

/// <summary>Operator-owned, explicitly disabled-by-default contract observer settings. No host wiring.</summary>
public sealed class InfobaseRegistrationObserverOptions
{
    public bool Enabled { get; init; } = false;
    public string? TrustedRacToolId { get; init; }
    /// <summary>
    /// Operator assertion of the trusted RAC version, not independently verified by this observer.
    /// Actual trusted executable inventory remains the operator contract through the existing tool-ID resolver.
    /// The observed profile requires exactly 8.3.22.2557; the legacy synthetic profile does not require a version.
    /// </summary>
    public string? TrustedRacVersion { get; init; }
    public string? Endpoint { get; init; }
    public string? WorkingDirectory { get; init; }
    public TimeSpan Timeout { get; init; }
    public Guid CanonicalClusterId { get; init; }
    public Guid CanonicalSqlServerId { get; init; }
    public Guid RacEndpointMappingId { get; init; }
    public Guid SqlEndpointMappingId { get; init; }
    public Guid PhysicalClusterId { get; init; }
    public string? ExpectedSqlServer { get; init; }
    public string? OutputProfileId { get; init; }
    public override string ToString() => "InfobaseRegistrationObserverOptions [details omitted]";
}

public sealed record InfobaseRegistrationObserverCredentials(string ClusterUser, string ClusterPassword)
{
    public override string ToString() => "InfobaseRegistrationObserverCredentials [details omitted]";
}

public sealed class InfobaseRegistrationObserverException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// Read-only observation using explicitly selected synthetic or observed-profile metadata; disabled by default.
/// TrustedRacVersion is an operator assertion, not binary verification; trusted executable inventory and
/// the tool-ID resolver remain the operator contract. No profile autodetection or fallback is performed.
/// A successful observation establishes neither ownership nor restore permission, authority, or fresh evidence.
/// The trusted runner MUST NOT log raw stdout or stderr: output can contain secrets unknown before parsing,
/// including RAC permission codes. ProcessCommand credential redaction does not cover these output secrets.
/// The runner contract requires all callbacks to finish before RunAsync completes; callbacks
/// arriving after completion are rejected, but cannot retract a value already returned to a caller.
/// Cancellation and deadline require a cooperative runner: no detached process or false hard-preempt.
/// </summary>
public sealed class InfobaseRegistrationObserver
{
    private readonly IProcessRunner runner;
    private readonly TimeProvider clock;
    private readonly bool enabled;
    private readonly string toolId = "";
    private readonly string endpoint = "";
    private readonly string directory = "";
    private readonly string expectedSqlServer = "";
    private readonly string outputProfileId = "";
    private readonly TimeSpan timeout;
    private readonly Guid canonicalClusterId, canonicalSqlServerId, racMappingId, sqlMappingId, physicalClusterId;

    public InfobaseRegistrationObserver(IProcessRunner runner, InfobaseRegistrationObserverOptions options,
        TimeProvider? timeProvider = null)
    {
        this.runner = runner ?? throw Error("InfobaseRegistrationObserverInvalidOptions");
        if (options is null) throw Error("InfobaseRegistrationObserverInvalidOptions");
        enabled = options.Enabled;
        clock = timeProvider ?? TimeProvider.System;
        if (!enabled) return; // Intentionally ignore every other setting while disabled.
        if (string.IsNullOrWhiteSpace(options.TrustedRacToolId) || options.TrustedRacToolId.Length > 128 ||
            options.TrustedRacToolId.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(options.WorkingDirectory) || options.WorkingDirectory.Length > 4096 ||
            options.WorkingDirectory.Any(char.IsControl) || !Path.IsPathFullyQualified(options.WorkingDirectory) ||
            !ValidEndpoint(options.Endpoint) || !ValidServer(options.ExpectedSqlServer) ||
            !(options.OutputProfileId == InfobaseRegistrationReadback.ProfileId ||
              (options.OutputProfileId == InfobaseRegistrationReadback.ObservedProfileId &&
               string.Equals(options.TrustedRacVersion, "8.3.22.2557", StringComparison.Ordinal))) ||
            options.Timeout < TimeSpan.FromSeconds(1) || options.Timeout > TimeSpan.FromSeconds(60) ||
            options.CanonicalClusterId == Guid.Empty || options.CanonicalSqlServerId == Guid.Empty ||
            options.RacEndpointMappingId == Guid.Empty || options.SqlEndpointMappingId == Guid.Empty ||
            options.PhysicalClusterId == Guid.Empty)
            throw Error("InfobaseRegistrationObserverInvalidOptions");
        toolId = options.TrustedRacToolId;
        endpoint = options.Endpoint!;
        directory = options.WorkingDirectory;
        timeout = options.Timeout;
        canonicalClusterId = options.CanonicalClusterId;
        canonicalSqlServerId = options.CanonicalSqlServerId;
        racMappingId = options.RacEndpointMappingId;
        sqlMappingId = options.SqlEndpointMappingId;
        physicalClusterId = options.PhysicalClusterId;
        expectedSqlServer = options.ExpectedSqlServer!;
        outputProfileId = options.OutputProfileId!;
    }

    public async Task<InfobaseRegistrationReadbackResult> ObserveAsync(
        InfobaseRegistrationReadbackScope scope, InfobaseRegistrationObserverCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        if (!enabled) throw Error("InfobaseRegistrationObserverDisabled");
        if (scope is null || scope.FencingToken <= 0 ||
            new[] { scope.OperationId, scope.TargetIncarnationId, scope.ReservationId,
                scope.CanonicalClusterId, scope.CanonicalSqlServerId, scope.RacEndpointMappingId,
                scope.SqlEndpointMappingId, scope.AttemptId, scope.ClaimId, scope.InfobaseId,
                scope.ObservationId }.Any(id => id == Guid.Empty) ||
            !string.Equals(scope.ExpectedInfobaseName, $"bs_test_{scope.ReservationId:N}", StringComparison.Ordinal) ||
            !string.Equals(scope.ExpectedDatabaseName, $"bs_test_{scope.ReservationId:N}", StringComparison.Ordinal) ||
            !ValidServer(scope.ExpectedSqlServer) || scope.CanonicalClusterId != canonicalClusterId ||
            scope.CanonicalSqlServerId != canonicalSqlServerId || scope.RacEndpointMappingId != racMappingId ||
            scope.SqlEndpointMappingId != sqlMappingId ||
            !string.Equals(scope.ExpectedSqlServer, expectedSqlServer, StringComparison.Ordinal))
            throw Error("InfobaseRegistrationObserverScopeMismatch");
        if (credentials is null || !ValidSecret(credentials.ClusterUser) || !ValidSecret(credentials.ClusterPassword))
            throw Error("InfobaseRegistrationObserverCredentialsInvalid");
        if (cancellationToken.IsCancellationRequested) throw Error("InfobaseRegistrationObserverUnavailable");

        long started;
        try { started = clock.GetTimestamp(); }
        catch { throw Error("InfobaseRegistrationObserverUnavailable"); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        void CheckDeadline()
        {
            try
            {
                var elapsed = clock.GetElapsedTime(started);
                if (cancellationToken.IsCancellationRequested || deadline.IsCancellationRequested ||
                    elapsed < TimeSpan.Zero || elapsed >= timeout)
                    throw Error("InfobaseRegistrationObserverUnavailable");
            }
            catch { throw Error("InfobaseRegistrationObserverUnavailable"); }
        }
        CheckDeadline();
        // Canonical cluster is NOT the physical RAC cluster UUID.
        var command = new ProcessCommand(toolId, directory,
            [new("infobase"), new("info"), new("--cluster=" + physicalClusterId.ToString("D")),
             new("--infobase=" + scope.InfobaseId.ToString("D")),
             new("--cluster-user=" + credentials.ClusterUser, Sensitive: true),
             new("--cluster-pwd=" + credentials.ClusterPassword, Sensitive: true), new(endpoint)],
            timeout, [credentials.ClusterUser, credentials.ClusterPassword]);
        var output = new StringBuilder();
        var gate = new object();
        var stderr = false;
        var captureFailed = false;
        var closed = false;
        Task Capture(ProcessOutput item)
        {
            lock (gate)
            {
                if (closed || item is null || item.Text is null ||
                    item.Stream is not (ProcessOutputStream.StandardOutput or ProcessOutputStream.StandardError))
                {
                    captureFailed = true;
                    throw new InvalidOperationException("invalid_process_output");
                }
                if (item.Stream == ProcessOutputStream.StandardError) stderr |= item.Text.Length != 0;
                else if (item.Text.Length > InfobaseRegistrationReadback.MaxOutputCharacters - output.Length)
                {
                    captureFailed = true;
                    throw new InvalidOperationException("invalid_process_output");
                }
                else output.Append(item.Text);
            }
            return Task.CompletedTask;
        }
        ProcessResult result;
        try { result = await runner.RunAsync(command, Capture, deadline.Token).ConfigureAwait(false); }
        catch
        {
            lock (gate) closed = true;
            throw Error("InfobaseRegistrationObserverUnavailable");
        }
        string stdout;
        lock (gate)
        {
            closed = true;
            if (captureFailed || stderr || result is null || result.Completion != ProcessCompletion.Exited ||
                result.ProcessId is not > 0 || result.ExitCode != 0 || result.OutputTruncated)
                throw Error("InfobaseRegistrationObserverUnavailable");
            CheckDeadline();
            stdout = output.ToString();
        }
        try
        {
            var parsed = InfobaseRegistrationReadback.Parse(scope, 0, false, stdout, outputProfileId);
            lock (gate)
            {
                CheckDeadline();
                if (captureFailed) throw Error("InfobaseRegistrationObserverUnavailable");
                return parsed;
            }
        }
        catch (InfobaseRegistrationObserverException) { throw; }
        catch { throw Error("InfobaseRegistrationObserverUnavailable"); }
    }

    private static bool ValidSecret(string? value) => value is { Length: >= 1 and <= 4096 } &&
        !value.Any(char.IsControl);
    private static bool ValidEndpoint(string? value) => value is { Length: >= 1 and <= 255 } &&
        value[0] != '-' && !value.Any(char.IsWhiteSpace) && !value.Any(char.IsControl);
    private static bool ValidServer(string? value) => value is { Length: >= 1 and <= 255 } &&
        value[0] != '-' && value.All(c => c is >= '!' and <= '~');
    private static InfobaseRegistrationObserverException Error(string code) => new(code);
}
