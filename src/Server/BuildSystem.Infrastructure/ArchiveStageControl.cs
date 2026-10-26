using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Explicit opt-in stage-only service. Uses the control plane's scheduling transaction and expiry sweep.</summary>
public sealed class SqlArchiveStageControl(BuildSystemDbContext db, ArchiveStageOptions options) : IArchiveStageControl
{
    public Task<StageLeaseGrant?> PollAsync(string credential, CancellationToken ct)
    {
        RequireEnabled();
        return new SqlAgentControl(db).PollStageAsync(credential, ct);
    }
    public Task<LeaseStatus> HeartbeatAsync(string credential, LeaseHeartbeat heartbeat, CancellationToken ct)
    {
        RequireEnabled();
        return new SqlAgentControl(db).HeartbeatStageAsync(credential, heartbeat, ct);
    }
    private void RequireEnabled()
    {
        if (!options.Enabled) throw new RequestRejectedException(409, "StageDisabled");
    }
}

public sealed partial class SqlAgentControl
{
    internal Task<StageLeaseGrant?> PollStageAsync(string credential, CancellationToken ct) => Transaction<StageLeaseGrant?>(async now =>
    {
        var agent = await AuthenticateStageAsync(credential, ct);
        if (await db.AgentLeases.AnyAsync(x => x.AgentId == agent.Id && x.State == "NeedsAttention", ct)) return null;
        var existing = await db.AgentLeases.SingleOrDefaultAsync(x => x.AgentId == agent.Id && x.State == "Active", ct);
        if (existing is not null)
        {
            var kind = await db.EnvironmentJobs.Where(x => x.Id == existing.JobId).Select(x => x.Kind).SingleAsync(ct);
            if (kind != "ProvisionArchive") return null;
            var plan = await CompileStagePlanAsync(agent, existing.JobId, "Leased", ct);
            if (existing.ExpiresAt <= await StageNowAsync(ct)) throw Reject(409, "StaleLease");
            return new StageLeaseGrant(existing.JobId, existing.FencingToken, existing.ExpiresAt, "ArchiveStageOnly", plan);
        }
        // A lease row is a permanent history marker: no automatic reassignment or fence reuse.
        var candidate = await (from j in db.EnvironmentJobs
                               join e in db.TestEnvironments on j.EnvironmentId equals e.Id
                               join r in db.EnvironmentReservations on e.ReservationId equals r.Id
                               where j.Kind == "ProvisionArchive" && j.State == "Pending" &&
                                   e.PoolId == agent.PoolId && r.PoolId == agent.PoolId && r.State == "Held" &&
                                   !db.AgentLeases.Any(l => l.JobId == j.Id)
                               orderby j.CreatedAt, j.Id select (Guid?)j.Id).FirstOrDefaultAsync(ct);
        if (candidate is null) return null;
        var stagePlan = await CompileStagePlanAsync(agent, candidate.Value, "Pending", ct);
        var job = await db.EnvironmentJobs.SingleAsync(x => x.Id == candidate.Value, ct);
        now = await StageNowAsync(ct);
        var lease = new AgentLease { JobId = job.Id, AgentId = agent.Id, FencingToken = 1,
            State = "Active", ExpiresAt = now.AddMinutes(2) };
        db.AgentLeases.Add(lease);
        job.State = "Leased";
        agent.LastHeartbeat = now;
        db.AuditEvents.Add(new AuditEvent { Id = Guid.NewGuid(), ActorId = agent.Id,
            Action = "ArchiveStageLeased", TargetId = job.Id, CreatedAt = now });
        return new StageLeaseGrant(job.Id, lease.FencingToken, lease.ExpiresAt, "ArchiveStageOnly", stagePlan);
    }, ct);

    internal Task<LeaseStatus> HeartbeatStageAsync(string credential, LeaseHeartbeat heartbeat, CancellationToken ct)
    {
        if (heartbeat is null || heartbeat.JobId == Guid.Empty || heartbeat.FencingToken <= 0)
            throw Reject(400, "InvalidHeartbeat");
        return Transaction(async now =>
        {
            var agent = await AuthenticateStageAsync(credential, ct);
            var lease = await RequireLease(agent.Id, heartbeat.JobId, heartbeat.FencingToken, now, ct);
            if (lease.State != "Active") throw Reject(409, "LeaseNotActive");
            await CompileStagePlanAsync(agent, lease.JobId, "Leased", ct);
            now = await StageNowAsync(ct);
            if (lease.ExpiresAt <= now) throw Reject(409, "StaleLease");
            lease.ExpiresAt = now.AddMinutes(2);
            agent.LastHeartbeat = now;
            return new LeaseStatus(lease.ExpiresAt);
        }, ct);
    }

    private async Task<AgentRecord> AuthenticateStageAsync(string credential, CancellationToken ct)
    {
        // Called inside global transaction lock, not trusting earlier HTTP authentication or scoped cache.
        var id = await AuthenticateAsync(credential, ct);
        if (id is null) throw Reject(401, "AgentUnauthorized");
        return await RequireAuthenticated(id.Value, ct);
    }

    private async Task<StagePlan> CompileStagePlanAsync(AgentRecord agent, Guid jobId, string state, CancellationToken ct)
    {
        var sourceRoot = await (from j in db.EnvironmentJobs
                                join q in db.EnvironmentRequests on j.RequestId equals q.Id
                                join v in db.DiskArchiveVersions on q.ArchiveVersionId equals v.Id
                                join a in db.DiskArchives on v.ArchiveId equals a.Id
                                where j.Id == jobId select (Guid?)a.RootId).SingleOrDefaultAsync(ct);
        if (sourceRoot is null) throw Reject(409, "StageIntentMismatch");
        await StageLockAsync("BuildSystem:root:" + sourceRoot.Value.ToString("N"), ct);
        await StageLockAsync("BuildSystem:pool:" + agent.PoolId.ToString("N"), ct);
        var i = await (from j in db.EnvironmentJobs
                       join q in db.EnvironmentRequests on j.RequestId equals q.Id
                       join e in db.TestEnvironments on j.EnvironmentId equals e.Id
                       join r in db.EnvironmentReservations on e.ReservationId equals r.Id
                       join v in db.DiskArchiveVersions on q.ArchiveVersionId equals v.Id
                       join a in db.DiskArchives on v.ArchiveId equals a.Id
                       join root in db.ArchiveRoots on a.RootId equals root.Id
                       join pool in db.EnvironmentPools on e.PoolId equals pool.Id
                       where j.Id == jobId select new { j, q, e, r, v, a, root, pool }).SingleOrDefaultAsync(ct);
        if (i is null || i.j.Kind != "ProvisionArchive" || i.j.State != state ||
            i.q.JobId != i.j.Id || i.q.EnvironmentId != i.e.Id || i.e.RequestId != i.q.Id ||
            i.j.EnvironmentId != i.e.Id || i.q.OwnerId != i.e.OwnerId || i.q.OwnerId != i.r.OwnerId ||
            i.q.ProjectId != i.e.ProjectId || i.r.State != "Held" ||
            i.e.State is not ("Requested" or "Provisioning") || i.q.State is not ("Pending" or "Provisioning") ||
            !i.e.HasUnsettledProvisioningOperation || i.root.Id != sourceRoot.Value ||
            !i.root.Enabled || i.root.AgentScopeId != agent.Id || !i.pool.Enabled ||
            i.pool.Id != agent.PoolId || i.r.PoolId != agent.PoolId || i.pool.AgentScopeId != agent.Id ||
            i.root.MaxArchiveBytes <= 0 || i.root.MaxStagingBytes <= 0 || i.pool.MaxStagingBytes <= 0 ||
            i.v.State != "Ready" || i.v.SizeBytes <= 0 || i.v.SizeBytes > i.root.MaxArchiveBytes ||
            i.v.SizeBytes > i.root.MaxStagingBytes || i.v.SizeBytes > i.pool.MaxStagingBytes ||
            i.r.StagingBytes < i.v.SizeBytes || i.v.Format is not ("dt" or "bak") ||
            (i.v.Format == "dt" ? !i.pool.SupportsDt : !i.pool.SupportsBak) ||
            i.v.Sha256 is null || i.v.Sha256.Length != 64 || !i.v.Sha256.All(Uri.IsHexDigit) ||
            !CanonicalStagePath(i.a.RelativePath, i.v.Format) ||
            !await db.Projects.AnyAsync(x => x.Id == i.q.ProjectId && x.Enabled, ct) ||
            !await db.ArchiveRootProjects.AnyAsync(x => x.RootId == i.root.Id && x.ProjectId == i.q.ProjectId, ct) ||
            !await db.ProjectPools.AnyAsync(x => x.ProjectId == i.q.ProjectId && x.PoolId == agent.PoolId && x.Enabled, ct))
            throw Reject(409, "StageIntentMismatch");
        try
        {
            using var doc = JsonDocument.Parse(i.j.SnapshotJson);
            var s = doc.RootElement;
            if (Id("reservationId") != i.r.Id || Id("poolId") != i.pool.Id || Id("sourceRootId") != i.root.Id ||
                Id("sourceArchiveId") != i.a.Id || Id("archiveVersionId") != i.v.Id || Id("AgentScopeId") != agent.Id ||
                s.GetProperty("sourceRelativePath").GetString() != i.a.RelativePath ||
                !string.Equals(s.GetProperty("Sha256").GetString(), i.v.Sha256, StringComparison.OrdinalIgnoreCase) ||
                s.GetProperty("SizeBytes").GetInt64() != i.v.SizeBytes || s.GetProperty("Format").GetString() != i.v.Format ||
                s.GetProperty("stagingBytes").GetInt64() < i.v.SizeBytes)
                throw Reject(409, "StageIntentMismatch");
            return new StagePlan(i.r.Id, i.v.Id, i.root.Id, s.GetProperty("sourceRelativePath").GetString()!,
                i.v.Sha256.ToUpperInvariant(), i.v.SizeBytes, i.v.Format);
            Guid Id(string name) => s.GetProperty(name).GetGuid();
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        { throw Reject(409, "StageIntentMismatch"); }
    }

    private Task<DateTimeOffset> StageNowAsync(CancellationToken ct) => db.Database.SqlQuery<DateTimeOffset>(
        $"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(ct);

    private Task StageLockAsync(string resource, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync(
        $"DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=15000; IF @r IN (-1, -3) THROW 51001, 'Transient stage lock contention', 1; IF @r < 0 THROW 51000, 'Stage lock cancelled or invalid', 1;", ct);

    private static bool CanonicalStagePath(string path, string format)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 400 || path.Contains('\\') ||
            !path.EndsWith("." + format, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var p in path.Split('/'))
        {
            if (p.Length == 0 || p.Length > 255 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') ||
                p.Any(c => char.IsControl(c) || ":<>\"|?*".Contains(c))) return false;
            var stem = p.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" or "CONIN$" or "CONOUT$" ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                 "123456789¹²³".Contains(stem[3]))) return false;
        }
        return true;
    }
}
// end
