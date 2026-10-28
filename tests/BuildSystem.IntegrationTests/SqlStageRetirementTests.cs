using BuildSystem.Application;
using BuildSystem.Contracts;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildSystem.IntegrationTests;

// Requires explicit disposable SQL opt-in; these facts never substitute in-memory EF for SQL locking.
public sealed class SqlStageRetirementTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly string Hash = new('A', 64);
    private sealed record Seed(Actor Admin, Actor Owner, Guid Project, OrderReceipt Order,
        AgentCredential Credential, StageLeaseGrant? Grant, ReportStagedArtifact? Report);

    private static async Task<Seed> PrepareAsync(SqlServerTestDatabase sql, string state = "StageCompleted")
    {
        await sql.ApplyMigrationsAsync();
        var admin = new Actor(Guid.NewGuid(), true); var owner = new Actor(Guid.NewGuid(), false);
        var project = Guid.NewGuid(); var pool = Guid.NewGuid(); var root = Guid.NewGuid();
        var archive = Guid.NewGuid(); var version = Guid.NewGuid(); var agent = Guid.NewGuid();
        await using var db = sql.NewContext();
        db.Users.AddRange(new BuildSystemUser { Id = admin.UserId, UserName = "admin", NormalizedUserName = "ADMIN" },
            new BuildSystemUser { Id = owner.UserId, UserName = "owner", NormalizedUserName = "OWNER" });
        db.Projects.Add(new Project { Id = project, Name = "retirement", IsolationPolicyId = "isolated", PreparationProfileVersion = "v1" });
        db.ProjectMemberships.Add(new ProjectMembership { UserId = owner.UserId, ProjectId = project });
        db.ArchiveRoots.Add(new ArchiveRoot { Id = root, Enabled = true, AgentScopeId = agent,
            IsolationPolicyId = "isolated", MaxArchiveBytes = 10000, MaxStagingBytes = 10000 });
        db.ArchiveRootProjects.Add(new ArchiveRootProject { RootId = root, ProjectId = project });
        db.DiskArchives.Add(new DiskArchive { Id = archive, RootId = root, RelativePath = "retirement.dt" });
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
        var control = new SqlAgentControl(db);
        var ticket = await control.CreateEnrollmentAsync(admin, new(agent, "retirement-worker", pool), Ct);
        await control.RegisterAsync(new(agent, ticket.EnrollmentId, ticket.Token, "retirement-worker"), Ct);
        var credential = await control.ApproveAsync(admin, agent, Ct);
        var order = await new SqlEnvironmentOrders(db).CreateAsync(owner, "retirement-order",
            new(project, "retirement", "archive", new(version), 7), Ct);
        if (state == "Pending") return new(admin, owner, project, order, credential, null, null);
        var grant = Assert.IsType<StageLeaseGrant>(await new SqlArchiveStageControl(db,
            new ArchiveStageOptions { Enabled = true }).PollAsync(credential.Token, Ct));
        var report = new ReportStagedArtifact(grant.JobId, grant.FencingToken, grant.Plan.ReservationId,
            version, Guid.NewGuid(), Hash, 10, "dt");
        if (state == "Leased") return new(admin, owner, project, order, credential, grant, report);
        await new SqlArtifactReceipts(db).ReportAsync(credential.Token, report, Ct);
        if (state == "ArtifactReported") return new(admin, owner, project, order, credential, grant, report);
        await new SqlStageCompletion(db, new ArchiveStageOptions { Enabled = true })
            .CompleteAsync(credential.Token, new(grant.JobId, grant.FencingToken, report.ArtifactId), Ct);
        return new(admin, owner, project, order, credential, grant, report);
    }

    private static StageRetirementRequest Input(Seed seed) => new(seed.Order.JobId, seed.Report?.ArtifactId ?? Guid.NewGuid());
    private static async Task<StageRetirementReceipt> RequestAsync(SqlServerTestDatabase sql, Seed seed,
        Actor? actor = null, StageRetirementRequest? input = null)
    {
        await using var db = sql.NewContext();
        return await new SqlStageRetirement(db).RequestAsync(actor ?? seed.Owner, seed.Order.EnvironmentId, input ?? Input(seed), Ct);
    }
    private static async Task AssertRetainedAsync(SqlServerTestDatabase sql, Seed seed)
    {
        await using var db = sql.NewContext();
        Assert.Equal("StageCompleted", (await db.EnvironmentJobs.SingleAsync()).State);
        Assert.Equal("Completed", (await db.AgentLeases.SingleAsync()).State);
        var artifact = await db.StagedArtifacts.SingleAsync();
        Assert.Equal("ContentVerified", artifact.State); Assert.Equal(Hash, artifact.Sha256); Assert.Equal(10, artifact.SizeBytes);
        Assert.Equal(seed.Report!.ArtifactId, artifact.ArtifactId);
        var reservation = await db.EnvironmentReservations.SingleAsync();
        Assert.Equal("Held", reservation.State); Assert.Equal(100, reservation.ReservedBytes); Assert.Equal(10, reservation.StagingBytes);
        var environment = await db.TestEnvironments.SingleAsync();
        Assert.Equal("Requested", environment.State); Assert.True(environment.HasUnsettledProvisioningOperation); Assert.Null(environment.ExpiresAt);
        Assert.Equal("Pending", (await db.EnvironmentRequests.SingleAsync()).State);
    }

    [SqlServerFact]
    public async Task Exact_replay_creates_one_intent_one_audit_and_changes_no_lifecycle_state()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        int before; await using (var db = sql.NewContext()) before = await db.AuditEvents.CountAsync();
        var first = await RequestAsync(sql, seed);
        await using (var db = sql.NewContext())
            await db.AgentLeases.Where(x => x.JobId == seed.Order.JobId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-10)));
        var replay = await RequestAsync(sql, seed);
        Assert.Equal(first, replay); Assert.NotEqual(Guid.Empty, first.RetirementId);
        Assert.Equal(seed.Order.EnvironmentId, first.EnvironmentId); Assert.Equal(seed.Order.JobId, first.JobId);
        Assert.Equal(seed.Report!.ArtifactId, first.ArtifactId); Assert.Equal("Requested", first.State);
        Assert.NotEqual(default, first.CreatedAt);
        await using (var db = sql.NewContext())
        {
            Assert.Single(await db.StageRetirementIntents.ToListAsync());
            Assert.Equal(before + 1, await db.AuditEvents.CountAsync());
            Assert.Single(await db.AuditEvents.Where(x => x.Action == "StageRetirementRequested").ToListAsync());
        }
        await AssertRetainedAsync(sql, seed);
    }

    [SqlServerFact]
    public async Task Foreign_owner_is_denied_admin_is_allowed_and_current_owner_access_is_rechecked()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var foreign = new Actor(Guid.NewGuid(), false);
        var denied = await Assert.ThrowsAsync<RequestRejectedException>(() => RequestAsync(sql, seed, foreign));
        Assert.Equal(404, denied.StatusCode);
        await using (var db = sql.NewContext()) Assert.Empty(await db.StageRetirementIntents.ToListAsync());
        var accepted = await RequestAsync(sql, seed, seed.Admin);
        Assert.Equal(accepted, await RequestAsync(sql, seed));
        await using (var db = sql.NewContext())
            await db.ProjectMemberships.Where(m => m.ProjectId == seed.Project && m.UserId == seed.Owner.UserId).ExecuteDeleteAsync();
        denied = await Assert.ThrowsAsync<RequestRejectedException>(() => RequestAsync(sql, seed));
        Assert.Equal(404, denied.StatusCode);
        await using (var db = sql.NewContext())
        {
            db.ProjectMemberships.Add(new ProjectMembership { ProjectId = seed.Project, UserId = seed.Owner.UserId });
            await db.SaveChangesAsync();
            await db.Projects.Where(p => p.Id == seed.Project).ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, false));
        }
        denied = await Assert.ThrowsAsync<RequestRejectedException>(() => RequestAsync(sql, seed));
        Assert.Equal(404, denied.StatusCode);
        denied = await Assert.ThrowsAsync<RequestRejectedException>(() => RequestAsync(sql, seed, seed.Admin));
        Assert.Equal(404, denied.StatusCode);
        await using (var db = sql.NewContext())
        {
            await db.Projects.Where(p => p.Id == seed.Project).ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, true));
            var rootId = (await db.DiskArchives.SingleAsync()).RootId;
            await db.ArchiveRoots.Where(r => r.Id == rootId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Enabled, false));
        }
        denied = await Assert.ThrowsAsync<RequestRejectedException>(() => RequestAsync(sql, seed, seed.Admin));
        Assert.Equal(409, denied.StatusCode);
        await using (var db = sql.NewContext()) Assert.Single(await db.StageRetirementIntents.ToListAsync());
        await AssertRetainedAsync(sql, seed);
    }

    [SqlServerFact]
    public async Task Empty_or_conflicting_expected_identities_create_no_intent()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        foreach (var input in new[] { new StageRetirementRequest(Guid.Empty, seed.Report!.ArtifactId),
            new(seed.Order.JobId, Guid.Empty), new(Guid.NewGuid(), seed.Report.ArtifactId), new(seed.Order.JobId, Guid.NewGuid()) })
        {
            var error = await Assert.ThrowsAsync<RequestRejectedException>(() => RequestAsync(sql, seed, input: input));
            Assert.Contains(error.StatusCode, new[] { 400, 409 });
        }
        await using var db = sql.NewContext(); Assert.Empty(await db.StageRetirementIntents.ToListAsync());
        await AssertRetainedAsync(sql, seed);
    }

    [SqlServerFact]
    public async Task Pending_leased_and_artifact_reported_without_completion_cannot_request_retirement()
    {
        foreach (var state in new[] { "Pending", "Leased", "ArtifactReported" })
        {
            await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql, state);
            var error = await Assert.ThrowsAsync<RequestRejectedException>(() => RequestAsync(sql, seed));
            Assert.Equal(409, error.StatusCode);
            await using var db = sql.NewContext(); Assert.Empty(await db.StageRetirementIntents.ToListAsync());
            Assert.Equal("Held", (await db.EnvironmentReservations.SingleAsync()).State);
            Assert.True((await db.TestEnvironments.SingleAsync()).HasUnsettledProvisioningOperation);
            Assert.Equal(state == "Pending" ? "Pending" : "Leased", (await db.EnvironmentJobs.SingleAsync()).State);
        }
    }

    [SqlServerFact]
    public async Task Persisted_intent_cannot_be_mutated_through_SaveChanges()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        await RequestAsync(sql, seed);
        await using (var db = sql.NewContext())
        {
            var intent = await db.StageRetirementIntents.SingleAsync();
            db.Entry(intent).Property("ArtifactId").CurrentValue = Guid.NewGuid();
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await using (var db = sql.NewContext())
        {
            var intent = await db.StageRetirementIntents.SingleAsync();
            Assert.Equal(seed.Report!.ArtifactId, db.Entry(intent).Property("ArtifactId").CurrentValue);
            db.StageRetirementIntents.Remove(intent);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await using (var db = sql.NewContext()) Assert.Single(await db.StageRetirementIntents.ToListAsync());
        await AssertRetainedAsync(sql, seed);
    }

    [SqlServerFact]
    public async Task Concurrent_requests_commit_one_intent_with_identical_receipts()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        int audits; await using (var db = sql.NewContext()) audits = await db.AuditEvents.CountAsync();
        var requests = Enumerable.Range(0, 6).Select(_ => RequestAsync(sql, seed)).ToArray();
        var receipts = await Task.WhenAll(requests);
        Assert.All(receipts, receipt => Assert.Equal(receipts[0], receipt));
        await using (var db = sql.NewContext())
        {
            Assert.Single(await db.StageRetirementIntents.ToListAsync());
            Assert.Equal(audits + 1, await db.AuditEvents.CountAsync());
        }
        await AssertRetainedAsync(sql, seed);
    }
}
// end
