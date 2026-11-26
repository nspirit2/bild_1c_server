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
                                    (input.StageOnly || (p.IsolationVerified && p.ScheduledJobsSuppressionVerified && p.ResourceLimitVerified)) &&
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
                pool.IsolationPolicyId != source.Root.IsolationPolicyId ||
                (!input.StageOnly && (!pool.IsolationVerified || !pool.ScheduledJobsSuppressionVerified || !pool.ResourceLimitVerified)) ||
                (source.Version.Format == "dt" ? !pool.SupportsDt : !pool.SupportsBak) || pool.MaxStagingBytes <= 0)
                throw Reject(409, "PoolNotReady");
            var limits = await db.EnvironmentQuotas.SingleOrDefaultAsync(x => x.SubjectId == actor.UserId, ct)
                ?? throw Reject(409, "QuotaNotConfigured");
            // ProvenJobIds validates the receipt against the current environment, request, job, reservation,
            // pool and source root. Use that same receipt's reservation when applying the accounting exemption.
            var provenCancellations = from cancellation in db.PendingArchiveCancellations
                                      join jobId in StageCancellationProof.ProvenJobIds(db) on cancellation.JobId equals jobId
                                      select cancellation;
            var rows = await (from r in db.EnvironmentReservations
                              join environment in db.TestEnvironments on r.Id equals environment.ReservationId into environments
                              from e in environments.DefaultIfEmpty()
                              where r.OwnerId == actor.UserId || r.PoolId == poolId || e.OwnerId == actor.UserId || e.PoolId == poolId ||
                                  db.PendingArchiveCancellations.Any(c => c.ReservationId == r.Id && c.PoolId == poolId)
                              select new
                              {
                                  Reservation = r,
                                  Environment = e,
                                  VerifiedCancellation = provenCancellations.Any(c => c.ReservationId == r.Id &&
                                      c.EnvironmentId == e.Id && c.RequestId == e.RequestId && c.PoolId == r.PoolId),
                                  HasCancellationReceipt = db.PendingArchiveCancellations.Any(c => c.ReservationId == r.Id ||
                                      c.EnvironmentId == e.Id || c.RequestId == e.RequestId ||
                                      db.EnvironmentRequests.Any(q => q.Id == e.RequestId && q.JobId == c.JobId))
                              }).ToListAsync(ct);
            var snapshot = new List<EnvironmentQuotaEntry>(rows.Count);
            foreach (var row in rows)
            {
                var r = row.Reservation;
                var e = row.Environment;
                // Unproven terminal metadata must neither release a quota slot nor cause a domain-policy exception.
                if (e is null || e.OwnerId != r.OwnerId || e.PoolId != r.PoolId || r.Id == Guid.Empty ||
                    r.OwnerId == Guid.Empty || r.PoolId == Guid.Empty || r.ReservedBytes <= 0 ||
                    !Enum.TryParse<EnvironmentState>(e.State, out var state) || state.ToString() != e.State ||
                    (state == EnvironmentState.Deleted && e.HasUnsettledProvisioningOperation) ||
                    (!row.VerifiedCancellation && (state == EnvironmentState.Cancelled || row.HasCancellationReceipt || IsReleased(r.State))))
                    throw Reject(409, "QuotaExceeded");
                snapshot.Add(new EnvironmentQuotaEntry(r.Id, r.OwnerId, r.PoolId, r.ReservedBytes, state,
                    e.HasUnsettledProvisioningOperation, row.VerifiedCancellation));
            }
            var reservationId = Guid.NewGuid();
            var name = "bs_test_" + reservationId.ToString("N");
            var proposed = new BuildSystem.Domain.EnvironmentReservation(reservationId, actor.UserId, poolId, source.Version.EstimatedDbBytes,
                new ResourceIdentity(pool.SqlServerId.ToString("D"), name), new ResourceIdentity(pool.ClusterId.ToString("D"), name));
            var decision = EnvironmentQuotaPolicy.CanReserve(proposed, TimeSpan.FromDays(input.TtlDays),
                new EnvironmentQuotaLimits(limits.MaxActiveEnvironments, limits.MaxConcurrentProvisioning, limits.MaxStorageBytes, TimeSpan.FromDays(limits.MaxTtlDays)),
                new PoolBudget(pool.Enabled, pool.CapacityBytes, pool.MaxEnvironmentBytes, pool.MaxConcurrentProvisioning), snapshot);
            if (!decision.Allowed) throw Reject(409, "QuotaExceeded");
            // The separate cleanup ledger still releases staging only. A bare Released state never does.
            var coherentStagingReleases =
                from release in db.StagingAllocationReleases
                join reservation in db.EnvironmentReservations on release.ReservationId equals reservation.Id
                join result in db.StageCleanupResults on release.ResultId equals result.ResultId
                join auth in db.StageCleanupAuthorizations on release.AuthorizationId equals auth.AuthorizationId
                join intent in db.StageRetirementIntents on auth.RetirementId equals intent.RetirementId
                join artifact in db.StagedArtifacts on auth.ArtifactId equals artifact.ArtifactId
                join job in db.EnvironmentJobs on intent.JobId equals job.Id
                join environment in db.TestEnvironments on intent.EnvironmentId equals environment.Id
                join request in db.EnvironmentRequests on environment.RequestId equals request.Id
                join lease in db.AgentLeases on job.Id equals lease.JobId
                join version in db.DiskArchiveVersions on artifact.ArchiveVersionId equals version.Id
                join archive in db.DiskArchives on version.ArchiveId equals archive.Id
                where result.AuthorizationId == auth.AuthorizationId && result.ArtifactId == artifact.ArtifactId &&
                    intent.ArtifactId == artifact.ArtifactId && intent.ReservationId == reservation.Id &&
                    artifact.ReservationId == reservation.Id && artifact.JobId == intent.JobId &&
                    EF.Functions.Collate(intent.State, "Latin1_General_100_BIN2") == "Requested" && EF.Functions.DataLength(intent.State) == 18 &&
                    EF.Functions.Collate(artifact.State, "Latin1_General_100_BIN2") == "ContentVerified" && EF.Functions.DataLength(artifact.State) == 30 &&
                    artifact.SizeBytes == version.SizeBytes &&
                    EF.Functions.Collate(artifact.Format, "Latin1_General_100_BIN2") == EF.Functions.Collate(version.Format, "Latin1_General_100_BIN2") &&
                    EF.Functions.DataLength(artifact.Format) == EF.Functions.DataLength(version.Format) &&
                    EF.Functions.Collate(artifact.Sha256, "Latin1_General_100_CI_AS") == EF.Functions.Collate(version.Sha256, "Latin1_General_100_CI_AS") &&
                    EF.Functions.DataLength(artifact.Sha256) == EF.Functions.DataLength(version.Sha256) &&
                    result.AgentId == auth.AgentId && auth.AgentId == intent.AgentId && intent.AgentId == artifact.AgentId &&
                    artifact.FencingToken > 0 && result.StageFencingToken == auth.StageFencingToken &&
                    auth.StageFencingToken == intent.FencingToken && intent.FencingToken == artifact.FencingToken &&
                    EF.Functions.Collate(lease.State, "Latin1_General_100_BIN2") == "Completed" &&
                    EF.Functions.DataLength(lease.State) == 18 && lease.AgentId == artifact.AgentId &&
                    lease.FencingToken == artifact.FencingToken &&
                    job.EnvironmentId == environment.Id && job.RequestId == request.Id &&
                    request.EnvironmentId == environment.Id && request.JobId == job.Id && request.ArchiveVersionId == version.Id &&
                    environment.ReservationId == reservation.Id && environment.PoolId == reservation.PoolId &&
                    request.OwnerId == environment.OwnerId && request.OwnerId == reservation.OwnerId && request.ProjectId == environment.ProjectId &&
                    EF.Functions.Collate(job.Kind, "Latin1_General_100_BIN2") == "ProvisionArchive" && EF.Functions.DataLength(job.Kind) == 32 &&
                    EF.Functions.Collate(job.State, "Latin1_General_100_BIN2") == "StageCompleted" && EF.Functions.DataLength(job.State) == 28 &&
                    environment.HasUnsettledProvisioningOperation &&
                    (EF.Functions.Collate(environment.State, "Latin1_General_100_BIN2") == "Requested" && EF.Functions.DataLength(environment.State) == 18 ||
                     EF.Functions.Collate(environment.State, "Latin1_General_100_BIN2") == "Provisioning" && EF.Functions.DataLength(environment.State) == 24) &&
                    (EF.Functions.Collate(request.State, "Latin1_General_100_BIN2") == "Pending" && EF.Functions.DataLength(request.State) == 14 ||
                     EF.Functions.Collate(request.State, "Latin1_General_100_BIN2") == "Provisioning" && EF.Functions.DataLength(request.State) == 24) &&
                    EF.Functions.Collate(reservation.State, "Latin1_General_100_BIN2") == "Held" && EF.Functions.DataLength(reservation.State) == 8 &&
                    release.PoolId == reservation.PoolId && release.RootId == archive.RootId &&
                    release.ReleasedBytes > 0 && release.ReleasedBytes == reservation.StagingBytes &&
                    release.ReleasedBytes == artifact.SizeBytes &&
                    !db.StagingAllocationReleases.Any(other => other.AuthorizationId == release.AuthorizationId &&
                        other.ReleaseId != release.ReleaseId) &&
                    !db.RestoreIntents.Any(hold => hold.EnvironmentId == intent.EnvironmentId ||
                        hold.StageJobId == intent.JobId || hold.ReservationId == reservation.Id || hold.ArtifactId == artifact.ArtifactId ||
                        hold.RequestId == request.Id)
                select release;
            var heldStaging = db.EnvironmentReservations.Where(r =>
                !coherentStagingReleases.Any(release => release.ReservationId == r.Id));
            var poolStaging = await heldStaging.Where(r => r.PoolId == poolId &&
                !provenCancellations.Any(c => c.ReservationId == r.Id && c.PoolId == poolId))
                .Select(r => r.StagingBytes).ToListAsync(ct);
            var rootId = source.Root.Id;
            var reservationsOnRoot = from e in db.TestEnvironments
                                     join q in db.EnvironmentRequests on e.RequestId equals q.Id
                                     join v in db.DiskArchiveVersions on q.ArchiveVersionId equals v.Id
                                     join a in db.DiskArchives on v.ArchiveId equals a.Id
                                     where a.RootId == rootId
                                     select e.ReservationId;
            var rootStaging = await db.EnvironmentReservations.Where(r =>
                (reservationsOnRoot.Contains(r.Id) ||
                    db.PendingArchiveCancellations.Any(c => c.ReservationId == r.Id && c.RootId == rootId) ||
                    db.StagingAllocationReleases.Any(release => release.ReservationId == r.Id && release.RootId == rootId)) &&
                !provenCancellations.Any(c => c.ReservationId == r.Id && c.RootId == rootId))
                .Select(r => new { r.StagingBytes, r.State,
                    HasRelease = coherentStagingReleases.Any(release => release.ReservationId == r.Id && release.RootId == rootId),
                    HasCancellationReceipt = db.PendingArchiveCancellations.Any(c => c.ReservationId == r.Id ||
                        db.TestEnvironments.Any(e => e.ReservationId == r.Id && (c.EnvironmentId == e.Id || c.RequestId == e.RequestId ||
                            db.EnvironmentRequests.Any(q => q.Id == e.RequestId && q.JobId == c.JobId)))) }).ToListAsync(ct);
            // Check cancellation conflicts before applying the cleanup ledger exclusion. A reservation
            // on this root may be outside the owner/pool snapshot and already have a cleanup release.
            var heldOnRoot = rootStaging.Where(x => !x.HasRelease).ToList();
            if (rootStaging.Any(x => x.HasCancellationReceipt) || heldOnRoot.Any(x => x.StagingBytes < 0 || IsReleased(x.State)) ||
                poolStaging.Sum(x => (decimal)x) + source.Version.SizeBytes > pool.MaxStagingBytes ||
                heldOnRoot.Sum(x => (decimal)x.StagingBytes) + source.Version.SizeBytes > source.Root.MaxStagingBytes)
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
                Kind = input.StageOnly ? "ObserveArchiveStage" : "ProvisionArchive",
                SnapshotJson = JsonSerializer.Serialize(new { reservationId, poolId, pool.SqlServerId, pool.ClusterId, pool.AgentScopeId,
                    sqlDbName = name, infobaseName = name, sourceRootId = source.Root.Id, sourceArchiveId = source.Archive.Id, sourceRelativePath = source.Archive.RelativePath,
                    automaticDeletionEnabled = false, automaticDeletionGraceDays = (int?)null,
                    archiveVersionId = source.Version.Id, source.Version.Sha256, source.Version.SizeBytes, source.Version.Format,
                    reservedBytes = source.Version.EstimatedDbBytes, stagingBytes = source.Version.SizeBytes,
                    project.PreparationProfileVersion, project.IsolationPolicyId, input.TtlDays,
                    admissionMode = input.StageOnly ? "archive-stage-only" : "archive",
                    stageOnly = input.StageOnly, executable = false, scheduledJobsMustRemainBlocked = true }) });
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

    private static bool IsReleased(string state) => string.Equals(state.TrimEnd(), "Released", StringComparison.OrdinalIgnoreCase);

    private static RequestRejectedException Reject(int status, string code) => new(status, code);
}
// End of order admission implementation.
