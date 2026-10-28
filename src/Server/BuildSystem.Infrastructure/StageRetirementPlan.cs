using System.Data;
using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>
/// Inspects at most one existing retirement intent. Never creates or extends authority,
/// sweeps expiry, acknowledges delivery, audits, dispatches cleanup or releases resources.
/// A returned plan must not be consumed by an execution worker as deletion permission.
/// </summary>
public sealed class SqlStageRetirementPlans(BuildSystemDbContext db, StageRetirementOptions options) : IStageRetirementPlans
{
    public Task<StageRetirementPlan?> PollAsync(string credential, CancellationToken ct)
    {
        if (!options.Enabled) throw new RequestRejectedException(409, "RetirementDisabled");
        return db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await Lock("BuildSystem:agent-control", ct);
            // Use a fresh control instance in this same transaction, not HTTP authentication's cached identity.
            var id = await new SqlAgentControl(db).AuthenticateAsync(credential, ct);
            if (id is null) throw new RequestRejectedException(401, "AgentUnauthorized");
            var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id.Value, ct);
            if (agent is null || agent.State != "Approved" || agent.TokenHash is null ||
                !TokenProtector.Verify(credential, agent.TokenHash))
                throw new RequestRejectedException(401, "AgentUnauthorized");
            // Even historically expired Active leases block inspection: no expiry sweep or silent settlement.
            if (await db.AgentLeases.AsNoTracking().AnyAsync(l => l.AgentId == id.Value &&
                (l.State == "Active" || l.State == "NeedsAttention"), ct))
            {
                await tx.CommitAsync(ct); return null;
            }
            var intent = await db.StageRetirementIntents.AsNoTracking()
                .Where(r => r.AgentId == id.Value && r.State == "Requested")
                .OrderBy(r => r.RequestedAt).ThenBy(r => r.RetirementId).Take(1).SingleOrDefaultAsync(ct);
            if (intent is null) { await tx.CommitAsync(ct); return null; }
            var rootId = await (from q in db.EnvironmentRequests.AsNoTracking()
                                join e in db.TestEnvironments.AsNoTracking() on q.Id equals e.RequestId
                                join v in db.DiskArchiveVersions.AsNoTracking() on q.ArchiveVersionId equals v.Id
                                join a in db.DiskArchives.AsNoTracking() on v.ArchiveId equals a.Id
                                where e.Id == intent.EnvironmentId select (Guid?)a.RootId).SingleOrDefaultAsync(ct);
            if (rootId is null) throw Mismatch();
            await Lock("BuildSystem:root:" + rootId.Value.ToString("N"), ct);
            await Lock("BuildSystem:pool:" + agent.PoolId.ToString("N"), ct);
            var i = await (from e in db.TestEnvironments.AsNoTracking()
                           join q in db.EnvironmentRequests.AsNoTracking() on e.RequestId equals q.Id
                           join j in db.EnvironmentJobs.AsNoTracking() on q.JobId equals j.Id
                           join r in db.EnvironmentReservations.AsNoTracking() on e.ReservationId equals r.Id
                           join l in db.AgentLeases.AsNoTracking() on j.Id equals l.JobId
                           join artifact in db.StagedArtifacts.AsNoTracking() on j.Id equals artifact.JobId
                           join v in db.DiskArchiveVersions.AsNoTracking() on q.ArchiveVersionId equals v.Id
                           join a in db.DiskArchives.AsNoTracking() on v.ArchiveId equals a.Id
                           join root in db.ArchiveRoots.AsNoTracking() on a.RootId equals root.Id
                           join pool in db.EnvironmentPools.AsNoTracking() on e.PoolId equals pool.Id
                           where e.Id == intent.EnvironmentId
                           select new { e, q, j, r, l, artifact, v, a, root, pool }).SingleOrDefaultAsync(ct);
            if (i is null || intent.RetirementId == Guid.Empty || intent.AgentId != agent.Id ||
                intent.JobId != i.j.Id || intent.ArtifactId != i.artifact.ArtifactId || intent.ReservationId != i.r.Id ||
                intent.FencingToken != i.l.FencingToken || i.j.Kind != "ProvisionArchive" ||
                i.j.State != "StageCompleted" || i.l.State != "Completed" || i.artifact.State != "ContentVerified" ||
                i.r.State != "Held" || !i.e.HasUnsettledProvisioningOperation ||
                i.e.State is not ("Requested" or "Provisioning") || i.q.State is not ("Pending" or "Provisioning") ||
                i.q.EnvironmentId != i.e.Id || i.j.EnvironmentId != i.e.Id || i.j.RequestId != i.q.Id ||
                i.q.OwnerId != i.e.OwnerId || i.q.OwnerId != i.r.OwnerId || i.q.ProjectId != i.e.ProjectId ||
                i.artifact.JobId != i.j.Id || i.artifact.ReservationId != i.r.Id || i.artifact.ArchiveVersionId != i.v.Id ||
                i.artifact.AgentId != agent.Id || i.l.AgentId != agent.Id || i.l.FencingToken <= 0 ||
                i.artifact.FencingToken != i.l.FencingToken || i.root.Id != rootId.Value || !i.root.Enabled ||
                !i.pool.Enabled || i.pool.Id != i.r.PoolId || i.pool.Id != agent.PoolId ||
                i.root.AgentScopeId != agent.Id || i.pool.AgentScopeId != agent.Id ||
                i.artifact.SizeBytes <= 0 || i.r.StagingBytes < i.artifact.SizeBytes || i.v.SizeBytes != i.artifact.SizeBytes ||
                i.artifact.Format is not ("dt" or "bak") || i.v.Format != i.artifact.Format ||
                i.artifact.Sha256 is null || i.artifact.Sha256.Length != 64 || !i.artifact.Sha256.All(Uri.IsHexDigit) ||
                !string.Equals(i.v.Sha256, i.artifact.Sha256, StringComparison.OrdinalIgnoreCase) ||
                !SqlAgentControl.CanonicalStagePath(i.a.RelativePath, i.artifact.Format) ||
                !await db.Projects.AsNoTracking().AnyAsync(p => p.Id == i.e.ProjectId && p.Enabled, ct) ||
                !await db.ArchiveRootProjects.AsNoTracking().AnyAsync(m => m.ProjectId == i.e.ProjectId && m.RootId == i.root.Id, ct) ||
                !await db.ProjectPools.AsNoTracking().AnyAsync(m => m.ProjectId == i.e.ProjectId && m.PoolId == i.pool.Id && m.Enabled, ct))
                throw Mismatch();
            // Source Ready availability and historical lease expiry are deliberately not required.
            // Immutable pinned bytes and current authorization/bindings still must agree exactly.
            try
            {
                using var document = JsonDocument.Parse(i.j.SnapshotJson);
                var s = document.RootElement;
                if (Id("reservationId") != i.r.Id || Id("poolId") != i.pool.Id || Id("sourceRootId") != i.root.Id ||
                    Id("sourceArchiveId") != i.a.Id || Id("archiveVersionId") != i.v.Id || Id("AgentScopeId") != agent.Id ||
                    s.GetProperty("sourceRelativePath").GetString() != i.a.RelativePath ||
                    !string.Equals(s.GetProperty("Sha256").GetString(), i.artifact.Sha256, StringComparison.OrdinalIgnoreCase) ||
                    s.GetProperty("SizeBytes").GetInt64() != i.artifact.SizeBytes ||
                    s.GetProperty("Format").GetString() != i.artifact.Format || s.GetProperty("stagingBytes").GetInt64() < i.artifact.SizeBytes)
                    throw Mismatch();
                Guid Id(string name) => s.GetProperty(name).GetGuid();
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
            { throw Mismatch(); }
            var result = new StageRetirementPlan(intent.RetirementId, i.e.Id, i.j.Id, i.artifact.ArtifactId,
                i.r.Id, i.v.Id, i.l.FencingToken, i.artifact.Sha256, i.artifact.SizeBytes, i.artifact.Format);
            await tx.CommitAsync(ct);
            return result;
        });
    }
    private Task Lock(string resource, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=15000; IF @r IN (-1, -3) THROW 51001, 'Transient retirement plan lock contention', 1; IF @r < 0 THROW 51000, 'Retirement plan lock cancelled or invalid', 1;", ct);
    private static RequestRejectedException Mismatch() => new(409, "RetirementPlanMismatch");
}
// end
