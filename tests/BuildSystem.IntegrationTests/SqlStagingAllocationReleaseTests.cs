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
    public Task Conflicting_restore_hold_blocks_first_cleanup_report() => AssertConflictingRestoreHoldAsync(acceptResult: false);

    [SqlServerFact]
    public Task Conflicting_restore_hold_blocks_cleanup_replay_and_staging_release() => AssertConflictingRestoreHoldAsync(acceptResult: true);

    private static async Task AssertConflictingRestoreHoldAsync(bool acceptResult)
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql, acceptResult);
        await using var db = sql.NewContext();
        var plan = seed.Authorization.Plan;
        await SeedConflictingRestoreHoldAsync(db, seed);
        var audits = await db.AuditEvents.CountAsync(Ct);
        var report = new ReportStageCleanup(seed.ResultId, seed.Authorization.AuthorizationId, plan.RetirementId,
            plan.JobId, plan.ArtifactId, plan.FencingToken);
        var rejected = await Assert.ThrowsAsync<RequestRejectedException>(() =>
            new SqlStageCleanupResults(db, new StageCleanupResultOptions { Enabled = true })
                .ReportAsync(seed.Credential.Token, report, Ct));
        Assert.Equal((409, "CleanupResultMismatch"), (rejected.StatusCode, rejected.Code));
        Assert.Equal(1, await db.RestoreIntents.CountAsync(Ct));
        Assert.Equal(1, await db.StageCleanupAuthorizations.CountAsync(Ct));
        if (acceptResult)
        {
            var release = await Assert.ThrowsAsync<RequestRejectedException>(() => ReleaseAsync(sql, seed));
            Assert.Equal((409, "RestoreIntentHold"), (release.StatusCode, release.Code));
            var prior = await db.StageCleanupResults.AsNoTracking().SingleAsync(Ct);
            Assert.Equal((seed.ResultId, seed.Authorization.AuthorizationId, plan.ArtifactId),
                (prior.ResultId, prior.AuthorizationId, prior.ArtifactId));
        }
        else Assert.False(await db.StageCleanupResults.AnyAsync(Ct));
        Assert.False(await db.StagingAllocationReleases.AnyAsync(Ct));
        Assert.Equal(audits, await db.AuditEvents.CountAsync(Ct));
    }

    private static async Task SeedConflictingRestoreHoldAsync(BuildSystemDbContext db, Seed seed)
    {
        var plan = seed.Authorization.Plan;
        var pool = await db.EnvironmentPools.SingleAsync(p => p.Id == seed.Pool, Ct);
        var targetName = "bs_test_" + plan.ReservationId.ToString("N");
        // The public commands cannot create both records; seed the contradiction in disposable SQL only.
        db.RestoreIntents.Add(new RestoreIntentRecord
        {
            RestoreIntentId = Guid.NewGuid(), EnvironmentId = plan.EnvironmentId, RequestId = seed.Order.RequestId,
            StageJobId = plan.JobId, ReservationId = plan.ReservationId, ArtifactId = plan.ArtifactId,
            PoolId = seed.Pool, RootId = seed.Root, ArchiveVersionId = seed.Version,
            Sha256 = plan.Sha256, SizeBytes = plan.SizeBytes, Format = plan.Format,
            CanonicalClusterId = pool.ClusterId, CanonicalSqlServerId = pool.SqlServerId,
            InfobaseName = targetName, SqlDbName = targetName, TargetIncarnation = Guid.NewGuid(),
            AgentId = seed.Credential.AgentId, FencingToken = plan.FencingToken,
            RequestedBy = seed.Owner.UserId, RequestedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(Ct);
    }

    [SqlServerFact]
    public async Task Conflicting_restore_hold_keeps_staging_reserved_on_root_and_pool()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        var receipt = await ReleaseAsync(sql, seed);
        await using var db = sql.NewContext();
        await SeedConflictingRestoreHoldAsync(db, seed);
        var audits = await db.AuditEvents.CountAsync(Ct);
        var root = db.ArchiveRoots.Where(r => r.Id == seed.Root);
        var pool = db.EnvironmentPools.Where(p => p.Id == seed.Pool);

        Assert.Equal(1, await root.ExecuteUpdateAsync(s => s.SetProperty(r => r.MaxStagingBytes, 10L), Ct));
        await RejectOrderAsync(sql, seed, "restore-conflict-root");
        Assert.Equal(1, await root.ExecuteUpdateAsync(s => s.SetProperty(r => r.MaxStagingBytes, 10000L), Ct));
        Assert.Equal(1, await pool.ExecuteUpdateAsync(s => s.SetProperty(p => p.MaxStagingBytes, 10L), Ct));
        await RejectOrderAsync(sql, seed, "restore-conflict-pool");

        Assert.Equal(1, await db.EnvironmentReservations.CountAsync(Ct));
        Assert.Equal(1, await db.RestoreIntents.CountAsync(Ct));
        Assert.Equal(receipt.ReleaseId, await db.StagingAllocationReleases.Select(r => r.ReleaseId).SingleAsync(Ct));
        Assert.Equal(audits, await db.AuditEvents.CountAsync(Ct));
    }

    [SqlServerFact]
    public Task Conflicting_cancellation_blocks_first_cleanup_report() => AssertConflictingCancellationAsync(acceptResult: false);

    [SqlServerFact]
    public Task Conflicting_cancellation_blocks_cleanup_replay_and_staging_release() => AssertConflictingCancellationAsync(acceptResult: true);

    private static async Task AssertConflictingCancellationAsync(bool acceptResult)
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql, acceptResult);
        await using var db = sql.NewContext();
        var plan = seed.Authorization.Plan;
        // Deliberately contradictory history in this disposable fixture; the cancellation writer cannot produce it.
        db.PendingArchiveCancellations.Add(new PendingArchiveCancellationRecord
        {
            EnvironmentId = seed.Order.EnvironmentId, RequestId = seed.Order.RequestId, JobId = seed.Order.JobId,
            ReservationId = plan.ReservationId, RootId = seed.Root, PoolId = seed.Pool,
            ActorId = seed.Owner.UserId, CancelledAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(Ct);
        var audits = await db.AuditEvents.CountAsync(Ct);
        var report = new ReportStageCleanup(seed.ResultId, seed.Authorization.AuthorizationId, plan.RetirementId,
            plan.JobId, plan.ArtifactId, plan.FencingToken);
        var reportError = await Assert.ThrowsAsync<RequestRejectedException>(() =>
            new SqlStageCleanupResults(db, new StageCleanupResultOptions { Enabled = true })
                .ReportAsync(seed.Credential.Token, report, Ct));
        Assert.Equal((409, "CleanupResultMismatch"), (reportError.StatusCode, reportError.Code));
        if (acceptResult)
        {
            var releaseError = await Assert.ThrowsAsync<RequestRejectedException>(() => ReleaseAsync(sql, seed));
            Assert.Equal((409, "StagingReleaseMismatch"), (releaseError.StatusCode, releaseError.Code));
        }
        Assert.Equal(acceptResult ? 1 : 0, await db.StageCleanupResults.CountAsync(Ct));
        Assert.False(await db.StagingAllocationReleases.AnyAsync(Ct));
        Assert.Equal(audits, await db.AuditEvents.CountAsync(Ct));
        Assert.Equal("Held", await db.EnvironmentReservations.Where(x => x.Id == plan.ReservationId).Select(x => x.State).SingleAsync(Ct));
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
    public async Task Release_rejects_existing_ledger_matching_only_cleanup_authorization()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        var first = await ReleaseAsync(sql, seed);
        var other = await OrderAsync(sql, seed, "other-release-authorization");
        await using var db = sql.NewContext();
        var grant = Assert.IsType<StageLeaseGrant>(await new SqlArchiveStageControl(db, new ArchiveStageOptions { Enabled = true })
            .PollAsync(seed.Credential.Token, Ct));
        Assert.Equal(other.JobId, grant.JobId);
        var artifact = ArtifactId(grant.Plan.ReservationId, seed.Version);
        await new SqlArtifactReceipts(db).ReportAsync(seed.Credential.Token,
            new(grant.JobId, grant.FencingToken, grant.Plan.ReservationId, seed.Version, artifact, Hash, 10, "dt"), Ct);
        await new SqlStageCompletion(db, new ArchiveStageOptions { Enabled = true })
            .CompleteAsync(seed.Credential.Token, new(grant.JobId, grant.FencingToken, artifact), Ct);
        var retirement = await new SqlStageRetirement(db).RequestAsync(seed.Owner, other.EnvironmentId, new(other.JobId, artifact), Ct);
        var authorization = await new SqlStageCleanup(db, new StageCleanupOptions { Enabled = true })
            .ClaimAsync(seed.Credential.Token, new(retirement.RetirementId, artifact), Ct);
        var resultId = Guid.NewGuid();
        await new SqlStageCleanupResults(db, new StageCleanupResultOptions { Enabled = true }).ReportAsync(seed.Credential.Token,
            new(resultId, authorization.AuthorizationId, retirement.RetirementId, other.JobId, artifact, grant.FencingToken), Ct);

        // Keep the foreign key valid while making the existing ledger conflict with the second order.
        Assert.Equal(1, await db.StagingAllocationReleases.Where(r => r.ReleaseId == first.ReleaseId)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.AuthorizationId, authorization.AuthorizationId), Ct));
        var audits = await db.AuditEvents.CountAsync(Ct);
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => ReleaseAsync(sql, seed, result: resultId,
            expected: new(authorization.AuthorizationId, grant.Plan.ReservationId)));
        Assert.Equal((409, "StagingReleaseMismatch"), (error.StatusCode, error.Code));
        var ledger = await db.StagingAllocationReleases.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((first.ReleaseId, first.ResultId, first.ReservationId, authorization.AuthorizationId),
            (ledger.ReleaseId, ledger.ResultId, ledger.ReservationId, ledger.AuthorizationId));
        Assert.Equal(audits, await db.AuditEvents.CountAsync(Ct));
    }

    [SqlServerFact]
    public async Task Conflicting_cancellation_rejects_exact_release_replay_and_preserves_existing_ledger()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        var release = await ReleaseAsync(sql, seed);
        Assert.Equal(release, await ReleaseAsync(sql, seed));
        await using var db = sql.NewContext();
        db.PendingArchiveCancellations.Add(new PendingArchiveCancellationRecord
        {
            EnvironmentId = seed.Order.EnvironmentId, RequestId = seed.Order.RequestId, JobId = seed.Order.JobId,
            ReservationId = release.ReservationId, RootId = seed.Root, PoolId = seed.Pool,
            ActorId = seed.Owner.UserId, CancelledAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(Ct);
        var audits = await db.AuditEvents.CountAsync(Ct);
        var rejected = await Assert.ThrowsAsync<RequestRejectedException>(() => ReleaseAsync(sql, seed));
        Assert.Equal((409, "StagingReleaseMismatch"), (rejected.StatusCode, rejected.Code));
        var ledger = await db.StagingAllocationReleases.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((release.ReleaseId, release.ResultId, release.ReservationId, release.ReleasedBytes, release.ReleasedAt),
            (ledger.ReleaseId, ledger.ResultId, ledger.ReservationId, ledger.ReleasedBytes, ledger.ReleasedAt));
        Assert.Equal(audits, await db.AuditEvents.CountAsync(Ct));
    }

    [SqlServerFact]
    public async Task Conflicting_cancellation_after_cleanup_release_blocks_admission_on_shared_root()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        await ReleaseAsync(sql, seed);
        await using var db = sql.NewContext();
        var otherOwner = new Actor(Guid.NewGuid(), false);
        var otherPool = Guid.NewGuid(); var otherProject = Guid.NewGuid();
        db.Users.Add(new BuildSystemUser { Id = otherOwner.UserId, UserName = "other", NormalizedUserName = "OTHER" });
        db.Projects.Add(new Project { Id = otherProject, Name = "shared-root", IsolationPolicyId = "isolated", PreparationProfileVersion = "v1" });
        db.ProjectMemberships.Add(new ProjectMembership { UserId = otherOwner.UserId, ProjectId = otherProject });
        db.ArchiveRootProjects.Add(new ArchiveRootProject { RootId = seed.Root, ProjectId = otherProject });
        db.EnvironmentPools.Add(Pool(otherPool, seed.Credential.AgentId));
        db.ProjectPools.Add(new ProjectPool { ProjectId = otherProject, PoolId = otherPool, Enabled = true });
        db.EnvironmentQuotas.Add(new EnvironmentQuota { SubjectId = otherOwner.UserId, MaxActiveEnvironments = 5,
            MaxConcurrentProvisioning = 5, MaxStorageBytes = 100000, MaxTtlDays = 30 });
        await db.SaveChangesAsync(Ct);
        var orders = new SqlEnvironmentOrders(db);
        var request = new CreateEnvironmentRequest(otherProject, "shared root", "archive", new(seed.Version), 7);
        var normal = await orders.CreateAsync(otherOwner, "before-conflict", request, Ct);
        Assert.Equal(otherPool, (await db.TestEnvironments.SingleAsync(e => e.Id == normal.EnvironmentId, Ct)).PoolId);

        var plan = seed.Authorization.Plan;
        db.PendingArchiveCancellations.Add(new PendingArchiveCancellationRecord
        {
            EnvironmentId = plan.EnvironmentId, RequestId = seed.Order.RequestId, JobId = plan.JobId,
            ReservationId = plan.ReservationId, RootId = seed.Root, PoolId = seed.Pool,
            ActorId = seed.Owner.UserId, CancelledAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(Ct);
        var rejected = await Assert.ThrowsAsync<RequestRejectedException>(() => orders.CreateAsync(otherOwner, "after-conflict", request, Ct));
        Assert.Equal((409, "QuotaExceeded"), (rejected.StatusCode, rejected.Code));
        Assert.Equal(2, await db.EnvironmentReservations.CountAsync(Ct));
        Assert.Equal(1, await db.StagingAllocationReleases.CountAsync(Ct));
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
    public async Task Conflicting_ledger_bytes_or_report_token_do_not_free_staging_quota()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        var release = await ReleaseAsync(sql, seed);
        await using var db = sql.NewContext();
        var ledger = db.StagingAllocationReleases.Where(r => r.ReleaseId == release.ReleaseId);
        Assert.Equal(1, await ledger.ExecuteUpdateAsync(s => s.SetProperty(r => r.ReleasedBytes, 9L), Ct));
        var root = db.ArchiveRoots.Where(r => r.Id == seed.Root);
        var pool = db.EnvironmentPools.Where(p => p.Id == seed.Pool);
        var audits = await db.AuditEvents.CountAsync(Ct);

        Assert.Equal(1, await root.ExecuteUpdateAsync(s => s.SetProperty(r => r.MaxStagingBytes, 10L), Ct));
        await RejectOrderAsync(sql, seed, "inconsistent-release-root");
        Assert.Equal(1, await root.ExecuteUpdateAsync(s => s.SetProperty(r => r.MaxStagingBytes, 10000L), Ct));
        Assert.Equal(1, await pool.ExecuteUpdateAsync(s => s.SetProperty(p => p.MaxStagingBytes, 10L), Ct));
        await RejectOrderAsync(sql, seed, "inconsistent-release-pool");
        Assert.Equal(1, await db.EnvironmentReservations.CountAsync(Ct));
        Assert.Equal(audits, await db.AuditEvents.CountAsync(Ct));
        Assert.Equal(9L, await ledger.Select(r => r.ReleasedBytes).SingleAsync(Ct));

        Assert.Equal(1, await ledger.ExecuteUpdateAsync(s => s.SetProperty(r => r.ReleasedBytes, release.ReleasedBytes), Ct));
        var result = db.StageCleanupResults.Where(r => r.ResultId == seed.ResultId);
        var token = seed.Grant.FencingToken;
        Assert.Equal(1, await result.ExecuteUpdateAsync(s => s.SetProperty(r => r.StageFencingToken, token + 1), Ct));
        await RejectOrderAsync(sql, seed, "inconsistent-release-token");
        Assert.Equal(1, await db.EnvironmentReservations.CountAsync(Ct));
        Assert.Equal(audits, await db.AuditEvents.CountAsync(Ct));
        Assert.Equal(1, await result.ExecuteUpdateAsync(s => s.SetProperty(r => r.StageFencingToken, token), Ct));
        var lease = db.AgentLeases.Where(l => l.JobId == seed.Order.JobId);
        foreach (var (key, state) in new[] { ("active", "Active"), ("case", "completed"), ("padding", "Completed ") })
        {
            Assert.Equal(1, await lease.ExecuteUpdateAsync(s => s.SetProperty(l => l.State, state), Ct));
            await RejectOrderAsync(sql, seed, "inconsistent-lease-state-" + key);
        }
        Assert.Equal(1, await lease.ExecuteUpdateAsync(s => s.SetProperty(l => l.State, "Completed")
            .SetProperty(l => l.FencingToken, token + 1), Ct));
        await RejectOrderAsync(sql, seed, "inconsistent-lease-token");
        Assert.Equal(1, await db.EnvironmentReservations.CountAsync(Ct));
        Assert.Equal(audits, await db.AuditEvents.CountAsync(Ct));
        Assert.Equal(1, await lease.ExecuteUpdateAsync(s => s.SetProperty(l => l.FencingToken, token), Ct));
        var job = db.EnvironmentJobs.Where(j => j.Id == seed.Order.JobId);
        Assert.Equal(1, await job.ExecuteUpdateAsync(s => s.SetProperty(j => j.State, "stagecompleted"), Ct));
        await RejectOrderAsync(sql, seed, "inconsistent-release-job-state");
        Assert.Equal(1, await job.ExecuteUpdateAsync(s => s.SetProperty(j => j.State, "StageCompleted")
            .SetProperty(j => j.Kind, "ObserveArchiveStage"), Ct));
        await RejectOrderAsync(sql, seed, "inconsistent-release-job-kind");
        Assert.Equal(1, await db.EnvironmentReservations.CountAsync(Ct));
        Assert.Equal(audits, await db.AuditEvents.CountAsync(Ct));
        Assert.Equal(1, await job.ExecuteUpdateAsync(s => s.SetProperty(j => j.Kind, "ProvisionArchive"), Ct));
        var environment = db.TestEnvironments.Where(e => e.Id == seed.Order.EnvironmentId);
        Assert.Equal(1, await environment.ExecuteUpdateAsync(s => s.SetProperty(e => e.State, "Ready"), Ct));
        await RejectOrderAsync(sql, seed, "inconsistent-release-environment-state");
        Assert.Equal(1, await environment.ExecuteUpdateAsync(s => s.SetProperty(e => e.State, "Requested")
            .SetProperty(e => e.HasUnsettledProvisioningOperation, false), Ct));
        await RejectOrderAsync(sql, seed, "inconsistent-release-settled");
        Assert.Equal(1, await environment.ExecuteUpdateAsync(s => s.SetProperty(e => e.HasUnsettledProvisioningOperation, true), Ct));
        var request = db.EnvironmentRequests.Where(q => q.Id == seed.Order.RequestId);
        Assert.Equal(1, await request.ExecuteUpdateAsync(s => s.SetProperty(q => q.State, "Pending "), Ct));
        await RejectOrderAsync(sql, seed, "inconsistent-release-request-state");
        Assert.Equal(1, await request.ExecuteUpdateAsync(s => s.SetProperty(q => q.State, "Pending"), Ct));
        var reservation = db.EnvironmentReservations.Where(r => r.Id == release.ReservationId);
        Assert.Equal(1, await reservation.ExecuteUpdateAsync(s => s.SetProperty(r => r.State, "held"), Ct));
        await RejectOrderAsync(sql, seed, "inconsistent-release-reservation-state");
        Assert.Equal(1, await reservation.ExecuteUpdateAsync(s => s.SetProperty(r => r.State, "Held"), Ct));
        Assert.Equal(1, await db.EnvironmentReservations.CountAsync(Ct));
        Assert.Equal(audits, await db.AuditEvents.CountAsync(Ct));
        var intent = db.StageRetirementIntents.Where(i => i.RetirementId == seed.Authorization.Plan.RetirementId);
        Assert.Equal(1, await intent.ExecuteUpdateAsync(s => s.SetProperty(i => i.State, "Requested "), Ct));
        await RejectOrderAsync(sql, seed, "inconsistent-release-intent-state");
        Assert.Equal(1, await intent.ExecuteUpdateAsync(s => s.SetProperty(i => i.State, "Requested"), Ct));
        var artifact = db.StagedArtifacts.Where(a => a.ArtifactId == seed.Authorization.Plan.ArtifactId);
        Assert.Equal(1, await artifact.ExecuteUpdateAsync(s => s.SetProperty(a => a.State, "contentverified"), Ct));
        await RejectOrderAsync(sql, seed, "inconsistent-release-artifact-state");
        Assert.Equal(1, await artifact.ExecuteUpdateAsync(s => s.SetProperty(a => a.State, "ContentVerified")
            .SetProperty(a => a.Sha256, new string('B', 64)), Ct));
        await RejectOrderAsync(sql, seed, "inconsistent-release-artifact-hash");
        Assert.Equal(1, await artifact.ExecuteUpdateAsync(s => s.SetProperty(a => a.Sha256, Hash.ToLowerInvariant()), Ct));
        Assert.Equal(1, await db.EnvironmentReservations.CountAsync(Ct));
        Assert.Equal(audits, await db.AuditEvents.CountAsync(Ct));
        var accepted = await OrderAsync(sql, seed, "coherent-release-pool");
        Assert.NotEqual(seed.Order.EnvironmentId, accepted.EnvironmentId);
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
