using System.Data;
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
            var cancellationLike = receipts.Count != 0 || CancellationLike(environment.State, "Cancelled") ||
                CancellationLike(request.State, "Cancelled") || CancellationLike(job.State, "Cancelled") ||
                CancellationLike(reservation.State, "Released");
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
            var code = cancellationLike ? StageCancellationProof.Code : Classify(job, environment, request, reservation, lease, artifact, now);
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
        if (receipt.EnvironmentId != environment.Id || receipt.RequestId != request.Id ||
            receipt.JobId != job.Id || receipt.ReservationId != reservation.Id ||
            !await StageCancellationProof.ProvenJobIds(db).AnyAsync(id => id == job.Id, ct))
            throw new RequestRejectedException(409, inconsistentCode);
    }

    internal static bool CancellationLike(string state, string value) =>
        string.Equals(state.TrimEnd(' '), value, StringComparison.OrdinalIgnoreCase);

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
