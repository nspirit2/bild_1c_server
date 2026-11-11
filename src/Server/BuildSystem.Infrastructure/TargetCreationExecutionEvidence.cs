using System.Data;
using BuildSystem.Application;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Read-only historical issuance evidence; never reconstructs or renews a grant.</summary>
public sealed class SqlTargetCreationExecutionEvidenceReads(BuildSystemDbContext db,
    TargetCreationOperationOptions options) : ITargetCreationExecutionEvidenceReads
{
    public Task<TargetCreationExecutionEvidenceView> GetAsync(Actor actor, Guid operationId, CancellationToken ct)
    {
        if (!options.Enabled) throw Reject(409, "TargetCreationDisabled");
        if (operationId == Guid.Empty) throw Reject(400, "InvalidTargetCreation");
        return db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var ledger = new SqlTargetCreationOperations(db, options);
            await ledger.Lock("BuildSystem:agent-control", ct);
            await ledger.Admin(actor, ct); // Current DB role before even operation existence is disclosed.
            var operation = await db.TargetCreationOperations.AsNoTracking()
                .SingleOrDefaultAsync(x => x.OperationId == operationId, ct)
                ?? throw Reject(404, "TargetCreationNotFound");
            var intent = await db.RestoreIntents.AsNoTracking()
                .SingleOrDefaultAsync(x => x.RestoreIntentId == operation.RestoreIntentId, ct)
                ?? throw Reject(409, "TargetCreationMismatch");
            _ = SqlTargetCreationOperations.Receipt(operation, intent, false);
            // No Admission/current credential check: expiry/revocation must not erase historical evidence.
            var claim = await db.TargetCreationExecutionClaims.AsNoTracking()
                .Where(x => x.OperationId == operationId)
                .Select(x => new TargetCreationExecutionHistoricalClaim(x.ClaimId, x.PermitId, x.AttemptId,
                    x.AgentId, x.CredentialId, x.EndpointMappingId, x.IssuedAt, x.ExpiresAt))
                .SingleOrDefaultAsync(ct);
            if (claim is not null &&
                (new[] { claim.ClaimId, claim.PermitId, claim.AttemptId, claim.AgentId,
                    claim.CredentialId, claim.EndpointMappingId }.Any(x => x == Guid.Empty) ||
                 claim.AttemptId != operation.AttemptId || claim.AgentId != intent.AgentId ||
                 claim.IssuedAt.Offset != TimeSpan.Zero || claim.ExpiresAt.Offset != TimeSpan.Zero ||
                 claim.ExpiresAt <= claim.IssuedAt || claim.ExpiresAt - claim.IssuedAt > TimeSpan.FromMinutes(5)))
                throw Reject(409, "TargetCreationMismatch");
            var result = new TargetCreationExecutionEvidenceView(operationId, claim is not null, claim);
            // Read-only transaction: no lifecycle updates, audit append, credential envelope or target IO.
            await tx.CommitAsync(ct);
            return result;
        });
    }

    private static RequestRejectedException Reject(int status, string code) => new(status, code);
}