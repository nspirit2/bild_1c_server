using System.Data;
using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>SQL-authoritative probe-only control plane. All writers use the same transaction-owned scheduling lock.</summary>
public sealed class SqlAgentControl(BuildSystemDbContext db) : IAgentControl
{
    private const string Probe = "ControlPlaneProbe";
    private readonly Dictionary<Guid, string> authenticated = new();

    public Task<EnrollmentTicket> CreateEnrollmentAsync(Actor actor, CreateEnrollment request, CancellationToken ct)
    {
        RequireAdmin(actor);
        if (request is null || request.ExpectedAgentId == Guid.Empty || request.PoolId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.ExpectedName) || request.ExpectedName.Length > 120 ||
            request.ExpectedName != request.ExpectedName.Trim() || request.ExpectedName.Any(char.IsControl))
            throw Reject(400, "InvalidEnrollment");
        return Transaction(async now =>
        {
            if (!await db.EnvironmentPools.AnyAsync(x => x.Id == request.PoolId && x.Enabled, ct))
                throw Reject(409, "PoolUnavailable");
            if (await db.Agents.AnyAsync(x => x.Id == request.ExpectedAgentId, ct)) throw Reject(409, "AgentExists");
            var token = TokenProtector.CreateToken();
            var row = new AgentEnrollment { Id = Guid.NewGuid(), ExpectedAgentId = request.ExpectedAgentId,
                ExpectedName = request.ExpectedName, PoolId = request.PoolId, TokenHash = TokenProtector.Hash(token), ExpiresAt = now.AddMinutes(15) };
            db.AgentEnrollments.Add(row);
            Audit(actor, "AgentEnrollmentCreated", row.Id, now);
            return new EnrollmentTicket(row.Id, token, row.ExpiresAt);
        }, ct);
    }

    public Task<RegistrationReceipt> RegisterAsync(RegisterAgent request, CancellationToken ct)
    {
        if (request is null || request.AgentId == Guid.Empty || request.EnrollmentId == Guid.Empty ||
            string.IsNullOrEmpty(request.Token) || request.Token.Length > 256 || request.Name is null)
            throw Reject(400, "InvalidRegistration");
        return Transaction(async now =>
        {
            var enrollment = await db.AgentEnrollments.SingleOrDefaultAsync(x => x.Id == request.EnrollmentId, ct);
            if (enrollment is null || enrollment.ConsumedAt is not null || enrollment.ExpiresAt <= now ||
                enrollment.ExpectedAgentId != request.AgentId || enrollment.ExpectedName != request.Name ||
                !TokenProtector.Verify(request.Token, enrollment.TokenHash)) throw Reject(403, "EnrollmentRejected");
            if (!await db.EnvironmentPools.AnyAsync(x => x.Id == enrollment.PoolId && x.Enabled, ct)) throw Reject(409, "PoolUnavailable");
            if (await db.Agents.AnyAsync(x => x.Id == request.AgentId, ct)) throw Reject(409, "AgentExists");
            enrollment.ConsumedAt = now;
            db.Agents.Add(new AgentRecord { Id = request.AgentId, Name = enrollment.ExpectedName, PoolId = enrollment.PoolId });
            return new RegistrationReceipt(request.AgentId, "Pending");
        }, ct);
    }

    public Task<AgentCredential> ApproveAsync(Actor actor, Guid agentId, CancellationToken ct) => Credential(actor, agentId, false, ct);
    public Task<AgentCredential> RotateAsync(Actor actor, Guid agentId, CancellationToken ct) => Credential(actor, agentId, true, ct);

    private Task<AgentCredential> Credential(Actor actor, Guid agentId, bool rotate, CancellationToken ct)
    {
        RequireAdmin(actor);
        return Transaction(async now =>
        {
            var agent = await FindAgent(agentId, ct);
            if (agent.State != (rotate ? "Approved" : "Pending")) throw Reject(409, "InvalidAgentState");
            if (!await db.EnvironmentPools.AnyAsync(x => x.Id == agent.PoolId && x.Enabled, ct)) throw Reject(409, "PoolUnavailable");
            var token = TokenProtector.CreateToken();
            agent.TokenHash = TokenProtector.Hash(token);
            agent.State = "Approved";
            Audit(actor, rotate ? "AgentTokenRotated" : "AgentApproved", agentId, now);
            return new AgentCredential(agentId, token);
        }, ct);
    }

    public async Task RevokeAsync(Actor actor, Guid agentId, CancellationToken ct)
    {
        RequireAdmin(actor);
        await Transaction(async now =>
        {
            var agent = await FindAgent(agentId, ct);
            agent.State = "Revoked";
            agent.TokenHash = null;
            foreach (var lease in await db.AgentLeases.Where(x => x.AgentId == agentId && x.State == "Active").ToListAsync(ct))
                await NeedsAttention(lease, ct);
            Audit(actor, "AgentRevoked", agentId, now);
            return true;
        }, ct);
    }

    public async Task<Guid?> AuthenticateAsync(string token, CancellationToken ct)
    {
        authenticated.Clear();
        if (string.IsNullOrEmpty(token) || token.Length > 256) return null;
        var hash = TokenProtector.Hash(token);
        var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(x => x.TokenHash == hash && x.State == "Approved", ct);
        if (agent is null || !TokenProtector.Verify(token, agent.TokenHash!)) return null;
        authenticated[agent.Id] = hash;
        return agent.Id;
    }

    public async Task SetCapabilitiesAsync(Guid agentId, AgentCapabilities capabilities, CancellationToken ct)
    {
        if (capabilities?.SupportedJobKinds is null || capabilities.SupportedJobKinds.Length > 1 ||
            capabilities.SupportedJobKinds.Any(x => x != Probe)) throw Reject(400, "UnsupportedCapabilities");
        await Transaction(async now =>
        {
            var agent = await RequireAuthenticated(agentId, ct);
            agent.SupportsControlPlaneProbe = capabilities.SupportedJobKinds.Contains(Probe);
            return true;
        }, ct);
    }

    public Task<LeaseGrant?> PollAsync(Guid agentId, CancellationToken ct) => Transaction<LeaseGrant?>(async now =>
    {
        var agent = await RequireAuthenticated(agentId, ct);
        // An unresolved prior outcome blocks this agent, not just this job.
        if (await db.AgentLeases.AnyAsync(x => x.AgentId == agentId && x.State == "NeedsAttention", ct)) return null;
        var existing = await db.AgentLeases.SingleOrDefaultAsync(x => x.AgentId == agentId && x.State == "Active", ct);
        if (existing is not null)
        {
            var existingJob = await db.EnvironmentJobs.SingleAsync(x => x.Id == existing.JobId, ct);
            // A lost poll response must be recoverable. Redeliver the same immutable grant;
            // do not extend its expiry, increment its fence, or allocate another job.
            if (existing.ExpiresAt > now && existingJob.Kind == Probe && existingJob.State == "Running")
                return new LeaseGrant(existing.JobId, existing.FencingToken, existing.ExpiresAt, Probe, "{}");
            return null;
        }
        if (!agent.SupportsControlPlaneProbe) return null;
        // Capability is not authorization. Pool binding comes ONLY from administrator-approved enrollment.
        // Existing lease rows are never eligible again: no automatic reassignment, even after expiry.
        var job = await (from j in db.EnvironmentJobs
                         join e in db.TestEnvironments on j.EnvironmentId equals e.Id
                         join r in db.EnvironmentReservations on e.ReservationId equals r.Id
                         join p in db.EnvironmentPools on r.PoolId equals p.Id
                         where j.State == "Pending" && j.Kind == Probe && e.PoolId == agent.PoolId &&
                             r.PoolId == agent.PoolId && r.State == "Held" && p.Enabled &&
                             !db.AgentLeases.Any(l => l.JobId == j.Id)
                         orderby j.CreatedAt, j.Id select j).FirstOrDefaultAsync(ct);
        if (job is null) return null;
        var lease = new AgentLease { JobId = job.Id, AgentId = agent.Id, FencingToken = 1, ExpiresAt = now.AddMinutes(2) };
        db.AgentLeases.Add(lease);
        job.State = "Running";
        agent.LastHeartbeat = now;
        // Never send an uncompiled provisioning snapshot to a worker. Probe has no external execution inputs.
        return new LeaseGrant(job.Id, lease.FencingToken, lease.ExpiresAt, Probe, "{}");
    }, ct);

    public Task<LeaseStatus> HeartbeatAsync(Guid agentId, LeaseHeartbeat heartbeat, CancellationToken ct)
    {
        if (heartbeat is null) throw Reject(400, "InvalidHeartbeat");
        return Transaction(async now =>
        {
            var agent = await RequireAuthenticated(agentId, ct);
            var lease = await RequireLease(agentId, heartbeat.JobId, heartbeat.FencingToken, now, ct);
            if (lease.State != "Active") throw Reject(409, "LeaseNotActive");
            var job = await db.EnvironmentJobs.SingleAsync(x => x.Id == lease.JobId, ct);
            if (job.State != "Running") throw Reject(409, "LeaseNotActive");
            lease.ExpiresAt = now.AddMinutes(2);
            agent.LastHeartbeat = now;
            return new LeaseStatus(lease.ExpiresAt);
        }, ct);
    }

    public Task<EventReceipt> ReportAsync(Guid agentId, ReportAgentEvent report, CancellationToken ct)
    {
        if (report is null || report.EventId == Guid.Empty || report.Code is not ("ProbeCompleted" or "WorkerUnsupported" or "LeaseOutcomeUnknown"))
            throw Reject(400, "InvalidAgentEvent");
        return Transaction(async now =>
        {
            await RequireAuthenticated(agentId, ct);
            // The live lease check deliberately precedes deduplication: stale replay is also a conflict.
            var lease = await RequireLease(agentId, report.JobId, report.FencingToken, now, ct);
            var hash = TokenProtector.Hash(JsonSerializer.Serialize(new { agentId, report.JobId, report.FencingToken, report.Code }));
            var prior = await db.AgentEvents.SingleOrDefaultAsync(x => x.EventId == report.EventId, ct);
            if (prior is not null)
            {
                if (prior.AgentId != agentId || prior.JobId != report.JobId || prior.FencingToken != report.FencingToken || prior.PayloadHash != hash)
                    throw Reject(409, "EventIdConflict");
                return new EventReceipt(report.EventId);
            }
            if (lease.State != "Active") throw Reject(409, "LeaseNotActive");
            var job = await db.EnvironmentJobs.SingleAsync(x => x.Id == report.JobId, ct);
            if (job.State != "Running" || job.Kind != Probe) throw Reject(409, "UnsupportedJob");
            db.AgentEvents.Add(new AgentEvent { EventId = report.EventId, AgentId = agentId, JobId = report.JobId,
                FencingToken = report.FencingToken, Code = report.Code, PayloadHash = hash, CreatedAt = now });
            if (report.Code == "ProbeCompleted")
            {
                job.State = "Succeeded";
                lease.State = "Completed";
                // Deliberately leave environment/request/reservations/quota untouched: a probe is NOT provisioning.
            }
            else await NeedsAttention(lease, ct);
            return new EventReceipt(report.EventId);
        }, ct);
    }

    public async Task<IReadOnlyList<AgentView>> ListAsync(Actor actor, CancellationToken ct)
    {
        RequireAdmin(actor);
        return await db.Agents.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => new AgentView(x.Id, x.Name, x.State, x.PoolId, x.LastHeartbeat)).ToListAsync(ct);
    }

    private async Task<AgentRecord> FindAgent(Guid id, CancellationToken ct) =>
        await db.Agents.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw Reject(404, "AgentNotFound");

    private async Task<AgentRecord> RequireAuthenticated(Guid agentId, CancellationToken ct)
    {
        if (!authenticated.TryGetValue(agentId, out var hash)) throw Reject(401, "AgentUnauthorized");
        var agent = await FindAgent(agentId, ct);
        if (agent.State != "Approved" || agent.TokenHash != hash) throw Reject(401, "AgentUnauthorized");
        return agent;
    }

    private async Task<AgentLease> RequireLease(Guid agentId, Guid jobId, long fence, DateTimeOffset now, CancellationToken ct)
    {
        var lease = await db.AgentLeases.SingleOrDefaultAsync(x => x.JobId == jobId, ct);
        if (lease is null || lease.AgentId != agentId || lease.FencingToken != fence || lease.ExpiresAt <= now ||
            lease.State is not ("Active" or "Completed" or "NeedsAttention")) throw Reject(409, "StaleLease");
        return lease;
    }

    private async Task NeedsAttention(AgentLease lease, CancellationToken ct)
    {
        lease.State = "NeedsAttention";
        var job = await db.EnvironmentJobs.SingleAsync(x => x.Id == lease.JobId, ct);
        job.State = "NeedsAttention";
        var environment = await db.TestEnvironments.SingleAsync(x => x.Id == job.EnvironmentId, ct);
        environment.State = "NeedsAttention";
        environment.HasUnsettledProvisioningOperation = true;
        var request = await db.EnvironmentRequests.SingleAsync(x => x.Id == job.RequestId, ct);
        request.State = "NeedsAttention";
        // No reservation release and no quota credit. Reconciliation is outside this probe-only packet.
    }

    private Task<T> Transaction<T>(Func<DateTimeOffset, Task<T>> action, CancellationToken ct) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource={"BuildSystem:agent-control"}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=15000; IF @r IN (-1, -3) THROW 51001, 'Transient control plane lock contention', 1; IF @r < 0 THROW 51000, 'Control plane lock cancelled or invalid', 1;", ct);
            var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(ct);
            foreach (var lease in await db.AgentLeases.Where(x => x.State == "Active" && x.ExpiresAt <= now).ToListAsync(ct))
                await NeedsAttention(lease, ct);
            // Persist expiry independently of the requested action: rejecting a stale heartbeat cannot roll it back.
            await db.SaveChangesAsync(ct);
            await tx.CreateSavepointAsync("BeforeAction", ct);
            try
            {
                var result = await action(now);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return result;
            }
            catch (RequestRejectedException)
            {
                await tx.RollbackToSavepointAsync("BeforeAction", ct);
                await tx.CommitAsync(ct);
                db.ChangeTracker.Clear();
                throw;
            }
        });

    private void Audit(Actor actor, string action, Guid target, DateTimeOffset now) => db.AuditEvents.Add(new AuditEvent
        { Id = Guid.NewGuid(), ActorId = actor.UserId, Action = action, TargetId = target, CreatedAt = now });
    private static void RequireAdmin(Actor actor)
    {
        if (actor is null || actor.UserId == Guid.Empty || !actor.IsAdministrator) throw Reject(403, "Forbidden");
    }
    private static RequestRejectedException Reject(int status, string code) => new(status, code);
}
// End of agent control plane.
