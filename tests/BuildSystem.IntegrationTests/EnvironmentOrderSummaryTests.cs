using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildSystem.IntegrationTests;

public sealed class EnvironmentOrderSummaryTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private sealed record Seed(Actor Owner, Actor Foreign, Actor Admin, Guid Project, Guid Root, Guid Version,
        OrderReceipt Provision, OrderReceipt Stage);

    private static async Task<Seed> PrepareAsync(SqlServerTestDatabase sql)
    {
        await sql.ApplyMigrationsAsync();
        var owner = new Actor(Guid.NewGuid(), false); var foreign = new Actor(Guid.NewGuid(), false);
        var admin = new Actor(Guid.NewGuid(), true);
        var project = Guid.NewGuid(); var root = Guid.NewGuid(); var pool = Guid.NewGuid();
        var archive = Guid.NewGuid(); var version = Guid.NewGuid(); var agent = Guid.NewGuid();
        await using var db = sql.NewContext();
        db.Users.AddRange(new BuildSystemUser { Id = owner.UserId, UserName = "summary-owner" },
            new BuildSystemUser { Id = foreign.UserId, UserName = "summary-foreign" },
            new BuildSystemUser { Id = admin.UserId, UserName = "summary-admin" });
        db.Projects.Add(new Project { Id = project, Name = "project", Enabled = true, IsolationPolicyId = "isolated", PreparationProfileVersion = "v1" });
        db.ProjectMemberships.AddRange(new ProjectMembership { UserId = owner.UserId, ProjectId = project },
            new ProjectMembership { UserId = foreign.UserId, ProjectId = project });
        db.ArchiveRoots.Add(new ArchiveRoot { Id = root, Enabled = true, AgentScopeId = agent, IsolationPolicyId = "isolated", MaxArchiveBytes = 10000, MaxStagingBytes = 10000 });
        db.ArchiveRootProjects.Add(new ArchiveRootProject { RootId = root, ProjectId = project });
        db.DiskArchives.Add(new DiskArchive { Id = archive, RootId = root, RelativePath = "private-source.dt" });
        db.DiskArchiveVersions.Add(new DiskArchiveVersion { Id = version, ArchiveId = archive, Sha256 = new string('A', 64),
            SizeBytes = 100, Format = "dt", State = "Ready", EstimatedDbBytes = 1000 });
        db.EnvironmentPools.Add(new EnvironmentPool { Id = pool, Enabled = true, ClusterId = Guid.NewGuid(), SqlServerId = Guid.NewGuid(),
            AgentScopeId = agent, IsolationPolicyId = "isolated", CapacityBytes = 100000, MaxEnvironmentBytes = 10000,
            MaxStagingBytes = 10000, MaxConcurrentProvisioning = 2, SupportsDt = true, IsolationVerified = true,
            ScheduledJobsSuppressionVerified = true, ResourceLimitVerified = true });
        db.ProjectPools.Add(new ProjectPool { ProjectId = project, PoolId = pool, Enabled = true });
        db.EnvironmentQuotas.Add(new EnvironmentQuota { SubjectId = owner.UserId, MaxActiveEnvironments = 2,
            MaxConcurrentProvisioning = 2, MaxStorageBytes = 100000, MaxTtlDays = 30 });
        await db.SaveChangesAsync();
        var orders = new SqlEnvironmentOrders(db);
        var request = new CreateEnvironmentRequest(project, "same-name", "archive", new(version), 7);
        var provision = await orders.CreateAsync(owner, "provision", request, Ct);
        var stage = await orders.CreateAsync(owner, "stage", request with { Mode = "archive-stage-only" }, Ct);
        return new(owner, foreign, admin, project, root, version, provision, stage);
    }

    private static async Task<EnvironmentOrderSummaryView?> ReadAsync(SqlServerTestDatabase sql, Actor actor, Guid id)
    {
        await using var db = sql.NewContext();
        var result = await new SqlEnvironmentOrderSummary(db).GetAsync(actor, id, Ct);
        Assert.Empty(db.ChangeTracker.Entries());
        return result;
    }

    private static async Task<string> StateAsync(SqlServerTestDatabase sql)
    {
        await using var db = sql.NewContext();
        return JsonSerializer.Serialize(new {
            Environments = await db.TestEnvironments.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            Requests = await db.EnvironmentRequests.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            Jobs = await db.EnvironmentJobs.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            Reservations = await db.EnvironmentReservations.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            Leases = await db.AgentLeases.AsNoTracking().OrderBy(x => x.JobId).ToListAsync(),
            Artifacts = await db.StagedArtifacts.AsNoTracking().OrderBy(x => x.ArtifactId).ToListAsync(),
            Audits = await db.AuditEvents.AsNoTracking().OrderBy(x => x.Id).ToListAsync()
        });
    }

    [Fact]
    public async Task Invalid_actor_and_environment_reject_before_provider_connection()
    {
        await using var db = new BuildSystemDbContext(new DbContextOptionsBuilder<BuildSystemDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
        var summary = new SqlEnvironmentOrderSummary(db);
        foreach (var actor in new Actor[] { null!, new(Guid.Empty, true) })
            Assert.Equal(403, (await Assert.ThrowsAsync<RequestRejectedException>(() => summary.GetAsync(actor, Guid.Empty, Ct))).StatusCode);
        var invalid = await Assert.ThrowsAsync<RequestRejectedException>(() =>
            summary.GetAsync(new(Guid.NewGuid(), false), Guid.Empty, Ct));
        Assert.Equal(400, invalid.StatusCode); Assert.Equal("InvalidEnvironmentOrderSummary", invalid.Code);
    }

    [SqlServerFact]
    public async Task Both_order_kinds_keep_exact_historical_identity_and_current_visibility_without_mutations()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        await using (var db = sql.NewContext())
        {
            await db.Projects.Where(x => x.Id == seed.Project).ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false));
            await db.ArchiveRoots.Where(x => x.Id == seed.Root).ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false));
            await db.ArchiveRootProjects.Where(x => x.RootId == seed.Root).ExecuteDeleteAsync();
            await db.DiskArchiveVersions.Where(x => x.Id == seed.Version).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Unavailable"));
            await db.TestEnvironments.ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddDays(-1)));
            await db.EnvironmentJobs.ExecuteUpdateAsync(s => s.SetProperty(x => x.SnapshotJson, "not-json-secret"));
        }
        var before = await StateAsync(sql);
        foreach (var (receipt, kind) in new[] { (seed.Provision, "ProvisionArchive"), (seed.Stage, "ObserveArchiveStage") })
        {
            var view = Assert.IsType<EnvironmentOrderSummaryView>(await ReadAsync(sql, seed.Owner, receipt.EnvironmentId));
            Assert.Equal(receipt.EnvironmentId, view.EnvironmentId); Assert.Equal(seed.Project, view.ProjectId);
            Assert.Equal(receipt.RequestId, view.RequestId); Assert.Equal(receipt.JobId, view.JobId);
            Assert.Equal("same-name", view.DisplayName); Assert.Equal(kind, view.JobKind);
            Assert.Equal("Requested", view.EnvironmentState); Assert.Equal("Pending", view.RequestState); Assert.Equal("Pending", view.JobState);
            Assert.Equal(7, view.TtlDays); Assert.True(view.CreatedAt > DateTimeOffset.UnixEpoch); Assert.Equal(TimeSpan.Zero, view.CreatedAt.Offset);
            Assert.Equal(new EnvironmentOrderSourceView(seed.Version, "dt", new string('A', 64), 100), view.Source);
            Assert.Equal(view, await ReadAsync(sql, seed.Admin, receipt.EnvironmentId));
            Assert.Null(await ReadAsync(sql, seed.Foreign, receipt.EnvironmentId));
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(view));
            Assert.Equal(new[] { "CreatedAt", "DisplayName", "EnvironmentId", "EnvironmentState", "JobId", "JobKind", "JobState",
                "ProjectId", "RequestId", "RequestState", "Source", "TtlDays" },
                json.RootElement.EnumerateObject().Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal));
            Assert.Equal(new[] { "ArchiveVersionId", "Format", "Sha256", "SizeBytes" },
                json.RootElement.GetProperty("Source").EnumerateObject().Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal));
        }
        Assert.Null(await ReadAsync(sql, seed.Admin, Guid.NewGuid()));
        Assert.Equal(before, await StateAsync(sql));
        await using (var db = sql.NewContext())
            await db.ProjectMemberships.Where(x => x.UserId == seed.Owner.UserId).ExecuteDeleteAsync();
        before = await StateAsync(sql);
        Assert.Null(await ReadAsync(sql, seed.Owner, seed.Provision.EnvironmentId));
        Assert.NotNull(await ReadAsync(sql, seed.Admin, seed.Provision.EnvironmentId));
        Assert.Equal(before, await StateAsync(sql));
    }

    [SqlServerFact]
    public async Task Broken_reciprocal_bindings_and_invalid_metadata_fail_closed_with_fixed_conflict()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var receipt = seed.Provision;
        // Each corruption is persisted with fixture SQL and undone after proving reads are side-effect free.
        var corruptions = new (Func<BuildSystemDbContext, Task> Corrupt, Func<BuildSystemDbContext, Task> Restore)[] {
            (db => db.EnvironmentRequests.Where(x => x.Id == receipt.RequestId).ExecuteUpdateAsync(s => s.SetProperty(x => x.EnvironmentId, seed.Stage.EnvironmentId)),
             db => db.EnvironmentRequests.Where(x => x.Id == receipt.RequestId).ExecuteUpdateAsync(s => s.SetProperty(x => x.EnvironmentId, receipt.EnvironmentId))),
            (db => db.EnvironmentRequests.Where(x => x.Id == receipt.RequestId).ExecuteUpdateAsync(s => s.SetProperty(x => x.OwnerId, seed.Foreign.UserId)),
             db => db.EnvironmentRequests.Where(x => x.Id == receipt.RequestId).ExecuteUpdateAsync(s => s.SetProperty(x => x.OwnerId, seed.Owner.UserId))),
            (db => db.EnvironmentRequests.Where(x => x.Id == receipt.RequestId).ExecuteUpdateAsync(s => s.SetProperty(x => x.JobId, seed.Stage.JobId)),
             db => db.EnvironmentRequests.Where(x => x.Id == receipt.RequestId).ExecuteUpdateAsync(s => s.SetProperty(x => x.JobId, receipt.JobId))),
            (db => db.EnvironmentJobs.Where(x => x.Id == receipt.JobId).ExecuteUpdateAsync(s => s.SetProperty(x => x.EnvironmentId, seed.Stage.EnvironmentId)),
             db => db.EnvironmentJobs.Where(x => x.Id == receipt.JobId).ExecuteUpdateAsync(s => s.SetProperty(x => x.EnvironmentId, receipt.EnvironmentId))),
            (db => db.EnvironmentJobs.Where(x => x.Id == receipt.JobId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Kind, "OtherJob")),
             db => db.EnvironmentJobs.Where(x => x.Id == receipt.JobId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Kind, "ProvisionArchive"))),
            (db => db.TestEnvironments.Where(x => x.Id == receipt.EnvironmentId).ExecuteUpdateAsync(s => s.SetProperty(x => x.DisplayName, new string('N', 121))),
             db => db.TestEnvironments.Where(x => x.Id == receipt.EnvironmentId).ExecuteUpdateAsync(s => s.SetProperty(x => x.DisplayName, "same-name"))),
            (db => db.EnvironmentRequests.Where(x => x.Id == receipt.RequestId).ExecuteUpdateAsync(s => s.SetProperty(x => x.TtlDays, 0)),
             db => db.EnvironmentRequests.Where(x => x.Id == receipt.RequestId).ExecuteUpdateAsync(s => s.SetProperty(x => x.TtlDays, 7)))
        };
        foreach (var (corrupt, restore) in corruptions)
        {
            await using (var db = sql.NewContext()) await corrupt(db);
            var before = await StateAsync(sql);
            var error = await Assert.ThrowsAsync<RequestRejectedException>(() => ReadAsync(sql, seed.Owner, receipt.EnvironmentId));
            Assert.Equal(409, error.StatusCode); Assert.Equal("EnvironmentOrderSummaryInconsistent", error.Code);
            Assert.Equal(error.Code, error.Message);
            Assert.Null(await ReadAsync(sql, seed.Foreign, receipt.EnvironmentId));
            Assert.Equal(before, await StateAsync(sql));
            await using (var db = sql.NewContext()) await restore(db);
        }
        Assert.NotNull(await ReadAsync(sql, seed.Owner, receipt.EnvironmentId));
    }
}
// EOF
