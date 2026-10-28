using System.Data;
using System.Text.Json;
using BuildSystem.Application;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Records human intent only. Never sweeps expiry, dispatches cleanup, or changes retained authority/resources.</summary>
public sealed class SqlStageRetirement(BuildSystemDbContext db) : IStageRetirement
{
    public Task<StageRetirementReceipt> RequestAsync(Actor actor, Guid environmentId, StageRetirementRequest input, CancellationToken ct)
    {
        if (environmentId == Guid.Empty || input is null || input.ExpectedJobId == Guid.Empty || input.ExpectedArtifactId == Guid.Empty)
            throw Reject(400, "InvalidStageRetirement");
        if (actor is null || actor.UserId == Guid.Empty) throw Reject(404, "StageRetirementNotFound");
        return db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await Lock("BuildSystem:agent-control", ct);
            var environment = await db.TestEnvironments.SingleOrDefaultAsync(e => e.Id == environmentId &&
                db.Projects.Any(p => p.Id == e.ProjectId && p.Enabled) &&
                (actor.IsAdministrator || (e.OwnerId == actor.UserId &&
                    db.ProjectMemberships.Any(m => m.UserId == actor.UserId && m.ProjectId == e.ProjectId))), ct);
            if (environment is null) throw Reject(404, "StageRetirementNotFound");
            // Resolve locks from relational request/version/archive identity, not client IDs or snapshot JSON.
            var rootId = await (from q in db.EnvironmentRequests
                                join v in db.DiskArchiveVersions on q.ArchiveVersionId equals v.Id
                                join a in db.DiskArchives on v.ArchiveId equals a.Id
                                where q.Id == environment.RequestId select (Guid?)a.RootId).SingleOrDefaultAsync(ct);
            if (rootId is null) throw Mismatch();
            await Lock("BuildSystem:root:" + rootId.Value.ToString("N"), ct);
            await Lock("BuildSystem:pool:" + environment.PoolId.ToString("N"), ct);
            var i = await (from e in db.TestEnvironments
                           join q in db.EnvironmentRequests on e.RequestId equals q.Id
                           join j in db.EnvironmentJobs on q.JobId equals j.Id
                           join r in db.EnvironmentReservations on e.ReservationId equals r.Id
                           join l in db.AgentLeases on j.Id equals l.JobId
                           join artifact in db.StagedArtifacts on j.Id equals artifact.JobId
                           join agent in db.Agents on l.AgentId equals agent.Id
                           join v in db.DiskArchiveVersions on q.ArchiveVersionId equals v.Id
                           join a in db.DiskArchives on v.ArchiveId equals a.Id
                           join root in db.ArchiveRoots on a.RootId equals root.Id
                           join pool in db.EnvironmentPools on e.PoolId equals pool.Id
                           where e.Id == environmentId
                           select new { e, q, j, r, l, artifact, agent, v, a, root, pool }).SingleOrDefaultAsync(ct);
            if (i is null || i.j.Id != input.ExpectedJobId || i.artifact.ArtifactId != input.ExpectedArtifactId ||
                i.j.Kind != "ProvisionArchive" || i.j.State != "StageCompleted" || i.l.State != "Completed" ||
                i.artifact.State != "ContentVerified" || i.r.State != "Held" || !i.e.HasUnsettledProvisioningOperation ||
                i.e.State is not ("Requested" or "Provisioning") || i.q.State is not ("Pending" or "Provisioning") ||
                i.q.EnvironmentId != i.e.Id || i.j.EnvironmentId != i.e.Id || i.j.RequestId != i.q.Id ||
                i.q.OwnerId != i.e.OwnerId || i.q.OwnerId != i.r.OwnerId || i.q.ProjectId != i.e.ProjectId ||
                i.artifact.JobId != i.j.Id || i.artifact.ReservationId != i.r.Id || i.artifact.ArchiveVersionId != i.v.Id ||
                i.artifact.AgentId != i.agent.Id || i.l.AgentId != i.agent.Id || i.l.FencingToken <= 0 ||
                i.artifact.FencingToken != i.l.FencingToken || i.root.Id != rootId.Value || !i.root.Enabled ||
                !i.pool.Enabled || i.pool.Id != i.r.PoolId || i.pool.Id != i.agent.PoolId ||
                i.root.AgentScopeId != i.agent.Id || i.pool.AgentScopeId != i.agent.Id ||
                i.artifact.SizeBytes <= 0 || i.r.StagingBytes < i.artifact.SizeBytes || i.v.SizeBytes != i.artifact.SizeBytes ||
                i.artifact.Format is not ("dt" or "bak") || i.v.Format != i.artifact.Format ||
                i.artifact.Sha256 is null || i.artifact.Sha256.Length != 64 || !i.artifact.Sha256.All(Uri.IsHexDigit) ||
                !string.Equals(i.v.Sha256, i.artifact.Sha256, StringComparison.OrdinalIgnoreCase) ||
                !SqlAgentControl.CanonicalStagePath(i.a.RelativePath, i.artifact.Format) ||
                !await db.Projects.AnyAsync(p => p.Id == i.e.ProjectId && p.Enabled, ct) ||
                (!actor.IsAdministrator && (i.e.OwnerId != actor.UserId || !await db.ProjectMemberships.AnyAsync(m => m.ProjectId == i.e.ProjectId && m.UserId == actor.UserId, ct))) ||
                !await db.ArchiveRootProjects.AnyAsync(m => m.ProjectId == i.e.ProjectId && m.RootId == i.root.Id, ct) ||
                !await db.ProjectPools.AnyAsync(m => m.ProjectId == i.e.ProjectId && m.PoolId == i.pool.Id && m.Enabled, ct))
                throw Mismatch();
            // Match the immutable stage admission snapshot even when current source availability is no longer Ready.
            try
            {
                using var document = JsonDocument.Parse(i.j.SnapshotJson);
                var s = document.RootElement;
                if (Id("reservationId") != i.r.Id || Id("poolId") != i.pool.Id || Id("sourceRootId") != i.root.Id ||
                    Id("sourceArchiveId") != i.a.Id || Id("archiveVersionId") != i.v.Id || Id("AgentScopeId") != i.agent.Id ||
                    s.GetProperty("sourceRelativePath").GetString() != i.a.RelativePath ||
                    !string.Equals(s.GetProperty("Sha256").GetString(), i.artifact.Sha256, StringComparison.OrdinalIgnoreCase) ||
                    s.GetProperty("SizeBytes").GetInt64() != i.artifact.SizeBytes ||
                    s.GetProperty("Format").GetString() != i.artifact.Format || s.GetProperty("stagingBytes").GetInt64() < i.artifact.SizeBytes)
                    throw Mismatch();
                Guid Id(string name) => s.GetProperty(name).GetGuid();
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
            { throw Mismatch(); }
            var prior = await db.StageRetirementIntents.SingleOrDefaultAsync(x => x.EnvironmentId == environmentId, ct);
            if (prior is not null)
            {
                // RequestedBy remains the original authorized requester, including an admin/owner replay.
                if (prior.JobId != i.j.Id || prior.ArtifactId != i.artifact.ArtifactId || prior.ReservationId != i.r.Id ||
                    prior.AgentId != i.agent.Id || prior.FencingToken != i.l.FencingToken || prior.State != "Requested") throw Mismatch();
                await tx.CommitAsync(ct);
                return Receipt(prior);
            }
            var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(ct);
            var intent = new StageRetirementIntent { RetirementId = Guid.NewGuid(), EnvironmentId = environmentId,
                JobId = i.j.Id, ArtifactId = i.artifact.ArtifactId, ReservationId = i.r.Id, AgentId = i.agent.Id,
                FencingToken = i.l.FencingToken, RequestedBy = actor.UserId, RequestedAt = now };
            db.StageRetirementIntents.Add(intent);
            db.AuditEvents.Add(new AuditEvent { Id = Guid.NewGuid(), ActorId = actor.UserId,
                Action = "StageRetirementRequested", TargetId = intent.RetirementId, CreatedAt = now });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Receipt(intent);
        });
    }
    private Task Lock(string resource, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=15000; IF @r IN (-1, -3) THROW 51001, 'Transient retirement lock contention', 1; IF @r < 0 THROW 51000, 'Retirement lock cancelled or invalid', 1;", ct);
    private static StageRetirementReceipt Receipt(StageRetirementIntent x) => new(x.RetirementId, x.EnvironmentId, x.JobId, x.ArtifactId, x.State, x.RequestedAt);
    private static RequestRejectedException Mismatch() => Reject(409, "StageRetirementMismatch");
    private static RequestRejectedException Reject(int status, string code) => new(status, code);
}
// end
