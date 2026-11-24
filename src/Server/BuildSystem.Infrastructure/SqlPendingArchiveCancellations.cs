using System.Data;
using System.Text.Json;
using BuildSystem.Application;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Opt-in metadata transition only. Never invokes the agent expiry sweep or external cleanup.</summary>
public sealed class SqlPendingArchiveCancellations(BuildSystemDbContext db, bool enabled = false) : IPendingArchiveCancellations
{
    private const string AuditAction = "PendingArchiveCancelled";

    public Task<PendingArchiveCancellationReceipt> CancelAsync(Actor actor, Guid environmentId,
        PendingArchiveCancellationRequest input, CancellationToken ct)
    {
        if (!enabled) throw new RequestRejectedException(409, "PendingArchiveCancellationDisabled");
        if (environmentId == Guid.Empty || input is null || input.ExpectedJobId == Guid.Empty)
            throw new RequestRejectedException(400, "InvalidPendingArchiveCancellation");
        if (actor is null || actor.UserId == Guid.Empty) throw NotFound();

        return db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            // Only lock-key hints. Do not take serializable row/range locks before the matching application locks.
            var ownerPool = await db.TestEnvironments.Where(e => e.Id == environmentId)
                .Select(e => new { e.OwnerId, e.PoolId }).SingleOrDefaultAsync(ct);
            var rootHint = await (from e in db.TestEnvironments
                                  join q in db.EnvironmentRequests on e.RequestId equals q.Id
                                  join v in db.DiskArchiveVersions on q.ArchiveVersionId equals v.Id
                                  join a in db.DiskArchives on v.ArchiveId equals a.Id
                                  where e.Id == environmentId select (Guid?)a.RootId).SingleOrDefaultAsync(ct);

            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await LockAsync("BuildSystem:agent-control", ct);
            var hasHints = ownerPool is not null && ownerPool.OwnerId != Guid.Empty && ownerPool.PoolId != Guid.Empty &&
                rootHint.HasValue && rootHint.Value != Guid.Empty;
            if (hasHints)
            {
                await LockAsync("BuildSystem:owner:" + ownerPool!.OwnerId.ToString("N"), ct);
                await LockAsync("BuildSystem:root:" + rootHint!.Value.ToString("N"), ct);
                await LockAsync("BuildSystem:pool:" + ownerPool.PoolId.ToString("N"), ct);
            }

            // A stale principal's administrator claim is not authority. Authorize before disclosing state,
            // including replays and cases where the lock hints could not be resolved.
            var userId = actor.UserId;
            var admin = from membership in db.UserRoles
                        join role in db.Roles on membership.RoleId equals role.Id
                        where membership.UserId == userId && role.NormalizedName == "ADMINISTRATOR"
                        select role.Id;
            var authorized = db.TestEnvironments.Where(e => e.Id == environmentId &&
                db.Users.Any(u => u.Id == userId) && db.Projects.Any(p => p.Id == e.ProjectId && p.Enabled) &&
                (admin.Any() || (e.OwnerId == userId && db.ProjectMemberships.Any(m => m.UserId == userId && m.ProjectId == e.ProjectId))));
            if (!await authorized.AnyAsync(ct)) throw NotFound();
            if (!hasHints) throw Mismatch();

            var i = await (from e in authorized
                           join q in db.EnvironmentRequests on e.RequestId equals q.Id
                           join j in db.EnvironmentJobs on q.JobId equals j.Id
                           join r in db.EnvironmentReservations on e.ReservationId equals r.Id
                           join v in db.DiskArchiveVersions on q.ArchiveVersionId equals v.Id
                           join a in db.DiskArchives on v.ArchiveId equals a.Id
                           join root in db.ArchiveRoots on a.RootId equals root.Id
                           join pool in db.EnvironmentPools on e.PoolId equals pool.Id
                           select new { e, q, j, r, v, a, root, pool }).AsTracking().SingleOrDefaultAsync(ct);
            if (i is null || i.j.Id != input.ExpectedJobId || i.j.Kind != "ProvisionArchive" ||
                i.e.OwnerId != ownerPool!.OwnerId || i.root.Id != rootHint || i.pool.Id != ownerPool.PoolId ||
                i.q.EnvironmentId != i.e.Id || i.j.EnvironmentId != i.e.Id || i.j.RequestId != i.q.Id ||
                i.q.OwnerId != i.e.OwnerId || i.q.OwnerId != i.r.OwnerId || i.q.ProjectId != i.e.ProjectId ||
                i.r.PoolId != i.pool.Id || i.e.ExpiresAt is not null ||
                new[] { i.q.Id, i.r.Id, i.v.Id, i.a.Id, i.e.ProjectId }.Contains(Guid.Empty)) throw Mismatch();

            // These reverse edges are not all constrained by unique foreign keys. Do not release a
            // reservation if another durable request/job also claims the same operation.
            var requestId = i.q.Id;
            var jobId = i.j.Id;
            var reservationId = i.r.Id;
            if (await db.EnvironmentRequests.AnyAsync(q => q.Id != requestId && (q.JobId == jobId || q.EnvironmentId == environmentId), ct) ||
                await db.EnvironmentJobs.AnyAsync(j => j.Id != jobId && j.EnvironmentId == environmentId, ct)) throw Mismatch();

            var receipts = await db.PendingArchiveCancellations.AsNoTracking().Where(c => c.EnvironmentId == environmentId ||
                c.RequestId == requestId || c.JobId == jobId || c.ReservationId == reservationId).ToListAsync(ct);
            if (receipts.Count > 1) throw Mismatch();
            var prior = receipts.SingleOrDefault();
            if (prior is null)
            {
                // Current routing is a first-transition check, not part of replaying historical proof.
                if (i.pool.AgentScopeId == Guid.Empty || i.pool.SqlServerId == Guid.Empty || i.pool.ClusterId == Guid.Empty ||
                    i.root.AgentScopeId != i.pool.AgentScopeId ||
                    i.r.SqlServerId != i.pool.SqlServerId || i.r.ClusterId != i.pool.ClusterId ||
                    !SnapshotMatches(i.j, i.q, i.r, i.v, i.a, i.root, i.pool) ||
                    i.q.State != "Pending" || i.j.State != "Pending" || i.e.State != "Requested" ||
                    i.r.State != "Held" || !i.e.HasUnsettledProvisioningOperation ||
                    await db.AuditEvents.AnyAsync(a => a.TargetId == environmentId && EF.Functions.Collate(a.Action, AuditMatchCollation) == AuditAction, ct)) throw Mismatch();
            }
            else if (prior.EnvironmentId != environmentId || prior.RequestId != requestId || prior.JobId != jobId ||
                prior.ReservationId != reservationId || prior.RootId != i.root.Id || prior.PoolId != i.pool.Id ||
                prior.ActorId == Guid.Empty || prior.CancelledAt == default || prior.CancelledAt.Offset != TimeSpan.Zero ||
                i.q.State != "Cancelled" || i.j.State != "Cancelled" || i.e.State != "Cancelled" ||
                i.r.State != "Released" || i.e.HasUnsettledProvisioningOperation) throw Mismatch();

            if (await HasPriorAuthorityAsync(environmentId, requestId, jobId, reservationId, ct)) throw Mismatch();
            if (prior is not null)
            {
                if (!await StageCancellationProof.ProvenJobIds(db).AnyAsync(id => id == jobId, ct)) throw Mismatch();
                await tx.CommitAsync(ct);
                return Receipt(prior);
            }

            var now = await db.Database.SqlQuery<DateTimeOffset>(
                $"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(ct);
            var receipt = new PendingArchiveCancellationRecord
            {
                EnvironmentId = environmentId, RequestId = requestId, JobId = jobId, ReservationId = reservationId,
                RootId = i.root.Id, PoolId = i.pool.Id, ActorId = userId, CancelledAt = now
            };
            db.PendingArchiveCancellations.Add(receipt);
            db.AuditEvents.Add(new AuditEvent { Id = Guid.NewGuid(), ActorId = userId,
                Action = AuditAction, TargetId = environmentId, CreatedAt = now });
            i.q.State = i.j.State = i.e.State = "Cancelled";
            i.e.HasUnsettledProvisioningOperation = false;
            i.r.State = "Released";
            await db.SaveChangesAsync(ct);
            // The first success must satisfy the same proof as replay, history and quota.
            if (!await StageCancellationProof.ProvenJobIds(db).AnyAsync(id => id == jobId, ct)) throw Mismatch();
            await tx.CommitAsync(ct);
            return Receipt(receipt);
        });
    }

    private const string AuditMatchCollation = "Latin1_General_100_CI_AS";

    private async Task<bool> HasPriorAuthorityAsync(Guid environmentId, Guid requestId, Guid jobId, Guid reservationId,
        CancellationToken ct)
    {
        // Match by every direct order anchor so an inconsistent secondary pin cannot hide a blocker.
        // Cleanup authorizations/results require artifacts and/or retirement intents; creation
        // operations, permits and execution claims require a retained restore intent through FKs.
        // Registration evidence and observation receipts are diagnostic, not execution authority.
        return await db.AgentLeases.AnyAsync(x => x.JobId == jobId, ct) ||
            await db.AgentEvents.AnyAsync(x => x.JobId == jobId, ct) ||
            await db.StagedArtifacts.AnyAsync(x => x.JobId == jobId || x.ReservationId == reservationId, ct) ||
            await db.StageRetirementIntents.AnyAsync(x => x.EnvironmentId == environmentId || x.JobId == jobId || x.ReservationId == reservationId, ct) ||
            await db.StagingAllocationReleases.AnyAsync(x => x.ReservationId == reservationId, ct) ||
            await db.RestoreIntents.AnyAsync(x => x.EnvironmentId == environmentId || x.RequestId == requestId ||
                x.StageJobId == jobId || x.ReservationId == reservationId, ct) ||
            await db.TargetObservations.AnyAsync(x => x.EnvironmentId == environmentId || x.StageJobId == jobId || x.ReservationId == reservationId, ct) ||
            await db.AuditEvents.AnyAsync(x => x.TargetId == jobId && EF.Functions.Collate(x.Action, AuditMatchCollation) == "ArchiveStageLeased", ct);
    }

    private static bool SnapshotMatches(EnvironmentJob job, EnvironmentRequest request, EnvironmentReservation reservation,
        DiskArchiveVersion version, DiskArchive archive, ArchiveRoot root, EnvironmentPool pool)
    {
        if (version.Format is not ("dt" or "bak") || version.SizeBytes <= 0 ||
            version.EstimatedDbBytes <= 0 || version.EstimatedDbBytes != reservation.ReservedBytes ||
            version.SizeBytes != reservation.StagingBytes || version.Sha256 is not { Length: 64 } ||
            !version.Sha256.All(Uri.IsHexDigit) || !SqlAgentControl.CanonicalStagePath(archive.RelativePath, version.Format) ||
            request.TtlDays is < 1 or > 36500) return false;

        var targetName = "bs_test_" + reservation.Id.ToString("N");
        if (reservation.SqlDbName != targetName || reservation.InfobaseName != targetName) return false;
        try
        {
            using var document = JsonDocument.Parse(job.SnapshotJson);
            var s = document.RootElement;
            // Reject ambiguous property names rather than silently selecting a duplicate or alias.
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (s.EnumerateObject().Any(p => !names.Add(p.Name))) return false;
            return Id("reservationId") == reservation.Id && Id("poolId") == pool.Id &&
                Id("SqlServerId") == reservation.SqlServerId && Id("ClusterId") == reservation.ClusterId &&
                Id("AgentScopeId") == pool.AgentScopeId && Text("sqlDbName") == reservation.SqlDbName &&
                Text("infobaseName") == reservation.InfobaseName && Id("sourceRootId") == root.Id &&
                Id("sourceArchiveId") == archive.Id && Text("sourceRelativePath") == archive.RelativePath &&
                Id("archiveVersionId") == version.Id && Text("Sha256") == version.Sha256 &&
                Size("SizeBytes") == version.SizeBytes && Text("Format") == version.Format &&
                Size("reservedBytes") == reservation.ReservedBytes && Size("stagingBytes") == reservation.StagingBytes &&
                Text("PreparationProfileVersion") is { Length: <= 128 } profile && !string.IsNullOrWhiteSpace(profile) &&
                Text("IsolationPolicyId") is { Length: <= 128 } isolation && !string.IsNullOrWhiteSpace(isolation) &&
                Size("TtlDays") == request.TtlDays && Text("admissionMode") == "archive" &&
                s.GetProperty("stageOnly").ValueKind == JsonValueKind.False &&
                s.GetProperty("executable").ValueKind == JsonValueKind.False &&
                s.GetProperty("scheduledJobsMustRemainBlocked").ValueKind == JsonValueKind.True &&
                s.GetProperty("automaticDeletionEnabled").ValueKind == JsonValueKind.False &&
                s.GetProperty("automaticDeletionGraceDays").ValueKind == JsonValueKind.Null;
            Guid Id(string key) => s.GetProperty(key).GetGuid();
            string? Text(string key) => s.GetProperty(key).GetString();
            long Size(string key) => s.GetProperty(key).GetInt64();
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    private Task LockAsync(string resource, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync(
        $"DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=15000; IF @r IN (-1, -3) THROW 51001, 'Transient cancellation lock contention', 1; IF @r < 0 THROW 51000, 'Cancellation lock cancelled or invalid', 1;", ct);

    private static PendingArchiveCancellationReceipt Receipt(PendingArchiveCancellationRecord row) =>
        new(row.EnvironmentId, row.RequestId, row.JobId, row.ReservationId, row.RootId, row.PoolId, row.ActorId, row.CancelledAt);

    private static RequestRejectedException NotFound() => new(404, "PendingArchiveCancellationNotFound");
    private static RequestRejectedException Mismatch() => new(409, "PendingArchiveCancellationMismatch");
}
