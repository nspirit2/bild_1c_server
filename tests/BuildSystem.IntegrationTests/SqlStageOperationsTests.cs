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
