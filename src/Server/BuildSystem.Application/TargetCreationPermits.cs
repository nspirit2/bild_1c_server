using BuildSystem.Domain;

namespace BuildSystem.Application;

/// <summary>Separate default-off switch for the internal, nonexecuting permit ledger.</summary>
public sealed class TargetCreationPermitOptions
{
    public bool Enabled { get; init; }
    /// <summary>Separate default-off opt-in for assigned-agent, nonexecuting start metadata.</summary>
    public bool AgentStartEnabled { get; init; }
    /// <summary>Default-off opt-in for explicit credential binding and bound start metadata; never enables execution.</summary>
    public bool CredentialBindingEnabled { get; init; }
    public override string ToString() => "TargetCreationPermitOptions [redacted]";
}

/// <summary>Durable metadata only, never a grant to execute or cancel a target mutation.</summary>
public sealed record TargetCreationPermitReceipt(Guid PermitId, Guid OperationId, Guid AttemptId,
    long ExpectedOperationRevision, DateTimeOffset IssuedAt, DateTimeOffset StartBefore,
    TargetCreationPermitState State, long Revision, DateTimeOffset? StartedAt,
    long? StartExpectedRevision, bool NewlyRecorded, TargetCreationOperationReceipt Operation)
{
    /// <summary>Null for legacy unbound, permanently nonexecuting metadata. A bound pair is historical, not an active grant.</summary>
    public Guid? CredentialId { get; init; }
    /// <summary>Immutable selected mapping identity, never an endpoint alias or secret.</summary>
    public Guid? EndpointMappingId { get; init; }
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
    /// <summary>Legacy unbound metadata only; never eligible for future execution.</summary>
    Task<TargetCreationPermitReceipt> IssueAsync(Actor actor, Guid operationId, long expectedOperationRevision,
        Guid permitId, Guid attemptId, CancellationToken ct);
    /// <summary>
    /// Pins an explicit stored credential and mapping for this creation attempt. Still metadata only.
    /// Any future executor must require both pins, recheck current credentials and validate actual trusted endpoints.
    /// Revocation after a recorded start cannot undo any physical phase; this ledger makes no such claim.
    /// </summary>
    Task<TargetCreationPermitReceipt> IssueWithCredentialAsync(Actor actor, Guid operationId, long expectedOperationRevision,
        Guid permitId, Guid attemptId, Guid credentialId, Guid endpointMappingId, CancellationToken ct);
    Task<TargetCreationPermitReceipt> RecordStartAsync(Actor actor, Guid permitId, long expectedPermitRevision,
        Guid attemptId, CancellationToken ct);
    Task<TargetCreationPermitReceipt> RevokeAsync(Actor actor, Guid permitId, long expectedPermitRevision, CancellationToken ct);
    Task<TargetCreationPermitReceipt> GetAsync(Actor actor, Guid permitId, CancellationToken ct);
}

/// <summary>
/// Assigned-agent credential barrier for recording historical, nonexecuting start metadata only.
/// A receipt never grants target execution, cancellation, worker delivery or scheduled-job release.
/// This is separate from the administrator permit API; no HTTP adapter is provided here.
/// </summary>
public interface ITargetCreationPermitAgentStarts
{
    Task<TargetCreationPermitReceipt> RecordAgentStartAsync(string token, Guid permitId,
        long expectedPermitRevision, Guid attemptId, CancellationToken ct);
}
