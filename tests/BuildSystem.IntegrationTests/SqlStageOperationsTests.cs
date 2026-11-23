using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Contracts;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildSystem.IntegrationTests;

public sealed class SqlStageOperationsTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly string Hash = new('A', 64);
    private sealed record Seed(Actor Admin, Actor Owner, Actor Foreign, Guid Project, Guid Root,
        Guid Pool, Guid Agent, IReadOnlyList<OrderReceipt> Owned, IReadOnlyList<OrderReceipt> Other, OrderReceipt Probe);

    private static async Task<Seed> PrepareAsync(SqlServerTestDatabase sql)
    {
        await sql.ApplyMigrationsAsync();
        var admin = new Actor(Guid.NewGuid(), true); var owner = new Actor(Guid.NewGuid(), false);
        var foreign = new Actor(Guid.NewGuid(), false);
        var project = Guid.NewGuid(); var pool = Guid.NewGuid(); var root = Guid.NewGuid();
        var archive = Guid.NewGuid(); var version = Guid.NewGuid(); var agent = Guid.NewGuid();
        await using var db = sql.NewContext();
        db.Users.AddRange(new BuildSystemUser { Id = admin.UserId, UserName = "admin", NormalizedUserName = "ADMIN" },
            new BuildSystemUser { Id = owner.UserId, UserName = "owner", NormalizedUserName = "OWNER" },
            new BuildSystemUser { Id = foreign.UserId, UserName = "foreign", NormalizedUserName = "FOREIGN" });
        db.Projects.Add(new Project { Id = project, Name = "project", IsolationPolicyId = "isolated", PreparationProfileVersion = "v1" });
        db.ProjectMemberships.AddRange(new ProjectMembership { UserId = owner.UserId, ProjectId = project },
            new ProjectMembership { UserId = foreign.UserId, ProjectId = project });
        db.ArchiveRoots.Add(new ArchiveRoot { Id = root, Enabled = true, AgentScopeId = agent,
            IsolationPolicyId = "isolated", MaxArchiveBytes = 10000, MaxStagingBytes = 10000 });
        db.ArchiveRootProjects.Add(new ArchiveRootProject { RootId = root, ProjectId = project });
        db.DiskArchives.Add(new DiskArchive { Id = archive, RootId = root, RelativePath = "private-source.dt" });
        db.DiskArchiveVersions.Add(new DiskArchiveVersion { Id = version, ArchiveId = archive,
            Sha256 = Hash, SizeBytes = 10, Format = "dt", State = "Ready", EstimatedDbBytes = 100 });
        db.EnvironmentPools.Add(new EnvironmentPool { Id = pool, Enabled = true,
            ClusterId = Guid.NewGuid(), SqlServerId = Guid.NewGuid(), AgentScopeId = agent,
            IsolationPolicyId = "isolated", CapacityBytes = 100000, MaxEnvironmentBytes = 10000,
            MaxStagingBytes = 10000, MaxConcurrentProvisioning = 20, SupportsDt = true, SupportsBak = true,
            IsolationVerified = true, ScheduledJobsSuppressionVerified = true, ResourceLimitVerified = true });
        db.ProjectPools.Add(new ProjectPool { ProjectId = project, PoolId = pool, Enabled = true });
        foreach (var actor in new[] { owner, foreign })
            db.EnvironmentQuotas.Add(new EnvironmentQuota { SubjectId = actor.UserId, MaxActiveEnvironments = 10,
                MaxConcurrentProvisioning = 10, MaxStorageBytes = 100000, MaxTtlDays = 30 });
        await db.SaveChangesAsync();
        var owned = new List<OrderReceipt>(); var others = new List<OrderReceipt>();
        for (var n = 0; n < 3; n++) owned.Add(await new SqlEnvironmentOrders(db).CreateAsync(owner, "own-" + n,
            new CreateEnvironmentRequest(project, "own-" + n, "archive", new ArchiveSource(version), 7), Ct));
        for (var n = 0; n < 2; n++) others.Add(await new SqlEnvironmentOrders(db).CreateAsync(foreign, "other-" + n,
            new CreateEnvironmentRequest(project, "other-" + n, "archive", new ArchiveSource(version), 7), Ct));
        var probe = await new SqlEnvironmentOrders(db).CreateAsync(owner, "probe",
            new CreateEnvironmentRequest(project, "probe", "archive", new ArchiveSource(version), 7), Ct);
        // Fixture-only kind substitution tests list exclusion, not probe scheduling or provisioning execution.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [EnvironmentJobs] SET [Kind]='ControlPlaneProbe' WHERE [Id]={probe.JobId}");
        return new(admin, owner, foreign, project, root, pool, agent, owned, others, probe);
    }

    private static async Task<string> DurableStateAsync(SqlServerTestDatabase sql)
    {
        await using var db = sql.NewContext();
        return JsonSerializer.Serialize(new
        {
            Jobs = await db.EnvironmentJobs.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            Requests = await db.EnvironmentRequests.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            Environments = await db.TestEnvironments.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            Reservations = await db.EnvironmentReservations.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            Leases = await db.AgentLeases.AsNoTracking().OrderBy(x => x.JobId).ToListAsync(),
            Artifacts = await db.StagedArtifacts.AsNoTracking().OrderBy(x => x.ArtifactId).ToListAsync(),
            Cancellations = await db.PendingArchiveCancellations.AsNoTracking().OrderBy(x => x.EnvironmentId).ToListAsync(),
            Audits = await db.AuditEvents.AsNoTracking().OrderBy(x => x.Id).ToListAsync()
        });
    }

    [SqlServerFact]
    public async Task Owner_and_administrator_lists_are_isolated_and_never_include_probe_jobs()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        await using var db = sql.NewContext(); var service = new SqlStageOperations(db);
        var own = await service.ListAsync(seed.Owner, new StageOperationsQuery(), Ct);
        var other = await service.ListAsync(seed.Foreign, new StageOperationsQuery(), Ct);
        var admin = await service.ListAsync(seed.Admin, new StageOperationsQuery(), Ct);
        Assert.Equal(seed.Owned.Select(x => x.JobId).Order(), own.Items.Select(x => x.JobId).Order());
        Assert.Equal(seed.Other.Select(x => x.JobId).Order(), other.Items.Select(x => x.JobId).Order());
        Assert.Equal(5, admin.Items.Count); Assert.DoesNotContain(admin.Items, x => x.JobId == seed.Probe.JobId);
        Assert.Empty((await service.ListAsync(new Actor(Guid.NewGuid(), false), new StageOperationsQuery(), Ct)).Items);
    }

    [SqlServerFact]
    public async Task Keyset_pagination_handles_tied_creation_times_without_duplicates_and_binds_filter()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var tied = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await using (var setup = sql.NewContext())
            await setup.EnvironmentJobs.ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, tied));
        await using var db = sql.NewContext(); var service = new SqlStageOperations(db);
        var seen = new HashSet<Guid>(); var ordered = new List<string>();
        string? cursor = null; string? firstCursor = null; var pages = 0;
        do
        {
            var page = await service.ListAsync(seed.Admin, new StageOperationsQuery(2, cursor, "Pending"), Ct);
            Assert.InRange(page.Items.Count, 1, 2);
            foreach (var item in page.Items)
            {
                Assert.Equal(tied, item.CreatedAt); Assert.True(seen.Add(item.JobId));
                ordered.Add(item.JobId.ToString("D"));
            }
            cursor = page.NextCursor; firstCursor ??= cursor;
            Assert.True(++pages <= 3, "Cursor must advance and terminate.");
        } while (cursor is not null);
        Assert.Equal(3, pages); Assert.Equal(5, seen.Count);
        Assert.Equal(ordered.OrderByDescending(x => x, StringComparer.Ordinal), ordered);
        Assert.Equal(seed.Owned.Concat(seed.Other).Select(x => x.JobId).Order(), seen.Order());
        Assert.NotNull(firstCursor);
        var changedFilter = await Assert.ThrowsAsync<RequestRejectedException>(() => service.ListAsync(seed.Admin,
            new StageOperationsQuery(2, firstCursor, "Leased"), Ct));
        Assert.Equal(400, changedFilter.StatusCode);
        var changedActor = await Assert.ThrowsAsync<RequestRejectedException>(() => service.ListAsync(seed.Owner,
            new StageOperationsQuery(2, firstCursor, "Pending"), Ct));
        Assert.Equal(400, changedActor.StatusCode);
    }

    [SqlServerFact]
    public async Task Supported_job_state_filters_are_exact_and_observation_does_not_mutate_rows()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        // Fixtures create all supported list states without claiming these mutations prove the lifecycle.
        await using (var setup = sql.NewContext())
        {
            await setup.EnvironmentJobs.Where(x => x.Id == seed.Owned[0].JobId).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Leased"));
            await setup.EnvironmentJobs.Where(x => x.Id == seed.Owned[1].JobId).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "StageCompleted"));
            await setup.EnvironmentJobs.Where(x => x.Id == seed.Owned[2].JobId).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "NeedsAttention"));
        }
        var before = await DurableStateAsync(sql);
        await using var db = sql.NewContext(); var service = new SqlStageOperations(db);
        foreach (var state in new[] { "Pending", "Leased", "StageCompleted", "NeedsAttention" })
        {
            var page = await service.ListAsync(seed.Admin, new StageOperationsQuery(State: state), Ct);
            Assert.NotEmpty(page.Items); Assert.All(page.Items, item => Assert.Equal(state, item.JobState));
            Assert.NotEqual(default, page.ObservedAt);
        }
        Assert.Equal(before, await DurableStateAsync(sql));
    }

    private static Task<PendingArchiveCancellationReceipt> CancelAsync(BuildSystemDbContext db, Actor owner, OrderReceipt order) =>
        new SqlPendingArchiveCancellations(db, enabled: true).CancelAsync(owner, order.EnvironmentId, new(order.JobId), Ct);

    [SqlServerFact]
    public async Task Proven_cancellations_are_owner_scoped_marked_and_keyset_paginated()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        await using (var setup = sql.NewContext())
        {
            await CancelAsync(setup, seed.Owner, seed.Owned[0]);
            await CancelAsync(setup, seed.Foreign, seed.Other[0]);
            var tied = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            await setup.EnvironmentJobs.ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, tied));
        }
        var before = await DurableStateAsync(sql);
        await using var db = sql.NewContext(); var service = new SqlStageOperations(db);
        var ownerView = Assert.Single((await service.ListAsync(seed.Owner, new(State: "Cancelled"), Ct)).Items);
        Assert.Equal(seed.Owned[0].JobId, ownerView.JobId);
        Assert.Equal(seed.Other[0].JobId, Assert.Single((await service.ListAsync(seed.Foreign, new(State: "Cancelled"), Ct)).Items).JobId);
        Assert.Equal("Cancelled", ownerView.JobState);
        Assert.Equal("Cancelled", ownerView.EnvironmentState);
        Assert.Equal("Released", ownerView.ReservationState);
        Assert.False(ownerView.Unsettled);
        Assert.Equal(10, ownerView.StagingBytes);
        var status = Assert.IsType<StageStatusView>(await new SqlStageStatus(db).GetAsync(seed.Owner, ownerView.EnvironmentId, Ct));
        Assert.Equal("CancelledBeforeFirstLease", status.ReconciliationCode);
        Assert.Equal(status.ReconciliationCode, ownerView.CancellationCode);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ownerView));
        Assert.Equal(ownerView.CancellationCode, json.RootElement.GetProperty("cancellationCode").GetString());
        Assert.Equal(10, json.RootElement.EnumerateObject().Count());
        var expected = new[] { seed.Owned[0].JobId, seed.Other[0].JobId }.OrderByDescending(id => id.ToString("D"), StringComparer.Ordinal).ToArray();
        var first = await service.ListAsync(seed.Admin, new(1, State: "Cancelled"), Ct);
        Assert.Equal(expected[0], Assert.Single(first.Items).JobId); Assert.NotNull(first.NextCursor);
        var second = await service.ListAsync(seed.Admin, new(1, first.NextCursor, "Cancelled"), Ct);
        Assert.Equal(expected[1], Assert.Single(second.Items).JobId); Assert.Null(second.NextCursor);
        await Assert.ThrowsAsync<RequestRejectedException>(() => service.ListAsync(seed.Admin, new(1, first.NextCursor), Ct));
        var all = await service.ListAsync(seed.Admin, new(), Ct);
        Assert.Equal(5, all.Items.Count);
        Assert.Equal(2, all.Items.Count(x => x.CancellationCode is not null));
        Assert.Equal(before, await DurableStateAsync(sql));
    }

    [SqlServerFact]
    public async Task Cancellation_like_contradictions_are_filtered_before_limit_and_cursor()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var retainedReceipt = seed.Owned[0]; var missingReceipt = seed.Owned[1]; var releasedOnly = seed.Owned[2];
        await using (var setup = sql.NewContext())
        {
            var receipt = await CancelAsync(setup, seed.Owner, retainedReceipt);
            await setup.EnvironmentJobs.Where(x => x.Id == receipt.JobId).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Pending"));
            await setup.EnvironmentRequests.Where(x => x.Id == receipt.RequestId).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Pending"));
            await setup.TestEnvironments.Where(x => x.Id == receipt.EnvironmentId).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.State, "Requested").SetProperty(x => x.HasUnsettledProvisioningOperation, true));
            await setup.EnvironmentReservations.Where(x => x.Id == receipt.ReservationId).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Held"));
            await setup.EnvironmentRequests.Where(x => x.Id == missingReceipt.RequestId).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "cAnCeLlEd "));
            var reservationId = await setup.TestEnvironments.Where(x => x.Id == releasedOnly.EnvironmentId).Select(x => x.ReservationId).SingleAsync();
            await setup.EnvironmentReservations.Where(x => x.Id == reservationId).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Released"));
            var newest = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
            await setup.EnvironmentJobs.Where(x => x.Id == retainedReceipt.JobId || x.Id == missingReceipt.JobId || x.Id == releasedOnly.JobId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, newest));
        }
        var before = await DurableStateAsync(sql);
        await using var db = sql.NewContext(); var service = new SqlStageOperations(db);
        foreach (var state in new string?[] { null, "Pending" })
        {
            var first = await service.ListAsync(seed.Admin, new(1, State: state), Ct);
            var one = Assert.Single(first.Items); Assert.NotNull(first.NextCursor);
            var second = await service.ListAsync(seed.Admin, new(1, first.NextCursor, state), Ct);
            var two = Assert.Single(second.Items); Assert.Null(second.NextCursor);
            Assert.Equal(seed.Other.Select(x => x.JobId).Order(), new[] { one.JobId, two.JobId }.Order());
            Assert.Null(one.CancellationCode); Assert.Null(two.CancellationCode);
        }
        Assert.Empty((await service.ListAsync(seed.Admin, new(State: "Cancelled"), Ct)).Items);
        foreach (var order in new[] { retainedReceipt, missingReceipt, releasedOnly })
        {
            var cleanupError = await Assert.ThrowsAsync<RequestRejectedException>(() =>
                new SqlCleanupStatus(db).GetAsync(seed.Admin, order.EnvironmentId, Ct));
            Assert.Equal((409, "CleanupStatusInconsistent"), (cleanupError.StatusCode, cleanupError.Code));
        }
        Assert.Equal(before, await DurableStateAsync(sql));
    }

    [SqlServerFact]
    public async Task Scheduler_skips_oldest_order_with_a_cancellation_receipt_after_state_drift()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        await using var db = sql.NewContext();
        var order = seed.Owned[0];
        var receipt = await CancelAsync(db, seed.Owner, order);
        var oldest = DateTimeOffset.UnixEpoch;
        await db.EnvironmentJobs.Where(x => x.Id == order.JobId).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.State, "Pending").SetProperty(x => x.CreatedAt, oldest));
        await db.EnvironmentRequests.Where(x => x.Id == order.RequestId).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Pending"));
        await db.TestEnvironments.Where(x => x.Id == order.EnvironmentId).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.State, "Requested").SetProperty(x => x.HasUnsettledProvisioningOperation, true));
        await db.EnvironmentReservations.Where(x => x.Id == receipt.ReservationId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Held"));

        var control = new SqlAgentControl(db);
        const string name = "stage-agent";
        var enrollment = await control.CreateEnrollmentAsync(seed.Admin, new CreateEnrollment(seed.Agent, name, seed.Pool), Ct);
        await control.RegisterAsync(new RegisterAgent(seed.Agent, enrollment.EnrollmentId, enrollment.Token, name), Ct);
        var credential = await control.ApproveAsync(seed.Admin, seed.Agent, Ct);
        var grant = Assert.IsType<StageLeaseGrant>(await new SqlArchiveStageControl(db, new ArchiveStageOptions { Enabled = true })
            .PollAsync(credential.Token, Ct));
        Assert.Contains(grant.JobId, seed.Owned.Skip(1).Concat(seed.Other).Select(x => x.JobId));
        Assert.False(await db.AgentLeases.AnyAsync(x => x.JobId == order.JobId, Ct));
        Assert.False(await db.AuditEvents.AnyAsync(x => x.Action == "ArchiveStageLeased" && x.TargetId == order.JobId, Ct));
    }

    [SqlServerFact]
    public async Task Snapshot_and_authority_drift_are_not_recognized_by_list_or_detail()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var order = seed.Owned[0];
        await using var db = sql.NewContext(); var service = new SqlStageOperations(db);
        await CancelAsync(db, seed.Owner, order);
        var snapshot = await db.EnvironmentJobs.Where(x => x.Id == order.JobId).Select(x => x.SnapshotJson).SingleAsync();
        async Task AssertHiddenAsync()
        {
            Assert.DoesNotContain((await service.ListAsync(seed.Admin, new(), Ct)).Items, x => x.JobId == order.JobId);
            Assert.Empty((await service.ListAsync(seed.Admin, new(State: "Cancelled"), Ct)).Items);
            var error = await Assert.ThrowsAsync<RequestRejectedException>(() => new SqlStageStatus(db).GetAsync(seed.Admin, order.EnvironmentId, Ct));
            Assert.Equal(409, error.StatusCode);
        }
        var node = System.Text.Json.Nodes.JsonNode.Parse(snapshot)!.AsObject();
        node["stagingBytes"] = "10";
        var wrongType = node.ToJsonString();
        node["stagingBytes"] = 10;
        node["sourceArchiveId"] = Guid.NewGuid();
        var wrongSource = node.ToJsonString();
        foreach (var corrupt in new[] { "{bad", "[]", snapshot[..^1] + ",\"STAGINGBYTES\":10}", wrongType, wrongSource })
        {
            await db.EnvironmentJobs.Where(x => x.Id == order.JobId).ExecuteUpdateAsync(s => s.SetProperty(x => x.SnapshotJson, corrupt));
            await AssertHiddenAsync();
        }
        await db.EnvironmentJobs.Where(x => x.Id == order.JobId).ExecuteUpdateAsync(s => s.SetProperty(x => x.SnapshotJson, snapshot));
        db.AuditEvents.Add(new AuditEvent { Id = Guid.NewGuid(), Action = "ArchiveStageLeased", ActorId = seed.Agent,
            TargetId = order.JobId, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        await AssertHiddenAsync();
    }

    [SqlServerFact]
    public async Task Invalid_limits_states_and_cursors_are_rejected_as_bad_requests()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var before = await DurableStateAsync(sql);
        await using var db = sql.NewContext(); var service = new SqlStageOperations(db);
        foreach (var query in new[] { new StageOperationsQuery(0), new StageOperationsQuery(101),
            new StageOperationsQuery(State: "Ready"), new StageOperationsQuery(State: "pending"),
            new StageOperationsQuery(Cursor: "not-a-valid-cursor"), new StageOperationsQuery(Cursor: new string('x', 4096)) })
            Assert.Equal(400, (await Assert.ThrowsAsync<RequestRejectedException>(() => service.ListAsync(seed.Owner, query, Ct))).StatusCode);
        Assert.Equal(before, await DurableStateAsync(sql));
    }

    [SqlServerFact]
    public async Task Membership_revocation_and_disabled_projects_hide_owner_rows_but_not_admin_inspection()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        await using var db = sql.NewContext(); var service = new SqlStageOperations(db);
        Assert.Equal(3, (await service.ListAsync(seed.Owner, new StageOperationsQuery(), Ct)).Items.Count);
        await db.ProjectMemberships.Where(x => x.UserId == seed.Owner.UserId && x.ProjectId == seed.Project).ExecuteDeleteAsync();
        Assert.Empty((await service.ListAsync(seed.Owner, new StageOperationsQuery(), Ct)).Items);
        Assert.Equal(5, (await service.ListAsync(seed.Admin, new StageOperationsQuery(), Ct)).Items.Count);
        db.ProjectMemberships.Add(new ProjectMembership { UserId = seed.Owner.UserId, ProjectId = seed.Project }); await db.SaveChangesAsync();
        await db.Projects.Where(x => x.Id == seed.Project).ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false));
        Assert.Empty((await service.ListAsync(seed.Owner, new StageOperationsQuery(), Ct)).Items);
        Assert.Empty((await service.ListAsync(seed.Foreign, new StageOperationsQuery(), Ct)).Items);
        Assert.Equal(5, (await service.ListAsync(seed.Admin, new StageOperationsQuery(), Ct)).Items.Count);
    }

    [SqlServerFact]
    public async Task Page_and_items_are_exact_metadata_allowlists_and_reads_preserve_audit_and_state()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var before = await DurableStateAsync(sql);
        await using var db = sql.NewContext();
        var page = await new SqlStageOperations(db).ListAsync(seed.Owner, new StageOperationsQuery(2), Ct);
        var json = JsonSerializer.Serialize(page); using var doc = JsonDocument.Parse(json);
        Assert.Equal(new[] { "Items", "NextCursor", "ObservedAt" },
            doc.RootElement.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).ToArray());
        foreach (var item in doc.RootElement.GetProperty("Items").EnumerateArray())
            Assert.Equal(new[] { "CreatedAt", "EnvironmentId", "EnvironmentState", "JobId", "JobState", "RequestId", "ReservationState", "StagingBytes", "Unsettled" },
                item.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(seed.Owner.UserId.ToString(), json); Assert.DoesNotContain(seed.Agent.ToString(), json);
        Assert.DoesNotContain("private-source.dt", json); Assert.DoesNotContain(Hash, json);
        Assert.DoesNotContain("Snapshot", json); Assert.DoesNotContain("Token", json);
        Assert.Equal(before, await DurableStateAsync(sql));
    }
}
// end
