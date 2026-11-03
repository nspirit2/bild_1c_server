using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Contracts;
using BuildSystem.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildSystem.IntegrationTests;

// Disposable metadata SQL only: no restore worker, filesystem cleanup, cluster or target SQL operations.
public sealed class SqlRestoreIntentTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly string Hash = new('A', 64);
    private sealed record Seed(Actor Admin, Actor Owner, Guid Project, Guid Pool, OrderReceipt Order,
        AgentCredential Credential, StageLeaseGrant? Grant, ReportStagedArtifact? Report,
        RestoreIntentOptions Options);

    private static async Task<Seed> PrepareAsync(SqlServerTestDatabase sql, string state = "StageCompleted", string format = "dt")
    {
        await sql.ApplyMigrationsAsync();
        var admin = new Actor(Guid.NewGuid(), true); var owner = new Actor(Guid.NewGuid(), false);
        var project = Guid.NewGuid(); var pool = Guid.NewGuid(); var root = Guid.NewGuid();
        var archive = Guid.NewGuid(); var version = Guid.NewGuid(); var agent = Guid.NewGuid();
        var cluster = Guid.NewGuid(); var server = Guid.NewGuid();
        var options = new RestoreIntentOptions { Enabled = true,
            CanonicalMappings = new() { [pool] = new(cluster, server) } };
        await using var db = sql.NewContext();
        db.Users.AddRange(new BuildSystemUser { Id = admin.UserId, UserName = "admin-" + admin.UserId.ToString("N"),
                NormalizedUserName = "ADMIN-" + admin.UserId.ToString("N") },
            new BuildSystemUser { Id = owner.UserId, UserName = "owner-" + owner.UserId.ToString("N"),
                NormalizedUserName = "OWNER-" + owner.UserId.ToString("N") });
        db.Projects.Add(new Project { Id = project, Name = "restore-intent", IsolationPolicyId = "isolated", PreparationProfileVersion = "v1" });
        db.ProjectMemberships.Add(new ProjectMembership { UserId = owner.UserId, ProjectId = project });
        db.ArchiveRoots.Add(new ArchiveRoot { Id = root, Enabled = true, AgentScopeId = agent,
            IsolationPolicyId = "isolated", MaxArchiveBytes = 10000, MaxStagingBytes = 10000 });
        db.ArchiveRootProjects.Add(new ArchiveRootProject { RootId = root, ProjectId = project });
        db.DiskArchives.Add(new DiskArchive { Id = archive, RootId = root, RelativePath = "restore-intent." + format });
        db.DiskArchiveVersions.Add(new DiskArchiveVersion { Id = version, ArchiveId = archive,
            Sha256 = Hash, SizeBytes = 10, Format = format, State = "Ready", EstimatedDbBytes = 100 });
        db.EnvironmentPools.Add(new EnvironmentPool { Id = pool, Enabled = true,
            ClusterId = cluster, SqlServerId = server, AgentScopeId = agent,
            IsolationPolicyId = "isolated", CapacityBytes = 100000, MaxEnvironmentBytes = 10000,
            MaxStagingBytes = 10000, MaxConcurrentProvisioning = 5, SupportsDt = true, SupportsBak = true,
            IsolationVerified = true, ScheduledJobsSuppressionVerified = true, ResourceLimitVerified = true });
        db.ProjectPools.Add(new ProjectPool { ProjectId = project, PoolId = pool, Enabled = true });
        db.EnvironmentQuotas.Add(new EnvironmentQuota { SubjectId = owner.UserId, MaxActiveEnvironments = 5,
            MaxConcurrentProvisioning = 5, MaxStorageBytes = 100000, MaxTtlDays = 30 });
        await db.SaveChangesAsync();
        var control = new SqlAgentControl(db);
        var ticket = await control.CreateEnrollmentAsync(admin, new(agent, "restore-worker", pool), Ct);
        await control.RegisterAsync(new(agent, ticket.EnrollmentId, ticket.Token, "restore-worker"), Ct);
        var credential = await control.ApproveAsync(admin, agent, Ct);
        var order = await new SqlEnvironmentOrders(db).CreateAsync(owner, "restore-order",
            new(project, "restore", "archive", new(version), 7), Ct);
        if (state == "Pending") return new(admin, owner, project, pool, order, credential, null, null, options);
        var grant = Assert.IsType<StageLeaseGrant>(await new SqlArchiveStageControl(db,
            new ArchiveStageOptions { Enabled = true }).PollAsync(credential.Token, Ct));
        var report = new ReportStagedArtifact(grant.JobId, grant.FencingToken, grant.Plan.ReservationId,
            version, Guid.NewGuid(), Hash, 10, format);
        if (state == "Leased") return new(admin, owner, project, pool, order, credential, grant, report, options);
        await new SqlArtifactReceipts(db).ReportAsync(credential.Token, report, Ct);
        if (state != "ArtifactReported")
            await new SqlStageCompletion(db, new ArchiveStageOptions { Enabled = true })
                .CompleteAsync(credential.Token, new(grant.JobId, grant.FencingToken, report.ArtifactId), Ct);
        return new(admin, owner, project, pool, order, credential, grant, report, options);
    }

    private static RequestRestoreIntent Input(Seed seed) => new(seed.Order.JobId, seed.Report?.ArtifactId ?? Guid.NewGuid());
    private static async Task<RestoreIntentReceipt> RequestAsync(SqlServerTestDatabase sql, Seed seed,
        Actor? actor = null, RequestRestoreIntent? input = null, RestoreIntentOptions? options = null)
    {
        await using var db = sql.NewContext();
        return await new SqlRestoreIntents(db, options ?? seed.Options)
            .RequestAsync(actor ?? seed.Owner, seed.Order.EnvironmentId, input ?? Input(seed), Ct);
    }
    private static async Task<StageRetirementReceipt> RetireAsync(SqlServerTestDatabase sql, Seed seed)
    {
        await using var db = sql.NewContext();
        return await new SqlStageRetirement(db).RequestAsync(seed.Owner, seed.Order.EnvironmentId,
            new(seed.Order.JobId, seed.Report!.ArtifactId), Ct);
    }
    // Include every scalar (including row versions), not only the expected state labels.
    private static async Task<string> LifecycleAsync(SqlServerTestDatabase sql)
    {
        await using var db = sql.NewContext();
        await db.EnvironmentJobs.LoadAsync(); await db.AgentLeases.LoadAsync();
        await db.StagedArtifacts.LoadAsync(); await db.TestEnvironments.LoadAsync();
        await db.EnvironmentReservations.LoadAsync(); await db.EnvironmentQuotas.LoadAsync();
        await db.EnvironmentRequests.LoadAsync(); await db.StagingAllocationReleases.LoadAsync();
        return JsonSerializer.Serialize(db.ChangeTracker.Entries()
            .Select(e => e.Metadata.Name + ":" + JsonSerializer.Serialize(e.Properties.OrderBy(p => p.Metadata.Name)
                .ToDictionary(p => p.Metadata.Name, p => p.CurrentValue)))
            .OrderBy(s => s).ToArray());
    }
    private static async Task AssertNoIntentAsync(SqlServerTestDatabase sql, int audits)
    {
        await using var db = sql.NewContext();
        Assert.Empty(await db.RestoreIntents.ToListAsync());
        Assert.Equal(audits, await db.AuditEvents.CountAsync());
    }
    private static async Task<int> AuditsAsync(SqlServerTestDatabase sql)
    {
        await using var db = sql.NewContext(); return await db.AuditEvents.CountAsync();
    }

    [SqlServerFact]
    public async Task Default_off_rejects_without_metadata_or_audit_changes()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var before = await LifecycleAsync(sql); var audits = await AuditsAsync(sql);
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => RequestAsync(sql, seed, options: new()));
        Assert.Equal(409, error.StatusCode); Assert.Equal("RestoreIntentDisabled", error.Code);
        await AssertNoIntentAsync(sql, audits); Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Exact_replay_preserves_one_immutable_nonexecuting_receipt_and_all_lifecycle_and_quota_scalars()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var before = await LifecycleAsync(sql); var audits = await AuditsAsync(sql);
        var first = await RequestAsync(sql, seed); var replay = await RequestAsync(sql, seed);
        Assert.Equal(first, replay); Assert.False(first.Executable);
        Assert.NotEqual(Guid.Empty, first.RestoreIntentId); Assert.NotEqual(Guid.Empty, first.TargetIncarnation);
        Assert.NotEqual(first.RestoreIntentId, first.TargetIncarnation);
        Assert.Equal(seed.Order.EnvironmentId, first.EnvironmentId); Assert.Equal(seed.Order.JobId, first.StageJobId);
        Assert.Equal(seed.Report!.ArtifactId, first.ArtifactId); Assert.Equal(seed.Grant!.Plan.ReservationId, first.ReservationId);
        Assert.Equal(seed.Options.CanonicalMappings[seed.Pool].CanonicalClusterId, first.CanonicalClusterId);
        Assert.Equal(seed.Options.CanonicalMappings[seed.Pool].CanonicalSqlServerId, first.CanonicalSqlServerId);
        Assert.False(string.IsNullOrWhiteSpace(first.InfobaseName)); Assert.False(string.IsNullOrWhiteSpace(first.SqlDbName));
        Assert.NotEqual(default, first.RequestedAt);
        await using (var db = sql.NewContext())
        {
            var row = Assert.Single(await db.RestoreIntents.ToListAsync());
            Assert.Equal(first.RestoreIntentId, row.RestoreIntentId); Assert.Equal(first.TargetIncarnation, row.TargetIncarnation);
            Assert.Equal(seed.Report.ArchiveVersionId, row.ArchiveVersionId); Assert.Equal(Hash, row.Sha256);
            Assert.Equal(10, row.SizeBytes); Assert.Equal("dt", row.Format);
            Assert.Equal("v1", row.PreparationProfileVersion); Assert.True(row.ScheduledJobsMustRemainBlocked);
            Assert.Equal(64, row.SnapshotSha256.Length); Assert.False(row.Executable);
            Assert.Equal(seed.Owner.UserId, row.RequestedBy); Assert.Equal(first.RequestedAt, row.RequestedAt);
            Assert.Equal(audits + 1, await db.AuditEvents.CountAsync());
            Assert.Single(await db.AuditEvents.Where(a => a.Action == "RestoreIntentRequested").ToListAsync());
        }
        Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Empty_and_wrong_pins_are_rejected_before_and_after_an_exact_request()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var before = await LifecycleAsync(sql); var audits = await AuditsAsync(sql);
        var invalid = new[] { new RequestRestoreIntent(Guid.Empty, seed.Report!.ArtifactId),
            new(seed.Order.JobId, Guid.Empty), new(Guid.NewGuid(), seed.Report.ArtifactId), new(seed.Order.JobId, Guid.NewGuid()) };
        foreach (var input in invalid)
        {
            var error = await Assert.ThrowsAsync<RequestRejectedException>(() => RequestAsync(sql, seed, input: input));
            Assert.Equal(input.ExpectedStageJobId == Guid.Empty || input.ExpectedArtifactId == Guid.Empty ? 400 : 409, error.StatusCode);
        }
        await AssertNoIntentAsync(sql, audits);
        var first = await RequestAsync(sql, seed);
        foreach (var input in invalid.Skip(2))
            Assert.Equal(409, (await Assert.ThrowsAsync<RequestRejectedException>(() => RequestAsync(sql, seed, input: input))).StatusCode);
        Assert.Equal(first, await RequestAsync(sql, seed));
        Assert.Equal(audits + 1, await AuditsAsync(sql)); Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Foreign_owner_is_denied_and_revoked_membership_is_rechecked_on_replay()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var audits = await AuditsAsync(sql);
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => RequestAsync(sql, seed, new Actor(Guid.NewGuid(), false)));
        Assert.Equal(404, error.StatusCode); await AssertNoIntentAsync(sql, audits);
        var first = await RequestAsync(sql, seed, seed.Admin);
        Assert.Equal(first, await RequestAsync(sql, seed));
        await using (var db = sql.NewContext())
            await db.ProjectMemberships.Where(m => m.ProjectId == seed.Project && m.UserId == seed.Owner.UserId).ExecuteDeleteAsync();
        error = await Assert.ThrowsAsync<RequestRejectedException>(() => RequestAsync(sql, seed));
        Assert.Equal(404, error.StatusCode); Assert.Equal(audits + 1, await AuditsAsync(sql));
        Assert.Equal(first, await RequestAsync(sql, seed, seed.Admin));
    }

    [SqlServerFact]
    public async Task Missing_or_empty_canonical_mapping_and_disabled_pool_deny_intent()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var audits = await AuditsAsync(sql);
        foreach (var options in new[] { new RestoreIntentOptions { Enabled = true },
            new RestoreIntentOptions { Enabled = true, CanonicalMappings = new() { [seed.Pool] = new(Guid.Empty, Guid.NewGuid()) } },
            new RestoreIntentOptions { Enabled = true, CanonicalMappings = new() { [seed.Pool] = new(Guid.NewGuid(), Guid.Empty) } } })
            Assert.Equal(409, (await Assert.ThrowsAsync<RequestRejectedException>(() => RequestAsync(sql, seed, options: options))).StatusCode);
        await using (var db = sql.NewContext())
            await db.EnvironmentPools.Where(p => p.Id == seed.Pool).ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, false));
        var before = await LifecycleAsync(sql);
        Assert.Equal(409, (await Assert.ThrowsAsync<RequestRejectedException>(() => RequestAsync(sql, seed))).StatusCode);
        await AssertNoIntentAsync(sql, audits); Assert.Equal(before, await LifecycleAsync(sql));
        await using (var db = sql.NewContext())
            await db.EnvironmentPools.Where(p => p.Id == seed.Pool)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, true).SetProperty(p => p.ScheduledJobsSuppressionVerified, false));
        Assert.Equal(409, (await Assert.ThrowsAsync<RequestRejectedException>(() => RequestAsync(sql, seed))).StatusCode);
        await using (var db = sql.NewContext())
            await db.EnvironmentPools.Where(p => p.Id == seed.Pool)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.ScheduledJobsSuppressionVerified, true).SetProperty(p => p.SupportsDt, false));
        Assert.Equal(409, (await Assert.ThrowsAsync<RequestRejectedException>(() => RequestAsync(sql, seed))).StatusCode);
        await using (var db = sql.NewContext())
        {
            await db.EnvironmentPools.Where(p => p.Id == seed.Pool).ExecuteUpdateAsync(s => s.SetProperty(p => p.SupportsDt, true));
            await new SqlAgentControl(db).RevokeAsync(seed.Admin, seed.Credential.AgentId, Ct);
        }
        audits = await AuditsAsync(sql); before = await LifecycleAsync(sql);
        Assert.Equal(409, (await Assert.ThrowsAsync<RequestRejectedException>(() => RequestAsync(sql, seed))).StatusCode);
        await AssertNoIntentAsync(sql, audits); Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Non_dt_and_unfinished_stages_cannot_request_restore()
    {
        foreach (var (state, format) in new[] { ("StageCompleted", "bak"), ("Pending", "dt"), ("Leased", "dt"), ("ArtifactReported", "dt") })
        {
            await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql, state, format);
            var before = await LifecycleAsync(sql); var audits = await AuditsAsync(sql);
            Assert.Equal(409, (await Assert.ThrowsAsync<RequestRejectedException>(() => RequestAsync(sql, seed))).StatusCode);
            await AssertNoIntentAsync(sql, audits); Assert.Equal(before, await LifecycleAsync(sql));
        }
    }

    [SqlServerFact]
    public async Task Persisted_intent_rejects_mutation_and_deletion_through_SaveChanges()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var first = await RequestAsync(sql, seed); var before = await LifecycleAsync(sql); var audits = await AuditsAsync(sql);
        await using (var db = sql.NewContext())
        {
            var row = await db.RestoreIntents.SingleAsync();
            db.Entry(row).Property("ArtifactId").CurrentValue = Guid.NewGuid();
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await using (var db = sql.NewContext())
        {
            db.RestoreIntents.Remove(await db.RestoreIntents.SingleAsync());
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        Assert.Equal(first, await RequestAsync(sql, seed)); Assert.Equal(audits, await AuditsAsync(sql));
        Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Existing_retirement_denies_restore_without_mutating_retirement_or_allocations()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var retirement = await RetireAsync(sql, seed); var before = await LifecycleAsync(sql); var audits = await AuditsAsync(sql);
        Assert.Equal(409, (await Assert.ThrowsAsync<RequestRejectedException>(() => RequestAsync(sql, seed))).StatusCode);
        await AssertNoIntentAsync(sql, audits); Assert.Equal(retirement, await RetireAsync(sql, seed));
        Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Restore_blocks_retirement_and_defensively_blocks_mixed_historical_cleanup_metadata()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        await RequestAsync(sql, seed);
        Assert.Equal(409, (await Assert.ThrowsAsync<RequestRejectedException>(() => RetireAsync(sql, seed))).StatusCode);
        // Deliberate impossible-through-API history probes defense in depth, not real cleanup execution.
        var retirement = Guid.NewGuid(); var authorization = Guid.NewGuid(); var result = Guid.NewGuid();
        await using (var db = sql.NewContext())
        {
            db.StageRetirementIntents.Add(new StageRetirementIntent { RetirementId = retirement,
                EnvironmentId = seed.Order.EnvironmentId, JobId = seed.Order.JobId, ArtifactId = seed.Report!.ArtifactId,
                ReservationId = seed.Grant!.Plan.ReservationId, AgentId = seed.Credential.AgentId,
                FencingToken = seed.Grant.FencingToken, RequestedBy = seed.Owner.UserId, RequestedAt = DateTimeOffset.UtcNow });
            var plan = new StageRetirementPlan(retirement, seed.Order.EnvironmentId, seed.Order.JobId,
                seed.Report.ArtifactId, seed.Grant.Plan.ReservationId, seed.Report.ArchiveVersionId,
                seed.Grant.FencingToken, Hash, 10, "dt");
            db.StageCleanupAuthorizations.Add(new StageCleanupAuthorizationRecord { AuthorizationId = authorization,
                RetirementId = retirement, ArtifactId = seed.Report.ArtifactId, AgentId = seed.Credential.AgentId,
                StageFencingToken = seed.Grant.FencingToken, PlanJson = JsonSerializer.Serialize(plan), AuthorizedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var before = await LifecycleAsync(sql); var audits = await AuditsAsync(sql);
        await using (var db = sql.NewContext())
        {
            try
            {
                Assert.Null(await new SqlStageRetirementPlans(db, new StageRetirementOptions { Enabled = true })
                    .PollAsync(seed.Credential.Token, Ct));
            }
            catch (RequestRejectedException error) { Assert.Equal(409, error.StatusCode); }
        }
        await using (var db = sql.NewContext())
            Assert.Equal(409, (await Assert.ThrowsAsync<RequestRejectedException>(() =>
                new SqlStageCleanup(db, new StageCleanupOptions { Enabled = true })
                    .ClaimAsync(seed.Credential.Token, new(retirement, seed.Report!.ArtifactId), Ct))).StatusCode);
        await using (var db = sql.NewContext())
        {
            db.StageCleanupResults.Add(new StageCleanupResultRecord { ResultId = result, AuthorizationId = authorization,
                ArtifactId = seed.Report!.ArtifactId, AgentId = seed.Credential.AgentId,
                StageFencingToken = seed.Grant!.FencingToken, ReceivedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        await using (var db = sql.NewContext())
            Assert.Equal(409, (await Assert.ThrowsAsync<RequestRejectedException>(() =>
                new SqlStagingAllocationRelease(db, new StagingAllocationReleaseOptions { Enabled = true })
                    .ReleaseAsync(seed.Admin, result, new(authorization, seed.Grant!.Plan.ReservationId), Ct))).StatusCode);
        Assert.Equal(audits, await AuditsAsync(sql)); Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Concurrent_restore_requests_share_one_receipt_one_incarnation_and_one_audit()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var before = await LifecycleAsync(sql); var audits = await AuditsAsync(sql);
        var receipts = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => RequestAsync(sql, seed)));
        Assert.All(receipts, receipt => Assert.Equal(receipts[0], receipt));
        await using (var db = sql.NewContext()) Assert.Single(await db.RestoreIntents.ToListAsync());
        Assert.Equal(audits + 1, await AuditsAsync(sql)); Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Service_snapshots_canonical_mapping_before_operator_dictionary_is_mutated()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var options = new RestoreIntentOptions { Enabled = true,
            CanonicalMappings = new(seed.Options.CanonicalMappings) };
        var before = await LifecycleAsync(sql); var audits = await AuditsAsync(sql);
        await using var db = sql.NewContext();
        var service = new SqlRestoreIntents(db, options);
        options.CanonicalMappings.Clear();
        var receipt = await service.RequestAsync(seed.Owner, seed.Order.EnvironmentId, Input(seed), Ct);
        Assert.Equal(seed.Options.CanonicalMappings[seed.Pool].CanonicalClusterId, receipt.CanonicalClusterId);
        Assert.Equal(seed.Options.CanonicalMappings[seed.Pool].CanonicalSqlServerId, receipt.CanonicalSqlServerId);
        options.CanonicalMappings[seed.Pool] = new(Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(receipt, await service.RequestAsync(seed.Owner, seed.Order.EnvironmentId, Input(seed), Ct));
        Assert.Equal(receipt, await RequestAsync(sql, seed));
        await using (var inspect = sql.NewContext()) Assert.Single(await inspect.RestoreIntents.ToListAsync());
        Assert.Equal(audits + 1, await AuditsAsync(sql)); Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Sql_unique_constraints_independently_protect_sql_and_cluster_slots_across_pools_and_agents()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var firstSeed = await PrepareAsync(sql); var secondSeed = await PrepareAsync(sql);
        var first = await RequestAsync(sql, firstSeed); var second = await RequestAsync(sql, secondSeed);
        Assert.NotEqual(firstSeed.Pool, secondSeed.Pool);
        Assert.NotEqual(firstSeed.Credential.AgentId, secondSeed.Credential.AgentId);
        Assert.NotEqual(first.CanonicalClusterId, second.CanonicalClusterId);
        Assert.NotEqual(first.CanonicalSqlServerId, second.CanonicalSqlServerId);
        var before = await LifecycleAsync(sql); var audits = await AuditsAsync(sql);
        // Both graphs and rows are valid. Bypass the EF immutability guard only inside this
        // disposable metadata DB, colliding one independently unique target slot at a time.
        await using (var db = sql.NewContext())
        {
            var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [RestoreIntents] SET [CanonicalSqlServerId] = {first.CanonicalSqlServerId}, [SqlDbName] = {first.SqlDbName} WHERE [RestoreIntentId] = {second.RestoreIntentId}", Ct));
            Assert.Contains(error.Number, new[] { 2601, 2627 });
            Assert.Contains("IX_RestoreIntents_CanonicalSqlServerId_SqlDbName", error.Message);
        }
        await using (var db = sql.NewContext())
        {
            var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [RestoreIntents] SET [CanonicalClusterId] = {first.CanonicalClusterId}, [InfobaseName] = {first.InfobaseName} WHERE [RestoreIntentId] = {second.RestoreIntentId}", Ct));
            Assert.Contains(error.Number, new[] { 2601, 2627 });
            Assert.Contains("IX_RestoreIntents_CanonicalClusterId_InfobaseName", error.Message);
        }
        await using (var db = sql.NewContext()) Assert.Equal(2, await db.RestoreIntents.CountAsync());
        Assert.Equal(first, await RequestAsync(sql, firstSeed)); Assert.Equal(second, await RequestAsync(sql, secondSeed));
        Assert.Equal(audits, await AuditsAsync(sql)); Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Sql_check_constraints_keep_intent_nonexecuting_and_scheduled_jobs_blocked_without_Ef_guard()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var receipt = await RequestAsync(sql, seed); var before = await LifecycleAsync(sql); var audits = await AuditsAsync(sql);
        // Direct metadata updates intentionally bypass SaveChanges to prove the migrated SQL constraints.
        await using (var db = sql.NewContext())
        {
            var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [RestoreIntents] SET [Executable] = CAST(1 AS bit) WHERE [RestoreIntentId] = {receipt.RestoreIntentId}", Ct));
            Assert.Equal(547, error.Number); Assert.Contains("CK_RestoreIntents_NonExecutable", error.Message);
        }
        await using (var db = sql.NewContext())
        {
            var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [RestoreIntents] SET [ScheduledJobsMustRemainBlocked] = CAST(0 AS bit) WHERE [RestoreIntentId] = {receipt.RestoreIntentId}", Ct));
            Assert.Equal(547, error.Number); Assert.Contains("CK_RestoreIntents_ScheduledJobsBlocked", error.Message);
        }
        await using (var db = sql.NewContext())
        {
            var row = Assert.Single(await db.RestoreIntents.ToListAsync());
            Assert.False(row.Executable); Assert.True(row.ScheduledJobsMustRemainBlocked);
        }
        Assert.Equal(receipt, await RequestAsync(sql, seed));
        Assert.Equal(audits, await AuditsAsync(sql)); Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Concurrent_retirement_and_restore_have_exactly_one_winner()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var before = await LifecycleAsync(sql); var audits = await AuditsAsync(sql);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Compete(Func<Task> operation)
        {
            await start.Task;
            try { await operation(); return true; }
            catch (RequestRejectedException error) { Assert.Equal(409, error.StatusCode); return false; }
        }
        var restore = Compete(async () => { await RequestAsync(sql, seed); });
        var retire = Compete(async () => { await RetireAsync(sql, seed); });
        start.SetResult(); var winners = await Task.WhenAll(restore, retire);
        Assert.Single(winners, w => w);
        await using (var db = sql.NewContext())
            Assert.Equal(1, await db.RestoreIntents.CountAsync() + await db.StageRetirementIntents.CountAsync());
        Assert.Equal(audits + 1, await AuditsAsync(sql)); Assert.Equal(before, await LifecycleAsync(sql));
    }
}
// end
