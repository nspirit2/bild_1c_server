using System.Data;
using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Records a content-verification claim only; no provisioning transition or reservation release.</summary>
public sealed class SqlArtifactReceipts(BuildSystemDbContext db) : IArtifactReceipts
{
    public async Task<StagedArtifactReceipt> ReportAsync(string credential, ReportStagedArtifact report, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(credential) || credential.Length > 256) throw Reject(401, "AgentUnauthorized");
        if (report is null || report.JobId == Guid.Empty || report.FencingToken <= 0 ||
            report.ReservationId == Guid.Empty || report.ArchiveVersionId == Guid.Empty || report.ArtifactId == Guid.Empty ||
            report.Sha256 is null || report.Sha256.Length != 64 || !report.Sha256.All(Uri.IsHexDigit) ||
            report.SizeBytes <= 0 || report.Format is not ("dt" or "bak")) throw Reject(400, "InvalidStagedArtifact");
        var hash = TokenProtector.Hash(credential);
        var fingerprint = report.Sha256.ToUpperInvariant();
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                await LockAsync("BuildSystem:agent-control", ct);
                var agent = await db.Agents.SingleOrDefaultAsync(x => x.TokenHash == hash && x.State == "Approved", ct);
                if (agent is null || !TokenProtector.Verify(credential, agent.TokenHash!)) throw Reject(401, "AgentUnauthorized");
                // Discover lock identities without trusting caller IDs; reload everything after both locks.
                var source = await (from version in db.DiskArchiveVersions
                                    join archive in db.DiskArchives on version.ArchiveId equals archive.Id
                                    where version.Id == report.ArchiveVersionId
                                    select new { archive.RootId }).SingleOrDefaultAsync(ct);
                if (source is null) throw Reject(409, "ArtifactIntentMismatch");
                await LockAsync("BuildSystem:root:" + source.RootId.ToString("N"), ct);
                await LockAsync("BuildSystem:pool:" + agent.PoolId.ToString("N"), ct);
                var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(ct);
                // Always check the live lease before deduplication. Stale and revoked workers cannot replay.
                var lease = await db.AgentLeases.SingleOrDefaultAsync(x => x.JobId == report.JobId, ct);
                if (lease is null || lease.AgentId != agent.Id || lease.FencingToken != report.FencingToken ||
                    lease.State != "Active" || lease.ExpiresAt <= now) throw Reject(409, "StaleLease");
                var intent = await (from job in db.EnvironmentJobs
                                    join request in db.EnvironmentRequests on job.RequestId equals request.Id
                                    join environment in db.TestEnvironments on job.EnvironmentId equals environment.Id
                                    join reservation in db.EnvironmentReservations on environment.ReservationId equals reservation.Id
                                    join version in db.DiskArchiveVersions on request.ArchiveVersionId equals version.Id
                                    join archive in db.DiskArchives on version.ArchiveId equals archive.Id
                                    join root in db.ArchiveRoots on archive.RootId equals root.Id
                                    join pool in db.EnvironmentPools on environment.PoolId equals pool.Id
                                    where job.Id == report.JobId
                                    select new { job, request, environment, reservation, version, archive, root, pool }).SingleOrDefaultAsync(ct);
                if (intent is null || intent.job.Kind != "ProvisionArchive" || intent.job.State != "Leased" ||
                    intent.job.RequestId != intent.request.Id || intent.request.JobId != intent.job.Id ||
                    intent.job.EnvironmentId != intent.request.EnvironmentId ||
                    intent.request.OwnerId != intent.environment.OwnerId || intent.request.OwnerId != intent.reservation.OwnerId ||
                    intent.request.ProjectId != intent.environment.ProjectId ||
                    intent.environment.RequestId != intent.request.Id || intent.environment.Id != intent.job.EnvironmentId ||
                    intent.environment.ReservationId != report.ReservationId || intent.reservation.Id != report.ReservationId ||
                    intent.request.ArchiveVersionId != report.ArchiveVersionId || intent.version.Id != report.ArchiveVersionId ||
                    intent.root.Id != source.RootId || !intent.root.Enabled || intent.root.AgentScopeId != agent.Id ||
                    !intent.pool.Enabled || intent.pool.Id != agent.PoolId || intent.environment.PoolId != agent.PoolId ||
                    intent.reservation.PoolId != agent.PoolId || intent.pool.AgentScopeId != intent.root.AgentScopeId ||
                    intent.reservation.State != "Held" || intent.reservation.StagingBytes < report.SizeBytes ||
                    intent.environment.State is not ("Requested" or "Provisioning") ||
                    intent.request.State is not ("Pending" or "Provisioning") ||
                    !intent.environment.HasUnsettledProvisioningOperation ||
                    !string.Equals(intent.version.Sha256, fingerprint, StringComparison.OrdinalIgnoreCase) ||
                    intent.version.SizeBytes != report.SizeBytes || intent.version.Format != report.Format ||
                    !MatchesSnapshot(intent.job.SnapshotJson, intent.reservation.Id, intent.pool.Id, intent.root.Id,
                        intent.archive.Id, intent.version.Id, agent.Id, fingerprint, report.SizeBytes, report.Format))
                    throw Reject(409, "ArtifactIntentMismatch");
                var matches = await db.StagedArtifacts.Where(x => x.JobId == report.JobId ||
                    x.ArtifactId == report.ArtifactId || (x.ReservationId == report.ReservationId && x.ArchiveVersionId == report.ArchiveVersionId)).ToListAsync(ct);
                if (matches.Count > 1) throw Reject(409, "ArtifactConflict");
                var prior = matches.SingleOrDefault();
                if (prior is not null)
                {
                    if (prior.ArtifactId != report.ArtifactId || prior.JobId != report.JobId || prior.AgentId != agent.Id ||
                        prior.FencingToken != report.FencingToken || prior.ReservationId != report.ReservationId ||
                        prior.ArchiveVersionId != report.ArchiveVersionId || prior.Sha256 != fingerprint ||
                        prior.SizeBytes != report.SizeBytes || prior.Format != report.Format)
                        throw Reject(409, "ArtifactConflict");
                    await tx.CommitAsync(ct);
                    return new StagedArtifactReceipt(prior.ArtifactId, prior.State);
                }
                db.StagedArtifacts.Add(new StagedArtifact { ArtifactId = report.ArtifactId, JobId = report.JobId,
                    AgentId = agent.Id, FencingToken = report.FencingToken, ReservationId = report.ReservationId,
                    ArchiveVersionId = report.ArchiveVersionId, Sha256 = fingerprint, SizeBytes = report.SizeBytes,
                    Format = report.Format, CreatedAt = now });
                db.AuditEvents.Add(new AuditEvent { Id = Guid.NewGuid(), ActorId = agent.Id,
                    Action = "StagedArtifactReported", TargetId = report.ArtifactId, CreatedAt = now });
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return new StagedArtifactReceipt(report.ArtifactId, "ContentVerified");
            }
            catch
            {
                db.ChangeTracker.Clear();
                throw;
            }
        });
    }

    private static bool MatchesSnapshot(string json, Guid reservationId, Guid poolId, Guid rootId,
        Guid archiveId, Guid versionId, Guid agentId, string sha, long size, string format)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var s = doc.RootElement;
            return Id("reservationId") == reservationId && Id("poolId") == poolId &&
                Id("sourceRootId") == rootId && Id("sourceArchiveId") == archiveId &&
                Id("archiveVersionId") == versionId && Id("AgentScopeId") == agentId &&
                string.Equals(s.GetProperty("Sha256").GetString(), sha, StringComparison.OrdinalIgnoreCase) &&
                s.GetProperty("SizeBytes").GetInt64() == size && s.GetProperty("stagingBytes").GetInt64() >= size &&
                s.GetProperty("Format").GetString() == format;
            Guid Id(string name) => s.GetProperty(name).GetGuid();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        {
            return false;
        }
    }

    private Task LockAsync(string resource, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync(
        $"DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=15000; IF @r IN (-1, -3) THROW 51001, 'Transient artifact lock contention', 1; IF @r < 0 THROW 51000, 'Artifact lock cancelled or invalid', 1;", ct);
    private static RequestRejectedException Reject(int status, string code) => new(status, code);
}
