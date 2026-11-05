using BuildSystem.Domain;

namespace BuildSystem.Application;

/// <summary>Opt-in internal ledger only. Configured canonical identities are assertions, never discovery.</summary>
public sealed class TargetCreationOperationOptions
{
    public bool Enabled { get; init; }
    public Dictionary<Guid, RestoreIntentCanonicalMapping> CanonicalMappings { get; init; } = [];
    public override string ToString() => "TargetCreationOperationOptions [redacted]";
}

/// <summary>Durable metadata, never an execution grant, lease, ownership proof or release authorization.</summary>
public sealed record TargetCreationOperationReceipt(Guid OperationId, Guid RestoreIntentId, Guid EnvironmentId,
    Guid StageJobId, Guid ReservationId, Guid ArtifactId, Guid AgentId, Guid TargetIncarnationId,
    Guid CanonicalClusterId, Guid CanonicalSqlServerId, string InfobaseName, string SqlDbName,
    TargetCreationOperationState State, long Revision, Guid? AttemptId, long? AttemptExpectedRevision,
    TargetCreationQuarantineReason? QuarantineReason, bool NewlyRecorded)
{
    public bool Executable => false;
    public bool ScheduledJobsMustRemainBlocked => true;
    public override string ToString() => "TargetCreationOperationReceipt [NONEXECUTABLE]";
}

/// <summary>Admin-only internal one-attempt barrier. No API, worker delivery or physical side effects.</summary>
public interface ITargetCreationOperations
{
    Task<TargetCreationOperationReceipt> RegisterAsync(Actor actor, Guid environmentId, Guid expectedRestoreIntentId, CancellationToken ct);
    Task<TargetCreationOperationReceipt> BeginAttemptAsync(Actor actor, Guid operationId, long expectedRevision, Guid attemptId, CancellationToken ct);
    Task<TargetCreationOperationReceipt> QuarantineAsync(Actor actor, Guid operationId, long expectedRevision, TargetCreationQuarantineReason reason, CancellationToken ct);
}
// end