namespace BuildSystem.Contracts;

/// <summary>Server-assigned OBSERVATION-only correlation pins, not implemented server authority,
/// a lease, ownership proof, or permission to execute. Expected names are derived, never caller supplied.</summary>
public sealed record TargetObservationAssignment(
    Guid ObservationRequestId, Guid RestoreIntentId, Guid AgentId, Guid OperationId,
    Guid EnvironmentId, Guid StageJobId, long FencingToken, Guid ReservationId,
    Guid TargetIncarnationId, Guid CanonicalClusterId, Guid CanonicalSqlServerId,
    Guid RacEndpointMappingId, Guid SqlEndpointMappingId)
{
    public string InfobaseName => "bs_test_" + ReservationId.ToString("N");
    public string SqlDatabaseName => "bs_test_" + ReservationId.ToString("N");
    public bool ScheduledJobsMustRemainBlocked => true;
    public bool Executable => false;
    public override string ToString() => "TargetObservationAssignment [observation only; details omitted]";
}

/// <summary>Historical labels for a successfully completed pair of observations, never freshness grants.</summary>
public enum TargetObservationOutcome
{
    BothObservedAbsent = 0,
    InfobaseObservedPresent = 1,
    SqlDatabaseObservedPresent = 2,
    BothObservedPresent = 3
}

/// <summary>A durable historical receipt, not authority, freshness, ownership or permission to mutate.
/// Public DTO construction does not validate trust; the journal validates every supplied pin.</summary>
public sealed record ReportTargetObservation(Guid ReceiptId, TargetObservationAssignment Assignment,
    Guid RacObservationId, Guid SqlObservationId, TargetObservationOutcome Outcome, DateTimeOffset ObservedAt)
{
    public bool Executable => false;
    public bool ScheduledJobsMustRemainBlocked => true;
    public override string ToString() => "ReportTargetObservation [historical observation only; details omitted]";
}