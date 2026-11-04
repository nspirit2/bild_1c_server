using System.Data;
using BuildSystem.Application;
using BuildSystem.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Assigns observation correlation only. Does not claim, renew a lease, or authorize execution.</summary>
public sealed class SqlTargetObservations : ITargetObservations
{
    private readonly BuildSystemDbContext db;
    private readonly bool enabled;
    private readonly Dictionary<Guid, TargetObservationEndpointMapping> endpointMappings;

    public SqlTargetObservations(BuildSystemDbContext db, TargetObservationOptions options)
    {
        this.db = db;
        enabled = options.Enabled;
        endpointMappings = options.EndpointMappings is { Count: <= 1024 }
            ? new(options.EndpointMappings) : [];
    }

    public Task<TargetObservationAssignment> RequestAsync(Actor actor, Guid environmentId,
        Guid expectedRestoreIntentId, CancellationToken ct)
    {
        if (!enabled) throw Reject(409, "TargetObservationDisabled");
        if (environmentId == Guid.Empty || expectedRestoreIntentId == Guid.Empty)
            throw Reject(400, "InvalidTargetObservation");
        if (actor is null || actor.UserId == Guid.Empty) throw Reject(404, "TargetObservationNotFound");
        return db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await Lock("BuildSystem:agent-control", ct);
            var environment = await db.TestEnvironments.SingleOrDefaultAsync(e => e.Id == environmentId &&
                db.Projects.Any(p => p.Id == e.ProjectId && p.Enabled) &&
                (actor.IsAdministrator || (e.OwnerId == actor.UserId &&
                    db.ProjectMemberships.Any(m => m.UserId == actor.UserId && m.ProjectId == e.ProjectId))), ct);
            if (environment is null) throw Reject(404, "TargetObservationNotFound");
            // Match the global agent-control -> relational root -> pool scheduler lock order.
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
                           select new { e, q, j, r, l, artifact, agent, a, root, pool }).SingleOrDefaultAsync(ct);
            if (i is null) throw Mismatch();
            var retained = await db.RestoreIntents.AsNoTracking().SingleOrDefaultAsync(x =>
                x.EnvironmentId == environmentId && x.RestoreIntentId == expectedRestoreIntentId, ct);
            if (retained is null) throw Mismatch();
            var status = await RestoreIntentStatus.ReadAsync(db, i.e, i.q, i.j, i.r, i.l, i.artifact,
                "TargetObservationMismatch", ct);
            if (status is null || status.RestoreIntentId != expectedRestoreIntentId ||
                i.q.EnvironmentId != i.e.Id || i.j.EnvironmentId != i.e.Id || i.j.RequestId != i.q.Id ||
                !StageAdmission.IsStageJob(i.j) || i.q.OwnerId != i.e.OwnerId || i.r.OwnerId != i.e.OwnerId ||
                i.q.ProjectId != i.e.ProjectId || i.root.Id != rootId.Value || !i.root.Enabled ||
                !i.pool.Enabled || !i.pool.SupportsDt ||
                (i.j.Kind == "ProvisionArchive" && !i.pool.ScheduledJobsSuppressionVerified) ||
                i.agent.State != "Approved" || i.agent.Id != retained.AgentId || i.agent.PoolId != i.pool.Id ||
                i.root.AgentScopeId != i.agent.Id || i.pool.AgentScopeId != i.agent.Id ||
                !SqlAgentControl.CanonicalStagePath(i.a.RelativePath, retained.Format) ||
                !await db.Projects.AnyAsync(p => p.Id == i.e.ProjectId && p.Enabled &&
                    p.PreparationProfileVersion == retained.PreparationProfileVersion, ct) ||
                (!actor.IsAdministrator && (i.e.OwnerId != actor.UserId ||
                    !await db.ProjectMemberships.AnyAsync(m => m.UserId == actor.UserId && m.ProjectId == i.e.ProjectId, ct))) ||
                !await db.ArchiveRootProjects.AnyAsync(m => m.RootId == i.root.Id && m.ProjectId == i.e.ProjectId, ct) ||
                !await db.ProjectPools.AnyAsync(m => m.PoolId == i.pool.Id && m.ProjectId == i.e.ProjectId && m.Enabled, ct) ||
                await db.AgentLeases.AnyAsync(l => l.AgentId == i.agent.Id && (l.State == "Active" || l.State == "NeedsAttention"), ct) ||
                !endpointMappings.TryGetValue(i.pool.Id, out var mapping) || mapping is null ||
                mapping.RacEndpointMappingId == Guid.Empty || mapping.SqlEndpointMappingId == Guid.Empty)
                throw Mismatch();
            var prior = await db.TargetObservations.SingleOrDefaultAsync(x => x.RestoreIntentId == retained.RestoreIntentId, ct);
            if (prior is not null)
            {
                if (prior.ObservationRequestId == Guid.Empty || prior.OperationId == Guid.Empty ||
                    prior.AgentId != retained.AgentId || prior.EnvironmentId != retained.EnvironmentId ||
                    prior.StageJobId != retained.StageJobId || prior.ReservationId != retained.ReservationId ||
                    prior.FencingToken != retained.FencingToken || prior.TargetIncarnationId != retained.TargetIncarnation ||
                    prior.CanonicalClusterId != retained.CanonicalClusterId || prior.CanonicalSqlServerId != retained.CanonicalSqlServerId ||
                    prior.RacEndpointMappingId != mapping.RacEndpointMappingId || prior.SqlEndpointMappingId != mapping.SqlEndpointMappingId ||
                    prior.Executable || !prior.ScheduledJobsMustRemainBlocked || prior.RequestedBy == Guid.Empty || prior.RequestedAt == default)
                    throw Mismatch();
                await tx.CommitAsync(ct);
                return Assignment(prior);
            }
            var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(ct);
            var row = new TargetObservationRecord
            {
                ObservationRequestId = Guid.NewGuid(), RestoreIntentId = retained.RestoreIntentId,
                AgentId = retained.AgentId, OperationId = Guid.NewGuid(), EnvironmentId = retained.EnvironmentId,
                StageJobId = retained.StageJobId, ReservationId = retained.ReservationId,
                // Historical stage correlation only: NOT a fresh execution fence or renewable lease.
                FencingToken = retained.FencingToken, TargetIncarnationId = retained.TargetIncarnation,
                CanonicalClusterId = retained.CanonicalClusterId, CanonicalSqlServerId = retained.CanonicalSqlServerId,
                RacEndpointMappingId = mapping.RacEndpointMappingId, SqlEndpointMappingId = mapping.SqlEndpointMappingId,
                RequestedBy = actor.UserId, RequestedAt = now, Executable = false, ScheduledJobsMustRemainBlocked = true
            };
            db.TargetObservations.Add(row);
            db.AuditEvents.Add(new AuditEvent { Id = Guid.NewGuid(), ActorId = actor.UserId,
                Action = "TargetObservationRequested", TargetId = row.ObservationRequestId, CreatedAt = now });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Assignment(row);
        });
    }

    private Task Lock(string resource, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=15000; IF @r IN (-1, -3) THROW 51001, 'Transient target observation lock contention', 1; IF @r < 0 THROW 51000, 'Target observation lock cancelled or invalid', 1;", ct);
    private static TargetObservationAssignment Assignment(TargetObservationRecord x) => new(x.ObservationRequestId,
        x.RestoreIntentId, x.AgentId, x.OperationId, x.EnvironmentId, x.StageJobId, x.FencingToken,
        x.ReservationId, x.TargetIncarnationId, x.CanonicalClusterId, x.CanonicalSqlServerId,
        x.RacEndpointMappingId, x.SqlEndpointMappingId);
    private static RequestRejectedException Mismatch() => Reject(409, "TargetObservationMismatch");
    private static RequestRejectedException Reject(int status, string code) => new(status, code);
}
// end
