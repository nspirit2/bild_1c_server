namespace BuildSystem.Application;

/// <summary>Separate default-off execution-claim switch; does not enable an HTTP surface.</summary>
public sealed class TargetCreationExecutionClaimOptions
{
    public bool Enabled { get; init; }
    public override string ToString() => "TargetCreationExecutionClaimOptions [redacted]";
}

/// <summary>
/// Ephemeral, one-shot CREATE grant returned ONLY by the fresh committed transaction winner.
/// Never persist this DTO or reconstruct it from a historical row, including after a lost ACK.
/// ExpiresAt is the original permit start deadline, not a renewable execution lease.
/// </summary>
public sealed record TargetCreationExecutionClaimReceipt(Guid ClaimId, Guid PermitId, Guid OperationId,
    Guid AttemptId, Guid AgentId, Guid CredentialId, Guid EndpointMappingId,
    DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt)
{
    public bool NewlyIssued => true;
    public bool Executable => true;
    public bool ScheduledJobsMustRemainBlocked => true;
    public bool RetryAllowed => false;
    public override string ToString() => "TargetCreationExecutionClaimReceipt [one-shot; details omitted]";
}

/// <summary>
/// Trusted server port: current assigned Approved agent/admission and pinned attempt, credential
/// and endpoint mapping must be checked transactionally with permit revision 2 and its original
/// five-minute deadline. Enforce unique permit/operation/attempt claims. Repeats NEVER return a
/// fresh grant, even on lost ACK; no GET can confer authority and no physical automatic retry is allowed.
/// Revocation blocks unissued claims only; it cannot stop an already issued RAC invocation.
/// The cooperative caller must hold the target namespace lock and establish fresh target absence
/// before claiming, retaining the lock through invocation. This port cannot establish those facts.
/// </summary>
public interface ITargetCreationExecutionClaims
{
    Task<TargetCreationExecutionClaimReceipt> ClaimAsync(string token, Guid permitId,
        long expectedPermitRevision, Guid attemptId, Guid credentialId, Guid endpointMappingId,
        CancellationToken ct);
}