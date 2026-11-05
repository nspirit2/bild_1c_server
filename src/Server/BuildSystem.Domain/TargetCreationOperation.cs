namespace BuildSystem.Domain;

/// <summary>
/// Immutable server-side creation pins, not observations of an existing target or proof of ownership.
/// Canonical authority IDs must be supplied by trusted configuration; no alias resolution occurs here.
/// OperationId identifies this new creation operation, independently of RestoreIntentId.
/// </summary>
public sealed record TargetCreationOperationPins
{
    public TargetCreationOperationPins(Guid operationId, Guid restoreIntentId, Guid environmentId,
        Guid stageJobId, Guid reservationId, Guid artifactId, Guid agentId, Guid targetIncarnationId,
        Guid canonicalClusterId, Guid canonicalSqlServerId)
    {
        Guard.Id(operationId);
        Guard.Id(restoreIntentId);
        Guard.Id(environmentId);
        Guard.Id(stageJobId);
        Guard.Id(reservationId);
        Guard.Id(artifactId);
        Guard.Id(agentId);
        Guard.Id(targetIncarnationId);
        Guard.Id(canonicalClusterId);
        Guard.Id(canonicalSqlServerId);
        OperationId = operationId;
        RestoreIntentId = restoreIntentId;
        EnvironmentId = environmentId;
        StageJobId = stageJobId;
        ReservationId = reservationId;
        ArtifactId = artifactId;
        AgentId = agentId;
        TargetIncarnationId = targetIncarnationId;
        CanonicalClusterId = canonicalClusterId;
        CanonicalSqlServerId = canonicalSqlServerId;
    }

    public Guid OperationId { get; }
    public Guid RestoreIntentId { get; }
    public Guid EnvironmentId { get; }
    public Guid StageJobId { get; }
    public Guid ReservationId { get; }
    public Guid ArtifactId { get; }
    public Guid AgentId { get; }
    public Guid TargetIncarnationId { get; }
    public Guid CanonicalClusterId { get; }
    public Guid CanonicalSqlServerId { get; }
    public string InfobaseName => "bs_test_" + ReservationId.ToString("N");
    public string SqlDatabaseName => "bs_test_" + ReservationId.ToString("N");

    public override string ToString() => "TargetCreationOperationPins [details omitted]";
}

public enum TargetCreationOperationState { Registered, AttemptRecorded, ReconciliationRequired }
public enum TargetCreationQuarantineReason { OutcomeUnknown, Cancelled, AuthorityRejected, TargetConflict }

/// <summary>
/// Pure server-domain model of a one-attempt barrier, NEVER an execution grant. AttemptRecorded
/// records metadata only: it does not imply any physical side effect has started or completed.
/// The caller must persist pins, uniqueness constraints and revision CAS transactionally before
/// claiming cross-agent exclusion or durability. This object alone provides neither guarantee.
/// No stage fence, clock, lease, ownership, success or release authority is represented here.
/// </summary>
public sealed record TargetCreationOperation
{
    private TargetCreationOperation(TargetCreationOperationPins pins, TargetCreationOperationState state,
        long revision, Guid? attemptId, long? attemptExpectedRevision,
        TargetCreationQuarantineReason? quarantineReason)
    {
        Pins = pins with { };
        State = state;
        Revision = revision;
        AttemptId = attemptId;
        AttemptExpectedRevision = attemptExpectedRevision;
        QuarantineReason = quarantineReason;
    }

    public TargetCreationOperationPins Pins { get; }
    public TargetCreationOperationState State { get; }
    public long Revision { get; }
    public Guid? AttemptId { get; }
    public long? AttemptExpectedRevision { get; }
    public TargetCreationQuarantineReason? QuarantineReason { get; }
    public bool Executable => false;
    public bool ScheduledJobsMustRemainBlocked => true;

    public static TargetCreationOperation Register(TargetCreationOperationPins pins)
    {
        Guard.Require(pins is not null, "InvalidPins", "Creation pins are required.");
        return new(pins!, TargetCreationOperationState.Registered, 1, null, null, null);
    }

    /// <summary>
    /// Records the sole attempt. The same attempt with its original expected revision replays
    /// even after quarantine, preserving terminal state. NewlyRecorded never authorizes external mutation.
    /// </summary>
    public TargetCreationOperationAttemptReceipt BeginAttempt(long expectedRevision, Guid attemptId)
    {
        Guard.Require(expectedRevision > 0, "InvalidRevision", "Revision must be positive.");
        Guard.Id(attemptId);
        if (AttemptId == attemptId && AttemptExpectedRevision == expectedRevision)
            return new(this, false);

        Guard.Require(expectedRevision == Revision, "RevisionConflict", "Creation revision conflict.");
        Guard.Require(State == TargetCreationOperationState.Registered,
            "InvalidTransition", "Creation transition is not allowed.");
        return new(new(Pins, TargetCreationOperationState.AttemptRecorded, Revision + 1,
            attemptId, expectedRevision, null), true);
    }

    /// <summary>Terminal quarantine retains all pins and any recorded attempt; repeated Quarantine calls fail.</summary>
    public TargetCreationOperation Quarantine(long expectedCurrentRevision, TargetCreationQuarantineReason reason)
    {
        Guard.Require(expectedCurrentRevision > 0, "InvalidRevision", "Revision must be positive.");
        Guard.Require(Enum.IsDefined(reason), "InvalidReason", "Unsupported quarantine reason.");
        Guard.Require(expectedCurrentRevision == Revision, "RevisionConflict", "Creation revision conflict.");
        Guard.Require(State is TargetCreationOperationState.Registered or TargetCreationOperationState.AttemptRecorded,
            "InvalidTransition", "Creation transition is not allowed.");
        return new(Pins, TargetCreationOperationState.ReconciliationRequired, Revision + 1,
            AttemptId, AttemptExpectedRevision, reason);
    }

    public override string ToString() => "TargetCreationOperation [not execution authorization; details omitted]";
}

/// <summary>A domain transition receipt only, not persistence evidence or permission to execute.</summary>
public sealed record TargetCreationOperationAttemptReceipt
{
    internal TargetCreationOperationAttemptReceipt(TargetCreationOperation newState, bool newlyRecorded)
    {
        NewState = newState;
        NewlyRecorded = newlyRecorded;
    }

    public TargetCreationOperation NewState { get; }
    public bool NewlyRecorded { get; }

    public override string ToString() => "TargetCreationOperationAttemptReceipt [not execution authorization; details omitted]";
}
