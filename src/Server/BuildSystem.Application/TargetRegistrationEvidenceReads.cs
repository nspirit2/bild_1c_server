namespace BuildSystem.Application;

/// <summary>
/// Stored historical observations and immutable issuance pins only. Reported endpoint mappings
/// are labels, not attested creation endpoints; no value establishes ownership or authority.
/// </summary>
public sealed record TargetRegistrationEvidenceHistory(Guid ClaimId, Guid OperationId, Guid AgentId,
    Guid AttemptId, Guid PermitId, Guid CredentialId, Guid CreationEndpointMappingId, Guid RestoreIntentId,
    long FencingToken, Guid TargetIncarnationId, Guid ReservationId, Guid CanonicalClusterId,
    Guid CanonicalSqlServerId, Guid ReportedInfobaseId, Guid RacObservationId, Guid SqlObservationId,
    Guid RacEndpointMappingId, Guid SqlEndpointMappingId, string RegistrationProfileId, string InfobaseName,
    string SqlServer, string DatabaseName, int SqlDatabaseId, string SqlCreateDate, string SqlState,
    DateTimeOffset AcceptedAt)
{
    public bool Executable => false;
    public bool OwnershipEstablished => false;
    public bool RestoreAllowed => false;
    public bool RetryAllowed => false;
    public bool FreshAuthority => false;
    public bool RequiresIndependentVerification => true;
    public bool ScheduledJobsMustRemainBlocked => true;
    public bool ReportedEndpointMappingsAreAttested => false;
    public override string ToString() => "TargetRegistrationEvidenceHistory [NONEXECUTABLE; historical evidence; details redacted]";
}

/// <summary>Absence or presence of stored evidence conveys no execution or physical outcome authority.</summary>
public sealed record TargetRegistrationEvidenceView(Guid OperationId, bool EvidenceRecorded,
    TargetRegistrationEvidenceHistory? HistoricalEvidence)
{
    public bool Executable => false;
    public bool OwnershipEstablished => false;
    public bool RestoreAllowed => false;
    public bool RetryAllowed => false;
    public bool FreshAuthority => false;
    public bool RequiresIndependentVerification => true;
    public bool ScheduledJobsMustRemainBlocked => true;
    public bool ReportedEndpointMappingsAreAttested => false;
    public override string ToString() => "TargetRegistrationEvidenceView [NONEXECUTABLE; independent verification required; details redacted]";
}

/// <summary>
/// Current-administrator, read-only historical diagnostics, independent of current agent,
/// credential or admission state. Does not renew or reconstruct any authority.
/// </summary>
public interface ITargetRegistrationEvidenceReads
{
    Task<TargetRegistrationEvidenceView> GetAsync(Actor actor, Guid operationId, CancellationToken ct);
}
