using BuildSystem.Application;
using BuildSystem.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

// Callers hold a Serializable transaction and the global agent-control lock first.
internal static class TargetObservationAccess
{
    internal static async Task<AgentRecord> AuthenticateAsync(BuildSystemDbContext db, string credential, CancellationToken ct)
    {
        var hash = TokenProtector.Hash(credential);
        var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(x => x.TokenHash == hash && x.State == "Approved", ct);
        if (agent is null || !TokenProtector.Verify(credential, agent.TokenHash!))
            throw new RequestRejectedException(401, "AgentUnauthorized");
        return agent;
    }

    internal static async Task ValidateAsync(BuildSystemDbContext db, AgentRecord agent,
        TargetObservationRecord assigned, Dictionary<Guid, TargetObservationEndpointMapping> endpointMappings,
        string mismatchCode, CancellationToken ct)
    {
        // Discover scheduler locks from the relational source, not the reported pins.
        var source = await (from e in db.TestEnvironments
                            join q in db.EnvironmentRequests on e.RequestId equals q.Id
                            join v in db.DiskArchiveVersions on q.ArchiveVersionId equals v.Id
                            join a in db.DiskArchives on v.ArchiveId equals a.Id
                            where e.Id == assigned.EnvironmentId
                            select new { a.RootId, e.PoolId }).SingleOrDefaultAsync(ct);
        if (source is null) throw new RequestRejectedException(409, mismatchCode);
        await LockAsync(db, "BuildSystem:root:" + source.RootId.ToString("N"), ct);
        await LockAsync(db, "BuildSystem:pool:" + source.PoolId.ToString("N"), ct);
        var i = await (from e in db.TestEnvironments
                       join q in db.EnvironmentRequests on e.RequestId equals q.Id
                       join j in db.EnvironmentJobs on q.JobId equals j.Id
                       join r in db.EnvironmentReservations on e.ReservationId equals r.Id
                       join l in db.AgentLeases on j.Id equals l.JobId
                       join artifact in db.StagedArtifacts on j.Id equals artifact.JobId
                       join v in db.DiskArchiveVersions on q.ArchiveVersionId equals v.Id
                       join a in db.DiskArchives on v.ArchiveId equals a.Id
                       join root in db.ArchiveRoots on a.RootId equals root.Id
                       join pool in db.EnvironmentPools on e.PoolId equals pool.Id
                       where e.Id == assigned.EnvironmentId
                       select new { e, q, j, r, l, artifact, a, root, pool }).SingleOrDefaultAsync(ct);
        if (i is null) throw new RequestRejectedException(409, mismatchCode);
        var retained = await db.RestoreIntents.AsNoTracking().SingleOrDefaultAsync(x =>
            x.RestoreIntentId == assigned.RestoreIntentId, ct);
        if (retained is null) throw new RequestRejectedException(409, mismatchCode);
        var status = await RestoreIntentStatus.ReadAsync(db, i.e, i.q, i.j, i.r, i.l, i.artifact,
            mismatchCode, ct);
        if (status is null || status.RestoreIntentId != assigned.RestoreIntentId ||
            assigned.AgentId != retained.AgentId || assigned.EnvironmentId != retained.EnvironmentId ||
            assigned.StageJobId != retained.StageJobId || assigned.ReservationId != retained.ReservationId ||
            assigned.FencingToken != retained.FencingToken || assigned.TargetIncarnationId != retained.TargetIncarnation ||
            assigned.CanonicalClusterId != retained.CanonicalClusterId || assigned.CanonicalSqlServerId != retained.CanonicalSqlServerId ||
            i.q.EnvironmentId != i.e.Id || i.j.EnvironmentId != i.e.Id || i.j.RequestId != i.q.Id ||
            !StageAdmission.IsStageJob(i.j) || i.q.OwnerId != i.e.OwnerId || i.r.OwnerId != i.e.OwnerId ||
            i.q.ProjectId != i.e.ProjectId || i.root.Id != source.RootId || i.pool.Id != source.PoolId ||
            !i.root.Enabled || !i.pool.Enabled || !i.pool.SupportsDt ||
            (i.j.Kind == "ProvisionArchive" && !i.pool.ScheduledJobsSuppressionVerified) ||
            agent.Id != retained.AgentId || agent.PoolId != i.pool.Id ||
            i.root.AgentScopeId != agent.Id || i.pool.AgentScopeId != agent.Id ||
            !SqlAgentControl.CanonicalStagePath(i.a.RelativePath, retained.Format) ||
            !await db.Projects.AnyAsync(p => p.Id == i.e.ProjectId && p.Enabled &&
                p.PreparationProfileVersion == retained.PreparationProfileVersion, ct) ||
            // Conservative current owner membership: no trust in historical RequestedBy admin claims.
            !await db.ProjectMemberships.AnyAsync(m => m.UserId == i.e.OwnerId && m.ProjectId == i.e.ProjectId, ct) ||
            !await db.ArchiveRootProjects.AnyAsync(m => m.RootId == i.root.Id && m.ProjectId == i.e.ProjectId, ct) ||
            !await db.ProjectPools.AnyAsync(m => m.PoolId == i.pool.Id && m.ProjectId == i.e.ProjectId && m.Enabled, ct) ||
            await db.AgentLeases.AnyAsync(l => l.AgentId == agent.Id && (l.State == "Active" || l.State == "NeedsAttention"), ct) ||
            !endpointMappings.TryGetValue(i.pool.Id, out var mapping) || mapping is null ||
            mapping.RacEndpointMappingId == Guid.Empty || mapping.SqlEndpointMappingId == Guid.Empty ||
            assigned.RacEndpointMappingId != mapping.RacEndpointMappingId || assigned.SqlEndpointMappingId != mapping.SqlEndpointMappingId)
            throw new RequestRejectedException(409, mismatchCode);
    }

    internal static TargetObservationAssignment Assignment(TargetObservationRecord x) => new(x.ObservationRequestId,
        x.RestoreIntentId, x.AgentId, x.OperationId, x.EnvironmentId, x.StageJobId, x.FencingToken,
        x.ReservationId, x.TargetIncarnationId, x.CanonicalClusterId, x.CanonicalSqlServerId,
        x.RacEndpointMappingId, x.SqlEndpointMappingId);

    internal static Task LockAsync(BuildSystemDbContext db, string resource, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=15000; IF @r IN (-1, -3) THROW 51001, 'Transient target observation lock contention', 1; IF @r < 0 THROW 51000, 'Target observation lock cancelled or invalid', 1;", ct);
}
// end
