using BuildSystem.Domain;

namespace BuildSystem.Application;

/// <summary>Separate default-off switch for the internal, nonexecuting permit ledger.</summary>
public sealed class TargetCreationPermitOptions
{
    public bool Enabled { get; init; }
    public override string ToString() => "TargetCreationPermitOptions [redacted]";
}

/// <summary>Durable metadata only, never a grant to execute or cancel a target mutation.</summary>
public sealed record TargetCreationPermitReceipt(Guid PermitId, Guid OperationId, Guid AttemptId,
    long ExpectedOperationRevision, DateTimeOffset IssuedAt, DateTimeOffset StartBefore,
    TargetCreationPermitState State, long Revision, DateTimeOffset? StartedAt,
    long? StartExpectedRevision, bool NewlyRecorded, TargetCreationOperationReceipt Operation)
{
    public bool Executable => false;
    public bool ScheduledJobsMustRemainBlocked => true;
    public override string ToString() => "TargetCreationPermitReceipt [NONEXECUTABLE]";
}

/// <summary>
/// Internal current-administrator metadata barrier only. RecordStart is NOT assigned-agent
/// authentication, worker delivery, execution authorization or evidence of physical execution.
/// Trusted callers supply server-generated permit and attempt IDs. No HTTP adapter is provided.
/// </summary>
public interface ITargetCreationPermits
{
    Task<TargetCreationPermitReceipt> IssueAsync(Actor actor, Guid operationId, long expectedOperationRevision,
        Guid permitId, Guid attemptId, CancellationToken ct);
    Task<TargetCreationPermitReceipt> RecordStartAsync(Actor actor, Guid permitId, long expectedPermitRevision,
        Guid attemptId, CancellationToken ct);
    Task<TargetCreationPermitReceipt> RevokeAsync(Actor actor, Guid permitId, long expectedPermitRevision, CancellationToken ct);
    Task<TargetCreationPermitReceipt> GetAsync(Actor actor, Guid permitId, CancellationToken ct);
}
