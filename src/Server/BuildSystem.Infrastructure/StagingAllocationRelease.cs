using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using BuildSystem.Application;
using BuildSystem.Contracts;
using Microsoft.EntityFrameworkCore;
namespace BuildSystem.Infrastructure;

/// <summary>Explicit administrator accounting action after an accepted historical deletion report.
/// Changes only the staging ledger and audit, never reservation history, DB/environment quota or lifecycle.</summary>
public sealed class SqlStagingAllocationRelease(BuildSystemDbContext db, StagingAllocationReleaseOptions options) : IStagingAllocationRelease
{
    private static readonly JsonSerializerOptions PlanOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public Task<StagingAllocationReleaseReceipt> ReleaseAsync(Actor actor, Guid resultId, ReleaseStagingAllocation input, CancellationToken ct)
    {
        if (!options.Enabled) throw new RequestRejectedException(409, "StagingReleaseDisabled");
        if (!actor.IsAdministrator || actor.UserId == Guid.Empty) throw new RequestRejectedException(403, "Forbidden");
        if (resultId == Guid.Empty || input is null || input.ExpectedAuthorizationId == Guid.Empty || input.ExpectedReservationId == Guid.Empty)
            throw new RequestRejectedException(400, "InvalidStagingRelease");
        return db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await Lock("BuildSystem:agent-control", ct);
            var result = await db.StageCleanupResults.AsNoTracking().SingleOrDefaultAsync(x => x.ResultId == resultId, ct);
            if (result is null || result.AuthorizationId != input.ExpectedAuthorizationId) throw Mismatch();
            var auth = await db.StageCleanupAuthorizations.AsNoTracking().SingleOrDefaultAsync(x => x.AuthorizationId == result.AuthorizationId, ct);
            if (auth is null || auth.ArtifactId != result.ArtifactId || auth.AgentId != result.AgentId || auth.StageFencingToken != result.StageFencingToken) throw Mismatch();
            StageRetirementPlan? plan;
            try { plan = JsonSerializer.Deserialize<StageRetirementPlan>(auth.PlanJson, PlanOptions); }
            catch (JsonException) { throw Mismatch(); }
            if (plan is null || plan.RetirementId != auth.RetirementId || plan.ArtifactId != auth.ArtifactId || plan.FencingToken != auth.StageFencingToken ||
                plan.ReservationId != input.ExpectedReservationId || plan.SizeBytes <= 0 || plan.FencingToken <= 0 ||
                plan.JobId == Guid.Empty || plan.EnvironmentId == Guid.Empty || plan.ArchiveVersionId == Guid.Empty ||
                plan.Sha256 is not { Length: 64 } || !plan.Sha256.All(Uri.IsHexDigit) || plan.Format is not ("dt" or "bak")) throw Mismatch();
            var rootId = await (from v in db.DiskArchiveVersions.AsNoTracking() join a in db.DiskArchives.AsNoTracking() on v.ArchiveId equals a.Id
                                where v.Id == plan.ArchiveVersionId select (Guid?)a.RootId).SingleOrDefaultAsync(ct);
            var poolId = await db.EnvironmentReservations.AsNoTracking().Where(r => r.Id == plan.ReservationId).Select(r => (Guid?)r.PoolId).SingleOrDefaultAsync(ct);
            if (rootId is null || poolId is null) throw Mismatch();
            await Lock("BuildSystem:root:" + rootId.Value.ToString("N"), ct);
            await Lock("BuildSystem:pool:" + poolId.Value.ToString("N"), ct);
            var i = await (from retirement in db.StageRetirementIntents.AsNoTracking()
                           join artifact in db.StagedArtifacts.AsNoTracking() on retirement.ArtifactId equals artifact.ArtifactId
                           join job in db.EnvironmentJobs.AsNoTracking() on retirement.JobId equals job.Id
                           join lease in db.AgentLeases.AsNoTracking() on job.Id equals lease.JobId
                           join environment in db.TestEnvironments.AsNoTracking() on job.EnvironmentId equals environment.Id
                           join request in db.EnvironmentRequests.AsNoTracking() on job.RequestId equals request.Id
                           join reservation in db.EnvironmentReservations.AsNoTracking() on retirement.ReservationId equals reservation.Id
                           join version in db.DiskArchiveVersions.AsNoTracking() on artifact.ArchiveVersionId equals version.Id
                           join archive in db.DiskArchives.AsNoTracking() on version.ArchiveId equals archive.Id
                           where retirement.RetirementId == plan.RetirementId
                           select new { retirement, artifact, job, lease, environment, request, reservation, version, archive }).SingleOrDefaultAsync(ct);
            if (i is null || i.retirement.State != "Requested" || i.retirement.AgentId != auth.AgentId || i.retirement.FencingToken != plan.FencingToken ||
                i.retirement.EnvironmentId != plan.EnvironmentId || i.retirement.JobId != plan.JobId || i.retirement.ReservationId != plan.ReservationId || i.retirement.ArtifactId != plan.ArtifactId ||
                i.job.Kind != "ProvisionArchive" || i.job.State != "StageCompleted" || i.lease.State != "Completed" || i.lease.AgentId != auth.AgentId || i.lease.FencingToken != plan.FencingToken ||
                i.artifact.State != "ContentVerified" || i.artifact.ArtifactId != plan.ArtifactId || i.artifact.JobId != plan.JobId || i.artifact.AgentId != auth.AgentId ||
                i.artifact.FencingToken != plan.FencingToken || i.artifact.ReservationId != plan.ReservationId || i.artifact.ArchiveVersionId != plan.ArchiveVersionId ||
                i.artifact.SizeBytes != plan.SizeBytes || i.artifact.Sha256 != plan.Sha256 || i.artifact.Format != plan.Format ||
                i.reservation.State != "Held" || i.reservation.Id != plan.ReservationId || i.reservation.StagingBytes != plan.SizeBytes || i.reservation.PoolId != poolId.Value ||
                i.environment.Id != plan.EnvironmentId || i.environment.ReservationId != plan.ReservationId || i.environment.PoolId != poolId.Value ||
                i.environment.RequestId != i.request.Id || i.request.EnvironmentId != plan.EnvironmentId || i.request.JobId != plan.JobId || i.request.ArchiveVersionId != plan.ArchiveVersionId ||
                i.request.OwnerId != i.environment.OwnerId || i.request.OwnerId != i.reservation.OwnerId || i.request.ProjectId != i.environment.ProjectId ||
                i.version.Id != plan.ArchiveVersionId || i.version.SizeBytes != plan.SizeBytes || i.version.Format != plan.Format ||
                !string.Equals(i.version.Sha256, plan.Sha256, StringComparison.OrdinalIgnoreCase) || i.archive.RootId != rootId.Value) throw Mismatch();
            try
            {
                using var document = JsonDocument.Parse(i.job.SnapshotJson); var s = document.RootElement;
                if (s.GetProperty("reservationId").GetGuid() != plan.ReservationId || s.GetProperty("poolId").GetGuid() != poolId.Value ||
                    s.GetProperty("sourceRootId").GetGuid() != rootId.Value || s.GetProperty("sourceArchiveId").GetGuid() != i.archive.Id ||
                    s.GetProperty("archiveVersionId").GetGuid() != plan.ArchiveVersionId || s.GetProperty("AgentScopeId").GetGuid() != auth.AgentId ||
                    s.GetProperty("sourceRelativePath").GetString() != i.archive.RelativePath || s.GetProperty("stagingBytes").GetInt64() != plan.SizeBytes ||
                    s.GetProperty("SizeBytes").GetInt64() != plan.SizeBytes || s.GetProperty("Format").GetString() != plan.Format ||
                    !string.Equals(s.GetProperty("Sha256").GetString(), plan.Sha256, StringComparison.OrdinalIgnoreCase)) throw Mismatch();
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentException) { throw Mismatch(); }
            var priors = await db.StagingAllocationReleases.Where(x => x.ResultId == resultId || x.ReservationId == plan.ReservationId).Take(2).ToListAsync(ct);
            if (priors.Count > 1) throw Mismatch();
            var prior = priors.SingleOrDefault();
            if (prior is not null)
            {
                if (prior.ResultId != resultId || prior.AuthorizationId != auth.AuthorizationId || prior.ReservationId != plan.ReservationId ||
                    prior.RootId != rootId.Value || prior.PoolId != poolId.Value || prior.ReleasedBytes != plan.SizeBytes) throw Mismatch();
                await tx.CommitAsync(ct); return Receipt(prior);
            }
            var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(ct);
            var row = new StagingAllocationReleaseRecord { ReleaseId = Guid.NewGuid(), ResultId = resultId, AuthorizationId = auth.AuthorizationId,
                ReservationId = plan.ReservationId, RootId = rootId.Value, PoolId = poolId.Value, ReleasedBytes = plan.SizeBytes, ReleasedAt = now, ActorId = actor.UserId };
            db.StagingAllocationReleases.Add(row);
            db.AuditEvents.Add(new AuditEvent { Id = Guid.NewGuid(), ActorId = actor.UserId, Action = "StagingAllocationReleased", TargetId = row.ReleaseId, CreatedAt = now });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Receipt(row);
        });
    }
    private static StagingAllocationReleaseReceipt Receipt(StagingAllocationReleaseRecord row) => new(row.ReleaseId, row.ResultId, row.ReservationId, row.ReleasedBytes, row.ReleasedAt);
    private static RequestRejectedException Mismatch() => new(409, "StagingReleaseMismatch");
    private Task Lock(string resource, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=15000; IF @r IN (-1, -3) THROW 51001, 'Transient staging release lock contention', 1; IF @r < 0 THROW 51000, 'Staging release lock cancelled or invalid', 1;", ct);
}
// end
