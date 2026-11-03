using System.Data;
using BuildSystem.Application;
using BuildSystem.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Stores historical observation evidence, without changing any intent, lease, lifecycle or hold.</summary>
public sealed class SqlTargetObservationReceipts : ITargetObservationReceipts
{
    private readonly BuildSystemDbContext db;
    private readonly bool enabled;
    private readonly Dictionary<Guid, TargetObservationEndpointMapping> endpointMappings;

    public SqlTargetObservationReceipts(BuildSystemDbContext db, TargetObservationOptions options)
    {
        this.db = db;
        enabled = options.Enabled;
        endpointMappings = options.EndpointMappings is { Count: <= 1024 }
            ? new(options.EndpointMappings) : [];
    }

    public Task<TargetObservationReceipt> ReportAsync(string credential, ReportTargetObservation report, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(credential) || credential.Length > 256) throw Unauthorized();
        if (!enabled) throw Reject(409, "TargetObservationDisabled");
        if (report is null || report.ReceiptId == Guid.Empty || !ValidAssignment(report.Assignment) ||
            report.RacObservationId == Guid.Empty || report.SqlObservationId == Guid.Empty ||
            report.RacObservationId == report.SqlObservationId || (int)report.Outcome is < 0 or > 3 ||
            report.ObservedAt == default)
            throw Reject(400, "InvalidTargetObservationReceipt");
        var hash = TokenProtector.Hash(credential);
        return db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                await Lock("BuildSystem:agent-control", ct);
                var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(x => x.TokenHash == hash && x.State == "Approved", ct);
                if (agent is null || !TokenProtector.Verify(credential, agent.TokenHash!)) throw Unauthorized();
                var assigned = await db.TargetObservations.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.ObservationRequestId == report.Assignment.ObservationRequestId, ct);
                if (assigned is not null && assigned.AgentId != agent.Id) throw Unauthorized();
                if (assigned is null || Assignment(assigned) != report.Assignment ||
                    assigned.Executable || !assigned.ScheduledJobsMustRemainBlocked ||
                    assigned.RequestedBy == Guid.Empty || assigned.RequestedAt == default)
                    throw Mismatch();
                // Discover scheduler locks from the relational source, not the reported pins.
                var source = await (from e in db.TestEnvironments
                                    join q in db.EnvironmentRequests on e.RequestId equals q.Id
                                    join v in db.DiskArchiveVersions on q.ArchiveVersionId equals v.Id
                                    join a in db.DiskArchives on v.ArchiveId equals a.Id
                                    where e.Id == assigned.EnvironmentId
                                    select new { a.RootId, e.PoolId }).SingleOrDefaultAsync(ct);
                if (source is null) throw Mismatch();
                await Lock("BuildSystem:root:" + source.RootId.ToString("N"), ct);
                await Lock("BuildSystem:pool:" + source.PoolId.ToString("N"), ct);
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
                if (i is null) throw Mismatch();
                var retained = await db.RestoreIntents.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.RestoreIntentId == assigned.RestoreIntentId, ct);
                if (retained is null) throw Mismatch();
                var status = await RestoreIntentStatus.ReadAsync(db, i.e, i.q, i.j, i.r, i.l, i.artifact,
                    "TargetObservationReceiptMismatch", ct);
                if (status is null || status.RestoreIntentId != assigned.RestoreIntentId ||
                    assigned.AgentId != retained.AgentId || assigned.EnvironmentId != retained.EnvironmentId ||
                    assigned.StageJobId != retained.StageJobId || assigned.ReservationId != retained.ReservationId ||
                    assigned.FencingToken != retained.FencingToken || assigned.TargetIncarnationId != retained.TargetIncarnation ||
                    assigned.CanonicalClusterId != retained.CanonicalClusterId || assigned.CanonicalSqlServerId != retained.CanonicalSqlServerId ||
                    i.q.EnvironmentId != i.e.Id || i.j.EnvironmentId != i.e.Id || i.j.RequestId != i.q.Id ||
                    i.j.Kind != "ProvisionArchive" || i.q.OwnerId != i.e.OwnerId || i.r.OwnerId != i.e.OwnerId ||
                    i.q.ProjectId != i.e.ProjectId || i.root.Id != source.RootId || i.pool.Id != source.PoolId ||
                    !i.root.Enabled || !i.pool.Enabled || !i.pool.SupportsDt || !i.pool.ScheduledJobsSuppressionVerified ||
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
                    throw Mismatch();
                // Authentication and current bindings precede even an exact lost-ACK replay.
                // Cross-role comparisons also prevent swapping old RAC/SQL evidence into another assignment.
                var matches = await db.TargetObservationReceipts.Where(x => x.ReceiptId == report.ReceiptId ||
                    x.ObservationRequestId == assigned.ObservationRequestId ||
                    x.RacObservationId == report.RacObservationId || x.SqlObservationId == report.SqlObservationId ||
                    x.RacObservationId == report.SqlObservationId || x.SqlObservationId == report.RacObservationId)
                    .Take(2).ToListAsync(ct);
                if (matches.Count > 1) throw Mismatch();
                var prior = matches.SingleOrDefault();
                if (prior is not null)
                {
                    if (prior.ReceiptId != report.ReceiptId || prior.ObservationRequestId != assigned.ObservationRequestId ||
                        prior.RacObservationId != report.RacObservationId || prior.SqlObservationId != report.SqlObservationId ||
                        prior.Outcome != (int)report.Outcome || prior.ObservedAt != report.ObservedAt ||
                        prior.AcceptedAt == default || prior.Executable || !prior.ScheduledJobsMustRemainBlocked)
                        throw Mismatch();
                    await tx.CommitAsync(ct);
                    return Receipt(prior);
                }
                // ObservedAt is an agent historical label, deliberately not compared with now for freshness.
                var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(ct);
                var row = new TargetObservationReceiptRecord
                {
                    ReceiptId = report.ReceiptId, ObservationRequestId = assigned.ObservationRequestId,
                    RacObservationId = report.RacObservationId, SqlObservationId = report.SqlObservationId,
                    Outcome = (int)report.Outcome, ObservedAt = report.ObservedAt, AcceptedAt = now,
                    Executable = false, ScheduledJobsMustRemainBlocked = true
                };
                db.TargetObservationReceipts.Add(row);
                db.AuditEvents.Add(new AuditEvent { Id = Guid.NewGuid(), ActorId = agent.Id,
                    Action = "TargetObservationRecorded", TargetId = row.ReceiptId, CreatedAt = now });
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return Receipt(row);
            }
            catch
            {
                db.ChangeTracker.Clear();
                throw;
            }
        });
    }

    private static bool ValidAssignment(TargetObservationAssignment? x) => x is not null &&
        x.ObservationRequestId != Guid.Empty && x.RestoreIntentId != Guid.Empty && x.AgentId != Guid.Empty &&
        x.OperationId != Guid.Empty && x.EnvironmentId != Guid.Empty && x.StageJobId != Guid.Empty &&
        x.FencingToken > 0 && x.ReservationId != Guid.Empty && x.TargetIncarnationId != Guid.Empty &&
        x.CanonicalClusterId != Guid.Empty && x.CanonicalSqlServerId != Guid.Empty &&
        x.RacEndpointMappingId != Guid.Empty && x.SqlEndpointMappingId != Guid.Empty;
    private static TargetObservationAssignment Assignment(TargetObservationRecord x) => new(x.ObservationRequestId,
        x.RestoreIntentId, x.AgentId, x.OperationId, x.EnvironmentId, x.StageJobId, x.FencingToken,
        x.ReservationId, x.TargetIncarnationId, x.CanonicalClusterId, x.CanonicalSqlServerId,
        x.RacEndpointMappingId, x.SqlEndpointMappingId);
    private static TargetObservationReceipt Receipt(TargetObservationReceiptRecord x) => new(x.ReceiptId, x.ObservationRequestId, "Recorded");
    private Task Lock(string resource, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=15000; IF @r IN (-1, -3) THROW 51001, 'Transient target observation receipt lock contention', 1; IF @r < 0 THROW 51000, 'Target observation receipt lock cancelled or invalid', 1;", ct);
    private static RequestRejectedException Unauthorized() => Reject(401, "AgentUnauthorized");
    private static RequestRejectedException Mismatch() => Reject(409, "TargetObservationReceiptMismatch");
    private static RequestRejectedException Reject(int status, string code) => new(status, code);
}
// end
