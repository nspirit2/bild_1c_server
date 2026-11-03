using System.Diagnostics;
using BuildSystem.Agent.Execution;

namespace BuildSystem.Agent.Tools;

/// <summary>Operator-configured overall observation deadline, snapshotted at construction.</summary>
public sealed class TargetPresenceObserverOptions
{
    public required int TotalTimeoutSeconds { get; init; }
    public override string ToString() => "TargetPresenceObserverOptions [details omitted]";
}

/// <summary>Correlation pins only; no authority, endpoint credentials or caller-chosen observation IDs.</summary>
public sealed record TargetPresenceRequest(Guid OperationId, long FencingToken,
    Guid TargetIncarnationId, Guid ReservationId, Guid CanonicalClusterId, Guid CanonicalSqlServerId,
    Guid RacEndpointMappingId, Guid SqlEndpointMappingId)
{
    public override string ToString() => "TargetPresenceRequest [details omitted]";
}

/// <summary>Fixed redacted failure without inner exception or provider details.</summary>
public sealed class TargetPresenceObservationException : Exception
{
    internal TargetPresenceObservationException(string code) : base(code) => Code = code;
    public string Code { get; }
    public override string ToString() => $"TargetPresenceObservationException: {Code}";
}

/// <summary>
/// Read-only sequential RAC then SQL observation. Neither result grants permission to create,
/// restore or schedule work. The deadline is checked with a monotonic clock after each awaited
/// adapter (and before returning), but is not a hard preemptive bound when an adapter or provider
/// ignores cancellation, including during cleanup; no detached adapter tasks are launched here.
/// </summary>
public sealed class TargetPresenceObserver
{
    private readonly InfobaseInventoryObserver rac;
    private readonly SqlDatabaseObserver sql;
    private readonly TimeSpan totalTimeout;

    public TargetPresenceObserver(InfobaseInventoryObserver rac, SqlDatabaseObserver sql,
        TargetPresenceObserverOptions options)
    {
        this.rac = rac ?? throw new ArgumentNullException(nameof(rac));
        this.sql = sql ?? throw new ArgumentNullException(nameof(sql));
        ArgumentNullException.ThrowIfNull(options);
        if (options.TotalTimeoutSeconds is < 1 or > 120)
            throw new ArgumentException("invalid_target_presence_observer_options", nameof(options));
        totalTimeout = TimeSpan.FromSeconds(options.TotalTimeoutSeconds);
    }

    public async Task<TargetPresenceAssessmentResult> ObserveAsync(TargetPresenceRequest request,
        InfobaseInventoryObserverCredentials credentials, CancellationToken cancellationToken = default)
    {
        // Reject every invocation pin before either adapter can perform IO.
        if (request is null || request.FencingToken <= 0 || request.OperationId == Guid.Empty ||
            request.TargetIncarnationId == Guid.Empty || request.ReservationId == Guid.Empty ||
            request.CanonicalClusterId == Guid.Empty || request.CanonicalSqlServerId == Guid.Empty ||
            request.RacEndpointMappingId == Guid.Empty || request.SqlEndpointMappingId == Guid.Empty)
            throw new TargetPresenceObservationException("InvalidTargetPresenceRequest");

        var startedAt = Stopwatch.GetTimestamp();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(totalTimeout);
        void CheckDeadline()
        {
            if (linked.IsCancellationRequested || Stopwatch.GetElapsedTime(startedAt) >= totalTimeout)
                throw new TargetPresenceObservationException("TargetPresenceObservationUnavailable");
        }

        try
        {
            CheckDeadline();
            var racObservationId = Guid.NewGuid();
            var sqlObservationId = Guid.NewGuid();
            // The expected name and observation IDs originate exclusively here, anew for each call.
            var name = $"bs_test_{request.ReservationId:N}";
            var expected = new TargetPresenceAssessmentScope(request.OperationId, request.FencingToken,
                request.TargetIncarnationId, request.ReservationId, request.CanonicalClusterId,
                request.CanonicalSqlServerId, request.RacEndpointMappingId, request.SqlEndpointMappingId,
                racObservationId, sqlObservationId);
            var racScope = new InfobaseInventoryReadbackScope(request.OperationId, request.FencingToken,
                request.TargetIncarnationId, request.CanonicalClusterId, request.RacEndpointMappingId,
                request.ReservationId, racObservationId, name);
            var sqlScope = new SqlDatabaseObservationScope(request.OperationId, request.FencingToken,
                request.TargetIncarnationId, request.CanonicalSqlServerId, request.SqlEndpointMappingId,
                request.ReservationId, sqlObservationId, name);

            var racResult = await rac.ObserveAsync(racScope, credentials, linked.Token).ConfigureAwait(false);
            CheckDeadline();
            var sqlResult = await sql.ObserveAsync(sqlScope, linked.Token).ConfigureAwait(false);
            CheckDeadline();
            var assessment = TargetPresenceAssessment.Assess(expected, racResult, sqlResult);
            CheckDeadline();
            return assessment;
        }
        catch
        {
            // Adapter/provider diagnostics, credentials and partially completed observations never escape.
            throw new TargetPresenceObservationException("TargetPresenceObservationUnavailable");
        }
    }
}
