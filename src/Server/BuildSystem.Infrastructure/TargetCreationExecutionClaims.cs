using System.Data;
using BuildSystem.Application;
using BuildSystem.Domain;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>
/// Issues a distinct, durable, one-shot grant; never reconstructs authority from stored evidence.
/// Does not establish physical target absence, own a namespace lock, or invoke a worker/process.
/// </summary>
public sealed class SqlTargetCreationExecutionClaims(BuildSystemDbContext db,
    TargetCreationExecutionClaimOptions options, TargetCreationPermitOptions permits,
    TargetCreationOperationOptions creation, TargetCredentialOptions credentialOptions)
    : ITargetCreationExecutionClaims
{
    private readonly SqlTargetCreationOperations ledger = new(db, creation);

    public async Task<TargetCreationExecutionClaimReceipt> ClaimAsync(string token, Guid permitId,
        long expectedPermitRevision, Guid attemptId, Guid credentialId, Guid endpointMappingId, CancellationToken ct)
    {
        // Every switch and request-shape check precedes SQL, including the independent credential store.
        if (!options.Enabled || !permits.Enabled || !permits.AgentStartEnabled ||
            !permits.CredentialBindingEnabled || !creation.Enabled || !credentialOptions.Enabled)
            throw Reject(409, "TargetCreationExecutionClaimDisabled");
        if (permitId == Guid.Empty || attemptId == Guid.Empty || credentialId == Guid.Empty ||
            endpointMappingId == Guid.Empty || expectedPermitRevision != 2)
            throw Reject(400, "InvalidTargetCreationExecutionClaim");

        var invocations = 0;
        try
        {
            return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                // SQL retry strategies must not reissue authority after an ambiguous commit or cleanup.
                // This check is deliberately before all SQL and tracker work on every invocation.
                if (Interlocked.Increment(ref invocations) != 1) throw ServiceUnavailable();
                TargetCreationExecutionClaim claim;
                db.ChangeTracker.Clear();
                await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct))
                {
                    await ledger.Lock("BuildSystem:agent-control", ct);
                    if (string.IsNullOrEmpty(token) || token.Length > 256)
                        throw Reject(401, "AgentUnauthorized");
                    var hash = TokenProtector.Hash(token);
                    var agent = await db.Agents.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.TokenHash == hash && x.State == "Approved", ct);
                    if (agent is null || !TokenProtector.Verify(token, agent.TokenHash!))
                        throw Reject(401, "AgentUnauthorized");

                    var permit = await db.TargetCreationPermits.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.PermitId == permitId, ct) ?? throw Unavailable();
                    var operation = await db.TargetCreationOperations.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.OperationId == permit.OperationId, ct) ?? throw Unavailable();
                    var intent = await db.RestoreIntents.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.RestoreIntentId == operation.RestoreIntentId, ct) ?? throw Unavailable();
                    if (agent.Id != intent.AgentId) throw Reject(403, "TargetCreationPermitForbidden");
                    await ledger.Admission(intent, ct);
                    _ = SqlTargetCreationPermits.Receipt(permit, operation, intent, false);

                    var now = await db.Database.SqlQuery<DateTimeOffset>(
                        $"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(ct);
                    if (permit.PermitId != permitId || permit.OperationId == Guid.Empty ||
                        permit.OperationId != operation.OperationId || permit.AttemptId == Guid.Empty ||
                        permit.AttemptId != attemptId || permit.ExpectedOperationRevision != 1 ||
                        permit.Executable || !permit.ScheduledJobsMustRemainBlocked ||
                        permit.CredentialId != credentialId || permit.EndpointMappingId != endpointMappingId ||
                        permit.State != TargetCreationPermitState.StartRecorded || permit.Revision != expectedPermitRevision ||
                        permit.StartExpectedRevision != 1 || permit.StartedAt is not { } started ||
                        permit.IssuedAt.Offset != TimeSpan.Zero || permit.StartBefore.Offset != TimeSpan.Zero ||
                        started.Offset != TimeSpan.Zero || now.Offset != TimeSpan.Zero ||
                        permit.IssuedAt > DateTimeOffset.MaxValue.AddMinutes(-5) ||
                        permit.StartBefore != permit.IssuedAt.AddMinutes(5) || now < permit.IssuedAt ||
                        started < permit.IssuedAt || started >= permit.StartBefore || started > now || now >= permit.StartBefore ||
                        operation.State != TargetCreationOperationState.AttemptRecorded || operation.Revision != 2 ||
                        operation.AttemptId != attemptId || operation.AttemptExpectedRevision != 1)
                        throw Unavailable();

                    // Metadata only: no encrypted envelope is selected, decrypted or transported.
                    var credential = await db.TargetCredentials.AsNoTracking().Where(x => x.CredentialId == credentialId)
                        .Select(x => new { x.CredentialId, x.CanonicalClusterId, x.CanonicalSqlServerId,
                            x.EndpointMappingId, x.State, x.Revision, x.CreatedAt, x.RevokedAt })
                        .SingleOrDefaultAsync(ct);
                    if (credential is null || credential.CredentialId != credentialId ||
                        credential.CanonicalClusterId == Guid.Empty || credential.CanonicalSqlServerId == Guid.Empty ||
                        credential.EndpointMappingId == Guid.Empty || credential.EndpointMappingId != endpointMappingId ||
                        credential.CanonicalClusterId != intent.CanonicalClusterId ||
                        credential.CanonicalSqlServerId != intent.CanonicalSqlServerId ||
                        credential.State != TargetCredentialState.Stored || credential.Revision != 1 ||
                        credential.RevokedAt is not null || credential.CreatedAt.Offset != TimeSpan.Zero ||
                        credential.CreatedAt > now)
                        throw Reject(409, "TargetCreationCredentialUnavailable");

                    // Check all unique keys only after current authorization/admission and metadata checks.
                    // Existing evidence never becomes a DTO, including after a response/commit ACK is lost.
                    if (await db.TargetCreationExecutionClaims.AsNoTracking().AnyAsync(x =>
                        x.PermitId == permitId || x.OperationId == operation.OperationId || x.AttemptId == attemptId, ct))
                        throw Reject(409, "TargetCreationExecutionClaimAlreadyIssued");

                    claim = TargetCreationExecutionClaim.Issue(Guid.NewGuid(), permitId, operation.OperationId,
                        attemptId, agent.Id, credentialId, endpointMappingId, permit.IssuedAt, permit.StartBefore, now);
                    db.TargetCreationExecutionClaims.Add(claim);
                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                }
                // Disposal must succeed before even constructing the fresh executable receipt.
                return new TargetCreationExecutionClaimReceipt(claim.ClaimId, claim.PermitId, claim.OperationId,
                    claim.AttemptId, claim.AgentId, claim.CredentialId, claim.EndpointMappingId,
                    claim.IssuedAt, claim.ExpiresAt);
            });
        }
        catch (RequestRejectedException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw ServiceUnavailable(); }
    }

    private static RequestRejectedException Unavailable() => Reject(409, "TargetCreationExecutionClaimUnavailable");
    private static RequestRejectedException ServiceUnavailable() => Reject(503, "TargetCreationExecutionClaimUnavailable");
    private static RequestRejectedException Reject(int status, string code) => new(status, code);
}
