using System.Data;
using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Domain;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

public sealed class SqlEnvironmentOrders(BuildSystemDbContext db) : IEnvironmentOrders
{
    public async Task<OrderReceipt> CreateAsync(Actor actor, string idempotencyKey, CreateEnvironmentRequest request, CancellationToken ct)
    {
        var input = OrderInput.Validate(idempotencyKey, request);
        if (actor.UserId == Guid.Empty) throw Reject(403, "Forbidden");
        // SQL Server execution strategy wraps the ENTIRE transaction, never an individual save.
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            // Global order: owner, source root, selected pool. Future admission/cleanup writers must honor it.
            await LockAsync("BuildSystem:owner:" + actor.UserId.ToString("N"), ct);
            await RequireProjectAsync(actor, input.ProjectId, ct);
            var source = await LoadSourceAsync(input, ct, requireReady: false); // ACL rechecked even on replay.
            var prior = await db.EnvironmentRequests.SingleOrDefaultAsync(x => x.OwnerId == actor.UserId && x.IdempotencyKey == input.IdempotencyKey, ct);
            if (prior is not null)
            {
                if (prior.PayloadHash != input.PayloadHash) throw Reject(409, "IdempotencyConflict");
                await tx.CommitAsync(ct);
                return new OrderReceipt(prior.Id, prior.EnvironmentId, prior.JobId);
            }
            await LockAsync("BuildSystem:root:" + source.Root.Id.ToString("N"), ct);
            source = await LoadSourceAsync(input, ct);
            var poolId = await (from mapping in db.ProjectPools
                                join p in db.EnvironmentPools on mapping.PoolId equals p.Id
                                where mapping.ProjectId == input.ProjectId && mapping.Enabled && p.Enabled &&
                                    p.AgentScopeId == source.Root.AgentScopeId && p.IsolationPolicyId == source.Root.IsolationPolicyId &&
                                    p.IsolationVerified && p.ScheduledJobsSuppressionVerified && p.ResourceLimitVerified &&
                                    (source.Version.Format == "dt" ? p.SupportsDt : p.SupportsBak)
                                orderby p.Id select (Guid?)p.Id).FirstOrDefaultAsync(ct)
                ?? throw Reject(409, "PoolUnavailable");
            await LockAsync("BuildSystem:pool:" + poolId.ToString("N"), ct);
            // Reload authoritative configuration under the transaction and selected pool lock.
            var pool = await db.EnvironmentPools.SingleAsync(x => x.Id == poolId, ct);
            await RequireProjectAsync(actor, input.ProjectId, ct);
            source = await LoadSourceAsync(input, ct);
            var project = await db.Projects.SingleAsync(x => x.Id == input.ProjectId, ct);
            if (!pool.Enabled || !await db.ProjectPools.AnyAsync(x => x.ProjectId == input.ProjectId && x.PoolId == poolId && x.Enabled, ct) ||
                pool.SqlServerId == Guid.Empty || pool.ClusterId == Guid.Empty || pool.AgentScopeId == Guid.Empty ||
                pool.AgentScopeId != source.Root.AgentScopeId || string.IsNullOrWhiteSpace(project.PreparationProfileVersion) ||
                string.IsNullOrWhiteSpace(project.IsolationPolicyId) || project.IsolationPolicyId != pool.IsolationPolicyId ||
                pool.IsolationPolicyId != source.Root.IsolationPolicyId || !pool.IsolationVerified ||
                !pool.ScheduledJobsSuppressionVerified || !pool.ResourceLimitVerified ||
                (source.Version.Format == "dt" ? !pool.SupportsDt : !pool.SupportsBak) || pool.MaxStagingBytes <= 0)
                throw Reject(409, "PoolNotReady");
            var limits = await db.EnvironmentQuotas.SingleOrDefaultAsync(x => x.SubjectId == actor.UserId, ct)
                ?? throw Reject(409, "QuotaNotConfigured");
            var rows = await (from e in db.TestEnvironments join r in db.EnvironmentReservations on e.ReservationId equals r.Id
                              where e.OwnerId == actor.UserId || e.PoolId == poolId
                              select new { e.State, e.HasUnsettledProvisioningOperation, Reservation = r }).ToListAsync(ct);
            var snapshot = rows.Select(x => new EnvironmentQuotaEntry(x.Reservation.Id, x.Reservation.OwnerId, x.Reservation.PoolId,
                x.Reservation.ReservedBytes, Enum.Parse<EnvironmentState>(x.State), x.HasUnsettledProvisioningOperation)).ToArray();
            var reservationId = Guid.NewGuid();
            var name = "bs_test_" + reservationId.ToString("N");
            var proposed = new BuildSystem.Domain.EnvironmentReservation(reservationId, actor.UserId, poolId, source.Version.EstimatedDbBytes,
                new ResourceIdentity(pool.SqlServerId.ToString("D"), name), new ResourceIdentity(pool.ClusterId.ToString("D"), name));
            var decision = EnvironmentQuotaPolicy.CanReserve(proposed, TimeSpan.FromDays(input.TtlDays),
                new EnvironmentQuotaLimits(limits.MaxActiveEnvironments, limits.MaxConcurrentProvisioning, limits.MaxStorageBytes, TimeSpan.FromDays(limits.MaxTtlDays)),
                new PoolBudget(pool.Enabled, pool.CapacityBytes, pool.MaxEnvironmentBytes, pool.MaxConcurrentProvisioning), snapshot);
            if (!decision.Allowed) throw Reject(409, "QuotaExceeded");
            // Staging allocations remain held even after a failed job until explicit safe cleanup.
            var heldStaging = await db.EnvironmentReservations.Where(x => x.PoolId == poolId && x.State != "Released").Select(x => x.StagingBytes).ToListAsync(ct);
            var rootStaging = await (from r in db.EnvironmentReservations
                                     join e in db.TestEnvironments on r.Id equals e.ReservationId
                                     join q in db.EnvironmentRequests on e.RequestId equals q.Id
                                     join v in db.DiskArchiveVersions on q.ArchiveVersionId equals v.Id
                                     join a in db.DiskArchives on v.ArchiveId equals a.Id
                                     where a.RootId == source.Root.Id && r.State != "Released"
                                     select r.StagingBytes).ToListAsync(ct);
            if (heldStaging.Sum(x => (decimal)x) + source.Version.SizeBytes > pool.MaxStagingBytes ||
                rootStaging.Sum(x => (decimal)x) + source.Version.SizeBytes > source.Root.MaxStagingBytes)
                throw Reject(409, "QuotaExceeded");
            var now = DateTimeOffset.UtcNow;
            var receipt = new OrderReceipt(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            db.EnvironmentRequests.Add(new EnvironmentRequest { Id = receipt.RequestId, OwnerId = actor.UserId, ProjectId = input.ProjectId,
                EnvironmentId = receipt.EnvironmentId, JobId = receipt.JobId, ArchiveVersionId = input.ArchiveVersionId,
                IdempotencyKey = input.IdempotencyKey, PayloadHash = input.PayloadHash, DisplayName = input.DisplayName, TtlDays = input.TtlDays, CreatedAt = now });
            db.EnvironmentReservations.Add(new EnvironmentReservation { Id = reservationId, OwnerId = actor.UserId, PoolId = poolId,
                SqlServerId = pool.SqlServerId, ClusterId = pool.ClusterId, SqlDbName = name, InfobaseName = name,
                ReservedBytes = source.Version.EstimatedDbBytes, StagingBytes = source.Version.SizeBytes });
            db.TestEnvironments.Add(new EnvironmentRecord { Id = receipt.EnvironmentId, RequestId = receipt.RequestId, OwnerId = actor.UserId,
                ProjectId = input.ProjectId, PoolId = poolId, ReservationId = reservationId, DisplayName = input.DisplayName });
            // Durable intent only; NOT a compiled pipeline and there is intentionally no executor here.
            db.EnvironmentJobs.Add(new EnvironmentJob { Id = receipt.JobId, RequestId = receipt.RequestId, EnvironmentId = receipt.EnvironmentId, CreatedAt = now,
                SnapshotJson = JsonSerializer.Serialize(new { reservationId, poolId, pool.SqlServerId, pool.ClusterId, pool.AgentScopeId,
                    sqlDbName = name, infobaseName = name, sourceRootId = source.Root.Id, sourceArchiveId = source.Archive.Id, sourceRelativePath = source.Archive.RelativePath,
                    automaticDeletionEnabled = false, automaticDeletionGraceDays = (int?)null,
                    archiveVersionId = source.Version.Id, source.Version.Sha256, source.Version.SizeBytes, source.Version.Format,
                    reservedBytes = source.Version.EstimatedDbBytes, stagingBytes = source.Version.SizeBytes,
                    project.PreparationProfileVersion, project.IsolationPolicyId, input.TtlDays }) });
            db.AuditEvents.Add(new AuditEvent { Id = Guid.NewGuid(), ActorId = actor.UserId, Action = "EnvironmentOrderAccepted", TargetId = receipt.RequestId, CreatedAt = now });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return receipt;
        });
    }

    private async Task LockAsync(string resource, CancellationToken ct)
    {
        // sp_getapplock is transaction-owned; negative results MUST abort admission.
        await db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=15000; IF @r IN (-1, -3) THROW 51001, 'Transient admission lock contention', 1; IF @r < 0 THROW 51000, 'Admission lock cancelled or invalid', 1;", ct);
    }

    private async Task RequireProjectAsync(Actor actor, Guid projectId, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(x => x.Id == projectId && x.Enabled, ct) ||
            (!actor.IsAdministrator && !await db.ProjectMemberships.AnyAsync(x => x.ProjectId == projectId && x.UserId == actor.UserId, ct)))
            throw Reject(403, "Forbidden");
    }

    private async Task<(DiskArchiveVersion Version, DiskArchive Archive, ArchiveRoot Root)> LoadSourceAsync(ValidatedOrder input, CancellationToken ct, bool requireReady = true)
    {
        var result = await (from v in db.DiskArchiveVersions join a in db.DiskArchives on v.ArchiveId equals a.Id
                            join r in db.ArchiveRoots on a.RootId equals r.Id
                            where v.Id == input.ArchiveVersionId select new { Version = v, Archive = a, Root = r }).SingleOrDefaultAsync(ct);
        if (result is null || !await db.ArchiveRootProjects.AnyAsync(x => x.RootId == result.Root.Id && x.ProjectId == input.ProjectId, ct))
            throw Reject(403, "SourceForbidden");
        if (requireReady && (!result.Root.Enabled || result.Root.AgentScopeId == Guid.Empty || result.Root.MaxArchiveBytes <= 0 || result.Root.MaxStagingBytes <= 0 ||
            result.Version.State != "Ready" || result.Version.SizeBytes <= 0 || result.Version.EstimatedDbBytes <= 0 ||
            result.Version.SizeBytes > result.Root.MaxArchiveBytes || result.Version.Format is not ("dt" or "bak") ||
            result.Version.Sha256.Length != 64 || !result.Version.Sha256.All(Uri.IsHexDigit)))
            throw Reject(409, "ArchiveNotReady");
        return (result.Version, result.Archive, result.Root);
    }

    private IQueryable<EnvironmentRecord> Visible(Actor actor) => db.TestEnvironments.AsNoTracking().Where(x =>
        actor.IsAdministrator || (x.OwnerId == actor.UserId && db.ProjectMemberships.Any(m => m.UserId == actor.UserId && m.ProjectId == x.ProjectId)));

    public async Task<RequestView?> GetRequestAsync(Actor actor, Guid id, CancellationToken ct) =>
        await (from r in db.EnvironmentRequests.AsNoTracking() join e in Visible(actor) on r.EnvironmentId equals e.Id
               where r.Id == id select new RequestView(r.Id, r.EnvironmentId, r.JobId, r.State, r.DisplayName, r.ProjectId, r.CreatedAt)).SingleOrDefaultAsync(ct);

    public async Task<IReadOnlyList<EnvironmentView>> ListEnvironmentsAsync(Actor actor, CancellationToken ct) =>
        await Visible(actor).OrderBy(x => x.Id).Select(x => new EnvironmentView(x.Id, x.ProjectId, x.DisplayName, x.State, x.ExpiresAt)).ToListAsync(ct);

    public async Task<EnvironmentView?> GetEnvironmentAsync(Actor actor, Guid id, CancellationToken ct) =>
        await Visible(actor).Where(x => x.Id == id).Select(x => new EnvironmentView(x.Id, x.ProjectId, x.DisplayName, x.State, x.ExpiresAt)).SingleOrDefaultAsync(ct);

    private static RequestRejectedException Reject(int status, string code) => new(status, code);
}
// End of order admission implementation.
