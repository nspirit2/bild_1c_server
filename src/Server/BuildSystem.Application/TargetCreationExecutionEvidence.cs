namespace BuildSystem.Application;

/// <summary>
/// Inert historical issuance metadata, NOT a replayable grant. Deliberately omits OperationId,
/// revision and authority flags so this record is not the fresh execution-claim receipt shape.
/// Issuance does not establish whether any physical invocation started, succeeded or failed.
/// </summary>
public sealed record TargetCreationExecutionHistoricalClaim(Guid ClaimId, Guid PermitId, Guid AttemptId,
    Guid AgentId, Guid CredentialId, Guid EndpointMappingId, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "TargetCreationExecutionHistoricalClaim [inert historical metadata]";
}

/// <summary>
/// Administrator diagnostics only, including expired or revoked historical bindings. Neither
/// presence nor absence of a claim permits CREATE, retry, release or scheduled-job enablement.
/// A lost acknowledgement requires reconciliation, never grant reconstruction or replay.
/// </summary>
public sealed record TargetCreationExecutionEvidenceView(Guid OperationId, bool ClaimRecorded,
    TargetCreationExecutionHistoricalClaim? HistoricalClaim)
{
    public bool Executable => false;
    public bool FreshAuthority => false;
    public bool RetryAllowed => false;
    public bool ScheduledJobsMustRemainBlocked => true;
    public bool ReconciliationRequired => true;
    public bool PhysicalOutcomeKnown => false;
    public override string ToString() => "TargetCreationExecutionEvidenceView [NONEXECUTABLE; reconciliation required]";
}

/// <summary>
/// Current-administrator, read-only historical diagnostics. No execution grant, credential
/// delivery, admission renewal, physical outcome assertion or automatic retry is provided.
/// </summary>
public interface ITargetCreationExecutionEvidenceReads
{
    Task<TargetCreationExecutionEvidenceView> GetAsync(Actor actor, Guid operationId, CancellationToken ct);
}
