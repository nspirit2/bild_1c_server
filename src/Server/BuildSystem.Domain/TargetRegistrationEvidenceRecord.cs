namespace BuildSystem.Domain;

/// <summary>
/// Immutable historical evidence, bound to one issued claim and server-derived intent pins.
/// No field grants execution, ownership, restore, retry or freshness authority.
/// Persistence rejects updates/deletes and reuse of either observation ID in either role.
/// </summary>
public sealed class TargetRegistrationEvidenceRecord
{
    public Guid ClaimId { get; init; }
    public Guid OperationId { get; init; }
    public Guid AgentId { get; init; }
    public Guid AttemptId { get; init; }
    public Guid PermitId { get; init; }
    public Guid CredentialId { get; init; }
    public Guid CreationEndpointMappingId { get; init; }
    public Guid RestoreIntentId { get; init; }
    public long FencingToken { get; init; }
    public Guid TargetIncarnationId { get; init; }
    public Guid ReservationId { get; init; }
    public Guid CanonicalClusterId { get; init; }
    public Guid CanonicalSqlServerId { get; init; }
    public Guid ReportedInfobaseId { get; init; }
    public Guid RacObservationId { get; init; }
    public Guid SqlObservationId { get; init; }
    public Guid RacEndpointMappingId { get; init; }
    public Guid SqlEndpointMappingId { get; init; }
    public string RegistrationProfileId { get; init; } = "";
    public string InfobaseName { get; init; } = "";
    public string SqlServer { get; init; } = "";
    public string DatabaseName { get; init; } = "";
    public int SqlDatabaseId { get; init; }
    /// <summary>Canonical provider DateTime "O" string; preserves Kind, not a DateTimeOffset.</summary>
    public string SqlCreateDate { get; init; } = "";
    public string SqlState { get; init; } = "";
    public DateTimeOffset AcceptedAt { get; init; }
    public override string ToString() => "TargetRegistrationEvidenceRecord [immutable historical evidence; details redacted]";
}
