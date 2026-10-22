using BuildSystem.Application;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildSystem.IntegrationTests;

public sealed class SqlEnvironmentOrderTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static async Task<Seed> PrepareAsync(SqlServerTestDatabase database, int ownerMax = 2, int poolMax = 2)
    {
        await database.ApplyMigrationsAsync();
        var owner = Guid.NewGuid();
        var project = Guid.NewGuid();
        var root = Guid.NewGuid();
        var pool = Guid.NewGuid();
        var archive = Guid.NewGuid();
        var version = Guid.NewGuid();
        var agent = Guid.NewGuid();
        await using var db = database.NewContext();
        db.Users.Add(new BuildSystemUser { Id = owner, UserName = "owner", NormalizedUserName = "OWNER" });
        db.Projects.Add(new Project { Id = project, Name = "project", IsolationPolicyId = "isolated", PreparationProfileVersion = "v1" });
        db.ProjectMemberships.Add(new ProjectMembership { UserId = owner, ProjectId = project });
        db.ArchiveRoots.Add(new ArchiveRoot { Id = root, Enabled = true, AgentScopeId = agent, IsolationPolicyId = "isolated", MaxArchiveBytes = 10000, MaxStagingBytes = 10000 });
        db.ArchiveRootProjects.Add(new ArchiveRootProject { RootId = root, ProjectId = project });
        db.DiskArchives.Add(new DiskArchive { Id = archive, RootId = root, RelativePath = "sample.dt" });
        db.DiskArchiveVersions.Add(new DiskArchiveVersion { Id = version, ArchiveId = archive, Sha256 = new string('A', 64), SizeBytes = 100, Format = "dt", State = "Ready", EstimatedDbBytes = 1000 });
        db.EnvironmentPools.Add(new EnvironmentPool { Id = pool, Enabled = true, ClusterId = Guid.NewGuid(), SqlServerId = Guid.NewGuid(), AgentScopeId = agent, IsolationPolicyId = "isolated", CapacityBytes = 100000, MaxEnvironmentBytes = 10000, MaxStagingBytes = 10000, MaxConcurrentProvisioning = poolMax, SupportsDt = true, SupportsBak = true, IsolationVerified = true, ScheduledJobsSuppressionVerified = true, ResourceLimitVerified = true });
        db.ProjectPools.Add(new ProjectPool { ProjectId = project, PoolId = pool, Enabled = true });
        db.EnvironmentQuotas.Add(new EnvironmentQuota { SubjectId = owner, MaxActiveEnvironments = ownerMax, MaxConcurrentProvisioning = ownerMax, MaxStorageBytes = 100000, MaxTtlDays = 30 });
        await db.SaveChangesAsync();
        return new Seed(owner, project, root, pool, version);
    }

    private sealed record Seed(Guid Owner, Guid Project, Guid Root, Guid Pool, Guid Version)
    {
        public Actor Actor => new(Owner, false);
        public CreateEnvironmentRequest Order(string name = "sample") => new(Project, name, "archive", new ArchiveSource(Version), 7);
    }

    [SqlServerFact]
    public async Task Changed_payload_returns_409()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(database);
        await using var db = database.NewContext();
        var service = new SqlEnvironmentOrders(db);
        await service.CreateAsync(seed.Actor, "key", seed.Order(), Ct);
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => service.CreateAsync(seed.Actor, "key", seed.Order("different"), Ct));
        Assert.Equal(409, error.StatusCode);
        Assert.Equal("IdempotencyConflict", error.Code);
    }

    [SqlServerFact]
    public async Task Source_authorization_denies_order_without_orphans()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(database);
        await using var db = database.NewContext();
        db.ArchiveRootProjects.Remove(await db.ArchiveRootProjects.SingleAsync());
        await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => new SqlEnvironmentOrders(db).CreateAsync(seed.Actor, "key", seed.Order(), Ct));
        Assert.Equal(403, error.StatusCode);
        await AssertCountsAsync(database, 0);
    }

    [SqlServerFact]
    public async Task Quota_rejection_is_atomic_without_orphan_intent_or_reservation()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(database, ownerMax: 0);
        await using var db = database.NewContext();
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => new SqlEnvironmentOrders(db).CreateAsync(seed.Actor, "key", seed.Order(), Ct));
        Assert.Equal(409, error.StatusCode);
        Assert.Equal("QuotaExceeded", error.Code);
        await AssertCountsAsync(database, 0);
    }

    [SqlServerFact]
    public async Task New_context_persists_data_and_own_reads_exclude_foreign_owner()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(database);
        OrderReceipt receipt;
        await using (var db = database.NewContext()) receipt = await new SqlEnvironmentOrders(db).CreateAsync(seed.Actor, "key", seed.Order(), Ct);
        await using var check = database.NewContext();
        var service = new SqlEnvironmentOrders(check);
        Assert.NotNull(await service.GetRequestAsync(seed.Actor, receipt.RequestId, Ct));
        Assert.NotNull(await service.GetEnvironmentAsync(seed.Actor, receipt.EnvironmentId, Ct));
        Assert.Single(await service.ListEnvironmentsAsync(seed.Actor, Ct));
        var foreign = new Actor(Guid.NewGuid(), false);
        check.Users.Add(new BuildSystemUser { Id = foreign.UserId, UserName = "foreign", NormalizedUserName = "FOREIGN" });
        check.ProjectMemberships.Add(new ProjectMembership { UserId = foreign.UserId, ProjectId = seed.Project });
        await check.SaveChangesAsync();
        Assert.Null(await service.GetRequestAsync(foreign, receipt.RequestId, Ct));
        Assert.Null(await service.GetEnvironmentAsync(foreign, receipt.EnvironmentId, Ct));
        Assert.Empty(await service.ListEnvironmentsAsync(foreign, Ct));
        Assert.NotEmpty((await check.TestEnvironments.SingleAsync()).RowVersion);
    }

    [SqlServerFact]
    public async Task Source_acl_revocation_replay_must_not_leak_original_receipt()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(database);
        await using (var db = database.NewContext()) await new SqlEnvironmentOrders(db).CreateAsync(seed.Actor, "key", seed.Order(), Ct);
        await using (var db = database.NewContext())
        {
            db.ArchiveRootProjects.Remove(await db.ArchiveRootProjects.SingleAsync());
            await db.SaveChangesAsync();
        }
        await using var check = database.NewContext();
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => new SqlEnvironmentOrders(check).CreateAsync(seed.Actor, "key", seed.Order(), Ct));
        Assert.Equal(403, error.StatusCode);
        await AssertCountsAsync(database, 1);
    }

    [SqlServerFact]
    public async Task Replay_after_source_unavailable_still_returns_original_receipt()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(database);
        OrderReceipt receipt;
        await using (var db = database.NewContext()) receipt = await new SqlEnvironmentOrders(db).CreateAsync(seed.Actor, "key", seed.Order(), Ct);
        await using (var db = database.NewContext())
        {
            (await db.DiskArchiveVersions.SingleAsync()).State = "Unavailable";
            await db.SaveChangesAsync();
        }
        await using var check = database.NewContext();
        Assert.Equal(receipt, await new SqlEnvironmentOrders(check).CreateAsync(seed.Actor, "key", seed.Order(), Ct));
    }

    [SqlServerFact]
    public async Task Concurrent_same_owner_cannot_exceed_owner_quota()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(database, ownerMax: 1, poolMax: 2);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async i =>
        {
            await using var db = database.NewContext();
            try { await new SqlEnvironmentOrders(db).CreateAsync(seed.Actor, "key-" + i, seed.Order("order-" + i), Ct); return 202; }
            catch (RequestRejectedException e) { Assert.Equal("QuotaExceeded", e.Code); return e.StatusCode; }
        }));
        Assert.Equal(new[] { 202, 409 }, results.Order().ToArray());
        await AssertCountsAsync(database, 1);
    }

    [SqlServerFact]
    public async Task Concurrent_owners_cannot_exceed_pool_capacity()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(database, ownerMax: 2, poolMax: 1);
        var other = new Actor(Guid.NewGuid(), false);
        await using (var db = database.NewContext())
        {
            db.Users.Add(new BuildSystemUser { Id = other.UserId, UserName = "other", NormalizedUserName = "OTHER" });
            db.ProjectMemberships.Add(new ProjectMembership { UserId = other.UserId, ProjectId = seed.Project });
            db.EnvironmentQuotas.Add(new EnvironmentQuota { SubjectId = other.UserId, MaxActiveEnvironments = 2, MaxConcurrentProvisioning = 2, MaxStorageBytes = 100000, MaxTtlDays = 30 });
            await db.SaveChangesAsync();
        }
        var results = await Task.WhenAll(new[] { seed.Actor, other }.Select(async (actor, i) =>
        {
            await using var db = database.NewContext();
            try { await new SqlEnvironmentOrders(db).CreateAsync(actor, "key-" + i, seed.Order("order-" + i), Ct); return 202; }
            catch (RequestRejectedException e) { Assert.Equal("QuotaExceeded", e.Code); return e.StatusCode; }
        }));
        Assert.Equal(new[] { 202, 409 }, results.Order().ToArray());
        await AssertCountsAsync(database, 1);
    }

    private static async Task AssertCountsAsync(SqlServerTestDatabase database, int count)
    {
        await using var db = database.NewContext();
        Assert.Equal(count, await db.EnvironmentRequests.CountAsync());
        Assert.Equal(count, await db.EnvironmentReservations.CountAsync());
        Assert.Equal(count, await db.TestEnvironments.CountAsync());
        Assert.Equal(count, await db.EnvironmentJobs.CountAsync());
        Assert.Equal(count, await db.AuditEvents.CountAsync());
    }

    [SqlServerFact]
    public async Task Replay_returns_original_receipt_and_does_not_duplicate_rows()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(database);
        OrderReceipt first;
        await using (var db = database.NewContext()) first = await new SqlEnvironmentOrders(db).CreateAsync(seed.Actor, "key", seed.Order(), Ct);
        await using (var db = database.NewContext()) Assert.Equal(first, await new SqlEnvironmentOrders(db).CreateAsync(seed.Actor, "key", seed.Order(), Ct));
        await using var check = database.NewContext();
        Assert.Equal(1, await check.EnvironmentRequests.CountAsync());
        Assert.Equal(1, await check.EnvironmentReservations.CountAsync());
        Assert.Equal(1, await check.TestEnvironments.CountAsync());
        Assert.Equal(1, await check.EnvironmentJobs.CountAsync());
    }
}
