using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Contracts;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildSystem.IntegrationTests;

// Disposable metadata SQL only: delivery never opens RAC/IB/SQL targets or executes target commands.
public sealed class SqlTargetObservationDeliveryTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly string Hash = new('A', 64);
    private sealed record Seed(Actor Admin, Actor Owner, Guid Project, Guid Pool, Guid Root,
        OrderReceipt Order, AgentCredential Credential, StageLeaseGrant Grant, ReportStagedArtifact Staged,
        RestoreIntentReceipt Restore, TargetObservationOptions Options, TargetObservationAssignment Assignment);

    // Local copy of the private receipt fixture, retaining the stage report and restore pins.
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
        db.Projects.Add(new Project { Id = project, Name = "observation-delivery", IsolationPolicyId = "isolated", PreparationProfileVersion = "v1" });
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
        var ticket = await control.CreateEnrollmentAsync(admin, new(agent, "delivery-worker", pool), Ct);
        await control.RegisterAsync(new(agent, ticket.EnrollmentId, ticket.Token, "delivery-worker"), Ct);
        var credential = await control.ApproveAsync(admin, agent, Ct);
        var order = await new SqlEnvironmentOrders(db).CreateAsync(owner, "delivery-order",
            new(project, "delivery", "archive", new(version), 7), Ct);
        var grant = Assert.IsType<StageLeaseGrant>(await new SqlArchiveStageControl(db,
            new ArchiveStageOptions { Enabled = true }).PollAsync(credential.Token, Ct));
        var staged = new ReportStagedArtifact(grant.JobId, grant.FencingToken, grant.Plan.ReservationId,
            version, Guid.NewGuid(), Hash, 10, "dt");
        await new SqlArtifactReceipts(db).ReportAsync(credential.Token, staged, Ct);
        await new SqlStageCompletion(db, new ArchiveStageOptions { Enabled = true })
            .CompleteAsync(credential.Token, new(grant.JobId, grant.FencingToken, staged.ArtifactId), Ct);
        var restore = await new SqlRestoreIntents(db, new RestoreIntentOptions { Enabled = true,
            CanonicalMappings = new() { [pool] = new(cluster, server) } })
            .RequestAsync(owner, order.EnvironmentId, new(order.JobId, staged.ArtifactId), Ct);
        var options = new TargetObservationOptions { Enabled = true,
            EndpointMappings = new() { [pool] = new(Guid.NewGuid(), Guid.NewGuid()) } };
        var assignment = await new SqlTargetObservations(db, options)
            .RequestAsync(owner, order.EnvironmentId, restore.RestoreIntentId, Ct);
        return new(admin, owner, project, pool, root, order, credential, grant, staged, restore, options, assignment);
    }

    private static async Task<TargetObservationAssignment> ReadAsync(SqlServerTestDatabase sql, Seed seed,
        string? token = null, Guid? requestId = null, TargetObservationOptions? options = null)
    {
        await using var db = sql.NewContext();
        ITargetObservationDelivery service = new SqlTargetObservationDelivery(db, options ?? seed.Options);
        return await service.ReadAsync(token ?? seed.Credential.Token, requestId ?? seed.Assignment.ObservationRequestId, Ct);
    }

    private static async Task RejectedAsync(Func<Task<TargetObservationAssignment>> action, int status = 409)
        => Assert.Equal(status, (await Assert.ThrowsAsync<RequestRejectedException>(action)).StatusCode);

    // Compare every scalar/rowversion in these lifecycle, lease, assignment, receipt and audit tables;
    // this is deliberately not advertised as a snapshot of every metadata table in the database.
    private static async Task<string> LifecycleAsync(SqlServerTestDatabase sql)
    {
        await using var db = sql.NewContext();
        await db.EnvironmentJobs.LoadAsync(); await db.AgentLeases.LoadAsync();
        await db.StagedArtifacts.LoadAsync(); await db.TestEnvironments.LoadAsync();
        await db.EnvironmentReservations.LoadAsync(); await db.EnvironmentQuotas.LoadAsync();
        await db.EnvironmentRequests.LoadAsync(); await db.StagingAllocationReleases.LoadAsync();
        await db.RestoreIntents.LoadAsync(); await db.StageRetirementIntents.LoadAsync();
        await db.TargetObservations.LoadAsync(); await db.TargetObservationReceipts.LoadAsync();
        await db.AuditEvents.LoadAsync(); await db.Agents.LoadAsync();
        return JsonSerializer.Serialize(db.ChangeTracker.Entries()
            .Select(e => e.Metadata.Name + ":" + JsonSerializer.Serialize(e.Properties.OrderBy(p => p.Metadata.Name)
                .ToDictionary(p => p.Metadata.Name, p => p.CurrentValue)))
            .OrderBy(s => s).ToArray());
    }

    [SqlServerFact]
    public async Task Exact_delivery_and_replay_return_all_existing_pins_without_lifecycle_lease_receipt_assignment_or_audit_changes()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var before = await LifecycleAsync(sql);
        var first = await ReadAsync(sql, seed); var replay = await ReadAsync(sql, seed);
        Assert.Equal(seed.Assignment, first); Assert.Equal(first, replay);
        Assert.Equal(seed.Assignment.ObservationRequestId, first.ObservationRequestId);
        Assert.Equal(seed.Assignment.OperationId, first.OperationId);
        Assert.Equal(seed.Restore.RestoreIntentId, first.RestoreIntentId);
        Assert.Equal(seed.Credential.AgentId, first.AgentId); Assert.Equal(seed.Order.EnvironmentId, first.EnvironmentId);
        Assert.Equal(seed.Order.JobId, first.StageJobId); Assert.Equal(seed.Grant.FencingToken, first.FencingToken);
        Assert.Equal(seed.Grant.Plan.ReservationId, first.ReservationId);
        Assert.Equal(seed.Restore.TargetIncarnation, first.TargetIncarnationId);
        Assert.Equal(seed.Restore.CanonicalClusterId, first.CanonicalClusterId);
        Assert.Equal(seed.Restore.CanonicalSqlServerId, first.CanonicalSqlServerId);
        Assert.Equal(seed.Options.EndpointMappings[seed.Pool].RacEndpointMappingId, first.RacEndpointMappingId);
        Assert.Equal(seed.Options.EndpointMappings[seed.Pool].SqlEndpointMappingId, first.SqlEndpointMappingId);
        Assert.Equal(seed.Restore.InfobaseName, first.InfobaseName);
        Assert.Equal(seed.Restore.SqlDbName, first.SqlDatabaseName);
        Assert.False(first.Executable); Assert.True(first.ScheduledJobsMustRemainBlocked);
        await using (var db = sql.NewContext())
        {
            Assert.Single(await db.TargetObservations.ToListAsync());
            Assert.Empty(await db.TargetObservationReceipts.ToListAsync());
        }
        Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Wrong_unknown_malformed_rotated_and_revoked_tokens_fail_closed_before_delivery_or_history()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var other = await PrepareAsync(sql);
        await using (var db = sql.NewContext())
            await new SqlTargetObservationReceipts(db, seed.Options).ReportAsync(seed.Credential.Token,
                new(Guid.NewGuid(), seed.Assignment, Guid.NewGuid(), Guid.NewGuid(),
                    TargetObservationOutcome.BothObservedAbsent, DateTimeOffset.UtcNow), Ct);
        var before = await LifecycleAsync(sql);
        foreach (var token in new[] { "", "not-a-token", new string('x', 257) })
        {
            await RejectedAsync(() => ReadAsync(sql, seed, token), 401);
            await RejectedAsync(() => ReadAsync(sql, seed, token, Guid.NewGuid()), 401);
        }
        await RejectedAsync(() => ReadAsync(sql, seed, other.Credential.Token), 401);
        await RejectedAsync(() => ReadAsync(sql, seed, other.Credential.Token, Guid.NewGuid()));
        await RejectedAsync(() => ReadAsync(sql, seed, requestId: Guid.NewGuid()));
        await RejectedAsync(() => ReadAsync(sql, seed, requestId: Guid.Empty), 400);
        await RejectedAsync(() => ReadAsync(sql, seed, options: new()));
        Assert.Equal(before, await LifecycleAsync(sql));
        AgentCredential rotated;
        await using (var db = sql.NewContext())
            rotated = await new SqlAgentControl(db).RotateAsync(seed.Admin, seed.Credential.AgentId, Ct);
        before = await LifecycleAsync(sql);
        await RejectedAsync(() => ReadAsync(sql, seed), 401);
        Assert.Equal(seed.Assignment, await ReadAsync(sql, seed, rotated.Token));
        Assert.Equal(before, await LifecycleAsync(sql));
        await using (var db = sql.NewContext())
            await new SqlAgentControl(db).RevokeAsync(seed.Admin, seed.Credential.AgentId, Ct);
        before = await LifecycleAsync(sql);
        await RejectedAsync(() => ReadAsync(sql, seed, rotated.Token), 401);
        await RejectedAsync(() => ReadAsync(sql, seed, rotated.Token, Guid.NewGuid()), 401);
        Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Current_root_pool_project_profile_membership_acl_and_endpoint_mapping_drift_reject_until_restored()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var before = await LifecycleAsync(sql);
        foreach (var options in new[] { new TargetObservationOptions { Enabled = true },
            new TargetObservationOptions { Enabled = true, EndpointMappings = new() { [seed.Pool] = new(Guid.NewGuid(), seed.Assignment.SqlEndpointMappingId) } },
            new TargetObservationOptions { Enabled = true, EndpointMappings = new() { [seed.Pool] = new(seed.Assignment.RacEndpointMappingId, Guid.NewGuid()) } } })
            await RejectedAsync(() => ReadAsync(sql, seed, options: options));
        async Task CheckAsync()
        {
            var drifted = await LifecycleAsync(sql);
            await RejectedAsync(() => ReadAsync(sql, seed));
            Assert.Equal(drifted, await LifecycleAsync(sql));
        }
        await using (var db = sql.NewContext())
            await db.ArchiveRoots.Where(r => r.Id == seed.Root).ExecuteUpdateAsync(s => s.SetProperty(r => r.Enabled, false));
        await CheckAsync();
        await using (var db = sql.NewContext())
        {
            await db.ArchiveRoots.Where(r => r.Id == seed.Root).ExecuteUpdateAsync(s => s.SetProperty(r => r.Enabled, true));
            await db.EnvironmentPools.Where(p => p.Id == seed.Pool).ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, false));
        }
        await CheckAsync();
        await using (var db = sql.NewContext())
        {
            await db.EnvironmentPools.Where(p => p.Id == seed.Pool).ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, true));
            await db.Projects.Where(p => p.Id == seed.Project).ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, false));
        }
        await CheckAsync();
        await using (var db = sql.NewContext())
        {
            await db.Projects.Where(p => p.Id == seed.Project).ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, true).SetProperty(p => p.PreparationProfileVersion, "v2"));
        }
        await CheckAsync();
        await using (var db = sql.NewContext())
        {
            await db.Projects.Where(p => p.Id == seed.Project).ExecuteUpdateAsync(s => s.SetProperty(p => p.PreparationProfileVersion, "v1"));
            await db.ProjectMemberships.Where(m => m.UserId == seed.Owner.UserId && m.ProjectId == seed.Project).ExecuteDeleteAsync();
        }
        await CheckAsync();
        await using (var db = sql.NewContext())
        {
            db.ProjectMemberships.Add(new ProjectMembership { UserId = seed.Owner.UserId, ProjectId = seed.Project });
            await db.SaveChangesAsync();
            await db.ProjectPools.Where(m => m.ProjectId == seed.Project && m.PoolId == seed.Pool).ExecuteUpdateAsync(s => s.SetProperty(m => m.Enabled, false));
        }
        await CheckAsync();
        await using (var db = sql.NewContext())
        {
            await db.ProjectPools.Where(m => m.ProjectId == seed.Project && m.PoolId == seed.Pool).ExecuteUpdateAsync(s => s.SetProperty(m => m.Enabled, true));
            await db.ArchiveRootProjects.Where(m => m.RootId == seed.Root && m.ProjectId == seed.Project).ExecuteDeleteAsync();
        }
        await CheckAsync();
        await using (var db = sql.NewContext())
        {
            db.ArchiveRootProjects.Add(new ArchiveRootProject { RootId = seed.Root, ProjectId = seed.Project });
            await db.SaveChangesAsync();
        }
        Assert.Equal(seed.Assignment, await ReadAsync(sql, seed));
        Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Recorded_receipt_retains_historical_assignment_and_correlation_but_snapshot_drift_or_cleanup_still_reject()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var report = new ReportTargetObservation(Guid.NewGuid(), seed.Assignment, Guid.NewGuid(), Guid.NewGuid(),
            TargetObservationOutcome.BothObservedAbsent, DateTimeOffset.UtcNow);
        TargetObservationReceipt receipt;
        await using (var db = sql.NewContext())
            receipt = await new SqlTargetObservationReceipts(db, seed.Options).ReportAsync(seed.Credential.Token, report, Ct);
        var before = await LifecycleAsync(sql);
        Assert.Equal(seed.Assignment, await ReadAsync(sql, seed));
        Assert.Equal(seed.Assignment, await ReadAsync(sql, seed));
        Assert.Equal(report.ReceiptId, receipt.ReceiptId);
        Assert.Equal(seed.Assignment.ObservationRequestId, receipt.ObservationRequestId);
        Assert.Equal("Recorded", receipt.State);
        await using (var db = sql.NewContext())
        {
            Assert.Single(await db.TargetObservations.ToListAsync());
            var recorded = Assert.Single(await db.TargetObservationReceipts.ToListAsync());
            Assert.Equal(report.ReceiptId, recorded.ReceiptId);
            Assert.Equal(report.RacObservationId, recorded.RacObservationId);
            Assert.Equal(report.SqlObservationId, recorded.SqlObservationId);
            Assert.Equal(receipt, await new SqlTargetObservationReceipts(db, seed.Options).ReportAsync(seed.Credential.Token, report, Ct));
        }
        Assert.Equal(before, await LifecycleAsync(sql));
        string snapshot;
        await using (var db = sql.NewContext())
        {
            snapshot = (await db.EnvironmentJobs.SingleAsync(j => j.Id == seed.Order.JobId)).SnapshotJson;
            await db.EnvironmentJobs.Where(j => j.Id == seed.Order.JobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.SnapshotJson, snapshot + " "));
        }
        var drifted = await LifecycleAsync(sql);
        await RejectedAsync(() => ReadAsync(sql, seed));
        Assert.Equal(drifted, await LifecycleAsync(sql));
        await using (var db = sql.NewContext())
            await db.EnvironmentJobs.Where(j => j.Id == seed.Order.JobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.SnapshotJson, snapshot));
        Assert.Equal(seed.Assignment, await ReadAsync(sql, seed));
        await using (var db = sql.NewContext())
        {
            // Impossible through normal restore/retirement APIs; exercise historical-ledger defense.
            db.StageRetirementIntents.Add(new StageRetirementIntent { RetirementId = Guid.NewGuid(),
                EnvironmentId = seed.Order.EnvironmentId, JobId = seed.Order.JobId, ArtifactId = seed.Staged.ArtifactId,
                ReservationId = seed.Grant.Plan.ReservationId, AgentId = seed.Credential.AgentId,
                FencingToken = seed.Grant.FencingToken, RequestedBy = seed.Owner.UserId, RequestedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var retired = await LifecycleAsync(sql);
        await RejectedAsync(() => ReadAsync(sql, seed));
        Assert.Equal(retired, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Concurrent_delivery_from_two_contexts_returns_identical_existing_assignment_without_database_changes()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var before = await LifecycleAsync(sql);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<TargetObservationAssignment> Compete()
        {
            await using var db = sql.NewContext();
            var service = new SqlTargetObservationDelivery(db, seed.Options);
            await start.Task;
            return await service.ReadAsync(seed.Credential.Token, seed.Assignment.ObservationRequestId, Ct);
        }
        var left = Compete(); var right = Compete(); start.SetResult();
        var assignments = await Task.WhenAll(left, right);
        Assert.Equal(seed.Assignment, assignments[0]); Assert.Equal(assignments[0], assignments[1]);
        Assert.Equal(before, await LifecycleAsync(sql));
    }

    [Fact]
    public async Task Default_off_invalid_credentials_and_empty_request_id_reject_without_a_database_provider()
    {
        // A database operation fails here: no provider or connection is configured.
        await using var db = new BuildSystemDbContext(new DbContextOptionsBuilder<BuildSystemDbContext>().Options);
        var requestId = Guid.NewGuid();
        await RejectedAsync(() => new SqlTargetObservationDelivery(db, new()).ReadAsync("token", requestId, Ct));
        ITargetObservationDelivery service = new SqlTargetObservationDelivery(db, new TargetObservationOptions { Enabled = true });
        foreach (var invalid in new[] { "", new string('x', 257) })
            await RejectedAsync(() => service.ReadAsync(invalid, requestId, Ct), 401);
        await RejectedAsync(() => service.ReadAsync("token", Guid.Empty, Ct), 400);
        // Providerless context itself is the guard: database/model access would have thrown above.
    }
}
