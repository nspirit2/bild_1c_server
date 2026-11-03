using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Contracts;
using BuildSystem.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildSystem.IntegrationTests;

// Disposable metadata SQL only: never opens an IB/RAC target or executes a restore/target command.
public sealed class SqlTargetObservationReceiptTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly string Hash = new('A', 64);
    private sealed record Seed(Actor Admin, Actor Owner, Guid Project, Guid Pool, Guid Root,
        OrderReceipt Order, AgentCredential Credential, TargetObservationOptions Options,
        TargetObservationAssignment Assignment);

    // Local copy of the private assignment fixture; no existing test/helper files are changed.
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
        db.Projects.Add(new Project { Id = project, Name = "observation-receipt", IsolationPolicyId = "isolated", PreparationProfileVersion = "v1" });
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
        var ticket = await control.CreateEnrollmentAsync(admin, new(agent, "receipt-worker", pool), Ct);
        await control.RegisterAsync(new(agent, ticket.EnrollmentId, ticket.Token, "receipt-worker"), Ct);
        var credential = await control.ApproveAsync(admin, agent, Ct);
        var order = await new SqlEnvironmentOrders(db).CreateAsync(owner, "receipt-order",
            new(project, "receipt", "archive", new(version), 7), Ct);
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
        return new(admin, owner, project, pool, root, order, credential, options, assignment);
    }

    private static ReportTargetObservation Report(Seed seed) => new(Guid.NewGuid(), seed.Assignment,
        Guid.NewGuid(), Guid.NewGuid(), TargetObservationOutcome.BothObservedAbsent,
        new DateTimeOffset(2026, 11, 3, 9, 30, 0, TimeSpan.Zero).AddTicks(1234567));

    private static async Task<TargetObservationReceipt> SubmitAsync(SqlServerTestDatabase sql, Seed seed,
        ReportTargetObservation report, string? token = null, TargetObservationOptions? options = null)
    {
        await using var db = sql.NewContext();
        ITargetObservationReceipts service = new SqlTargetObservationReceipts(db, options ?? seed.Options);
        return await service.ReportAsync(token ?? seed.Credential.Token, report, Ct);
    }

    private static async Task RejectedAsync(Func<Task<TargetObservationReceipt>> action, int status = 409)
        => Assert.Equal(status, (await Assert.ThrowsAsync<RequestRejectedException>(action)).StatusCode);

    private static async Task<int> AuditsAsync(SqlServerTestDatabase sql)
    {
        await using var db = sql.NewContext(); return await db.AuditEvents.CountAsync();
    }

    // Include assignments and restore holds: recording historical labels must change neither.
    private static async Task<string> LifecycleAsync(SqlServerTestDatabase sql)
    {
        await using var db = sql.NewContext();
        await db.EnvironmentJobs.LoadAsync(); await db.AgentLeases.LoadAsync();
        await db.StagedArtifacts.LoadAsync(); await db.TestEnvironments.LoadAsync();
        await db.EnvironmentReservations.LoadAsync(); await db.EnvironmentQuotas.LoadAsync();
        await db.EnvironmentRequests.LoadAsync(); await db.StagingAllocationReleases.LoadAsync();
        await db.RestoreIntents.LoadAsync(); await db.TargetObservations.LoadAsync();
        return JsonSerializer.Serialize(db.ChangeTracker.Entries()
            .Select(e => e.Metadata.Name + ":" + JsonSerializer.Serialize(e.Properties.OrderBy(p => p.Metadata.Name)
                .ToDictionary(p => p.Metadata.Name, p => p.CurrentValue)))
            .OrderBy(s => s).ToArray());
    }

    [SqlServerFact]
    public async Task Exact_report_replay_normalizes_timestamp_instant_and_returns_one_ack_audit_without_lifecycle_changes()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var report = Report(seed); var before = await LifecycleAsync(sql); var audits = await AuditsAsync(sql);
        var first = await SubmitAsync(sql, seed, report);
        var replay = await SubmitAsync(sql, seed, report with { ObservedAt = report.ObservedAt.ToOffset(TimeSpan.FromHours(3)) });
        Assert.Equal(first, replay); Assert.Equal(report.ReceiptId, first.ReceiptId);
        Assert.Equal(seed.Assignment.ObservationRequestId, first.ObservationRequestId); Assert.Equal("Recorded", first.State);
        await using (var db = sql.NewContext())
        {
            var row = Assert.Single(await db.TargetObservationReceipts.ToListAsync());
            Assert.Equal(report.RacObservationId, row.RacObservationId); Assert.Equal(report.SqlObservationId, row.SqlObservationId);
            Assert.Equal(report.ObservedAt, row.ObservedAt); Assert.False(row.Executable); Assert.True(row.ScheduledJobsMustRemainBlocked);
            Assert.Single(await db.AuditEvents.Where(a => a.Action == "TargetObservationRecorded").ToListAsync());
        }
        Assert.Equal(audits + 1, await AuditsAsync(sql)); Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Wrong_malformed_rotated_and_revoked_agent_tokens_cannot_replay_a_recorded_receipt()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var other = await PrepareAsync(sql); var report = Report(seed) with { Outcome = TargetObservationOutcome.InfobaseObservedPresent };
        var first = await SubmitAsync(sql, seed, report); var audits = await AuditsAsync(sql);
        foreach (var token in new[] { "", "not-a-token", new string('x', 257), other.Credential.Token })
            await RejectedAsync(() => SubmitAsync(sql, seed, report, token), 401);
        Assert.Equal(audits, await AuditsAsync(sql));
        AgentCredential rotated;
        await using (var db = sql.NewContext()) rotated = await new SqlAgentControl(db).RotateAsync(seed.Admin, seed.Credential.AgentId, Ct);
        await RejectedAsync(() => SubmitAsync(sql, seed, report), 401);
        Assert.Equal(first, await SubmitAsync(sql, seed, report, rotated.Token));
        await using (var db = sql.NewContext()) await new SqlAgentControl(db).RevokeAsync(seed.Admin, seed.Credential.AgentId, Ct);
        audits = await AuditsAsync(sql); var before = await LifecycleAsync(sql);
        await RejectedAsync(() => SubmitAsync(sql, seed, report, rotated.Token), 401);
        Assert.Equal(audits, await AuditsAsync(sql)); Assert.Equal(before, await LifecycleAsync(sql));
        await using var check = sql.NewContext(); Assert.Single(await check.TargetObservationReceipts.ToListAsync());
    }

    [SqlServerFact]
    public async Task Every_assignment_pin_and_unknown_request_is_checked_before_acceptance_or_replay()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var report = Report(seed); var a = seed.Assignment; var audits = await AuditsAsync(sql);
        var mutations = new[] { a with { ObservationRequestId = Guid.NewGuid() }, a with { RestoreIntentId = Guid.NewGuid() },
            a with { AgentId = Guid.NewGuid() }, a with { OperationId = Guid.NewGuid() },
            a with { EnvironmentId = Guid.NewGuid() }, a with { StageJobId = Guid.NewGuid() },
            a with { FencingToken = a.FencingToken + 1 }, a with { ReservationId = Guid.NewGuid() },
            a with { TargetIncarnationId = Guid.NewGuid() }, a with { CanonicalClusterId = Guid.NewGuid() },
            a with { CanonicalSqlServerId = Guid.NewGuid() }, a with { RacEndpointMappingId = Guid.NewGuid() },
            a with { SqlEndpointMappingId = Guid.NewGuid() } };
        foreach (var assignment in mutations)
            await RejectedAsync(() => SubmitAsync(sql, seed, report with { Assignment = assignment }));
        await using (var db = sql.NewContext()) Assert.Empty(await db.TargetObservationReceipts.ToListAsync());
        Assert.Equal(audits, await AuditsAsync(sql));
        await SubmitAsync(sql, seed, report); audits = await AuditsAsync(sql); var before = await LifecycleAsync(sql);
        foreach (var assignment in mutations)
            await RejectedAsync(() => SubmitAsync(sql, seed, report with { Assignment = assignment }));
        Assert.Equal(audits, await AuditsAsync(sql)); Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task One_receipt_per_request_rejects_payload_conflicts_and_cross_request_observation_id_collisions()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var other = await PrepareAsync(sql); var report = Report(seed); var second = Report(other);
        var first = await SubmitAsync(sql, seed, report); var audits = await AuditsAsync(sql);
        foreach (var changed in new[] { report with { ReceiptId = Guid.NewGuid() },
            report with { Outcome = TargetObservationOutcome.BothObservedPresent },
            report with { ObservedAt = report.ObservedAt.AddTicks(1) },
            report with { RacObservationId = Guid.NewGuid() }, report with { SqlObservationId = Guid.NewGuid() } })
            await RejectedAsync(() => SubmitAsync(sql, seed, changed));
        foreach (var changed in new[] { second with { ReceiptId = report.ReceiptId },
            second with { RacObservationId = report.RacObservationId }, second with { SqlObservationId = report.SqlObservationId },
            second with { RacObservationId = report.SqlObservationId }, second with { SqlObservationId = report.RacObservationId } })
            await RejectedAsync(() => SubmitAsync(sql, other, changed));
        Assert.Equal(first, await SubmitAsync(sql, seed, report)); Assert.Equal(audits, await AuditsAsync(sql));
        await using var db = sql.NewContext(); Assert.Single(await db.TargetObservationReceipts.ToListAsync());
    }

    [SqlServerFact]
    public async Task Current_mapping_profile_root_pool_owner_membership_and_acl_drift_fail_closed_on_replay()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var report = Report(seed); var first = await SubmitAsync(sql, seed, report); var audits = await AuditsAsync(sql);
        foreach (var options in new[] { new TargetObservationOptions { Enabled = true },
            new TargetObservationOptions { Enabled = true, EndpointMappings = new() { [seed.Pool] = new(Guid.NewGuid(), seed.Assignment.SqlEndpointMappingId) } },
            new TargetObservationOptions { Enabled = true, EndpointMappings = new() { [seed.Pool] = new(seed.Assignment.RacEndpointMappingId, Guid.NewGuid()) } } })
            await RejectedAsync(() => SubmitAsync(sql, seed, report, options: options));
        await using (var db = sql.NewContext())
            await db.Projects.Where(p => p.Id == seed.Project).ExecuteUpdateAsync(s => s.SetProperty(p => p.PreparationProfileVersion, "v2"));
        await RejectedAsync(() => SubmitAsync(sql, seed, report));
        await using (var db = sql.NewContext())
        {
            await db.Projects.Where(p => p.Id == seed.Project).ExecuteUpdateAsync(s => s.SetProperty(p => p.PreparationProfileVersion, "v1"));
            await db.ArchiveRoots.Where(r => r.Id == seed.Root).ExecuteUpdateAsync(s => s.SetProperty(r => r.Enabled, false));
        }
        await RejectedAsync(() => SubmitAsync(sql, seed, report));
        await using (var db = sql.NewContext())
        {
            await db.ArchiveRoots.Where(r => r.Id == seed.Root).ExecuteUpdateAsync(s => s.SetProperty(r => r.Enabled, true));
            await db.EnvironmentPools.Where(p => p.Id == seed.Pool).ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, false));
        }
        await RejectedAsync(() => SubmitAsync(sql, seed, report));
        await using (var db = sql.NewContext())
        {
            await db.EnvironmentPools.Where(p => p.Id == seed.Pool).ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, true));
            await db.ProjectMemberships.Where(m => m.UserId == seed.Owner.UserId && m.ProjectId == seed.Project).ExecuteDeleteAsync();
        }
        await RejectedAsync(() => SubmitAsync(sql, seed, report));
        await using (var db = sql.NewContext())
        {
            db.ProjectMemberships.Add(new ProjectMembership { UserId = seed.Owner.UserId, ProjectId = seed.Project }); await db.SaveChangesAsync();
            await db.ProjectPools.Where(m => m.ProjectId == seed.Project && m.PoolId == seed.Pool).ExecuteUpdateAsync(s => s.SetProperty(m => m.Enabled, false));
        }
        await RejectedAsync(() => SubmitAsync(sql, seed, report));
        await using (var db = sql.NewContext())
            await db.ProjectPools.Where(m => m.ProjectId == seed.Project && m.PoolId == seed.Pool).ExecuteUpdateAsync(s => s.SetProperty(m => m.Enabled, true));
        Assert.Equal(first, await SubmitAsync(sql, seed, report)); Assert.Equal(audits, await AuditsAsync(sql));
    }

    [SqlServerFact]
    public async Task Concurrent_identical_reports_from_two_contexts_share_one_ack_and_audit()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var report = Report(seed); var before = await LifecycleAsync(sql); var audits = await AuditsAsync(sql);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<TargetObservationReceipt> Compete()
        {
            await using var db = sql.NewContext(); var service = new SqlTargetObservationReceipts(db, seed.Options);
            await start.Task; return await service.ReportAsync(seed.Credential.Token, report, Ct);
        }
        var left = Compete(); var right = Compete(); start.SetResult(); var receipts = await Task.WhenAll(left, right);
        Assert.Equal(receipts[0], receipts[1]); Assert.Equal("Recorded", receipts[0].State);
        await using (var db = sql.NewContext()) Assert.Single(await db.TargetObservationReceipts.ToListAsync());
        Assert.Equal(audits + 1, await AuditsAsync(sql)); Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Persisted_receipts_reject_sync_async_ef_changes_and_sql_uniqueness_and_safety_violations()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var other = await PrepareAsync(sql);
        var report = Report(seed) with { Outcome = TargetObservationOutcome.SqlDatabaseObservedPresent };
        var second = Report(other) with { Outcome = TargetObservationOutcome.BothObservedPresent };
        var first = await SubmitAsync(sql, seed, report); await SubmitAsync(sql, other, second);
        var before = await LifecycleAsync(sql); var audits = await AuditsAsync(sql);
        foreach (var asynchronous in new[] { false, true })
        foreach (var deleting in new[] { false, true })
        {
            await using var db = sql.NewContext();
            var row = await db.TargetObservationReceipts.SingleAsync(r => r.ReceiptId == report.ReceiptId);
            if (deleting) db.TargetObservationReceipts.Remove(row);
            else db.Entry(row).Property("ObservedAt").CurrentValue = report.ObservedAt.AddSeconds(1);
            if (asynchronous) await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            else Assert.ThrowsAny<InvalidOperationException>(() => db.SaveChanges());
        }
        // SQL bypass is confined to this disposable metadata database, to test migrated constraints.
        foreach (var column in new[] { "ObservationRequestId", "RacObservationId", "SqlObservationId" })
        {
            await using var db = sql.NewContext();
            var value = column == "ObservationRequestId" ? seed.Assignment.ObservationRequestId :
                column == "RacObservationId" ? report.RacObservationId : report.SqlObservationId;
            // Identifier comes only from the fixed local whitelist above; values remain parameters.
            var command = $"UPDATE [TargetObservationReceipts] SET [{column}] = @value WHERE [ReceiptId] = @receipt";
            var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
                command, new SqlParameter("@value", value), new SqlParameter("@receipt", second.ReceiptId)));
            Assert.Contains(error.Number, new[] { 2601, 2627 });
        }
        foreach (var violation in new[] { "[Executable] = CAST(1 AS bit)", "[ScheduledJobsMustRemainBlocked] = CAST(0 AS bit)",
            "[Outcome] = 99", "[RacObservationId] = [SqlObservationId]",
            "[RacObservationId] = '00000000-0000-0000-0000-000000000000'", "[SqlObservationId] = '00000000-0000-0000-0000-000000000000'" })
        {
            await using var db = sql.NewContext();
            // SQL clauses are selected exclusively from the literal test cases above.
            var command = $"UPDATE [TargetObservationReceipts] SET {violation} WHERE [ReceiptId] = @receipt";
            var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
                command, new SqlParameter("@receipt", report.ReceiptId)));
            Assert.Equal(547, error.Number);
        }
        Assert.Equal(first, await SubmitAsync(sql, seed, report));
        Assert.Equal(audits, await AuditsAsync(sql)); Assert.Equal(before, await LifecycleAsync(sql));
    }

    [Fact]
    public async Task Default_off_and_malformed_reports_reject_before_any_sql_connection_is_needed()
    {
        // No provider/connection is configured. A database operation would fail this test.
        await using var db = new BuildSystemDbContext(new DbContextOptionsBuilder<BuildSystemDbContext>().Options);
        var a = new TargetObservationAssignment(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var valid = new ReportTargetObservation(Guid.NewGuid(), a, Guid.NewGuid(), Guid.NewGuid(),
            TargetObservationOutcome.BothObservedAbsent, DateTimeOffset.UtcNow);
        await RejectedAsync(() => new SqlTargetObservationReceipts(db, new()).ReportAsync("token", valid, Ct));
        var service = new SqlTargetObservationReceipts(db, new TargetObservationOptions { Enabled = true });
        foreach (var invalid in new[] { valid with { ReceiptId = Guid.Empty }, valid with { Assignment = null! },
            valid with { RacObservationId = Guid.Empty }, valid with { SqlObservationId = Guid.Empty },
            valid with { SqlObservationId = valid.RacObservationId }, valid with { Outcome = (TargetObservationOutcome)99 },
            valid with { ObservedAt = default }, valid with { Assignment = a with { ObservationRequestId = Guid.Empty } },
            valid with { Assignment = a with { FencingToken = 0 } } })
            await RejectedAsync(() => service.ReportAsync("token", invalid, Ct), 400);
        await RejectedAsync(() => service.ReportAsync("token", null!, Ct), 400);
    }
}
