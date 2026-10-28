using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Contracts;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildSystem.IntegrationTests;

// Explicit disposable SQL opt-in. Logical staging release never changes historical bytes, DB reservations or environments.
public sealed class SqlStagingAllocationReleaseTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly string Hash = new('A', 64);
    private sealed record Seed(Actor Admin, Actor Owner, Guid Project, Guid Root, Guid Pool, Guid Version,
        OrderReceipt Order, AgentCredential Credential, StageLeaseGrant Grant, StageCleanupAuthorization Authorization, Guid ResultId);
    private static Guid ArtifactId(Guid reservation, Guid version)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"archive-stage-v1:{reservation:N}:{version:N}"));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x80); bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes.AsSpan(0, 16), bigEndian: true);
    }
    private static EnvironmentPool Pool(Guid id, Guid agent) => new() { Id = id, AgentScopeId = agent, Enabled = true,
        ClusterId = Guid.NewGuid(), SqlServerId = Guid.NewGuid(), IsolationPolicyId = "isolated", CapacityBytes = 100000,
        MaxEnvironmentBytes = 10000, MaxStagingBytes = 10000, MaxConcurrentProvisioning = 5, SupportsDt = true,
        SupportsBak = true, IsolationVerified = true, ScheduledJobsSuppressionVerified = true, ResourceLimitVerified = true };
    private static async Task<Seed> PrepareAsync(SqlServerTestDatabase sql, bool acceptResult = true)
    {
        await sql.ApplyMigrationsAsync();
        var admin = new Actor(Guid.NewGuid(), true); var owner = new Actor(Guid.NewGuid(), false);
        var project = Guid.NewGuid(); var root = Guid.NewGuid(); var pool = Guid.NewGuid();
        var archive = Guid.NewGuid(); var version = Guid.NewGuid(); var agent = Guid.NewGuid();
        await using var db = sql.NewContext();
        db.Users.AddRange(new BuildSystemUser { Id = admin.UserId, UserName = "admin", NormalizedUserName = "ADMIN" },
            new BuildSystemUser { Id = owner.UserId, UserName = "owner", NormalizedUserName = "OWNER" });
        db.Projects.Add(new Project { Id = project, Name = "cleanup-result", IsolationPolicyId = "isolated", PreparationProfileVersion = "v1" });
        db.ProjectMemberships.Add(new ProjectMembership { UserId = owner.UserId, ProjectId = project });
        db.ArchiveRoots.Add(new ArchiveRoot { Id = root, AgentScopeId = agent, Enabled = true, IsolationPolicyId = "isolated", MaxArchiveBytes = 10000, MaxStagingBytes = 10000 });
        db.ArchiveRootProjects.Add(new ArchiveRootProject { RootId = root, ProjectId = project });
        db.DiskArchives.Add(new DiskArchive { Id = archive, RootId = root, RelativePath = "cleanup-result.dt" });
        db.DiskArchiveVersions.Add(new DiskArchiveVersion { Id = version, ArchiveId = archive, Sha256 = Hash, SizeBytes = 10, Format = "dt", State = "Ready", EstimatedDbBytes = 100 });
        db.EnvironmentPools.Add(Pool(pool, agent)); db.ProjectPools.Add(new ProjectPool { ProjectId = project, PoolId = pool, Enabled = true });
        db.EnvironmentQuotas.Add(new EnvironmentQuota { SubjectId = owner.UserId, MaxActiveEnvironments = 5, MaxConcurrentProvisioning = 5, MaxStorageBytes = 100000, MaxTtlDays = 30 });
        await db.SaveChangesAsync();
        var control = new SqlAgentControl(db); var ticket = await control.CreateEnrollmentAsync(admin, new(agent, "result-worker", pool), Ct);
        await control.RegisterAsync(new(agent, ticket.EnrollmentId, ticket.Token, "result-worker"), Ct);
        var credential = await control.ApproveAsync(admin, agent, Ct);
        var order = await new SqlEnvironmentOrders(db).CreateAsync(owner, "cleanup-result-order", new(project, "cleanup", "archive", new(version), 7), Ct);
        var grant = Assert.IsType<StageLeaseGrant>(await new SqlArchiveStageControl(db, new ArchiveStageOptions { Enabled = true }).PollAsync(credential.Token, Ct));
        var artifact = ArtifactId(grant.Plan.ReservationId, version);
        await new SqlArtifactReceipts(db).ReportAsync(credential.Token, new(grant.JobId, grant.FencingToken, grant.Plan.ReservationId, version, artifact, Hash, 10, "dt"), Ct);
        await new SqlStageCompletion(db, new ArchiveStageOptions { Enabled = true }).CompleteAsync(credential.Token, new(grant.JobId, grant.FencingToken, artifact), Ct);
        var retirement = await new SqlStageRetirement(db).RequestAsync(owner, order.EnvironmentId, new(order.JobId, artifact), Ct);
        var authorization = await new SqlStageCleanup(db, new StageCleanupOptions { Enabled = true }).ClaimAsync(credential.Token, new(retirement.RetirementId, artifact), Ct);
        var resultId = Guid.NewGuid();
        if (acceptResult) await new SqlStageCleanupResults(db, new StageCleanupResultOptions { Enabled = true }).ReportAsync(credential.Token,
            new(resultId, authorization.AuthorizationId, retirement.RetirementId, order.JobId, artifact, grant.FencingToken), Ct);
        return new(admin, owner, project, root, pool, version, order, credential, grant, authorization, resultId);
    }
    private static ReleaseStagingAllocation Expected(Seed seed) => new(seed.Authorization.AuthorizationId, seed.Grant.Plan.ReservationId);
    private static async Task<StagingAllocationReleaseReceipt> ReleaseAsync(SqlServerTestDatabase sql, Seed seed,
        Actor? actor = null, Guid? result = null, ReleaseStagingAllocation? expected = null)
    {
        await using var db = sql.NewContext();
        return await new SqlStagingAllocationRelease(db, new StagingAllocationReleaseOptions { Enabled = true })
            .ReleaseAsync(actor ?? seed.Admin, result ?? seed.ResultId, expected ?? Expected(seed), Ct);
    }
    private static async Task<OrderReceipt> OrderAsync(SqlServerTestDatabase sql, Seed seed, string key)
    {
        await using var db = sql.NewContext();
        return await new SqlEnvironmentOrders(db).CreateAsync(seed.Owner, key, new(seed.Project, "another", "archive", new(seed.Version), 7), Ct);
    }
    private static async Task RejectOrderAsync(SqlServerTestDatabase sql, Seed seed, string key)
    {
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => OrderAsync(sql, seed, key));
        Assert.Equal(409, error.StatusCode); Assert.Equal("QuotaExceeded", error.Code);
    }
    private static async Task AssertRetainedAsync(SqlServerTestDatabase sql, Seed seed)
    {
        await using var db = sql.NewContext();
        Assert.Equal("StageCompleted", (await db.EnvironmentJobs.SingleAsync(j => j.Id == seed.Order.JobId)).State);
        Assert.Equal("Completed", (await db.AgentLeases.SingleAsync(l => l.JobId == seed.Order.JobId)).State);
        var artifact = await db.StagedArtifacts.SingleAsync(a => a.ArtifactId == seed.Authorization.Plan.ArtifactId);
        Assert.Equal("ContentVerified", artifact.State); Assert.Equal(Hash, artifact.Sha256); Assert.Equal(10, artifact.SizeBytes);
        var environment = await db.TestEnvironments.SingleAsync(e => e.Id == seed.Order.EnvironmentId);
        Assert.Equal("Requested", environment.State); Assert.True(environment.HasUnsettledProvisioningOperation); Assert.Null(environment.ExpiresAt);
        var reservation = await db.EnvironmentReservations.SingleAsync(r => r.Id == seed.Grant.Plan.ReservationId);
        Assert.Equal("Held", reservation.State); Assert.Equal(100, reservation.ReservedBytes); Assert.Equal(10, reservation.StagingBytes);
        Assert.Equal("Pending", (await db.EnvironmentRequests.SingleAsync(q => q.Id == seed.Order.RequestId)).State);
        Assert.Equal("Requested", (await db.StageRetirementIntents.SingleAsync()).State);
        var authorization = Assert.Single(await db.StageCleanupAuthorizations.ToListAsync());
        Assert.Equal(seed.Authorization.AuthorizationId, authorization.AuthorizationId);
        Assert.Equal(seed.Authorization.Plan, JsonSerializer.Deserialize<StageRetirementPlan>(authorization.PlanJson));
        // Human status remains historical metadata, not a claim that bytes exist or that an environment is Ready.
        var status = Assert.IsType<StageStatusView>(await new SqlStageStatus(db).GetAsync(seed.Admin, seed.Order.EnvironmentId, Ct));
        Assert.Equal(10, status.StagingBytes); Assert.Equal("StageCompleted", status.JobState);
        Assert.Equal("Requested", status.EnvironmentState); Assert.Equal("Held", status.ReservationState);
        Assert.Equal("ContentVerified", status.ArtifactState); Assert.True(status.Unsettled);
    }
    [SqlServerFact]
    public async Task Six_concurrent_releases_and_replay_commit_one_ledger_and_audit_without_lifecycle_changes()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        int audits; await using (var db = sql.NewContext()) audits = await db.AuditEvents.CountAsync();
        var receipts = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => ReleaseAsync(sql, seed)));
        var first = receipts[0]; Assert.All(receipts, r => Assert.Equal(first, r));
        Assert.Equal(first, await ReleaseAsync(sql, seed));
        Assert.NotEqual(Guid.Empty, first.ReleaseId); Assert.Equal(seed.ResultId, first.ResultId);
        Assert.Equal(seed.Grant.Plan.ReservationId, first.ReservationId); Assert.Equal(10, first.ReleasedBytes); Assert.NotEqual(default, first.ReleasedAt);
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(first, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(new[] { "releaseId", "releasedAt", "releasedBytes", "reservationId", "resultId" }, wire.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        await using (var db = sql.NewContext())
        {
            var row = Assert.Single(await db.StagingAllocationReleases.ToListAsync());
            Assert.Equal(first.ReleaseId, row.ReleaseId); Assert.Equal(first.ResultId, row.ResultId);
            Assert.Equal(first.ReservationId, row.ReservationId); Assert.Equal(first.ReleasedBytes, row.ReleasedBytes); Assert.Equal(first.ReleasedAt, row.ReleasedAt);
            Assert.Equal(seed.Authorization.AuthorizationId, row.AuthorizationId); Assert.Equal(seed.Root, row.RootId);
            Assert.Equal(seed.Pool, row.PoolId); Assert.Equal(seed.Admin.UserId, row.ActorId);
            Assert.Equal(audits + 1, await db.AuditEvents.CountAsync());
            Assert.Single(await db.AuditEvents.Where(a => a.Action == "StagingAllocationReleased").ToListAsync());
        }
        await AssertRetainedAsync(sql, seed);
    }
    [SqlServerFact]
    public async Task Non_admin_missing_receipt_and_wrong_expected_ids_cannot_create_release()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var denied = await Assert.ThrowsAsync<RequestRejectedException>(() => ReleaseAsync(sql, seed, actor: seed.Owner)); Assert.Equal(403, denied.StatusCode);
        foreach (var wrong in new[] { Expected(seed) with { ExpectedAuthorizationId = Guid.NewGuid() }, Expected(seed) with { ExpectedReservationId = Guid.NewGuid() } })
        {
            var error = await Assert.ThrowsAsync<RequestRejectedException>(() => ReleaseAsync(sql, seed, expected: wrong)); Assert.Equal(409, error.StatusCode);
        }
        var missing = await Assert.ThrowsAsync<RequestRejectedException>(() => ReleaseAsync(sql, seed, result: Guid.NewGuid())); Assert.Equal(409, missing.StatusCode);
        await using (var db = sql.NewContext()) Assert.Empty(await db.StagingAllocationReleases.ToListAsync());
        await AssertRetainedAsync(sql, seed);
    }
    [SqlServerFact]
    public async Task Root_and_pool_staging_admission_each_exclude_only_released_logical_allocation()
    {
        // Isolate both SQL sums: limiting only root catches a missing root predicate; limiting only pool catches a missing pool predicate.
        foreach (var rootLimited in new[] { true, false })
        {
            await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
            await using (var db = sql.NewContext())
            {
                await db.ArchiveRoots.Where(r => r.Id == seed.Root).ExecuteUpdateAsync(s => s.SetProperty(r => r.MaxStagingBytes, rootLimited ? 10L : 10000L));
                await db.EnvironmentPools.Where(p => p.Id == seed.Pool).ExecuteUpdateAsync(s => s.SetProperty(p => p.MaxStagingBytes, rootLimited ? 10000L : 10L));
            }
            await RejectOrderAsync(sql, seed, "stage-blocked-before");
            await ReleaseAsync(sql, seed);
            var second = await OrderAsync(sql, seed, "stage-accepted-after"); Assert.NotEqual(seed.Order.EnvironmentId, second.EnvironmentId);
            // The new reservation is still counted; the release does not globally disable staging admission.
            await RejectOrderAsync(sql, seed, "stage-blocked-third");
            await using (var db = sql.NewContext())
            {
                Assert.Equal(2, await db.TestEnvironments.CountAsync()); Assert.Equal(200, await db.EnvironmentReservations.SumAsync(r => r.ReservedBytes));
                Assert.Equal(20, await db.EnvironmentReservations.SumAsync(r => r.StagingBytes)); Assert.Single(await db.StagingAllocationReleases.ToListAsync());
            }
            await AssertRetainedAsync(sql, seed);
        }
    }
    [SqlServerFact]
    public async Task Stage_release_does_not_free_owner_environment_storage_or_pool_database_capacity()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql); await ReleaseAsync(sql, seed);
        await using (var db = sql.NewContext()) await db.EnvironmentQuotas.Where(q => q.SubjectId == seed.Owner.UserId).ExecuteUpdateAsync(s => s.SetProperty(q => q.MaxActiveEnvironments, 1));
        await RejectOrderAsync(sql, seed, "owner-env-still-full");
        await using (var db = sql.NewContext()) await db.EnvironmentQuotas.Where(q => q.SubjectId == seed.Owner.UserId).ExecuteUpdateAsync(s => s.SetProperty(q => q.MaxActiveEnvironments, 5).SetProperty(q => q.MaxStorageBytes, 100L));
        await RejectOrderAsync(sql, seed, "owner-db-storage-still-full");
        await using (var db = sql.NewContext())
        {
            await db.EnvironmentQuotas.Where(q => q.SubjectId == seed.Owner.UserId).ExecuteUpdateAsync(s => s.SetProperty(q => q.MaxStorageBytes, 100000L));
            await db.EnvironmentPools.Where(p => p.Id == seed.Pool).ExecuteUpdateAsync(s => s.SetProperty(p => p.CapacityBytes, 100L));
        }
        await RejectOrderAsync(sql, seed, "pool-db-storage-still-full");
        await using (var db = sql.NewContext()) Assert.Single(await db.TestEnvironments.ToListAsync());
        await AssertRetainedAsync(sql, seed);
    }
    [SqlServerFact]
    public async Task Historical_disabled_bindings_allow_release_but_authorization_without_result_does_not()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql, acceptResult: false);
        var missing = await Assert.ThrowsAsync<RequestRejectedException>(() => ReleaseAsync(sql, seed)); Assert.Equal(409, missing.StatusCode);
        await using (var db = sql.NewContext())
        {
            Assert.Empty(await db.StagingAllocationReleases.ToListAsync());
            await new SqlStageCleanupResults(db, new StageCleanupResultOptions { Enabled = true }).ReportAsync(seed.Credential.Token,
                new(seed.ResultId, seed.Authorization.AuthorizationId, seed.Authorization.Plan.RetirementId, seed.Order.JobId, seed.Authorization.Plan.ArtifactId, seed.Grant.FencingToken), Ct);
            await db.Projects.Where(p => p.Id == seed.Project).ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, false));
            await db.ArchiveRoots.Where(r => r.Id == seed.Root).ExecuteUpdateAsync(s => s.SetProperty(r => r.Enabled, false));
            await db.ProjectPools.Where(p => p.ProjectId == seed.Project).ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, false));
            await db.EnvironmentPools.Where(p => p.Id == seed.Pool).ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, false));
        }
        var first = await ReleaseAsync(sql, seed); Assert.Equal(10, first.ReleasedBytes); Assert.Equal(first, await ReleaseAsync(sql, seed));
        await AssertRetainedAsync(sql, seed);
    }
    [SqlServerFact]
    public async Task Immutable_release_ledger_rejects_mutation_and_deletion()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql); var first = await ReleaseAsync(sql, seed);
        await using (var db = sql.NewContext())
        {
            var row = await db.StagingAllocationReleases.SingleAsync(); db.Entry(row).Property("ReleasedBytes").CurrentValue = 999L;
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await using (var db = sql.NewContext())
        {
            db.StagingAllocationReleases.Remove(await db.StagingAllocationReleases.SingleAsync());
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        Assert.Equal(first, await ReleaseAsync(sql, seed)); await AssertRetainedAsync(sql, seed);
    }
}
// end
