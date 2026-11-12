using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildSystem.IntegrationTests;

// Disposable metadata SQL only; no executor, agent or external provisioning target is contacted.
public sealed class EnvironmentHistoryVisibilityTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [SqlServerFact]
    public async Task Owner_history_requires_current_project_membership_even_after_order_acceptance()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(database);
        OrderReceipt receipt;
        await using (var db = database.NewContext())
            receipt = await new SqlEnvironmentOrders(db).CreateAsync(seed.Owner, "owner-history", seed.Order(), Ct);

        await using var reader = database.NewContext();
        var service = new SqlEnvironmentOrders(reader);
        var environment = Assert.Single(await service.ListEnvironmentsAsync(seed.Owner, Ct));
        Assert.Equal(receipt.EnvironmentId, environment.Id);
        Assert.Equal(seed.Project, environment.ProjectId);
        Assert.Equal("Requested", environment.State);
        Assert.Equal(environment, await service.GetEnvironmentAsync(seed.Owner, receipt.EnvironmentId, Ct));
        var request = await service.GetRequestAsync(seed.Owner, receipt.RequestId, Ct);
        Assert.NotNull(request);
        Assert.Equal(seed.Project, request.ProjectId);
        Assert.Equal(receipt.EnvironmentId, request.EnvironmentId);
        Assert.Equal(receipt.JobId, request.JobId);
        var before = await OrderStateAsync(database);

        await using (var db = database.NewContext())
        {
            db.ProjectMemberships.Remove(await db.ProjectMemberships.SingleAsync(x =>
                x.UserId == seed.Owner.UserId && x.ProjectId == seed.Project));
            await db.SaveChangesAsync();
        }

        // Reuse the already-open reader and actor: visibility must consult current SQL membership.
        Assert.Empty(await service.ListEnvironmentsAsync(seed.Owner, Ct));
        Assert.Null(await service.GetEnvironmentAsync(seed.Owner, receipt.EnvironmentId, Ct));
        Assert.Null(await service.GetRequestAsync(seed.Owner, receipt.RequestId, Ct));
        Assert.Equal(before, await OrderStateAsync(database));
    }

    [SqlServerFact]
    public async Task Administrator_without_project_membership_reads_other_owners_history_and_exact_project_metadata()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(database);
        OrderReceipt receipt;
        await using (var db = database.NewContext())
            receipt = await new SqlEnvironmentOrders(db).CreateAsync(seed.Owner, "admin-history", seed.Order(), Ct);

        await using (var db = database.NewContext())
        {
            db.ProjectMemberships.Remove(await db.ProjectMemberships.SingleAsync(x =>
                x.UserId == seed.Administrator.UserId && x.ProjectId == seed.Project));
            await db.SaveChangesAsync();
        }

        await using var check = database.NewContext();
        Assert.False(await check.ProjectMemberships.AnyAsync(x => x.UserId == seed.Administrator.UserId));
        Assert.NotEqual(seed.Owner.UserId, seed.Administrator.UserId);
        var service = new SqlEnvironmentOrders(check);
        var expectedEnvironment = await service.GetEnvironmentAsync(seed.Owner, receipt.EnvironmentId, Ct);
        var expectedRequest = await service.GetRequestAsync(seed.Owner, receipt.RequestId, Ct);
        Assert.NotNull(expectedEnvironment);
        Assert.NotNull(expectedRequest);
        var before = await OrderStateAsync(database);

        // This API returns the full visible history; the UI display cap is not server pagination.
        var listed = Assert.Single(await service.ListEnvironmentsAsync(seed.Administrator, Ct));
        Assert.Equal(expectedEnvironment, listed);
        Assert.Equal(receipt.EnvironmentId, listed.Id);
        Assert.Equal(seed.Project, listed.ProjectId);
        Assert.Equal("Requested", listed.State);
        var environment = await service.GetEnvironmentAsync(seed.Administrator, receipt.EnvironmentId, Ct);
        Assert.Equal(expectedEnvironment, environment);
        Assert.Equal(seed.Project, environment!.ProjectId);
        var request = await service.GetRequestAsync(seed.Administrator, receipt.RequestId, Ct);
        Assert.Equal(expectedRequest, request);
        Assert.Equal(receipt.RequestId, request!.Id);
        Assert.Equal(receipt.EnvironmentId, request.EnvironmentId);
        Assert.Equal(receipt.JobId, request.JobId);
        Assert.Equal(seed.Project, request.ProjectId);
        Assert.Equal("history-order", request.DisplayName);
        Assert.Equal(before, await OrderStateAsync(database));
    }

    private sealed record Seed(Actor Owner, Actor Administrator, Guid Project, Guid Version)
    {
        public CreateEnvironmentRequest Order() => new(Project, "history-order", "archive", new ArchiveSource(Version), 7);
    }

    // SqlEnvironmentOrderTests.PrepareAsync is private: keep the same minimal admitted-order FK graph here.
    private static async Task<Seed> PrepareAsync(SqlServerTestDatabase database)
    {
        await database.ApplyMigrationsAsync();
        var seed = new Seed(new Actor(Guid.NewGuid(), false), new Actor(Guid.NewGuid(), true), Guid.NewGuid(), Guid.NewGuid());
        var root = Guid.NewGuid();
        var archive = Guid.NewGuid();
        var pool = Guid.NewGuid();
        var scope = Guid.NewGuid();
        await using var db = database.NewContext();
        db.Users.AddRange(new BuildSystemUser { Id = seed.Owner.UserId, UserName = "owner", NormalizedUserName = "OWNER" },
            new BuildSystemUser { Id = seed.Administrator.UserId, UserName = "admin", NormalizedUserName = "ADMIN" });
        // A legitimate fixture administrator; IEnvironmentOrders trusts Actor.IsAdministrator (the cookie claim).
        // Do not infer a database-role recheck or role-revocation contract for this API.
        var role = await db.Roles.SingleOrDefaultAsync(x => x.NormalizedName == "ADMINISTRATOR")
            ?? new IdentityRole<Guid> { Id = Guid.NewGuid(), Name = "Administrator", NormalizedName = "ADMINISTRATOR" };
        if (db.Entry(role).State == EntityState.Detached) db.Roles.Add(role);
        db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = seed.Administrator.UserId, RoleId = role.Id });
        db.Projects.Add(new Project { Id = seed.Project, Name = "history-project", IsolationPolicyId = "isolated", PreparationProfileVersion = "v1" });
        db.ProjectMemberships.AddRange(new ProjectMembership { UserId = seed.Owner.UserId, ProjectId = seed.Project },
            new ProjectMembership { UserId = seed.Administrator.UserId, ProjectId = seed.Project });
        db.ArchiveRoots.Add(new ArchiveRoot { Id = root, Enabled = true, AgentScopeId = scope, IsolationPolicyId = "isolated", MaxArchiveBytes = 10000, MaxStagingBytes = 10000 });
        db.ArchiveRootProjects.Add(new ArchiveRootProject { RootId = root, ProjectId = seed.Project });
        db.DiskArchives.Add(new DiskArchive { Id = archive, RootId = root, RelativePath = "sample.dt" });
        db.DiskArchiveVersions.Add(new DiskArchiveVersion { Id = seed.Version, ArchiveId = archive, Sha256 = new string('A', 64), SizeBytes = 100, Format = "dt", State = "Ready", EstimatedDbBytes = 1000 });
        db.EnvironmentPools.Add(new EnvironmentPool { Id = pool, Enabled = true, ClusterId = Guid.NewGuid(), SqlServerId = Guid.NewGuid(), AgentScopeId = scope,
            IsolationPolicyId = "isolated", CapacityBytes = 100000, MaxEnvironmentBytes = 10000, MaxStagingBytes = 10000, MaxConcurrentProvisioning = 2,
            SupportsDt = true, SupportsBak = true, IsolationVerified = true, ScheduledJobsSuppressionVerified = true, ResourceLimitVerified = true });
        db.ProjectPools.Add(new ProjectPool { ProjectId = seed.Project, PoolId = pool, Enabled = true });
        db.EnvironmentQuotas.Add(new EnvironmentQuota { SubjectId = seed.Owner.UserId, MaxActiveEnvironments = 2, MaxConcurrentProvisioning = 2, MaxStorageBytes = 100000, MaxTtlDays = 30 });
        await db.SaveChangesAsync();
        return seed;
    }

    private static async Task<string> OrderStateAsync(SqlServerTestDatabase database)
    {
        await using var db = database.NewContext();
        // Full persisted rows, including rowversion and immutable job snapshot, must stay unchanged.
        return JsonSerializer.Serialize(new
        {
            Environment = await db.TestEnvironments.AsNoTracking().SingleAsync(),
            Request = await db.EnvironmentRequests.AsNoTracking().SingleAsync(),
            Reservation = await db.EnvironmentReservations.AsNoTracking().SingleAsync(),
            Job = await db.EnvironmentJobs.AsNoTracking().SingleAsync(),
            Audit = await db.AuditEvents.AsNoTracking().SingleAsync()
        });
    }
}
