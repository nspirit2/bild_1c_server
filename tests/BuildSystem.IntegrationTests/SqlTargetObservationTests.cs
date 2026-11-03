using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Contracts;
using BuildSystem.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildSystem.IntegrationTests;

// Opt-in disposable metadata SQL only. No /RestoreIB, live RAC/SQL targets, or target commands.
public sealed class SqlTargetObservationTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly string Hash = new('A', 64);
    private sealed record Seed(Actor Admin, Actor Owner, Guid Project, Guid Pool, Guid Root,
        OrderReceipt Order, AgentCredential Credential, StageLeaseGrant Grant,
        ReportStagedArtifact Report, RestoreIntentReceipt Restore, TargetObservationOptions Options);

    // Intentionally local: SqlRestoreIntentTests preparation helpers are private.
    private static async Task<Seed> PrepareAsync(SqlServerTestDatabase sql)
    {
        await sql.ApplyMigrationsAsync();
        var admin = new Actor(Guid.NewGuid(), true); var owner = new Actor(Guid.NewGuid(), false);
        var project = Guid.NewGuid(); var pool = Guid.NewGuid(); var root = Guid.NewGuid();
        var archive = Guid.NewGuid(); var version = Guid.NewGuid(); var agent = Guid.NewGuid();
        var cluster = Guid.NewGuid(); var server = Guid.NewGuid();
        await using var db = sql.NewContext();
        db.Users.AddRange(new BuildSystemUser { Id = admin.UserId, UserName = "admin-" + admin.UserId.ToString("N"),
                NormalizedUserName = "ADMIN-" + admin.UserId.ToString("N") },
            new BuildSystemUser { Id = owner.UserId, UserName = "owner-" + owner.UserId.ToString("N"),
                NormalizedUserName = "OWNER-" + owner.UserId.ToString("N") });
        db.Projects.Add(new Project { Id = project, Name = "target-observation", IsolationPolicyId = "isolated", PreparationProfileVersion = "v1" });
        db.ProjectMemberships.Add(new ProjectMembership { UserId = owner.UserId, ProjectId = project });
        db.ArchiveRoots.Add(new ArchiveRoot { Id = root, Enabled = true, AgentScopeId = agent,
            IsolationPolicyId = "isolated", MaxArchiveBytes = 10000, MaxStagingBytes = 10000 });
        db.ArchiveRootProjects.Add(new ArchiveRootProject { RootId = root, ProjectId = project });
        db.DiskArchives.Add(new DiskArchive { Id = archive, RootId = root, RelativePath = "observation.dt" });
        db.DiskArchiveVersions.Add(new DiskArchiveVersion { Id = version, ArchiveId = archive,
            Sha256 = Hash, SizeBytes = 10, Format = "dt", State = "Ready", EstimatedDbBytes = 100 });
        db.EnvironmentPools.Add(new EnvironmentPool { Id = pool, Enabled = true,
            ClusterId = cluster, SqlServerId = server, AgentScopeId = agent,
            IsolationPolicyId = "isolated", CapacityBytes = 100000, MaxEnvironmentBytes = 10000,
            MaxStagingBytes = 10000, MaxConcurrentProvisioning = 5, SupportsDt = true,
            IsolationVerified = true, ScheduledJobsSuppressionVerified = true, ResourceLimitVerified = true });
        db.ProjectPools.Add(new ProjectPool { ProjectId = project, PoolId = pool, Enabled = true });
        db.EnvironmentQuotas.Add(new EnvironmentQuota { SubjectId = owner.UserId, MaxActiveEnvironments = 5,
            MaxConcurrentProvisioning = 5, MaxStorageBytes = 100000, MaxTtlDays = 30 });
        await db.SaveChangesAsync();
        var control = new SqlAgentControl(db);
        var ticket = await control.CreateEnrollmentAsync(admin, new(agent, "observation-worker", pool), Ct);
        await control.RegisterAsync(new(agent, ticket.EnrollmentId, ticket.Token, "observation-worker"), Ct);
        var credential = await control.ApproveAsync(admin, agent, Ct);
        var order = await new SqlEnvironmentOrders(db).CreateAsync(owner, "observation-order",
            new(project, "observation", "archive", new(version), 7), Ct);
        var grant = Assert.IsType<StageLeaseGrant>(await new SqlArchiveStageControl(db,
            new ArchiveStageOptions { Enabled = true }).PollAsync(credential.Token, Ct));
        var report = new ReportStagedArtifact(grant.JobId, grant.FencingToken, grant.Plan.ReservationId,
            version, Guid.NewGuid(), Hash, 10, "dt");
        await new SqlArtifactReceipts(db).ReportAsync(credential.Token, report, Ct);
        await new SqlStageCompletion(db, new ArchiveStageOptions { Enabled = true })
            .CompleteAsync(credential.Token, new(grant.JobId, grant.FencingToken, report.ArtifactId), Ct);
        var restore = await new SqlRestoreIntents(db, new RestoreIntentOptions { Enabled = true,
            CanonicalMappings = new() { [pool] = new(cluster, server) } })
            .RequestAsync(owner, order.EnvironmentId, new(order.JobId, report.ArtifactId), Ct);
        return new(admin, owner, project, pool, root, order, credential, grant, report, restore,
            new TargetObservationOptions { Enabled = true,
                EndpointMappings = new() { [pool] = new(Guid.NewGuid(), Guid.NewGuid()) } });
    }

    private static async Task<TargetObservationAssignment> RequestAsync(SqlServerTestDatabase sql, Seed seed,
        Actor? actor = null, Guid? expected = null, TargetObservationOptions? options = null, Guid? environment = null)
    {
        await using var db = sql.NewContext();
        return await new SqlTargetObservations(db, options ?? seed.Options).RequestAsync(actor ?? seed.Owner,
            environment ?? seed.Order.EnvironmentId, expected ?? seed.Restore.RestoreIntentId, Ct);
    }

    // Every scalar and row version, including the pre-existing immutable restore hold.
    private static async Task<string> LifecycleAsync(SqlServerTestDatabase sql)
    {
        await using var db = sql.NewContext();
        await db.EnvironmentJobs.LoadAsync(); await db.AgentLeases.LoadAsync();
        await db.StagedArtifacts.LoadAsync(); await db.TestEnvironments.LoadAsync();
        await db.EnvironmentReservations.LoadAsync(); await db.EnvironmentQuotas.LoadAsync();
        await db.EnvironmentRequests.LoadAsync(); await db.StagingAllocationReleases.LoadAsync();
        await db.RestoreIntents.LoadAsync();
        return JsonSerializer.Serialize(db.ChangeTracker.Entries()
            .Select(e => e.Metadata.Name + ":" + JsonSerializer.Serialize(e.Properties.OrderBy(p => p.Metadata.Name)
                .ToDictionary(p => p.Metadata.Name, p => p.CurrentValue)))
            .OrderBy(s => s).ToArray());
    }
    private static async Task<int> AuditsAsync(SqlServerTestDatabase sql)
    {
        await using var db = sql.NewContext(); return await db.AuditEvents.CountAsync();
    }
    private static async Task AssertRejectedAsync(Func<Task<TargetObservationAssignment>> request, int status = 409)
        => Assert.Equal(status, (await Assert.ThrowsAsync<RequestRejectedException>(request)).StatusCode);
    private static async Task AssertNoAssignmentAsync(SqlServerTestDatabase sql, int audits)
    {
        await using var db = sql.NewContext();
        Assert.Empty(await db.TargetObservations.ToListAsync());
        Assert.Equal(audits, await db.AuditEvents.CountAsync());
    }

    [SqlServerFact]
    public async Task Exact_replay_is_one_immutable_observation_only_assignment_without_lifecycle_changes()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var before = await LifecycleAsync(sql); var audits = await AuditsAsync(sql);
        var first = await RequestAsync(sql, seed); var replay = await RequestAsync(sql, seed);
        Assert.Equal(first, replay); Assert.False(first.Executable); Assert.True(first.ScheduledJobsMustRemainBlocked);
        Assert.NotEqual(Guid.Empty, first.ObservationRequestId); Assert.NotEqual(Guid.Empty, first.OperationId);
        Assert.NotEqual(first.ObservationRequestId, first.OperationId);
        Assert.Equal(seed.Restore.RestoreIntentId, first.RestoreIntentId);
        Assert.Equal(seed.Credential.AgentId, first.AgentId); Assert.Equal(seed.Order.EnvironmentId, first.EnvironmentId);
        Assert.Equal(seed.Order.JobId, first.StageJobId); Assert.Equal(seed.Grant.FencingToken, first.FencingToken);
        Assert.Equal(seed.Grant.Plan.ReservationId, first.ReservationId);
        Assert.Equal(seed.Restore.TargetIncarnation, first.TargetIncarnationId);
        Assert.Equal(seed.Restore.CanonicalClusterId, first.CanonicalClusterId);
        Assert.Equal(seed.Restore.CanonicalSqlServerId, first.CanonicalSqlServerId);
        Assert.Equal(seed.Options.EndpointMappings[seed.Pool].RacEndpointMappingId, first.RacEndpointMappingId);
        Assert.Equal(seed.Options.EndpointMappings[seed.Pool].SqlEndpointMappingId, first.SqlEndpointMappingId);
        Assert.Equal(seed.Restore.InfobaseName, first.InfobaseName); Assert.Equal(seed.Restore.SqlDbName, first.SqlDatabaseName);
        await using (var db = sql.NewContext())
        {
            Assert.Single(await db.TargetObservations.ToListAsync());
            Assert.Single(await db.AuditEvents.Where(a => a.Action == "TargetObservationRequested").ToListAsync());
        }
        Assert.Equal(audits + 1, await AuditsAsync(sql)); Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Current_owner_membership_is_required_on_replay_but_admin_can_assign()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var audits = await AuditsAsync(sql);
        await AssertRejectedAsync(() => RequestAsync(sql, seed, new Actor(Guid.NewGuid(), false)), 404);
        await AssertNoAssignmentAsync(sql, audits);
        var first = await RequestAsync(sql, seed, seed.Admin);
        Assert.Equal(first, await RequestAsync(sql, seed));
        await using (var db = sql.NewContext())
            await db.ProjectMemberships.Where(m => m.ProjectId == seed.Project && m.UserId == seed.Owner.UserId).ExecuteDeleteAsync();
        await AssertRejectedAsync(() => RequestAsync(sql, seed), 404);
        Assert.Equal(first, await RequestAsync(sql, seed, seed.Admin));
        Assert.Equal(audits + 1, await AuditsAsync(sql));
    }

    [SqlServerFact]
    public async Task Default_off_invalid_pins_and_missing_or_malformed_mappings_fail_without_writes()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var before = await LifecycleAsync(sql); var audits = await AuditsAsync(sql);
        await AssertRejectedAsync(() => RequestAsync(sql, seed, options: new()));
        await AssertRejectedAsync(() => RequestAsync(sql, seed, expected: Guid.Empty), 400);
        await AssertRejectedAsync(() => RequestAsync(sql, seed, environment: Guid.Empty), 400);
        await AssertRejectedAsync(() => RequestAsync(sql, seed, expected: Guid.NewGuid()));
        foreach (var options in new[] { new TargetObservationOptions { Enabled = true },
            new TargetObservationOptions { Enabled = true, EndpointMappings = new() { [seed.Pool] = new(Guid.Empty, Guid.NewGuid()) } },
            new TargetObservationOptions { Enabled = true, EndpointMappings = new() { [seed.Pool] = new(Guid.NewGuid(), Guid.Empty) } } })
            await AssertRejectedAsync(() => RequestAsync(sql, seed, options: options));
        await AssertNoAssignmentAsync(sql, audits); Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Endpoint_configuration_is_snapshotted_and_drift_cannot_rebind_an_existing_assignment()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var options = new TargetObservationOptions { Enabled = true, EndpointMappings = new(seed.Options.EndpointMappings) };
        await using var db = sql.NewContext(); var service = new SqlTargetObservations(db, options);
        options.EndpointMappings.Clear();
        var first = await service.RequestAsync(seed.Owner, seed.Order.EnvironmentId, seed.Restore.RestoreIntentId, Ct);
        Assert.Equal(first, await RequestAsync(sql, seed)); var audits = await AuditsAsync(sql);
        options.EndpointMappings[seed.Pool] = new(Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(first, await service.RequestAsync(seed.Owner, seed.Order.EnvironmentId, seed.Restore.RestoreIntentId, Ct));
        foreach (var mapping in new[] { new TargetObservationEndpointMapping(Guid.NewGuid(), first.SqlEndpointMappingId),
            new TargetObservationEndpointMapping(first.RacEndpointMappingId, Guid.NewGuid()) })
            await AssertRejectedAsync(() => RequestAsync(sql, seed, options: new TargetObservationOptions
                { Enabled = true, EndpointMappings = new() { [seed.Pool] = mapping } }));
        await AssertRejectedAsync(() => RequestAsync(sql, seed, expected: Guid.NewGuid()));
        Assert.Equal(audits, await AuditsAsync(sql));
    }

    [SqlServerFact]
    public async Task Revoked_agent_disabled_root_pool_and_changed_current_profile_fail_closed_even_on_replay()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var first = await RequestAsync(sql, seed); var audits = await AuditsAsync(sql);
        await using (var db = sql.NewContext())
            await db.ArchiveRoots.Where(r => r.Id == seed.Root).ExecuteUpdateAsync(s => s.SetProperty(r => r.Enabled, false));
        await AssertRejectedAsync(() => RequestAsync(sql, seed));
        await using (var db = sql.NewContext())
        {
            await db.ArchiveRoots.Where(r => r.Id == seed.Root).ExecuteUpdateAsync(s => s.SetProperty(r => r.Enabled, true));
            await db.EnvironmentPools.Where(p => p.Id == seed.Pool).ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, false));
        }
        await AssertRejectedAsync(() => RequestAsync(sql, seed));
        await using (var db = sql.NewContext())
        {
            await db.EnvironmentPools.Where(p => p.Id == seed.Pool).ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, true));
            await db.Projects.Where(p => p.Id == seed.Project).ExecuteUpdateAsync(s => s.SetProperty(p => p.PreparationProfileVersion, "v2"));
        }
        await AssertRejectedAsync(() => RequestAsync(sql, seed));
        await using (var db = sql.NewContext())
            await db.Projects.Where(p => p.Id == seed.Project).ExecuteUpdateAsync(s => s.SetProperty(p => p.PreparationProfileVersion, "v1"));
        Assert.Equal(first, await RequestAsync(sql, seed)); Assert.Equal(audits, await AuditsAsync(sql));
        await using (var db = sql.NewContext()) await new SqlAgentControl(db).RevokeAsync(seed.Admin, seed.Credential.AgentId, Ct);
        audits = await AuditsAsync(sql); var before = await LifecycleAsync(sql);
        await AssertRejectedAsync(() => RequestAsync(sql, seed));
        Assert.Equal(audits, await AuditsAsync(sql)); Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Changed_stage_snapshot_and_historical_cleanup_cannot_revive_an_assignment()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var first = await RequestAsync(sql, seed); var audits = await AuditsAsync(sql);
        string snapshot;
        await using (var db = sql.NewContext())
        {
            snapshot = (await db.EnvironmentJobs.SingleAsync(j => j.Id == seed.Order.JobId)).SnapshotJson;
            // Semantically identical JSON is still a different immutable stage hash.
            await db.EnvironmentJobs.Where(j => j.Id == seed.Order.JobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.SnapshotJson, snapshot + " "));
        }
        await AssertRejectedAsync(() => RequestAsync(sql, seed));
        await using (var db = sql.NewContext())
            await db.EnvironmentJobs.Where(j => j.Id == seed.Order.JobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.SnapshotJson, snapshot));
        Assert.Equal(first, await RequestAsync(sql, seed));
        await using (var db = sql.NewContext())
        {
            // Impossible through normal restore/retirement APIs: conservative historical-ledger defense.
            db.StageRetirementIntents.Add(new StageRetirementIntent { RetirementId = Guid.NewGuid(),
                EnvironmentId = seed.Order.EnvironmentId, JobId = seed.Order.JobId, ArtifactId = seed.Report.ArtifactId,
                ReservationId = seed.Grant.Plan.ReservationId, AgentId = seed.Credential.AgentId,
                FencingToken = seed.Grant.FencingToken, RequestedBy = seed.Owner.UserId, RequestedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var before = await LifecycleAsync(sql);
        await AssertRejectedAsync(() => RequestAsync(sql, seed));
        Assert.Equal(audits, await AuditsAsync(sql)); Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Persisted_assignments_reject_sync_async_changes_and_sql_uniqueness_and_safety_violations()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var first = await RequestAsync(sql, seed);
        var secondSeed = await PrepareAsync(sql); var second = await RequestAsync(sql, secondSeed);
        var before = await LifecycleAsync(sql); var audits = await AuditsAsync(sql);
        foreach (var asynchronous in new[] { false, true })
        foreach (var deleting in new[] { false, true })
        {
            await using var db = sql.NewContext();
            var row = await db.TargetObservations.SingleAsync(r => r.ObservationRequestId == first.ObservationRequestId);
            if (deleting) db.TargetObservations.Remove(row);
            else db.Entry(row).Property("OperationId").CurrentValue = Guid.NewGuid();
            if (asynchronous) await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            else Assert.ThrowsAny<InvalidOperationException>(() => db.SaveChanges());
        }
        // Bypass EF guards only in this disposable metadata DB to exercise migrated SQL constraints.
        await using (var db = sql.NewContext())
        {
            var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [TargetObservations] SET [RestoreIntentId] = {first.RestoreIntentId} WHERE [ObservationRequestId] = {second.ObservationRequestId}", Ct));
            Assert.Contains(error.Number, new[] { 2601, 2627 });
        }
        await using (var db = sql.NewContext())
        {
            var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [TargetObservations] SET [Executable] = CAST(1 AS bit) WHERE [ObservationRequestId] = {first.ObservationRequestId}", Ct));
            Assert.Equal(547, error.Number);
        }
        await using (var db = sql.NewContext())
        {
            var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [TargetObservations] SET [ScheduledJobsMustRemainBlocked] = CAST(0 AS bit) WHERE [ObservationRequestId] = {first.ObservationRequestId}", Ct));
            Assert.Equal(547, error.Number);
        }
        Assert.Equal(first, await RequestAsync(sql, seed)); Assert.Equal(second, await RequestAsync(sql, secondSeed));
        Assert.Equal(audits, await AuditsAsync(sql)); Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Concurrent_duplicate_requests_from_two_contexts_share_assignment_and_one_audit()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var before = await LifecycleAsync(sql); var audits = await AuditsAsync(sql);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<TargetObservationAssignment> Compete()
        {
            await using var db = sql.NewContext(); var service = new SqlTargetObservations(db, seed.Options);
            await start.Task;
            return await service.RequestAsync(seed.Owner, seed.Order.EnvironmentId, seed.Restore.RestoreIntentId, Ct);
        }
        var left = Compete(); var right = Compete(); start.SetResult();
        var assignments = await Task.WhenAll(left, right);
        Assert.Equal(assignments[0], assignments[1]);
        await using (var db = sql.NewContext()) Assert.Single(await db.TargetObservations.ToListAsync());
        Assert.Equal(audits + 1, await AuditsAsync(sql)); Assert.Equal(before, await LifecycleAsync(sql));
    }
}
