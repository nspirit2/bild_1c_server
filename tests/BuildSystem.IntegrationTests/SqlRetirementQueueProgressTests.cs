using System.Security.Cryptography;
using System.Text;
using BuildSystem.Application;
using BuildSystem.Contracts;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildSystem.IntegrationTests;

// Disposable SQL opt-in only. Queue progress is not physical deletion or quota release proof.
public sealed class SqlRetirementQueueProgressTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly string Hash = new('A', 64);
    private sealed record Item(OrderReceipt Order, StageLeaseGrant Grant, StageRetirementReceipt Intent);
    private sealed record Seed(Actor Admin, Actor Owner, Guid Project, Guid Version, AgentCredential Credential, Item First, Item Second);
    private static Guid ArtifactId(Guid reservation, Guid version)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"archive-stage-v1:{reservation:N}:{version:N}"));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x80); bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes.AsSpan(0, 16), bigEndian: true);
    }
    private static async Task<Seed> PrepareAsync(SqlServerTestDatabase sql)
    {
        await sql.ApplyMigrationsAsync();
        var admin = new Actor(Guid.NewGuid(), true); var owner = new Actor(Guid.NewGuid(), false);
        var project = Guid.NewGuid(); var root = Guid.NewGuid(); var pool = Guid.NewGuid();
        var archive = Guid.NewGuid(); var version = Guid.NewGuid(); var agent = Guid.NewGuid();
        await using var db = sql.NewContext();
        db.Users.AddRange(new BuildSystemUser { Id = admin.UserId, UserName = "admin", NormalizedUserName = "ADMIN" },
            new BuildSystemUser { Id = owner.UserId, UserName = "owner", NormalizedUserName = "OWNER" });
        db.Projects.Add(new Project { Id = project, Name = "queue-progress", IsolationPolicyId = "isolated", PreparationProfileVersion = "v1" });
        db.ProjectMemberships.Add(new ProjectMembership { UserId = owner.UserId, ProjectId = project });
        db.ArchiveRoots.Add(new ArchiveRoot { Id = root, AgentScopeId = agent, Enabled = true, IsolationPolicyId = "isolated", MaxArchiveBytes = 10000, MaxStagingBytes = 10000 });
        db.ArchiveRootProjects.Add(new ArchiveRootProject { RootId = root, ProjectId = project });
        db.DiskArchives.Add(new DiskArchive { Id = archive, RootId = root, RelativePath = "queue-progress.dt" });
        db.DiskArchiveVersions.Add(new DiskArchiveVersion { Id = version, ArchiveId = archive, Sha256 = Hash, SizeBytes = 10, Format = "dt", State = "Ready", EstimatedDbBytes = 100 });
        db.EnvironmentPools.Add(new EnvironmentPool { Id = pool, AgentScopeId = agent, Enabled = true,
            ClusterId = Guid.NewGuid(), SqlServerId = Guid.NewGuid(), IsolationPolicyId = "isolated", CapacityBytes = 100000,
            MaxEnvironmentBytes = 10000, MaxStagingBytes = 10000, MaxConcurrentProvisioning = 5, SupportsDt = true,
            SupportsBak = true, IsolationVerified = true, ScheduledJobsSuppressionVerified = true, ResourceLimitVerified = true });
        db.ProjectPools.Add(new ProjectPool { ProjectId = project, PoolId = pool, Enabled = true });
        db.EnvironmentQuotas.Add(new EnvironmentQuota { SubjectId = owner.UserId, MaxActiveEnvironments = 5, MaxConcurrentProvisioning = 5, MaxStorageBytes = 100000, MaxTtlDays = 30 });
        await db.SaveChangesAsync();
        var control = new SqlAgentControl(db); var ticket = await control.CreateEnrollmentAsync(admin, new(agent, "queue-worker", pool), Ct);
        await control.RegisterAsync(new(agent, ticket.EnrollmentId, ticket.Token, "queue-worker"), Ct);
        var credential = await control.ApproveAsync(admin, agent, Ct);
        async Task<Item> Complete(string key)
        {
            var order = await new SqlEnvironmentOrders(db).CreateAsync(owner, key, new(project, key, "archive", new(version), 7), Ct);
            var grant = Assert.IsType<StageLeaseGrant>(await new SqlArchiveStageControl(db, new ArchiveStageOptions { Enabled = true }).PollAsync(credential.Token, Ct));
            Assert.Equal(order.JobId, grant.JobId);
            var artifact = ArtifactId(grant.Plan.ReservationId, version);
            await new SqlArtifactReceipts(db).ReportAsync(credential.Token, new(grant.JobId, grant.FencingToken, grant.Plan.ReservationId, version, artifact, Hash, 10, "dt"), Ct);
            await new SqlStageCompletion(db, new ArchiveStageOptions { Enabled = true }).CompleteAsync(credential.Token, new(grant.JobId, grant.FencingToken, artifact), Ct);
            var intent = await new SqlStageRetirement(db).RequestAsync(owner, order.EnvironmentId, new(order.JobId, artifact), Ct);
            return new(order, grant, intent);
        }
        var first = await Complete("queue-first");
        var second = await Complete("queue-second");
        return new(admin, owner, project, version, credential, first, second);
    }
    private static async Task<StageRetirementPlan?> Poll(SqlServerTestDatabase sql, Seed seed)
    {
        await using var db = sql.NewContext();
        return await new SqlStageRetirementPlans(db, new StageRetirementOptions { Enabled = true }).PollAsync(seed.Credential.Token, Ct);
    }
    private static async Task<StageCleanupAuthorization> Claim(SqlServerTestDatabase sql, Seed seed, Item item, string? token = null)
    {
        await using var db = sql.NewContext();
        return await new SqlStageCleanup(db, new StageCleanupOptions { Enabled = true }).ClaimAsync(token ?? seed.Credential.Token,
            new(item.Intent.RetirementId, item.Intent.ArtifactId), Ct);
    }
    [SqlServerFact]
    public async Task Accepted_first_result_advances_poll_and_second_claim_while_first_claim_replay_remains_stable()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        Assert.Equal(seed.First.Intent.RetirementId, Assert.IsType<StageRetirementPlan>(await Poll(sql, seed)).RetirementId);
        var first = await Claim(sql, seed, seed.First);
        await using (var db = sql.NewContext())
            await new SqlStageCleanupResults(db, new StageCleanupResultOptions { Enabled = true }).ReportAsync(seed.Credential.Token,
                new(Guid.NewGuid(), first.AuthorizationId, first.Plan.RetirementId, first.Plan.JobId, first.Plan.ArtifactId, first.Plan.FencingToken), Ct);
        var next = Assert.IsType<StageRetirementPlan>(await Poll(sql, seed));
        Assert.Equal(seed.Second.Intent.RetirementId, next.RetirementId);
        var second = await Claim(sql, seed, seed.Second);
        Assert.Equal(next, second.Plan); Assert.NotEqual(first.AuthorizationId, second.AuthorizationId);
        Assert.Equal(first, await Claim(sql, seed, seed.First));
        await using var read = sql.NewContext();
        Assert.Equal(2, await read.StageCleanupAuthorizations.CountAsync());
        Assert.Equal(1, await read.StageCleanupResults.CountAsync());
        Assert.Equal(0, await read.StagingAllocationReleases.CountAsync());
        Assert.All(await read.EnvironmentReservations.ToListAsync(), r => { Assert.Equal("Held", r.State); Assert.Equal(10, r.StagingBytes); Assert.Equal(100, r.ReservedBytes); });
        Assert.All(await read.StageRetirementIntents.ToListAsync(), i => Assert.Equal("Requested", i.State));
    }
    [SqlServerFact]
    public async Task Explicit_non_oldest_claim_is_exact_and_unknown_or_foreign_intent_cannot_be_claimed()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var second = await Claim(sql, seed, seed.Second);
        Assert.Equal(seed.Second.Intent.RetirementId, second.Plan.RetirementId);
        Assert.Equal(seed.First.Intent.RetirementId, Assert.IsType<StageRetirementPlan>(await Poll(sql, seed)).RetirementId);
        await using var db = sql.NewContext();
        var service = new SqlStageCleanup(db, new StageCleanupOptions { Enabled = true });
        var missing = await Assert.ThrowsAsync<RequestRejectedException>(() => service.ClaimAsync(seed.Credential.Token, new(Guid.NewGuid(), seed.First.Intent.ArtifactId), Ct));
        Assert.Equal(409, missing.StatusCode);
        var foreignAgent = Guid.NewGuid(); var poolId = Guid.NewGuid();
        db.EnvironmentPools.Add(new EnvironmentPool { Id = poolId, AgentScopeId = foreignAgent, Enabled = true, ClusterId = Guid.NewGuid(), SqlServerId = Guid.NewGuid(), IsolationPolicyId = "isolated" });
        await db.SaveChangesAsync();
        var control = new SqlAgentControl(db); var ticket = await control.CreateEnrollmentAsync(seed.Admin, new(foreignAgent, "foreign-queue-worker", poolId), Ct);
        await control.RegisterAsync(new(foreignAgent, ticket.EnrollmentId, ticket.Token, "foreign-queue-worker"), Ct);
        var foreign = await control.ApproveAsync(seed.Admin, foreignAgent, Ct);
        var mismatch = await Assert.ThrowsAsync<RequestRejectedException>(() => Claim(sql, seed, seed.First, foreign.Token));
        Assert.Equal(409, mismatch.StatusCode);
        Assert.Single(await db.StageCleanupAuthorizations.ToListAsync());
    }
    [SqlServerFact]
    public async Task Authorization_without_result_keeps_oldest_and_active_or_attention_lease_blocks_without_sweep()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var first = await Claim(sql, seed, seed.First);
        Assert.Equal(first.Plan, await Poll(sql, seed)); Assert.Equal(first.Plan, await Poll(sql, seed));
        await using var db = sql.NewContext();
        var order = await new SqlEnvironmentOrders(db).CreateAsync(seed.Owner, "queue-third", new(seed.Project, "third", "archive", new(seed.Version), 7), Ct);
        var lease = Assert.IsType<StageLeaseGrant>(await new SqlArchiveStageControl(db, new ArchiveStageOptions { Enabled = true }).PollAsync(seed.Credential.Token, Ct));
        Assert.Equal(order.JobId, lease.JobId);
        var row = await db.AgentLeases.SingleAsync(l => l.JobId == order.JobId);
        row.ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1); await db.SaveChangesAsync();
        Assert.Null(await Poll(sql, seed));
        var blocked = await Assert.ThrowsAsync<RequestRejectedException>(() => Claim(sql, seed, seed.Second)); Assert.Equal(409, blocked.StatusCode);
        db.ChangeTracker.Clear(); row = await db.AgentLeases.SingleAsync(l => l.JobId == order.JobId); Assert.Equal("Active", row.State);
        row.State = "NeedsAttention"; await db.SaveChangesAsync();
        Assert.Null(await Poll(sql, seed));
        Assert.Single(await db.StageCleanupAuthorizations.ToListAsync()); Assert.Empty(await db.StageCleanupResults.ToListAsync());
    }
}
// end
