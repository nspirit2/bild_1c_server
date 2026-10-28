using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using BuildSystem.Application;
using BuildSystem.Contracts;
using Microsoft.EntityFrameworkCore;
namespace BuildSystem.Infrastructure;

/// <summary>Coherent read-only cleanup/accounting observation; no file existence claim or reconciliation.</summary>
public sealed class SqlCleanupStatus(BuildSystemDbContext db) : ICleanupStatus
{
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public Task<CleanupStatusView?> GetAsync(Actor actor, Guid environmentId, CancellationToken ct)
    {
        if (actor is null || actor.UserId == Guid.Empty) throw new RequestRejectedException(403, "Forbidden");
        return db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var env = await db.TestEnvironments.AsNoTracking().SingleOrDefaultAsync(e => e.Id == environmentId &&
                db.Projects.Any(p => p.Id == e.ProjectId && p.Enabled) &&
                (actor.IsAdministrator || (e.OwnerId == actor.UserId && db.ProjectMemberships.Any(m => m.UserId == actor.UserId && m.ProjectId == e.ProjectId))), ct);
            if (env is null) { await tx.CommitAsync(ct); return null; }
            var request = await db.EnvironmentRequests.AsNoTracking().SingleOrDefaultAsync(q => q.Id == env.RequestId, ct);
            if (request is null) throw Inconsistent();
            var job = await db.EnvironmentJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == request.JobId, ct);
            var reservation = await db.EnvironmentReservations.AsNoTracking().SingleOrDefaultAsync(r => r.Id == env.ReservationId, ct);
            if (job is null || reservation is null || request.EnvironmentId != env.Id || job.EnvironmentId != env.Id || job.RequestId != request.Id ||
                request.OwnerId != env.OwnerId || request.ProjectId != env.ProjectId || reservation.OwnerId != env.OwnerId || reservation.PoolId != env.PoolId || reservation.StagingBytes < 0)
                throw Inconsistent();
            if (job.Kind != "ProvisionArchive") { await tx.CommitAsync(ct); return null; }
            var lease = await db.AgentLeases.AsNoTracking().SingleOrDefaultAsync(l => l.JobId == job.Id, ct);
            var artifacts = await db.StagedArtifacts.AsNoTracking().Where(a => a.JobId == job.Id || a.ReservationId == reservation.Id).Take(2).ToListAsync(ct);
            if (artifacts.Count > 1) throw Inconsistent();
            var artifact = artifacts.SingleOrDefault();
            Guid? rootId = null;
            if (artifact is not null)
            {
                if (lease is null || artifact.JobId != job.Id || artifact.AgentId != lease.AgentId || artifact.FencingToken != lease.FencingToken ||
                    artifact.ReservationId != reservation.Id || artifact.ArchiveVersionId != request.ArchiveVersionId || artifact.State != "ContentVerified" ||
                    artifact.SizeBytes <= 0 || artifact.SizeBytes > reservation.StagingBytes) throw Inconsistent();
                var version = await db.DiskArchiveVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == artifact.ArchiveVersionId, ct);
                if (version is null || version.SizeBytes != artifact.SizeBytes || version.Format != artifact.Format || !string.Equals(version.Sha256, artifact.Sha256, StringComparison.OrdinalIgnoreCase)) throw Inconsistent();
                rootId = await db.DiskArchives.AsNoTracking().Where(a => a.Id == version.ArchiveId).Select(a => (Guid?)a.RootId).SingleOrDefaultAsync(ct);
                if (rootId is null) throw Inconsistent();
            }
            var intents = await db.StageRetirementIntents.AsNoTracking().Where(i => i.EnvironmentId == env.Id || i.JobId == job.Id || i.ReservationId == reservation.Id).Take(2).ToListAsync(ct);
            if (intents.Count > 1) throw Inconsistent();
            var intent = intents.SingleOrDefault();
            if (intent is not null && (artifact is null || lease is null || intent.EnvironmentId != env.Id || intent.JobId != job.Id || intent.ReservationId != reservation.Id ||
                intent.ArtifactId != artifact.ArtifactId || intent.AgentId != artifact.AgentId || intent.FencingToken != artifact.FencingToken || intent.State != "Requested" ||
                job.State != "StageCompleted" || lease.State != "Completed" || reservation.State != "Held")) throw Inconsistent();
            var intentId = intent?.RetirementId; var artifactId = artifact?.ArtifactId;
            var auths = await db.StageCleanupAuthorizations.AsNoTracking().Where(a =>
                a.RetirementId == intentId || a.ArtifactId == artifactId).Take(2).ToListAsync(ct);
            if (auths.Count > 1) throw Inconsistent();
            var auth = auths.SingleOrDefault();
            if (auth is not null)
            {
                if (intent is null || artifact is null || auth.RetirementId != intent.RetirementId || auth.ArtifactId != artifact.ArtifactId || auth.AgentId != intent.AgentId || auth.StageFencingToken != intent.FencingToken) throw Inconsistent();
                StageRetirementPlan? plan;
                try { plan = JsonSerializer.Deserialize<StageRetirementPlan>(auth.PlanJson, Json); } catch (JsonException) { throw Inconsistent(); }
                if (plan is null || plan.RetirementId != intent.RetirementId || plan.EnvironmentId != env.Id || plan.JobId != job.Id || plan.ArtifactId != artifact.ArtifactId ||
                    plan.ReservationId != reservation.Id || plan.ArchiveVersionId != artifact.ArchiveVersionId || plan.FencingToken != artifact.FencingToken ||
                    plan.Sha256 != artifact.Sha256 || plan.SizeBytes != artifact.SizeBytes || plan.Format != artifact.Format) throw Inconsistent();
                try
                {
                    using var snapshot = JsonDocument.Parse(job.SnapshotJson); var s = snapshot.RootElement;
                    if (s.GetProperty("reservationId").GetGuid() != reservation.Id || s.GetProperty("poolId").GetGuid() != reservation.PoolId ||
                        s.GetProperty("sourceRootId").GetGuid() != rootId || s.GetProperty("archiveVersionId").GetGuid() != artifact.ArchiveVersionId ||
                        s.GetProperty("AgentScopeId").GetGuid() != auth.AgentId || s.GetProperty("stagingBytes").GetInt64() != reservation.StagingBytes ||
                        s.GetProperty("SizeBytes").GetInt64() != artifact.SizeBytes || s.GetProperty("Format").GetString() != artifact.Format ||
                        !string.Equals(s.GetProperty("Sha256").GetString(), artifact.Sha256, StringComparison.OrdinalIgnoreCase)) throw Inconsistent();
                }
                catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentException) { throw Inconsistent(); }
            }
            var authorizationId = auth?.AuthorizationId;
            var results = await db.StageCleanupResults.AsNoTracking().Where(r =>
                r.AuthorizationId == authorizationId || r.ArtifactId == artifactId).Take(2).ToListAsync(ct);
            if (results.Count > 1) throw Inconsistent();
            var result = results.SingleOrDefault();
            if (result is not null && (auth is null || result.AuthorizationId != auth.AuthorizationId || result.ArtifactId != auth.ArtifactId || result.AgentId != auth.AgentId || result.StageFencingToken != auth.StageFencingToken)) throw Inconsistent();
            var resultId = result?.ResultId;
            var releases = await db.StagingAllocationReleases.AsNoTracking().Where(r => r.ReservationId == reservation.Id || r.ResultId == resultId ||
                r.AuthorizationId == authorizationId).Take(2).ToListAsync(ct);
            if (releases.Count > 1) throw Inconsistent();
            var release = releases.SingleOrDefault();
            if (release is not null && (result is null || auth is null || artifact is null || release.ResultId != result.ResultId || release.AuthorizationId != auth.AuthorizationId ||
                release.ReservationId != reservation.Id || release.PoolId != reservation.PoolId || release.RootId != rootId || release.ReleasedBytes != reservation.StagingBytes ||
                release.ReleasedBytes != artifact.SizeBytes || release.ReleasedBytes <= 0)) throw Inconsistent();
            var state = release is not null ? "StagingReleased" : result is not null ? "DeletedReported" : auth is not null ? "Authorized" : intent is not null ? "IntentRequested" : "None";
            var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(ct);
            var released = release?.ReleasedBytes ?? 0;
            var view = new CleanupStatusView(env.Id, job.Id, artifact?.ArtifactId, intent?.RetirementId, auth?.AuthorizationId, result?.ResultId, release?.ReleaseId,
                state, reservation.StagingBytes, reservation.StagingBytes - released, released, now);
            await tx.CommitAsync(ct); return view;
        });
    }
    private static RequestRejectedException Inconsistent() => new(409, "CleanupStatusInconsistent");
}
// end
