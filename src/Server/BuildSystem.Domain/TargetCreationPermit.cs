namespace BuildSystem.Domain;

public enum TargetCreationPermitState { Prepared, StartRecorded, Revoked }

/// <summary>
/// Pure, nonissuing metadata for the approved five-minute server-clock start deadline; NEVER
/// execution authorization. The caller must perform current online policy/authority checks and
/// transactionally persist operation/permit revision CAS before any external action. This model
/// supplies no offline permission, durability, exclusion, stage-fence authority or cancellation.
/// Expiry only rejects a first start: it neither cancels a running attempt nor renews a deadline.
/// Server-generated permit and attempt IDs must be supplied by the trusted caller.
/// </summary>
public sealed record TargetCreationPermit
{
    private TargetCreationPermit(Guid permitId, TargetCreationOperationPins pins, Guid attemptId,
        long expectedOperationRevision, DateTimeOffset issuedAt, DateTimeOffset startBefore,
        TargetCreationPermitState state, long revision, DateTimeOffset? startedAt,
        long? startExpectedRevision)
    {
        PermitId = permitId;
        Pins = pins with { };
        AttemptId = attemptId;
        ExpectedOperationRevision = expectedOperationRevision;
        IssuedAt = issuedAt;
        StartBefore = startBefore;
        State = state;
        Revision = revision;
        StartedAt = startedAt;
        StartExpectedRevision = startExpectedRevision;
    }

    public Guid PermitId { get; }
    public TargetCreationOperationPins Pins { get; }
    public Guid AttemptId { get; }
    public long ExpectedOperationRevision { get; }
    public DateTimeOffset IssuedAt { get; }
    public DateTimeOffset StartBefore { get; }
    public TargetCreationPermitState State { get; }
    public long Revision { get; }
    public DateTimeOffset? StartedAt { get; }
    public long? StartExpectedRevision { get; }
    public bool HasRecordedStart => StartedAt.HasValue;
    public bool Executable => false;
    public bool ScheduledJobsMustRemainBlocked => true;

    /// <summary>Prepares metadata only for a registered revision-one operation with no attempt.</summary>
    public static TargetCreationPermit Prepare(Guid permitId, TargetCreationOperation operation,
        Guid attemptId, DateTimeOffset serverNow)
    {
        Guard.Id(permitId);
        Guard.Require(operation is not null, "InvalidOperation", "Creation operation is required.");
        Guard.Id(attemptId);
        Guard.Require(operation!.State == TargetCreationOperationState.Registered &&
            operation.Revision == 1 && operation.AttemptId is null &&
            operation.AttemptExpectedRevision is null,
            "InvalidTransition", "Permit preparation requires an unattempted registered operation.");
        var issuedAt = serverNow.ToUniversalTime();
        Guard.Require(issuedAt <= DateTimeOffset.MaxValue.Subtract(TimeSpan.FromMinutes(5)),
            "InvalidServerTime", "Server time cannot represent the start deadline.");
        return new(permitId, operation.Pins, attemptId, operation.Revision, issuedAt,
            issuedAt.AddMinutes(5), TargetCreationPermitState.Prepared, 1, null, null);
    }

    /// <summary>
    /// Records only the pinned attempt, not permission to execute. A caller must still perform
    /// current online authorization; a replay is never a fresh authorization. Only the exact
    /// original attempt/revision pair replays, including after expiry or revocation, and only
    /// when a start was actually recorded. Server time is validated even for a replay.
    /// </summary>
    public TargetCreationPermitStartReceipt RecordStart(long expectedPermitRevision, Guid attemptId,
        DateTimeOffset serverNow)
    {
        Guard.Require(expectedPermitRevision > 0, "InvalidRevision", "Revision must be positive.");
        Guard.Id(attemptId);
        ValidateServerTime(serverNow);
        Guard.Require(attemptId == AttemptId, "AttemptMismatch", "Permit attempt does not match.");
        if (HasRecordedStart && StartExpectedRevision == expectedPermitRevision)
            return new(this, false);

        Guard.Require(expectedPermitRevision == Revision, "RevisionConflict", "Permit revision conflict.");
        Guard.Require(State == TargetCreationPermitState.Prepared,
            "InvalidTransition", "Permit transition is not allowed.");
        Guard.Require(serverNow < StartBefore, "PermitExpired", "Permit start deadline has passed.");
        return new(new(PermitId, Pins, AttemptId, ExpectedOperationRevision, IssuedAt, StartBefore,
            TargetCreationPermitState.StartRecorded, Revision + 1, serverNow.ToUniversalTime(),
            expectedPermitRevision), true);
    }

    /// <summary>
    /// Terminal metadata revocation, not cancellation of a running attempt. Retains the original
    /// deadline and recorded start; even an exact repeated revocation fails revision/transition checks.
    /// </summary>
    public TargetCreationPermit Revoke(long expectedCurrentRevision, DateTimeOffset serverNow)
    {
        Guard.Require(expectedCurrentRevision > 0, "InvalidRevision", "Revision must be positive.");
        ValidateServerTime(serverNow);
        Guard.Require(expectedCurrentRevision == Revision, "RevisionConflict", "Permit revision conflict.");
        Guard.Require(State is TargetCreationPermitState.Prepared or TargetCreationPermitState.StartRecorded,
            "InvalidTransition", "Permit transition is not allowed.");
        return new(PermitId, Pins, AttemptId, ExpectedOperationRevision, IssuedAt, StartBefore,
            TargetCreationPermitState.Revoked, Revision + 1, StartedAt, StartExpectedRevision);
    }

    private void ValidateServerTime(DateTimeOffset serverNow) =>
        Guard.Require(serverNow >= IssuedAt, "InvalidServerTime", "Server time precedes permit issuance.");

    public override string ToString() => "TargetCreationPermit [not execution authorization; details omitted]";
}

/// <summary>A nonexecutable domain transition receipt, never persistence evidence or authorization.</summary>
public sealed record TargetCreationPermitStartReceipt
{
    internal TargetCreationPermitStartReceipt(TargetCreationPermit newState, bool newlyRecorded)
    {
        NewState = newState;
        NewlyRecorded = newlyRecorded;
    }

    public TargetCreationPermit NewState { get; }
    public bool NewlyRecorded { get; }
    public bool Executable => false;
    public bool ScheduledJobsMustRemainBlocked => true;

    public override string ToString() => "TargetCreationPermitStartReceipt [not execution authorization; details omitted]";
}
