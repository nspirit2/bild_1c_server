using BuildSystem.Application;
using BuildSystem.Contracts;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildSystem.IntegrationTests;

public sealed class SqlAgentControlTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Probe = "ControlPlaneProbe";

    private sealed record Seed(Guid Admin, Guid Owner, Guid Project, Guid Pool, Guid Version)
    {
        public Actor Administrator => new(Admin, true);
        public Actor Developer => new(Owner, false);
    }

    private static async Task<Seed> PrepareAsync(SqlServerTestDatabase sql)
    {
        await sql.ApplyMigrationsAsync();
        var seed = new Seed(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var root = Guid.NewGuid();
        var archive = Guid.NewGuid();
        await using var db = sql.NewContext();
        db.Users.AddRange(new BuildSystemUser { Id = seed.Admin, UserName = "admin", NormalizedUserName = "ADMIN" },
            new BuildSystemUser { Id = seed.Owner, UserName = "owner", NormalizedUserName = "OWNER" });
        db.Projects.Add(new Project { Id = seed.Project, Name = "project", IsolationPolicyId = "isolated", PreparationProfileVersion = "v1" });
        db.ProjectMemberships.Add(new ProjectMembership { UserId = seed.Owner, ProjectId = seed.Project });
        var scope = Guid.NewGuid();
        db.ArchiveRoots.Add(new ArchiveRoot { Id = root, Enabled = true, AgentScopeId = scope, IsolationPolicyId = "isolated", MaxArchiveBytes = 10000, MaxStagingBytes = 10000 });
        db.ArchiveRootProjects.Add(new ArchiveRootProject { RootId = root, ProjectId = seed.Project });
        db.DiskArchives.Add(new DiskArchive { Id = archive, RootId = root, RelativePath = "sample.dt" });
        db.DiskArchiveVersions.Add(new DiskArchiveVersion { Id = seed.Version, ArchiveId = archive, Sha256 = new string('A', 64), SizeBytes = 100, Format = "dt", State = "Ready", EstimatedDbBytes = 1000 });
        db.EnvironmentPools.Add(new EnvironmentPool { Id = seed.Pool, Enabled = true, ClusterId = Guid.NewGuid(), SqlServerId = Guid.NewGuid(), AgentScopeId = scope,
            IsolationPolicyId = "isolated", CapacityBytes = 100000, MaxEnvironmentBytes = 10000, MaxStagingBytes = 10000, MaxConcurrentProvisioning = 5,
            SupportsDt = true, SupportsBak = true, IsolationVerified = true, ScheduledJobsSuppressionVerified = true, ResourceLimitVerified = true });
        db.ProjectPools.Add(new ProjectPool { ProjectId = seed.Project, PoolId = seed.Pool, Enabled = true });
        db.EnvironmentQuotas.Add(new EnvironmentQuota { SubjectId = seed.Owner, MaxActiveEnvironments = 5, MaxConcurrentProvisioning = 5, MaxStorageBytes = 100000, MaxTtlDays = 30 });
        await db.SaveChangesAsync();
        return seed;
    }

    // A probe has the same real SQL FK graph as an admitted request. Never mutate an immutable
    // ProvisionArchive snapshot into a probe after creation or expose a test-only seeding API.
    private static async Task<Guid> AddJobAsync(SqlServerTestDatabase sql, Seed seed, string kind = Probe)
    {
        await using var db = sql.NewContext();
        var request = Guid.NewGuid();
        var environment = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        var job = Guid.NewGuid();
        var pool = await db.EnvironmentPools.SingleAsync(x => x.Id == seed.Pool);
        var name = "bs_test_" + reservation.ToString("N");
        db.EnvironmentRequests.Add(new EnvironmentRequest { Id = request, OwnerId = seed.Owner, ProjectId = seed.Project,
            EnvironmentId = environment, JobId = job, ArchiveVersionId = seed.Version, IdempotencyKey = Guid.NewGuid().ToString("N"),
            PayloadHash = new string('B', 64), DisplayName = "probe", TtlDays = 7, CreatedAt = DateTimeOffset.UtcNow });
        db.EnvironmentReservations.Add(new EnvironmentReservation { Id = reservation, OwnerId = seed.Owner, PoolId = seed.Pool,
            SqlServerId = pool.SqlServerId, ClusterId = pool.ClusterId, SqlDbName = name, InfobaseName = name,
            ReservedBytes = 1000, StagingBytes = 100 });
        db.TestEnvironments.Add(new EnvironmentRecord { Id = environment, RequestId = request, OwnerId = seed.Owner,
            ProjectId = seed.Project, PoolId = seed.Pool, ReservationId = reservation, DisplayName = "probe" });
        db.EnvironmentJobs.Add(new EnvironmentJob { Id = job, RequestId = request, EnvironmentId = environment,
            Kind = kind, SnapshotJson = "{}", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return job;
    }

    private static async Task<AgentCredential> EnrollAsync(SqlServerTestDatabase sql, Seed seed, Guid id, string name)
    {
        await using var db = sql.NewContext();
        var service = new SqlAgentControl(db);
        var ticket = await service.CreateEnrollmentAsync(seed.Administrator, new CreateEnrollment(id, name, seed.Pool), Ct);
        Assert.Equal("Pending", (await service.RegisterAsync(new RegisterAgent(id, ticket.EnrollmentId, ticket.Token, name), Ct)).State);
        Assert.Null(await service.AuthenticateAsync(ticket.Token, Ct));
        return await service.ApproveAsync(seed.Administrator, id, Ct);
    }

    private static async Task<SqlAgentControl> AuthenticatedAsync(BuildSystemDbContext db, AgentCredential credential)
    {
        var service = new SqlAgentControl(db);
        Assert.Equal(credential.AgentId, await service.AuthenticateAsync(credential.Token, Ct));
        return service; // Authentication and operation MUST share this scoped service instance.
    }

    private static async Task AssertRejectedAsync(string code, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<RequestRejectedException>(action);
        Assert.Equal(code, exception.Code);
    }

    [SqlServerFact]
    public async Task Enrollment_is_single_use_exact_name_and_expires()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        await using var db = sql.NewContext();
        var service = new SqlAgentControl(db);
        var id = Guid.NewGuid();
        var ticket = await service.CreateEnrollmentAsync(seed.Administrator, new CreateEnrollment(id, "worker", seed.Pool), Ct);
        await AssertRejectedAsync("EnrollmentRejected", () => service.RegisterAsync(new RegisterAgent(id, ticket.EnrollmentId, ticket.Token, "Worker"), Ct));
        await AssertRejectedAsync("EnrollmentRejected", () => service.RegisterAsync(new RegisterAgent(Guid.NewGuid(), ticket.EnrollmentId, ticket.Token, "worker"), Ct));
        Assert.Equal("Pending", (await service.RegisterAsync(new RegisterAgent(id, ticket.EnrollmentId, ticket.Token, "worker"), Ct)).State);
        await AssertRejectedAsync("EnrollmentRejected", () => service.RegisterAsync(new RegisterAgent(id, ticket.EnrollmentId, ticket.Token, "worker"), Ct));
        var expiredId = Guid.NewGuid();
        var expired = await service.CreateEnrollmentAsync(seed.Administrator, new CreateEnrollment(expiredId, "expired", seed.Pool), Ct);
        await db.AgentEnrollments.Where(x => x.Id == expired.EnrollmentId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        await AssertRejectedAsync("EnrollmentRejected", () => service.RegisterAsync(new RegisterAgent(expiredId, expired.EnrollmentId, expired.Token, "expired"), Ct));
        Assert.Equal(1, await db.Agents.CountAsync());
    }

    [SqlServerFact]
    public async Task Pending_is_denied_and_rotate_revoke_invalidate_old_credential_and_binding()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        await using var db = sql.NewContext();
        var control = new SqlAgentControl(db);
        var id = Guid.NewGuid();
        var ticket = await control.CreateEnrollmentAsync(seed.Administrator, new CreateEnrollment(id, "worker", seed.Pool), Ct);
        await control.RegisterAsync(new RegisterAgent(id, ticket.EnrollmentId, ticket.Token, "worker"), Ct);
        Assert.Null(await control.AuthenticateAsync(ticket.Token, Ct));
        await AssertRejectedAsync("AgentUnauthorized", () => control.PollAsync(id, Ct));
        await AssertRejectedAsync("Forbidden", () => control.ApproveAsync(seed.Developer, id, Ct));
        var first = await control.ApproveAsync(seed.Administrator, id, Ct);
        Assert.Equal(id, await control.AuthenticateAsync(first.Token, Ct));
        var second = await control.RotateAsync(seed.Administrator, id, Ct);
        Assert.NotEqual(first.Token, second.Token);
        await AssertRejectedAsync("AgentUnauthorized", () => control.SetCapabilitiesAsync(id, new AgentCapabilities([Probe]), Ct));
        Assert.Null(await control.AuthenticateAsync(first.Token, Ct));
        Assert.Equal(id, await control.AuthenticateAsync(second.Token, Ct));
        await control.RevokeAsync(seed.Administrator, id, Ct);
        await AssertRejectedAsync("AgentUnauthorized", () => control.PollAsync(id, Ct));
        Assert.Null(await control.AuthenticateAsync(second.Token, Ct));
    }

    [SqlServerFact]
    public async Task Only_probe_is_schedulable_and_unsupported_capabilities_are_rejected()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        var provision = await AddJobAsync(sql, seed, "ProvisionArchive");
        var credential = await EnrollAsync(sql, seed, Guid.NewGuid(), "agent");
        await using var db = sql.NewContext();
        var control = await AuthenticatedAsync(db, credential);
        await AssertRejectedAsync("UnsupportedCapabilities", () => control.SetCapabilitiesAsync(credential.AgentId, new AgentCapabilities(["ProvisionArchive"]), Ct));
        await control.SetCapabilitiesAsync(credential.AgentId, new AgentCapabilities([Probe]), Ct);
        Assert.Null(await control.PollAsync(credential.AgentId, Ct));
        var probe = await AddJobAsync(sql, seed);
        var grant = await control.PollAsync(credential.AgentId, Ct);
        Assert.NotNull(grant);
        Assert.Equal(probe, grant.JobId);
        Assert.Equal(Probe, grant.Kind);
        Assert.Equal("{}", grant.SnapshotJson);
        Assert.Equal("Pending", (await db.EnvironmentJobs.AsNoTracking().SingleAsync(x => x.Id == provision)).State);
    }

    [SqlServerFact]
    public async Task Concurrent_agents_cannot_claim_same_job_and_lost_poll_redelivers_same_grant()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        var job = await AddJobAsync(sql, seed);
        var agents = await Task.WhenAll(EnrollAsync(sql, seed, Guid.NewGuid(), "a"), EnrollAsync(sql, seed, Guid.NewGuid(), "b"));
        var grants = await Task.WhenAll(agents.Select(async credential =>
        {
            await using var db = sql.NewContext();
            var control = await AuthenticatedAsync(db, credential);
            await control.SetCapabilitiesAsync(credential.AgentId, new AgentCapabilities([Probe]), Ct);
            return await control.PollAsync(credential.AgentId, Ct);
        }));
        var grant = Assert.Single(grants.OfType<LeaseGrant>());
        Assert.Equal(job, grant.JobId);
        await using var check = sql.NewContext();
        Assert.Equal(1, await check.AgentLeases.CountAsync());
        var winner = await check.AgentLeases.AsNoTracking().SingleAsync();
        var winnerCredential = agents.Single(x => x.AgentId == winner.AgentId);
        await using var retryDb = sql.NewContext();
        var retry = await AuthenticatedAsync(retryDb, winnerCredential);
        Assert.Equal(grant, await retry.PollAsync(winner.AgentId, Ct));
        Assert.Equal(1, await retryDb.AgentLeases.CountAsync());
        var loserCredential = agents.Single(x => x.AgentId != winner.AgentId);
        await using var loserDb = sql.NewContext();
        var loser = await AuthenticatedAsync(loserDb, loserCredential);
        Assert.Null(await loser.PollAsync(loserCredential.AgentId, Ct));
        await AssertRejectedAsync("StaleLease", () => loser.HeartbeatAsync(loserCredential.AgentId, new LeaseHeartbeat(job, grant.FencingToken), Ct));
    }

    [SqlServerFact]
    public async Task Stale_fence_and_expired_lease_reject_heartbeat_and_never_reissue_job()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        var job = await AddJobAsync(sql, seed);
        var credential = await EnrollAsync(sql, seed, Guid.NewGuid(), "agent");
        await using var db = sql.NewContext();
        var control = await AuthenticatedAsync(db, credential);
        await control.SetCapabilitiesAsync(credential.AgentId, new AgentCapabilities([Probe]), Ct);
        var grant = (await control.PollAsync(credential.AgentId, Ct))!;
        await AssertRejectedAsync("StaleLease", () => control.HeartbeatAsync(credential.AgentId, new LeaseHeartbeat(job, grant.FencingToken + 1), Ct));
        Assert.True((await control.HeartbeatAsync(credential.AgentId, new LeaseHeartbeat(job, grant.FencingToken), Ct)).ExpiresAt >= grant.ExpiresAt);
        await db.AgentLeases.Where(x => x.JobId == job)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        await AssertRejectedAsync("StaleLease", () => control.HeartbeatAsync(credential.AgentId, new LeaseHeartbeat(job, grant.FencingToken), Ct));
        Assert.Null(await control.PollAsync(credential.AgentId, Ct));
        await using var check = sql.NewContext();
        Assert.Equal("NeedsAttention", (await check.AgentLeases.AsNoTracking().SingleAsync()).State);
        Assert.Equal("NeedsAttention", (await check.EnvironmentJobs.AsNoTracking().SingleAsync()).State);
        Assert.Equal("NeedsAttention", (await check.TestEnvironments.AsNoTracking().SingleAsync()).State);
        Assert.True((await check.TestEnvironments.AsNoTracking().SingleAsync()).HasUnsettledProvisioningOperation);
        Assert.Equal("Held", (await check.EnvironmentReservations.AsNoTracking().SingleAsync()).State);
        Assert.Equal(1, await check.EnvironmentReservations.CountAsync());
        var second = await EnrollAsync(sql, seed, Guid.NewGuid(), "second");
        await using var otherDb = sql.NewContext();
        var other = await AuthenticatedAsync(otherDb, second);
        await other.SetCapabilitiesAsync(second.AgentId, new AgentCapabilities([Probe]), Ct);
        Assert.Null(await other.PollAsync(second.AgentId, Ct));
    }

    [SqlServerFact]
    public async Task Event_replay_acks_exact_payload_only_while_live_and_completion_never_releases_reservation()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        var job = await AddJobAsync(sql, seed);
        var credential = await EnrollAsync(sql, seed, Guid.NewGuid(), "agent");
        await using var db = sql.NewContext();
        var control = await AuthenticatedAsync(db, credential);
        await control.SetCapabilitiesAsync(credential.AgentId, new AgentCapabilities([Probe]), Ct);
        var grant = (await control.PollAsync(credential.AgentId, Ct))!;
        var report = new ReportAgentEvent(Guid.NewGuid(), job, grant.FencingToken, "ProbeCompleted");
        var receipt = await control.ReportAsync(credential.AgentId, report, Ct);
        Assert.Equal(receipt, await control.ReportAsync(credential.AgentId, report, Ct));
        await AssertRejectedAsync("EventIdConflict", () => control.ReportAsync(credential.AgentId, report with { Code = "WorkerUnsupported" }, Ct));
        await AssertRejectedAsync("StaleLease", () => control.ReportAsync(credential.AgentId, report with { FencingToken = grant.FencingToken + 1 }, Ct));
        Assert.Equal(1, await db.AgentEvents.CountAsync());
        await using (var check = sql.NewContext())
        {
            Assert.Equal("Succeeded", (await check.EnvironmentJobs.AsNoTracking().SingleAsync()).State);
            Assert.Equal("Requested", (await check.TestEnvironments.AsNoTracking().SingleAsync()).State);
            Assert.Equal("Held", (await check.EnvironmentReservations.AsNoTracking().SingleAsync()).State);
            Assert.Equal("Pending", (await check.EnvironmentRequests.AsNoTracking().SingleAsync()).State);
        }
        await db.AgentLeases.Where(x => x.JobId == job)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        await AssertRejectedAsync("StaleLease", () => control.ReportAsync(credential.AgentId, report, Ct));
    }
}
