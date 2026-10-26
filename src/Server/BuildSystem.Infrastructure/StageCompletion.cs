using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Completes only stage authority. Environment, request, quotas and reservations remain untouched.</summary>
public sealed class SqlStageCompletion(BuildSystemDbContext db, ArchiveStageOptions options) : IStageCompletion
{
    public Task<StageCompletionReceipt> CompleteAsync(string credential, CompleteStage request, CancellationToken ct)
    {
        if (!options.Enabled) throw new RequestRejectedException(409, "StageDisabled");
        return new SqlAgentControl(db).CompleteStageAsync(credential, request, ct);
    }
}

public sealed partial class SqlAgentControl
{
    internal Task<StageCompletionReceipt> CompleteStageAsync(string credential, CompleteStage request, CancellationToken ct)
    {
        if (request is null || request.JobId == Guid.Empty || request.FencingToken <= 0 || request.ArtifactId == Guid.Empty)
            throw Reject(400, "InvalidStageCompletion");
        // Reuse expiry sweep/savepoint: initially expired Active leases become NeedsAttention even on rejection.
        return Transaction(async now =>
        {
            var agent = await AuthenticateStageAsync(credential, ct);
            var sourceRoot = await (from artifact in db.StagedArtifacts
                                    join version in db.DiskArchiveVersions on artifact.ArchiveVersionId equals version.Id
                                    join archive in db.DiskArchives on version.ArchiveId equals archive.Id
                                    where artifact.ArtifactId == request.ArtifactId
                                    select (Guid?)archive.RootId).SingleOrDefaultAsync(ct);
            if (sourceRoot is null) throw Reject(409, "StageCompletionMismatch");
            await StageLockAsync("BuildSystem:root:" + sourceRoot.Value.ToString("N"), ct);
            await StageLockAsync("BuildSystem:pool:" + agent.PoolId.ToString("N"), ct);
            var i = await (from artifact in db.StagedArtifacts
                           join lease in db.AgentLeases on artifact.JobId equals lease.JobId
                           join job in db.EnvironmentJobs on artifact.JobId equals job.Id
                           join q in db.EnvironmentRequests on job.RequestId equals q.Id
                           join e in db.TestEnvironments on job.EnvironmentId equals e.Id
                           join r in db.EnvironmentReservations on e.ReservationId equals r.Id
                           join v in db.DiskArchiveVersions on artifact.ArchiveVersionId equals v.Id
                           join a in db.DiskArchives on v.ArchiveId equals a.Id
                           join root in db.ArchiveRoots on a.RootId equals root.Id
                           join pool in db.EnvironmentPools on e.PoolId equals pool.Id
                           where artifact.ArtifactId == request.ArtifactId
                           select new { artifact, lease, job, q, e, r, v, a, root, pool }).SingleOrDefaultAsync(ct);
            if (i is null || i.artifact.JobId != request.JobId || i.artifact.AgentId != agent.Id ||
                i.artifact.FencingToken != request.FencingToken || i.artifact.State != "ContentVerified" ||
                i.lease.JobId != request.JobId || i.lease.AgentId != agent.Id || i.lease.FencingToken != request.FencingToken ||
                i.job.Kind != "ProvisionArchive" || i.q.JobId != i.job.Id || i.q.EnvironmentId != i.e.Id ||
                i.e.RequestId != i.q.Id || i.job.EnvironmentId != i.e.Id || i.q.OwnerId != i.e.OwnerId ||
                i.q.OwnerId != i.r.OwnerId || i.q.ProjectId != i.e.ProjectId || i.r.State != "Held" ||
                i.e.State is not ("Requested" or "Provisioning") || i.q.State is not ("Pending" or "Provisioning") ||
                !i.e.HasUnsettledProvisioningOperation || i.root.Id != sourceRoot.Value || !i.root.Enabled ||
                i.root.AgentScopeId != agent.Id || !i.pool.Enabled || i.pool.Id != agent.PoolId ||
                i.r.PoolId != agent.PoolId || i.pool.AgentScopeId != agent.Id ||
                i.artifact.ReservationId != i.r.Id || i.q.ArchiveVersionId != i.v.Id ||
                i.artifact.SizeBytes <= 0 || i.r.StagingBytes < i.artifact.SizeBytes ||
                i.artifact.Format is not ("dt" or "bak") || i.v.SizeBytes != i.artifact.SizeBytes ||
                i.v.Format != i.artifact.Format || i.artifact.Sha256 is null || i.artifact.Sha256.Length != 64 ||
                !i.artifact.Sha256.All(Uri.IsHexDigit) ||
                !string.Equals(i.v.Sha256, i.artifact.Sha256, StringComparison.OrdinalIgnoreCase) ||
                !CanonicalStagePath(i.a.RelativePath, i.artifact.Format) ||
                !await db.Projects.AnyAsync(x => x.Id == i.q.ProjectId && x.Enabled, ct) ||
                !await db.ArchiveRootProjects.AnyAsync(x => x.RootId == i.root.Id && x.ProjectId == i.q.ProjectId, ct) ||
                !await db.ProjectPools.AnyAsync(x => x.ProjectId == i.q.ProjectId && x.PoolId == agent.PoolId && x.Enabled, ct))
                throw Reject(409, "StageCompletionMismatch");
            // Availability is intentionally not required: durable pinned bytes remain useful after source removal.
            // Current authorization, metadata identity and immutable snapshot still must agree.
            try
            {
                using var doc = JsonDocument.Parse(i.job.SnapshotJson);
                var s = doc.RootElement;
                if (Id("reservationId") != i.r.Id || Id("poolId") != i.pool.Id || Id("sourceRootId") != i.root.Id ||
                    Id("sourceArchiveId") != i.a.Id || Id("archiveVersionId") != i.v.Id || Id("AgentScopeId") != agent.Id ||
                    s.GetProperty("sourceRelativePath").GetString() != i.a.RelativePath ||
                    !string.Equals(s.GetProperty("Sha256").GetString(), i.artifact.Sha256, StringComparison.OrdinalIgnoreCase) ||
                    s.GetProperty("SizeBytes").GetInt64() != i.artifact.SizeBytes ||
                    s.GetProperty("Format").GetString() != i.artifact.Format ||
                    s.GetProperty("stagingBytes").GetInt64() < i.artifact.SizeBytes)
                    throw Reject(409, "StageCompletionMismatch");
                Guid Id(string name) => s.GetProperty(name).GetGuid();
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
            { throw Reject(409, "StageCompletionMismatch"); }

            // Only this exact terminal pair proves that first completion already committed.
            // Its replay is not conditioned on historical expiry; credential/bindings remain current above.
            if (i.job.State == "StageCompleted" && i.lease.State == "Completed")
                return new StageCompletionReceipt(request.JobId, request.FencingToken, request.ArtifactId, "StageCompleted");
            now = await StageNowAsync(ct); // Lock acquisition may have consumed the remaining lease time.
            if (i.job.State != "Leased" || i.lease.State != "Active" || i.lease.ExpiresAt <= now)
                throw Reject(409, "StageNotActive");
            i.job.State = "StageCompleted";
            i.lease.State = "Completed";
            db.AuditEvents.Add(new AuditEvent { Id = Guid.NewGuid(), ActorId = agent.Id,
                Action = "StageCompleted", TargetId = i.job.Id, CreatedAt = now });
            return new StageCompletionReceipt(request.JobId, request.FencingToken, request.ArtifactId, "StageCompleted");
        }, ct);
    }
}
// end
