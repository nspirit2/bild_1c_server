using System.Data;
using System.Text.Json;
using BuildSystem.Application;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Read-only coherent lifecycle observation. Never invokes expiry sweep or performs reconciliation.</summary>
public sealed class SqlStageStatus(BuildSystemDbContext db) : IStageStatus
{
    public Task<StageStatusView?> GetAsync(Actor actor, Guid environmentId, CancellationToken ct)
    {
        if (actor is null || actor.UserId == Guid.Empty)
            throw new RequestRejectedException(403, "Forbidden");
        return db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            // A stable relational view without scheduler applocks or writes. SQL Server retries
            // cover the entire read transaction, not independently sampled lifecycle fragments.
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var environment = await db.TestEnvironments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == environmentId &&
                (actor.IsAdministrator || (x.OwnerId == actor.UserId &&
                    db.ProjectMemberships.Any(m => m.UserId == actor.UserId && m.ProjectId == x.ProjectId) &&
                    db.Projects.Any(p => p.Id == x.ProjectId && p.Enabled))), ct);
            if (environment is null)
            {
                await tx.CommitAsync(ct);
                return null;
            }
            var request = await db.EnvironmentRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == environment.RequestId, ct);
            if (request is null) throw Inconsistent();
            var job = await db.EnvironmentJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.JobId, ct);
            if (job is null) throw Inconsistent();
            if (job.Kind != "ProvisionArchive")
            {
                await tx.CommitAsync(ct);
                return null;
            }
            var reservation = await db.EnvironmentReservations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == environment.ReservationId, ct);
            if (reservation is null || request.EnvironmentId != environment.Id || job.EnvironmentId != environment.Id ||
                job.RequestId != request.Id || request.JobId != job.Id || request.OwnerId != environment.OwnerId ||
                request.OwnerId != reservation.OwnerId || request.ProjectId != environment.ProjectId ||
                reservation.PoolId != environment.PoolId || reservation.StagingBytes < 0)
                throw Inconsistent();
            var receipts = await db.PendingArchiveCancellations.AsNoTracking().Where(x => x.EnvironmentId == environment.Id ||
                x.RequestId == request.Id || x.JobId == job.Id || x.ReservationId == reservation.Id).Take(2).ToListAsync(ct);
            // A retained receipt also makes drift back to apparently pending metadata anomalous.
            var cancellationLike = receipts.Count != 0 || environment.State == "Cancelled" ||
                request.State == "Cancelled" || job.State == "Cancelled";
            if (cancellationLike)
            {
                if (receipts.Count != 1) throw Inconsistent();
                await RequireCancellationProofAsync(db, environment, request, job, reservation, receipts[0], "StageStatusInconsistent", ct);
            }
            var lease = cancellationLike ? null : await db.AgentLeases.AsNoTracking().SingleOrDefaultAsync(x => x.JobId == job.Id, ct);
            var artifact = cancellationLike ? null : await db.StagedArtifacts.AsNoTracking().SingleOrDefaultAsync(x => x.JobId == job.Id, ct);
            if (artifact is not null)
            {
                if (lease is null || artifact.AgentId != lease.AgentId || artifact.FencingToken != lease.FencingToken ||
                    artifact.ReservationId != reservation.Id || artifact.ArchiveVersionId != request.ArchiveVersionId ||
                    artifact.SizeBytes <= 0 || artifact.SizeBytes > reservation.StagingBytes)
                    throw Inconsistent();
                var version = await db.DiskArchiveVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == artifact.ArchiveVersionId, ct);
                if (version is null || version.SizeBytes != artifact.SizeBytes || version.Format != artifact.Format ||
                    !string.Equals(version.Sha256, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw Inconsistent();
            }
            var restoreIntent = cancellationLike ? null : await RestoreIntentStatus.ReadAsync(db, environment, request, job, reservation,
                lease, artifact, "StageStatusInconsistent", ct);
            var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(ct);
            var code = cancellationLike ? "CancelledBeforeFirstLease" : Classify(job, environment, request, reservation, lease, artifact, now);
            var result = new StageStatusView(environment.Id, request.Id, job.Id, job.State, environment.State,
                request.State, reservation.State, reservation.StagingBytes, environment.HasUnsettledProvisioningOperation,
                artifact?.ArtifactId, artifact?.State, lease?.State, lease?.ExpiresAt, code, now)
            { RestoreIntent = restoreIntent };
            await tx.CommitAsync(ct);
            return result;
        });
    }

    // Call within the same serializable read after checking the tuple's edges and finding a unique receipt.
    internal static async Task RequireCancellationProofAsync(BuildSystemDbContext db, EnvironmentRecord environment,
        EnvironmentRequest request, EnvironmentJob job, EnvironmentReservation reservation,
        PendingArchiveCancellationRecord receipt, string inconsistentCode, CancellationToken ct)
    {
        RequestRejectedException Inconsistent() => new(409, inconsistentCode);
        var environmentId = environment.Id;
        var requestId = request.Id;
        var jobId = job.Id;
        var reservationId = reservation.Id;
        if (environment.State != "Cancelled" || request.State != "Cancelled" || job.State != "Cancelled" ||
            reservation.State != "Released" || environment.HasUnsettledProvisioningOperation || environment.ExpiresAt is not null ||
            receipt.EnvironmentId != environmentId || receipt.RequestId != requestId || receipt.JobId != jobId ||
            receipt.ReservationId != reservationId || receipt.PoolId != environment.PoolId ||
            receipt.ActorId == Guid.Empty || receipt.CancelledAt == default || receipt.CancelledAt.Offset != TimeSpan.Zero)
            throw Inconsistent();

        var source = await (from v in db.DiskArchiveVersions.AsNoTracking()
                            join a in db.DiskArchives.AsNoTracking() on v.ArchiveId equals a.Id
                            where v.Id == request.ArchiveVersionId
                            select new { a.Id, a.RootId }).SingleOrDefaultAsync(ct);
        if (source is null || receipt.RootId != source.RootId ||
            !MatchesCancellationSnapshot(job, receipt, request.ArchiveVersionId, source.Id, reservation.StagingBytes))
            throw Inconsistent();

        // Check every order anchor, including on rows whose other pins could be inconsistent.
        // Cleanup and target execution rows require an artifact, retirement or restore intent by FK.
        if (await db.EnvironmentRequests.AnyAsync(x => x.Id != requestId && (x.JobId == jobId || x.EnvironmentId == environmentId), ct) ||
            await db.EnvironmentJobs.AnyAsync(x => x.Id != jobId && x.EnvironmentId == environmentId, ct) ||
            await db.AgentLeases.AnyAsync(x => x.JobId == jobId, ct) ||
            await db.AgentEvents.AnyAsync(x => x.JobId == jobId, ct) ||
            await db.AuditEvents.AnyAsync(x => x.TargetId == jobId && x.Action == "ArchiveStageLeased", ct) ||
            await db.StagedArtifacts.AnyAsync(x => x.JobId == jobId || x.ReservationId == reservationId, ct) ||
            await db.StageRetirementIntents.AnyAsync(x => x.EnvironmentId == environmentId || x.JobId == jobId || x.ReservationId == reservationId, ct) ||
            await db.StagingAllocationReleases.AnyAsync(x => x.ReservationId == reservationId, ct) ||
            await db.RestoreIntents.AnyAsync(x => x.EnvironmentId == environmentId || x.RequestId == requestId ||
                x.StageJobId == jobId || x.ReservationId == reservationId, ct) ||
            await db.TargetObservations.AnyAsync(x => x.EnvironmentId == environmentId || x.StageJobId == jobId || x.ReservationId == reservationId, ct))
            throw Inconsistent();
    }

    private static bool MatchesCancellationSnapshot(EnvironmentJob job, PendingArchiveCancellationRecord receipt,
        Guid versionId, Guid archiveId, long stagingBytes)
    {
        try
        {
            using var document = JsonDocument.Parse(job.SnapshotJson);
            var snapshot = document.RootElement;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return snapshot.ValueKind == JsonValueKind.Object && snapshot.EnumerateObject().All(p => names.Add(p.Name)) &&
                snapshot.GetProperty("reservationId").GetGuid() == receipt.ReservationId &&
                snapshot.GetProperty("poolId").GetGuid() == receipt.PoolId &&
                snapshot.GetProperty("sourceRootId").GetGuid() == receipt.RootId &&
                snapshot.GetProperty("sourceArchiveId").GetGuid() == archiveId &&
                snapshot.GetProperty("archiveVersionId").GetGuid() == versionId &&
                stagingBytes > 0 && snapshot.GetProperty("stagingBytes").GetInt64() == stagingBytes;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static string Classify(EnvironmentJob job, EnvironmentRecord environment, EnvironmentRequest request,
        EnvironmentReservation reservation, AgentLease? lease, StagedArtifact? artifact, DateTimeOffset now)
    {
        // Known anomalous states are observations, never an authorization to reset/delete/reassign.
        if (job.State == "NeedsAttention" || environment.State == "NeedsAttention" || lease?.State == "NeedsAttention")
            return "NeedsAttention";
        if (reservation.State != "Held" || !environment.HasUnsettledProvisioningOperation ||
            environment.State is not ("Requested" or "Provisioning") || request.State is not ("Pending" or "Provisioning"))
            return "Inconsistent";
        if (job.State == "StageCompleted" && lease?.State == "Completed" && artifact?.State == "ContentVerified")
            return "StageCompleted";
        if (job.State == "Pending" && lease is null && artifact is null) return "AwaitingStage";
        if (job.State == "Leased" && lease?.State == "Active" && (artifact is null || artifact.State == "ContentVerified"))
        {
            if (lease.ExpiresAt <= now) return "LeaseExpired";
            return artifact is null ? "StageActive" : "ArtifactReported";
        }
        return "Inconsistent";
    }

    private static RequestRejectedException Inconsistent() => new(409, "StageStatusInconsistent");
}
// EOF
