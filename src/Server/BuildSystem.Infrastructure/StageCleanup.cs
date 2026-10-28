using System.Data;
using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>
/// Issues one immutable non-expiring authority for one pinned artifact. Does not execute deletion,
/// dispatch a worker, mark cleanup completed, sweep expiry, settle provisioning or release quota.
/// Credential revocation cannot undo a previously delivered grant; future claim/replay revalidates it.
/// </summary>
public sealed class SqlStageCleanup(BuildSystemDbContext db, StageCleanupOptions options) : IStageCleanup
{
    public Task<StageCleanupAuthorization> ClaimAsync(string credential, ClaimStageCleanup request, CancellationToken ct)
    {
        if (!options.Enabled) throw new RequestRejectedException(409, "CleanupDisabled");
        if (request is null || request.ExpectedRetirementId == Guid.Empty || request.ExpectedArtifactId == Guid.Empty)
            throw new RequestRejectedException(400, "InvalidCleanupClaim");
        return db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            // The shared inspection owns global->root->pool locks and all token/binding/snapshot rechecks,
            // but no nested transaction, expiry sweep or writes. Only the oldest matching intent is claimable.
            var plan = await new SqlStageRetirementPlans(db, new StageRetirementOptions { Enabled = true }).InspectAsync(credential, ct);
            if (plan is null || plan.RetirementId != request.ExpectedRetirementId || plan.ArtifactId != request.ExpectedArtifactId)
                throw Mismatch();
            var intent = await db.StageRetirementIntents.AsNoTracking().SingleAsync(x => x.RetirementId == plan.RetirementId, ct);
            var prior = await db.StageCleanupAuthorizations.SingleOrDefaultAsync(x => x.RetirementId == plan.RetirementId, ct);
            if (prior is not null)
            {
                StageRetirementPlan? stored;
                try { stored = JsonSerializer.Deserialize<StageRetirementPlan>(prior.PlanJson); }
                catch (JsonException) { throw Mismatch(); }
                if (prior.AuthorizationId == Guid.Empty || prior.ArtifactId != plan.ArtifactId ||
                    prior.AgentId != intent.AgentId || prior.StageFencingToken != plan.FencingToken || stored != plan)
                    throw Mismatch();
                await tx.CommitAsync(ct);
                return new StageCleanupAuthorization(prior.AuthorizationId, plan, prior.AuthorizedAt);
            }
            var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(ct);
            var row = new StageCleanupAuthorizationRecord { AuthorizationId = Guid.NewGuid(), RetirementId = plan.RetirementId,
                ArtifactId = plan.ArtifactId, AgentId = intent.AgentId, StageFencingToken = plan.FencingToken,
                AuthorizedAt = now, PlanJson = JsonSerializer.Serialize(plan) };
            db.StageCleanupAuthorizations.Add(row);
            db.AuditEvents.Add(new AuditEvent { Id = Guid.NewGuid(), ActorId = intent.AgentId,
                Action = "StageCleanupAuthorized", TargetId = row.AuthorizationId, CreatedAt = now });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new StageCleanupAuthorization(row.AuthorizationId, plan, now);
        });
    }
    private static RequestRejectedException Mismatch() => new(409, "CleanupClaimMismatch");
}
// end
