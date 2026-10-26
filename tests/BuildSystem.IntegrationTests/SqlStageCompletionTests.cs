using BuildSystem.Application;
using BuildSystem.Contracts;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildSystem.IntegrationTests;

public sealed class SqlStageCompletionTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly string Hash = new('A', 64);
    private sealed record Seed(Actor Admin, Actor Owner, Guid Project, Guid Pool, Guid Root, Guid Version,
        OrderReceipt Order, AgentCredential Credential, StageLeaseGrant Grant, ReportStagedArtifact Report);

    private static async Task<Seed> PrepareAsync(SqlServerTestDatabase sql, bool recordArtifact = true)
    {
        await sql.ApplyMigrationsAsync();
        var admin = new Actor(Guid.NewGuid(), true); var owner = new Actor(Guid.NewGuid(), false);
        var project = Guid.NewGuid(); var pool = Guid.NewGuid(); var root = Guid.NewGuid();
        var archive = Guid.NewGuid(); var version = Guid.NewGuid(); var agent = Guid.NewGuid();
        await using var db = sql.NewContext();
        db.Users.AddRange(new BuildSystemUser { Id = admin.UserId, UserName = "admin", NormalizedUserName = "ADMIN" },
            new BuildSystemUser { Id = owner.UserId, UserName = "owner", NormalizedUserName = "OWNER" });
        db.Projects.Add(new Project { Id = project, Name = "project", IsolationPolicyId = "isolated", PreparationProfileVersion = "v1" });
        db.ProjectMemberships.Add(new ProjectMembership { UserId = owner.UserId, ProjectId = project });
        db.ArchiveRoots.Add(new ArchiveRoot { Id = root, Enabled = true, AgentScopeId = agent,
            IsolationPolicyId = "isolated", MaxArchiveBytes = 10000, MaxStagingBytes = 10000 });
        db.ArchiveRootProjects.Add(new ArchiveRootProject { RootId = root, ProjectId = project });
        db.DiskArchives.Add(new DiskArchive { Id = archive, RootId = root, RelativePath = "sample.dt" });
        db.DiskArchiveVersions.Add(new DiskArchiveVersion { Id = version, ArchiveId = archive,
            Sha256 = Hash, SizeBytes = 10, Format = "dt", State = "Ready", EstimatedDbBytes = 100 });
        db.EnvironmentPools.Add(new EnvironmentPool { Id = pool, Enabled = true,
            ClusterId = Guid.NewGuid(), SqlServerId = Guid.NewGuid(), AgentScopeId = agent,
            IsolationPolicyId = "isolated", CapacityBytes = 100000, MaxEnvironmentBytes = 10000,
            MaxStagingBytes = 10000, MaxConcurrentProvisioning = 5, SupportsDt = true, SupportsBak = true,
            IsolationVerified = true, ScheduledJobsSuppressionVerified = true, ResourceLimitVerified = true });
        db.ProjectPools.Add(new ProjectPool { ProjectId = project, PoolId = pool, Enabled = true });
        db.EnvironmentQuotas.Add(new EnvironmentQuota { SubjectId = owner.UserId, MaxActiveEnvironments = 5,
            MaxConcurrentProvisioning = 5, MaxStorageBytes = 100000, MaxTtlDays = 30 });
        await db.SaveChangesAsync();
        var credential = await EnrollAsync(db, admin, pool, agent, "completion-worker");
        var order = await new SqlEnvironmentOrders(db).CreateAsync(owner, "completion-order",
            new CreateEnvironmentRequest(project, "completion", "archive", new ArchiveSource(version), 7), Ct);
        // Obtain authority through the actual scheduler; never hand-create a lease or force Leased.
        var grant = Assert.IsType<StageLeaseGrant>(await new SqlArchiveStageControl(db,
            new ArchiveStageOptions { Enabled = true }).PollAsync(credential.Token, Ct));
        var report = new ReportStagedArtifact(grant.JobId, grant.FencingToken, grant.Plan.ReservationId,
            version, Guid.NewGuid(), Hash, 10, "dt");
        if (recordArtifact)
            Assert.Equal("ContentVerified", (await new SqlArtifactReceipts(db).ReportAsync(credential.Token, report, Ct)).State);
        return new(admin, owner, project, pool, root, version, order, credential, grant, report);
    }

    private static async Task<AgentCredential> EnrollAsync(BuildSystemDbContext db, Actor admin, Guid pool, Guid id, string name)
    {
        var control = new SqlAgentControl(db);
        var ticket = await control.CreateEnrollmentAsync(admin, new CreateEnrollment(id, name, pool), Ct);
        await control.RegisterAsync(new RegisterAgent(id, ticket.EnrollmentId, ticket.Token, name), Ct);
        return await control.ApproveAsync(admin, id, Ct);
    }
    private static CompleteStage Input(Seed seed) => new(seed.Grant.JobId, seed.Grant.FencingToken, seed.Report.ArtifactId);
    private static async Task<StageCompletionReceipt> CompleteAsync(SqlServerTestDatabase sql, Seed seed,
        CompleteStage? input = null, string? token = null)
    {
        await using var db = sql.NewContext();
        return await new SqlStageCompletion(db, new ArchiveStageOptions { Enabled = true })
            .CompleteAsync(token ?? seed.Credential.Token, input ?? Input(seed), Ct);
    }
    private static async Task AssertHeldAsync(SqlServerTestDatabase sql)
    {
        await using var db = sql.NewContext();
        Assert.All(await db.EnvironmentReservations.ToListAsync(), x => Assert.Equal("Held", x.State));
        Assert.All(await db.EnvironmentRequests.ToListAsync(), x => Assert.Equal("Pending", x.State));
        Assert.All(await db.TestEnvironments.ToListAsync(), x =>
        {
            Assert.Equal("Requested", x.State); Assert.True(x.HasUnsettledProvisioningOperation); Assert.Null(x.ExpiresAt);
        });
    }

    [SqlServerFact]
    public async Task Completion_is_stage_only_and_does_not_redeliver_completed_grant_or_release_resources()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var receipt = await CompleteAsync(sql, seed);
        Assert.Equal(seed.Grant.JobId, receipt.JobId); Assert.Equal(seed.Grant.FencingToken, receipt.FencingToken);
        Assert.Equal(seed.Report.ArtifactId, receipt.ArtifactId); Assert.Equal("StageCompleted", receipt.State);
        await using (var db = sql.NewContext())
        {
            Assert.Equal("StageCompleted", (await db.EnvironmentJobs.SingleAsync()).State);
            Assert.Equal("Completed", (await db.AgentLeases.SingleAsync()).State);
            Assert.Equal("ContentVerified", (await db.StagedArtifacts.SingleAsync()).State);
            var reservation = await db.EnvironmentReservations.SingleAsync(); Assert.True(reservation.StagingBytes >= 10);
            Assert.Null(await new SqlArchiveStageControl(db, new ArchiveStageOptions { Enabled = true }).PollAsync(seed.Credential.Token, Ct));
        }
        await AssertHeldAsync(sql);
        // Artifact delivery replay is not a new execution permit after completion.
        await using (var db = sql.NewContext())
            Assert.Equal(409, (await Assert.ThrowsAsync<RequestRejectedException>(() =>
                new SqlArtifactReceipts(db).ReportAsync(seed.Credential.Token, seed.Report, Ct))).StatusCode);
        await using (var db = sql.NewContext())
        {
            var next = await new SqlEnvironmentOrders(db).CreateAsync(seed.Owner, "next-completion-order",
                new CreateEnvironmentRequest(seed.Project, "next", "archive", new ArchiveSource(seed.Version), 7), Ct);
            var grant = Assert.IsType<StageLeaseGrant>(await new SqlArchiveStageControl(db,
                new ArchiveStageOptions { Enabled = true }).PollAsync(seed.Credential.Token, Ct));
            Assert.Equal(next.JobId, grant.JobId); Assert.NotEqual(seed.Grant.JobId, grant.JobId);
        }
    }

    [SqlServerFact]
    public async Task Exact_completion_replay_after_expiry_returns_same_receipt_without_extra_audit()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var first = await CompleteAsync(sql, seed); int audits;
        await using (var db = sql.NewContext())
        {
            audits = await db.AuditEvents.CountAsync();
            await db.AgentLeases.Where(x => x.JobId == seed.Grant.JobId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }
        Assert.Equal(first, await CompleteAsync(sql, seed));
        await using var check = sql.NewContext();
        Assert.Equal(audits, await check.AuditEvents.CountAsync());
        Assert.Equal("Completed", (await check.AgentLeases.SingleAsync()).State);
        await AssertHeldAsync(sql);
        // Historical expiry is ignored on exact replay, not current authorization.
        await check.ArchiveRoots.Where(x => x.Id == seed.Root).ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false));
        Assert.Equal(409, (await Assert.ThrowsAsync<RequestRejectedException>(() => CompleteAsync(sql, seed))).StatusCode);
        Assert.Equal(audits, await check.AuditEvents.CountAsync());
    }

    [SqlServerFact]
    public async Task Conflicting_artifact_job_or_fence_never_terminalizes_active_stage()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var input = Input(seed);
        foreach (var changed in new[] { input with { ArtifactId = Guid.NewGuid() }, input with { JobId = Guid.NewGuid() }, input with { FencingToken = input.FencingToken + 1 } })
            Assert.Equal(409, (await Assert.ThrowsAsync<RequestRejectedException>(() => CompleteAsync(sql, seed, changed))).StatusCode);
        await using var check = sql.NewContext();
        Assert.Equal("Leased", (await check.EnvironmentJobs.SingleAsync()).State);
        Assert.Equal("Active", (await check.AgentLeases.SingleAsync()).State);
        await AssertHeldAsync(sql);
    }

    [SqlServerFact]
    public async Task Local_artifact_claim_without_server_receipt_cannot_complete()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql, recordArtifact: false);
        Assert.Equal(409, (await Assert.ThrowsAsync<RequestRejectedException>(() => CompleteAsync(sql, seed))).StatusCode);
        await using var check = sql.NewContext();
        Assert.Empty(await check.StagedArtifacts.ToListAsync());
        Assert.Equal("Leased", (await check.EnvironmentJobs.SingleAsync()).State);
        Assert.Equal("Active", (await check.AgentLeases.SingleAsync()).State);
        await AssertHeldAsync(sql);
    }

    [SqlServerFact]
    public async Task Expired_first_completion_persists_needs_attention_without_releasing_reservation()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        await using (var db = sql.NewContext())
            await db.AgentLeases.Where(x => x.JobId == seed.Grant.JobId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.Equal(409, (await Assert.ThrowsAsync<RequestRejectedException>(() => CompleteAsync(sql, seed))).StatusCode);
        await using var check = sql.NewContext();
        Assert.Equal("NeedsAttention", (await check.AgentLeases.SingleAsync()).State);
        Assert.NotEqual("StageCompleted", (await check.EnvironmentJobs.SingleAsync()).State);
        Assert.Equal("Held", (await check.EnvironmentReservations.SingleAsync()).State);
        Assert.True((await check.TestEnvironments.SingleAsync()).HasUnsettledProvisioningOperation);
        Assert.Equal("ContentVerified", (await check.StagedArtifacts.SingleAsync()).State);
    }

    [SqlServerFact]
    public async Task Rotation_and_revocation_recheck_current_token_even_for_terminal_replay()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        await CompleteAsync(sql, seed); AgentCredential rotated;
        await using (var db = sql.NewContext())
            rotated = await new SqlAgentControl(db).RotateAsync(seed.Admin, seed.Credential.AgentId, Ct);
        Assert.Equal(401, (await Assert.ThrowsAsync<RequestRejectedException>(() => CompleteAsync(sql, seed))).StatusCode);
        Assert.Equal("StageCompleted", (await CompleteAsync(sql, seed, token: rotated.Token)).State);
        await using (var db = sql.NewContext())
            await new SqlAgentControl(db).RevokeAsync(seed.Admin, seed.Credential.AgentId, Ct);
        Assert.Equal(401, (await Assert.ThrowsAsync<RequestRejectedException>(() => CompleteAsync(sql, seed, token: rotated.Token))).StatusCode);
        await AssertHeldAsync(sql);
    }

    [SqlServerFact]
    public async Task Other_agent_in_same_pool_and_changed_current_root_binding_cannot_complete()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        AgentCredential other;
        await using (var db = sql.NewContext()) other = await EnrollAsync(db, seed.Admin, seed.Pool, Guid.NewGuid(), "other-completion-worker");
        Assert.Equal(409, (await Assert.ThrowsAsync<RequestRejectedException>(() => CompleteAsync(sql, seed, token: other.Token))).StatusCode);
        await using (var db = sql.NewContext())
            await db.ArchiveRoots.Where(x => x.Id == seed.Root).ExecuteUpdateAsync(s => s.SetProperty(x => x.AgentScopeId, other.AgentId));
        Assert.Equal(409, (await Assert.ThrowsAsync<RequestRejectedException>(() => CompleteAsync(sql, seed))).StatusCode);
        await using var check = sql.NewContext();
        Assert.Equal("Active", (await check.AgentLeases.SingleAsync()).State);
        Assert.Equal("Leased", (await check.EnvironmentJobs.SingleAsync()).State);
    }

    [SqlServerFact]
    public async Task Default_disabled_completion_does_not_change_or_audit_stage()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        await using var db = sql.NewContext(); var audits = await db.AuditEvents.CountAsync();
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() =>
            new SqlStageCompletion(db, new ArchiveStageOptions()).CompleteAsync(seed.Credential.Token, Input(seed), Ct));
        Assert.Equal(409, error.StatusCode); Assert.Equal("StageDisabled", error.Code);
        Assert.Equal(audits, await db.AuditEvents.CountAsync());
        Assert.Equal("Active", (await db.AgentLeases.SingleAsync()).State);
        Assert.Equal("Leased", (await db.EnvironmentJobs.SingleAsync()).State);
        await AssertHeldAsync(sql);
    }
}
// end
