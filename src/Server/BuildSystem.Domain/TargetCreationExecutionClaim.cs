namespace BuildSystem.Domain;

/// <summary>
/// Durable issuance evidence only, never a replayable execution grant. Persistence must make
/// ClaimId the primary key and PermitId, OperationId and AttemptId individually unique.
/// No reset/reissue/revocation transition exists: revocation cannot stop an issued invocation.
/// This pure factory checks shape/time only, not current authority, uniqueness or commit success.
/// </summary>
public sealed class TargetCreationExecutionClaim
{
    private TargetCreationExecutionClaim() { } // EF materialization only.

    public Guid ClaimId { get; private set; }
    public Guid PermitId { get; private set; }
    public Guid OperationId { get; private set; }
    public Guid AttemptId { get; private set; }
    public Guid AgentId { get; private set; }
    public Guid CredentialId { get; private set; }
    public Guid EndpointMappingId { get; private set; }
    public DateTimeOffset IssuedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>
    /// Trusted caller supplies server-generated claim ID, persisted permit times and its server clock.
    /// The claim keeps the original exclusive deadline; it never starts another five-minute window.
    /// </summary>
    public static TargetCreationExecutionClaim Issue(Guid claimId, Guid permitId, Guid operationId,
        Guid attemptId, Guid agentId, Guid credentialId, Guid endpointMappingId,
        DateTimeOffset permitIssuedAt, DateTimeOffset permitStartBefore, DateTimeOffset claimedAt)
    {
        foreach (var id in new[] { claimId, permitId, operationId, attemptId, agentId, credentialId, endpointMappingId })
            Guard.Id(id);
        Guard.Require(permitStartBefore - permitIssuedAt == TimeSpan.FromMinutes(5),
            "InvalidPermitWindow", "Claim requires the original five-minute permit window.");
        Guard.Require(claimedAt >= permitIssuedAt,
            "InvalidServerTime", "Claim time precedes permit issuance.");
        Guard.Require(claimedAt < permitStartBefore,
            "PermitExpired", "Permit start deadline has passed.");
        return new()
        {
            ClaimId = claimId, PermitId = permitId, OperationId = operationId, AttemptId = attemptId,
            AgentId = agentId, CredentialId = credentialId, EndpointMappingId = endpointMappingId,
            IssuedAt = claimedAt.ToUniversalTime(), ExpiresAt = permitStartBefore.ToUniversalTime()
        };
    }

    public override string ToString() => "TargetCreationExecutionClaim [historical evidence; details omitted]";
}