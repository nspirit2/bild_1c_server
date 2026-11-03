using System.Data;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using BuildSystem.Application;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Persists NONEXECUTABLE intent and a permanent hold, never authority or worker instructions.</summary>
public sealed class SqlRestoreIntents : IRestoreIntents
{
    private readonly BuildSystemDbContext db;
    private readonly bool enabled;
    private readonly Dictionary<Guid, RestoreIntentCanonicalMapping> canonicalMappings;
    public SqlRestoreIntents(BuildSystemDbContext db, RestoreIntentOptions options)
    {
        this.db = db;
        enabled = options.Enabled;
        canonicalMappings = options.CanonicalMappings is { Count: <= 1024 }
            ? new(options.CanonicalMappings) : [];
    }
    public Task<RestoreIntentReceipt> RequestAsync(Actor actor, Guid environmentId, RequestRestoreIntent input, CancellationToken ct)
    {
        if (!enabled) throw Reject(409, "RestoreIntentDisabled");
        if (environmentId == Guid.Empty || input is null || input.ExpectedStageJobId == Guid.Empty || input.ExpectedArtifactId == Guid.Empty)
            throw Reject(400, "InvalidRestoreIntent");
        if (actor is null || actor.UserId == Guid.Empty) throw Reject(404, "RestoreIntentNotFound");
        return db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await Lock("BuildSystem:agent-control", ct);
            var environment = await db.TestEnvironments.SingleOrDefaultAsync(e => e.Id == environmentId &&
                db.Projects.Any(p => p.Id == e.ProjectId && p.Enabled) &&
                (actor.IsAdministrator || (e.OwnerId == actor.UserId &&
                    db.ProjectMemberships.Any(m => m.UserId == actor.UserId && m.ProjectId == e.ProjectId))), ct);
            if (environment is null) throw Reject(404, "RestoreIntentNotFound");
            // Resolve locks from relational request/version/archive identity, not client IDs or snapshot JSON.
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
                           select new { e, q, j, r, l, artifact, agent, v, a, root, pool }).SingleOrDefaultAsync(ct);
            if (i is null || i.j.Id != input.ExpectedStageJobId || i.artifact.ArtifactId != input.ExpectedArtifactId ||
                i.j.Kind != "ProvisionArchive" || i.j.State != "StageCompleted" || i.l.State != "Completed" ||
                i.artifact.State != "ContentVerified" || i.r.State != "Held" || !i.e.HasUnsettledProvisioningOperation ||
                i.e.State is not ("Requested" or "Provisioning") || i.q.State is not ("Pending" or "Provisioning") ||
                i.q.EnvironmentId != i.e.Id || i.j.EnvironmentId != i.e.Id || i.j.RequestId != i.q.Id ||
                i.q.OwnerId != i.e.OwnerId || i.q.OwnerId != i.r.OwnerId || i.q.ProjectId != i.e.ProjectId ||
                i.artifact.JobId != i.j.Id || i.artifact.ReservationId != i.r.Id || i.artifact.ArchiveVersionId != i.v.Id ||
                i.artifact.AgentId != i.agent.Id || i.l.AgentId != i.agent.Id || i.l.FencingToken <= 0 ||
                i.artifact.FencingToken != i.l.FencingToken || i.root.Id != rootId.Value || !i.root.Enabled ||
                !i.pool.Enabled || !i.pool.SupportsDt || !i.pool.ScheduledJobsSuppressionVerified || i.agent.State != "Approved" || i.pool.Id != i.r.PoolId || i.pool.Id != i.agent.PoolId ||
                i.root.AgentScopeId != i.agent.Id || i.pool.AgentScopeId != i.agent.Id ||
                i.artifact.SizeBytes <= 0 || i.r.StagingBytes < i.artifact.SizeBytes || i.v.SizeBytes != i.artifact.SizeBytes ||
                i.artifact.Format != "dt" || i.v.Format != i.artifact.Format ||
                i.artifact.Sha256 is null || i.artifact.Sha256.Length != 64 || !i.artifact.Sha256.All(Uri.IsHexDigit) ||
                !string.Equals(i.v.Sha256, i.artifact.Sha256, StringComparison.OrdinalIgnoreCase) ||
                !SqlAgentControl.CanonicalStagePath(i.a.RelativePath, i.artifact.Format) ||
                !await db.Projects.AnyAsync(p => p.Id == i.e.ProjectId && p.Enabled, ct) ||
                (!actor.IsAdministrator && (i.e.OwnerId != actor.UserId || !await db.ProjectMemberships.AnyAsync(m => m.ProjectId == i.e.ProjectId && m.UserId == actor.UserId, ct))) ||
                !await db.ArchiveRootProjects.AnyAsync(m => m.ProjectId == i.e.ProjectId && m.RootId == i.root.Id, ct) ||
                !await db.ProjectPools.AnyAsync(m => m.ProjectId == i.e.ProjectId && m.PoolId == i.pool.Id && m.Enabled, ct))
                throw Mismatch();
            // Match the immutable stage admission snapshot even when current source availability is no longer Ready.
            try
            {
                using var document = JsonDocument.Parse(i.j.SnapshotJson);
                var s = document.RootElement;
                if (Id("reservationId") != i.r.Id || Id("poolId") != i.pool.Id || Id("sourceRootId") != i.root.Id ||
                    Id("sourceArchiveId") != i.a.Id || Id("archiveVersionId") != i.v.Id || Id("AgentScopeId") != i.agent.Id ||
                    s.GetProperty("sourceRelativePath").GetString() != i.a.RelativePath ||
                    Id("SqlServerId") != i.pool.SqlServerId || Id("ClusterId") != i.pool.ClusterId ||
                    s.GetProperty("sqlDbName").GetString() != i.r.SqlDbName ||
                    s.GetProperty("infobaseName").GetString() != i.r.InfobaseName ||
                    !string.Equals(s.GetProperty("Sha256").GetString(), i.artifact.Sha256, StringComparison.OrdinalIgnoreCase) ||
                    s.GetProperty("SizeBytes").GetInt64() != i.artifact.SizeBytes ||
                    s.GetProperty("Format").GetString() != i.artifact.Format || s.GetProperty("stagingBytes").GetInt64() < i.artifact.SizeBytes)
                    throw Mismatch();
                Guid Id(string name) => s.GetProperty(name).GetGuid();
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
            { throw Mismatch(); }
            if (await db.AgentLeases.AnyAsync(l => l.AgentId == i.agent.Id &&
                (l.State == "Active" || l.State == "NeedsAttention"), ct)) throw Mismatch();
            var name = "bs_test_" + i.r.Id.ToString("N");
            var snapshotHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(i.j.SnapshotJson)));
            string profile;
            try
            {
                using var snapshot = JsonDocument.Parse(i.j.SnapshotJson);
                profile = snapshot.RootElement.GetProperty("PreparationProfileVersion").GetString() ?? "";
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
            { throw Mismatch(); }
            if (string.IsNullOrWhiteSpace(profile) || profile.Length > 128 ||
                !await db.Projects.AnyAsync(p => p.Id == i.e.ProjectId && p.PreparationProfileVersion == profile, ct)) throw Mismatch();
            if (i.r.SqlDbName != name || i.r.InfobaseName != name || i.r.ClusterId != i.pool.ClusterId ||
                i.r.SqlServerId != i.pool.SqlServerId || i.pool.ClusterId == Guid.Empty || i.pool.SqlServerId == Guid.Empty ||
                !canonicalMappings.TryGetValue(i.pool.Id, out var mapping) ||
                mapping is null || mapping.CanonicalClusterId != i.pool.ClusterId ||
                mapping.CanonicalSqlServerId != i.pool.SqlServerId) throw Mismatch();
            // This configured resolver assertion is NOT alias discovery or proof of physical identity.
            var keys = new[] { CanonicalKey("cluster", mapping.CanonicalClusterId, name),
                CanonicalKey("sql", mapping.CanonicalSqlServerId, name) };
            foreach (var key in keys.Order(StringComparer.Ordinal)) await Lock(key, ct);
            // Every historical ledger is conservative: even replay cannot revive a retired/released artifact.
            if (await db.StageRetirementIntents.AnyAsync(x => x.ArtifactId == i.artifact.ArtifactId ||
                    x.EnvironmentId == i.e.Id || x.JobId == i.j.Id || x.ReservationId == i.r.Id, ct) ||
                await db.StageCleanupAuthorizations.AnyAsync(x => x.ArtifactId == i.artifact.ArtifactId, ct) ||
                await db.StageCleanupResults.AnyAsync(x => x.ArtifactId == i.artifact.ArtifactId, ct) ||
                await db.StagingAllocationReleases.AnyAsync(x => x.ReservationId == i.r.Id, ct))
                throw Reject(409, "RestoreIntentConflict");
            var priors = await db.RestoreIntents.Where(x => x.EnvironmentId == environmentId || x.StageJobId == i.j.Id ||
                x.ReservationId == i.r.Id || x.ArtifactId == i.artifact.ArtifactId ||
                (x.CanonicalClusterId == mapping.CanonicalClusterId && x.InfobaseName == name) ||
                (x.CanonicalSqlServerId == mapping.CanonicalSqlServerId && x.SqlDbName == name)).Take(2).ToListAsync(ct);
            if (priors.Count > 1) throw Mismatch();
            var prior = priors.SingleOrDefault();
            if (prior is not null)
            {
                if (prior.EnvironmentId != i.e.Id || prior.StageJobId != i.j.Id || prior.ArtifactId != i.artifact.ArtifactId ||
                    prior.ReservationId != i.r.Id || prior.AgentId != i.agent.Id || prior.FencingToken != i.l.FencingToken ||
                    prior.CanonicalClusterId != mapping.CanonicalClusterId || prior.CanonicalSqlServerId != mapping.CanonicalSqlServerId ||
                    prior.InfobaseName != name || prior.SqlDbName != name || prior.TargetIncarnation == Guid.Empty || prior.Executable ||
                    prior.RequestId != i.q.Id || prior.PoolId != i.pool.Id || prior.RootId != i.root.Id ||
                    prior.ArchiveVersionId != i.v.Id || prior.Sha256 != i.artifact.Sha256 || prior.SizeBytes != i.artifact.SizeBytes ||
                    prior.Format != i.artifact.Format || prior.SnapshotSha256 != snapshotHash ||
                    prior.PreparationProfileVersion != profile || !prior.ScheduledJobsMustRemainBlocked)
                    throw Mismatch();
                await tx.CommitAsync(ct);
                return Receipt(prior);
            }
            var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(ct);
            var intent = new RestoreIntentRecord { RestoreIntentId = Guid.NewGuid(), EnvironmentId = environmentId,
                StageJobId = i.j.Id, ArtifactId = i.artifact.ArtifactId, ReservationId = i.r.Id, AgentId = i.agent.Id,
                RequestId = i.q.Id, PoolId = i.pool.Id, RootId = i.root.Id, ArchiveVersionId = i.v.Id,
                Sha256 = i.artifact.Sha256, SizeBytes = i.artifact.SizeBytes, Format = i.artifact.Format,
                SnapshotSha256 = snapshotHash, PreparationProfileVersion = profile, ScheduledJobsMustRemainBlocked = true,
                FencingToken = i.l.FencingToken, RequestedBy = actor.UserId, RequestedAt = now,
                TargetIncarnation = Guid.NewGuid(), CanonicalClusterId = mapping.CanonicalClusterId,
                CanonicalSqlServerId = mapping.CanonicalSqlServerId, InfobaseName = name, SqlDbName = name, Executable = false };
            db.RestoreIntents.Add(intent);
            db.AuditEvents.Add(new AuditEvent { Id = Guid.NewGuid(), ActorId = actor.UserId,
                Action = "RestoreIntentRequested", TargetId = intent.RestoreIntentId, CreatedAt = now });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Receipt(intent);
        });
    }
    private Task Lock(string resource, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=15000; IF @r IN (-1, -3) THROW 51001, 'Transient restore intent lock contention', 1; IF @r < 0 THROW 51000, 'Restore intent lock cancelled or invalid', 1;", ct);
    private static string CanonicalKey(string kind, Guid id, string name) => "BuildSystem:restore:" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + ":" + id.ToString("N") + ":" + name)));
    private static RestoreIntentReceipt Receipt(RestoreIntentRecord x) => new(x.RestoreIntentId, x.EnvironmentId,
        x.StageJobId, x.ReservationId, x.ArtifactId, x.TargetIncarnation, x.CanonicalClusterId,
        x.CanonicalSqlServerId, x.InfobaseName, x.SqlDbName, x.RequestedAt);
    private static RequestRejectedException Mismatch() => Reject(409, "RestoreIntentMismatch");
    private static RequestRejectedException Reject(int status, string code) => new(status, code);
}
// end
