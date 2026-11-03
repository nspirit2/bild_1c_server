using BuildSystem.Agent.Execution;

namespace BuildSystem.Agent.Tools;

/// <summary>Caller-supplied correlation pins, not authenticated endpoint identity or authority.</summary>
public sealed record TargetPresenceAssessmentScope(Guid OperationId, long FencingToken,
    Guid TargetIncarnationId, Guid ReservationId, Guid CanonicalClusterId, Guid CanonicalSqlServerId,
    Guid RacEndpointMappingId, Guid SqlEndpointMappingId, Guid RacObservationId, Guid SqlObservationId)
{
    public override string ToString() => "TargetPresenceAssessmentScope [details omitted]";
}

/// <summary>Read-only occupancy observations, never permission or a target lifecycle state.</summary>
public enum TargetPresenceObservation
{
    BothObservedAbsent,
    InfobaseObservedPresent,
    SqlDatabaseObservedPresent,
    BothObservedPresent
}

/// <summary>
/// Minimal correlated observations only. Even both absent proves neither atomicity, freshness,
/// external database target ownership nor continuing absence. Scheduled jobs remain blocked.
/// </summary>
public sealed class TargetPresenceAssessmentResult
{
    internal TargetPresenceAssessmentResult(TargetPresenceAssessmentScope scope, TargetPresenceObservation observation)
        => (Scope, Observation) = (scope, observation);

    public TargetPresenceAssessmentScope Scope { get; }
    public TargetPresenceObservation Observation { get; }
    public bool Executable => false;
    public bool ScheduledJobsMustRemainBlocked => true;
    public override string ToString() => "TargetPresenceAssessmentResult [not authority; details omitted]";
}

/// <summary>Fixed redacted failure without provider, target or endpoint details.</summary>
public sealed class TargetPresenceAssessmentException : Exception
{
    internal TargetPresenceAssessmentException(string code) : base(code) => Code = code;
    public string Code { get; }
    public override string ToString() => $"TargetPresenceAssessmentException: {Code}";
}

/// <summary>
/// Pure bounded composition, with no IO or mutation. Distinct requested observation IDs provide
/// correlation only: they do not authenticate results, prove freshness or prevent same-scope replay.
/// </summary>
public static class TargetPresenceAssessment
{
    public static TargetPresenceAssessmentResult Assess(TargetPresenceAssessmentScope expected,
        InfobaseInventoryReadbackResult rac, SqlDatabaseObservationResult sql)
    {
        if (expected is null || expected.FencingToken <= 0 ||
            expected.OperationId == Guid.Empty || expected.TargetIncarnationId == Guid.Empty ||
            expected.ReservationId == Guid.Empty || expected.CanonicalClusterId == Guid.Empty ||
            expected.CanonicalSqlServerId == Guid.Empty || expected.RacEndpointMappingId == Guid.Empty ||
            expected.SqlEndpointMappingId == Guid.Empty || expected.RacObservationId == Guid.Empty ||
            expected.SqlObservationId == Guid.Empty || expected.RacObservationId == expected.SqlObservationId)
            throw Error("InvalidTargetPresenceScope");
        if (rac is null || sql is null)
            throw Error("TargetPresenceObservationUnavailable");

        var name = $"bs_test_{expected.ReservationId:N}";
        var racScope = new InfobaseInventoryReadbackScope(expected.OperationId, expected.FencingToken,
            expected.TargetIncarnationId, expected.CanonicalClusterId, expected.RacEndpointMappingId,
            expected.ReservationId, expected.RacObservationId, name);
        var sqlScope = new SqlDatabaseObservationScope(expected.OperationId, expected.FencingToken,
            expected.TargetIncarnationId, expected.CanonicalSqlServerId, expected.SqlEndpointMappingId,
            expected.ReservationId, expected.SqlObservationId, name);
        if (rac.Scope != racScope || sql.Scope != sqlScope)
            throw Error("TargetPresenceScopeMismatch");

        var observation = (rac.Observation, sql.Observation) switch
        {
            (InfobaseInventoryObservation.Absent, SqlDatabaseObservation.Absent) => TargetPresenceObservation.BothObservedAbsent,
            (InfobaseInventoryObservation.Present, SqlDatabaseObservation.Absent) => TargetPresenceObservation.InfobaseObservedPresent,
            (InfobaseInventoryObservation.Absent, SqlDatabaseObservation.Present) => TargetPresenceObservation.SqlDatabaseObservedPresent,
            (InfobaseInventoryObservation.Present, SqlDatabaseObservation.Present) => TargetPresenceObservation.BothObservedPresent,
            _ => throw Error("TargetPresenceObservationUnavailable")
        };
        return new(expected, observation);
    }

    private static TargetPresenceAssessmentException Error(string code) => new(code);
}
