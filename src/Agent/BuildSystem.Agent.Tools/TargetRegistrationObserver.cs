using BuildSystem.Agent.Execution;

namespace BuildSystem.Agent.Tools;

/// <summary>Operator-owned settings, snapshotted at construction; no host wiring.</summary>
public sealed class TargetRegistrationObserverOptions
{
    public bool Enabled { get; init; } = false;
    public int TotalTimeoutSeconds { get; init; }
    public override string ToString() => "TargetRegistrationObserverOptions [details omitted]";
}

/// <summary>Fixed redacted diagnostic, never an adapter or provider exception.</summary>
public sealed class TargetRegistrationObserverException : Exception
{
    internal TargetRegistrationObserverException(string code) : base(code) => Code = code;
    public string Code { get; }
    public override string ToString() => $"TargetRegistrationObserverException: {Code}";
}

/// <summary>
/// Disabled-by-default, evidence-only sequential RAC then SQL coordinator. The caller must supply
/// the expected registration scope and a distinct nonempty SQL observation ID. This does not verify
/// that the scope came from a saved create ACK, generate fresh IDs, prevent replay, establish ownership,
/// or grant authority to restore, write or schedule work. SQL metadata is observed, not owned.
/// Cancellation is cooperative, including adapter cleanup: no detached task or hard-preemption promise.
/// </summary>
public sealed class TargetRegistrationObserver
{
    private readonly InfobaseRegistrationObserver rac;
    private readonly SqlDatabaseObserver sql;
    private readonly TimeProvider clock;
    private readonly bool enabled;
    private readonly TimeSpan totalTimeout;

    public TargetRegistrationObserver(InfobaseRegistrationObserver rac, SqlDatabaseObserver sql,
        TargetRegistrationObserverOptions options, TimeProvider? timeProvider = null)
    {
        this.rac = rac ?? throw Error("TargetRegistrationObserverInvalidOptions");
        this.sql = sql ?? throw Error("TargetRegistrationObserverInvalidOptions");
        if (options is null) throw Error("TargetRegistrationObserverInvalidOptions");
        clock = timeProvider ?? TimeProvider.System;
        enabled = options.Enabled;
        if (!enabled) return; // Disabled settings need no usable timeout, but adapters remain required.
        if (options.TotalTimeoutSeconds is < 1 or > 120)
            throw Error("TargetRegistrationObserverInvalidOptions");
        totalTimeout = TimeSpan.FromSeconds(options.TotalTimeoutSeconds);
    }

    /// <summary>Fresh reads of caller correlation only; supplied IDs do not prove freshness or saved ACK provenance.</summary>
    public async Task<TargetRegistrationAssessmentResult> ObserveExpectedScopeAsync(
        TargetRegistrationAssessmentScope expected, InfobaseRegistrationObserverCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        if (!enabled) throw Error("TargetRegistrationObserverDisabled");
        try { TargetRegistrationAssessment.ValidateScope(expected); }
        catch { throw Error("TargetRegistrationObserverInvalidScope"); }

        // Every clock operation, timer setup, adapter call and assessment is within the redaction boundary.
        try
        {
            var started = clock.GetTimestamp();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(totalTimeout);
            void CheckDeadline()
            {
                var elapsed = clock.GetElapsedTime(started);
                if (cancellationToken.IsCancellationRequested || deadline.IsCancellationRequested ||
                    elapsed < TimeSpan.Zero || elapsed >= totalTimeout)
                    throw Error("TargetRegistrationObserverUnavailable");
            }

            CheckDeadline();
            var scope = expected.RegistrationScope;
            var racResult = await rac.ObserveAsync(scope, credentials, deadline.Token).ConfigureAwait(false);
            CheckDeadline();
            // A trusted adapter may be misconfigured for the synthetic profile. Stop BEFORE SQL IO.
            InfobaseRegistrationReadback.RequireMatch(racResult, scope, expected.ExpectedProfileId);
            var sqlScope = new SqlDatabaseObservationScope(scope.OperationId, scope.FencingToken,
                scope.TargetIncarnationId, scope.CanonicalSqlServerId, scope.SqlEndpointMappingId,
                scope.ReservationId, expected.SqlObservationId, scope.ExpectedDatabaseName);
            CheckDeadline();
            var sqlResult = await sql.ObserveAsync(sqlScope, deadline.Token).ConfigureAwait(false);
            CheckDeadline();
            var result = TargetRegistrationAssessment.Assess(expected, racResult, sqlResult);
            CheckDeadline();
            return result;
        }
        catch { throw Error("TargetRegistrationObserverUnavailable"); }
    }

    private static TargetRegistrationObserverException Error(string code) => new(code);
}
