using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using BuildSystem.Application;
using BuildSystem.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Records a durable historical report. Never re-grants authority or changes lifecycle/quota.</summary>
public sealed class SqlStageCleanupResults(BuildSystemDbContext db, StageCleanupResultOptions options) : IStageCleanupResults
{
    private static readonly JsonSerializerOptions PlanOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public Task<StageCleanupResultReceipt> ReportAsync(string credential, ReportStageCleanup input, CancellationToken ct)
    {
        if (!options.Enabled) throw new RequestRejectedException(409, "CleanupResultDisabled");
        if (input is null || input.ResultId == Guid.Empty || input.AuthorizationId == Guid.Empty || input.RetirementId == Guid.Empty ||
            input.JobId == Guid.Empty || input.ArtifactId == Guid.Empty || input.FencingToken <= 0)
            throw new RequestRejectedException(400, "InvalidCleanupResult");
        return db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await Lock("BuildSystem:agent-control", ct);
            var id = await new SqlAgentControl(db).AuthenticateAsync(credential, ct);
            if (id is null) throw Unauthorized();
            var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id.Value, ct);
            if (agent is null || agent.State != "Approved" || agent.TokenHash is null || !TokenProtector.Verify(credential, agent.TokenHash)) throw Unauthorized();
            var auth = await db.StageCleanupAuthorizations.AsNoTracking().SingleOrDefaultAsync(a => a.AuthorizationId == input.AuthorizationId, ct);
            if (auth is null || auth.AgentId != id.Value || auth.RetirementId != input.RetirementId || auth.ArtifactId != input.ArtifactId || auth.StageFencingToken != input.FencingToken) throw Mismatch();
            StageRetirementPlan? plan;
            try { plan = JsonSerializer.Deserialize<StageRetirementPlan>(auth.PlanJson, PlanOptions); }
            catch (JsonException) { throw Mismatch(); }
            if (plan is null || plan.RetirementId != input.RetirementId || plan.JobId != input.JobId || plan.ArtifactId != input.ArtifactId || plan.FencingToken != input.FencingToken ||
                plan.EnvironmentId == Guid.Empty || plan.ReservationId == Guid.Empty || plan.ArchiveVersionId == Guid.Empty || plan.SizeBytes <= 0 ||
                plan.Sha256 is not { Length: 64 } || !plan.Sha256.All(Uri.IsHexDigit) || plan.Format is not ("dt" or "bak")) throw Mismatch();
            var intent = await db.StageRetirementIntents.AsNoTracking().SingleOrDefaultAsync(x => x.RetirementId == input.RetirementId, ct);
            if (intent is null || intent.AgentId != id.Value || intent.JobId != input.JobId || intent.ArtifactId != input.ArtifactId ||
                intent.EnvironmentId != plan.EnvironmentId || intent.ReservationId != plan.ReservationId || intent.FencingToken != input.FencingToken) throw Mismatch();
            var pins = await (from artifact in db.StagedArtifacts.AsNoTracking()
                              join version in db.DiskArchiveVersions.AsNoTracking() on artifact.ArchiveVersionId equals version.Id
                              join archive in db.DiskArchives.AsNoTracking() on version.ArchiveId equals archive.Id
                              join reservation in db.EnvironmentReservations.AsNoTracking() on artifact.ReservationId equals reservation.Id
                              where artifact.ArtifactId == input.ArtifactId
                              select new { artifact, version, archive.RootId, reservation.PoolId }).SingleOrDefaultAsync(ct);
            if (pins is null || pins.artifact.AgentId != id.Value || pins.artifact.JobId != plan.JobId || pins.artifact.ReservationId != plan.ReservationId ||
                pins.artifact.ArchiveVersionId != plan.ArchiveVersionId || pins.artifact.FencingToken != plan.FencingToken || pins.artifact.SizeBytes != plan.SizeBytes ||
                pins.artifact.Format != plan.Format || pins.artifact.Sha256 != plan.Sha256 || pins.version.SizeBytes != plan.SizeBytes ||
                pins.version.Format != plan.Format || !string.Equals(pins.version.Sha256, plan.Sha256, StringComparison.OrdinalIgnoreCase)) throw Mismatch();
            // Historical immutable identity only: disabled/deleted bindings or unrelated active leases must not lose a past deletion report.
            await Lock("BuildSystem:root:" + pins.RootId.ToString("N"), ct);
            await Lock("BuildSystem:pool:" + pins.PoolId.ToString("N"), ct);
            var matches = await db.StageCleanupResults.Where(x => x.ResultId == input.ResultId || x.AuthorizationId == input.AuthorizationId || x.ArtifactId == input.ArtifactId).Take(2).ToListAsync(ct);
            if (matches.Count > 1) throw Mismatch();
            var prior = matches.SingleOrDefault();
            if (prior is not null)
            {
                if (prior.ResultId != input.ResultId || prior.AuthorizationId != input.AuthorizationId || prior.ArtifactId != input.ArtifactId || prior.AgentId != id.Value || prior.StageFencingToken != input.FencingToken) throw Mismatch();
                await tx.CommitAsync(ct);
                return Receipt(prior);
            }
            var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(ct);
            var row = new StageCleanupResultRecord { ResultId = input.ResultId, AuthorizationId = input.AuthorizationId, ArtifactId = input.ArtifactId,
                AgentId = id.Value, StageFencingToken = input.FencingToken, ReceivedAt = now };
            db.StageCleanupResults.Add(row);
            db.AuditEvents.Add(new AuditEvent { Id = Guid.NewGuid(), ActorId = id.Value, Action = "StageCleanupDeletedReported", TargetId = row.ResultId, CreatedAt = now });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Receipt(row);
        });
    }
    private static StageCleanupResultReceipt Receipt(StageCleanupResultRecord row) => new(row.ResultId, row.AuthorizationId, row.ArtifactId, "DeletedReported", row.ReceivedAt);
    private Task Lock(string resource, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=15000; IF @r IN (-1, -3) THROW 51001, 'Transient cleanup result lock contention', 1; IF @r < 0 THROW 51000, 'Cleanup result lock cancelled or invalid', 1;", ct);
    private static RequestRejectedException Unauthorized() => new(401, "AgentUnauthorized");
    private static RequestRejectedException Mismatch() => new(409, "CleanupResultMismatch");
}
// end
