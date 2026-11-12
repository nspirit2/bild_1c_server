using System.Text;
using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildSystem.IntegrationTests;

public sealed class SqlEnvironmentBrowseTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private sealed record Seed(Actor Owner, Actor Foreign, Actor Admin, Guid Project, Guid[] OwnerIds, Guid[] ForeignIds);

    private static async Task<Seed> PrepareAsync(SqlServerTestDatabase sql)
    {
        await sql.ApplyMigrationsAsync();
        var owner = new Actor(Guid.NewGuid(), false);
        var foreign = new Actor(Guid.NewGuid(), false);
        var admin = new Actor(Guid.NewGuid(), true);
        var project = Guid.NewGuid(); var root = Guid.NewGuid(); var archive = Guid.NewGuid();
        var version = Guid.NewGuid(); var pool = Guid.NewGuid();
        // Text order intentionally differs from SQL uniqueidentifier order (which prioritizes trailing bytes).
        var ids = new[] { "10000000-0000-0000-0000-000000000005", "20000000-0000-0000-0000-000000000004",
            "30000000-0000-0000-0000-000000000003", "40000000-0000-0000-0000-000000000002",
            "50000000-0000-0000-0000-000000000001" }.Select(Guid.Parse).ToArray();
        var others = Enumerable.Range(0, 2).Select(_ => Guid.NewGuid()).ToArray();
        await using var db = sql.NewContext();
        db.Users.AddRange(new BuildSystemUser { Id = owner.UserId, UserName = "browse-owner" },
            new BuildSystemUser { Id = foreign.UserId, UserName = "browse-foreign" },
            new BuildSystemUser { Id = admin.UserId, UserName = "browse-admin" });
        // Deliberately disabled project: historical reads depend on current membership, not Enabled.
        db.Projects.Add(new Project { Id = project, Name = "browse-project", Enabled = false });
        db.ProjectMemberships.Add(new ProjectMembership { ProjectId = project, UserId = owner.UserId });
        db.ArchiveRoots.Add(new ArchiveRoot { Id = root });
        db.DiskArchives.Add(new DiskArchive { Id = archive, RootId = root, RelativePath = "browse.dt" });
        db.DiskArchiveVersions.Add(new DiskArchiveVersion { Id = version, ArchiveId = archive, Sha256 = new string('A', 64) });
        db.EnvironmentPools.Add(new EnvironmentPool { Id = pool });
        await db.SaveChangesAsync();
        foreach (var id in ids.Concat(others))
        {
            var user = ids.Contains(id) ? owner.UserId : foreign.UserId;
            var request = Guid.NewGuid(); var reservation = Guid.NewGuid();
            db.EnvironmentRequests.Add(new EnvironmentRequest { Id = request, OwnerId = user, ProjectId = project,
                EnvironmentId = id, JobId = Guid.NewGuid(), ArchiveVersionId = version, IdempotencyKey = id.ToString("N"),
                DisplayName = "browse-name", CreatedAt = DateTimeOffset.UtcNow });
            db.EnvironmentReservations.Add(new EnvironmentReservation { Id = reservation, OwnerId = user, PoolId = pool,
                SqlServerId = Guid.NewGuid(), ClusterId = Guid.NewGuid(), SqlDbName = "db_" + id.ToString("N"),
                InfobaseName = "ib_" + id.ToString("N") });
            db.TestEnvironments.Add(new EnvironmentRecord { Id = id, RequestId = request, ReservationId = reservation,
                OwnerId = user, ProjectId = project, PoolId = pool, DisplayName = "browse-name" });
        }
        await db.SaveChangesAsync();
        return new(owner, foreign, admin, project, ids, others);
    }

    private static async Task<EnvironmentBrowsePage> ListAsync(SqlServerTestDatabase sql, Actor actor, EnvironmentBrowseQuery? query = null)
    {
        await using var db = sql.NewContext();
        return await new SqlEnvironmentBrowse(db).ListAsync(actor, query ?? new(), Ct);
    }

    private static async Task RejectAsync(int status, Func<Task> action) =>
        Assert.Equal(status, (await Assert.ThrowsAsync<RequestRejectedException>(action)).StatusCode);

    private static async Task<string> StateAsync(SqlServerTestDatabase sql)
    {
        await using var db = sql.NewContext();
        return JsonSerializer.Serialize(new {
            Environments = await db.TestEnvironments.AsNoTracking().OrderBy(e => e.Id).ToListAsync(),
            Requests = await db.EnvironmentRequests.AsNoTracking().OrderBy(e => e.Id).ToListAsync(),
            Reservations = await db.EnvironmentReservations.AsNoTracking().OrderBy(e => e.Id).ToListAsync(),
            Audits = await db.AuditEvents.AsNoTracking().OrderBy(e => e.Id).ToListAsync()
        });
    }

    [Fact]
    public async Task Invalid_actor_limits_and_cursors_reject_before_provider_connection()
    {
        await using var unused = new BuildSystemDbContext(new DbContextOptionsBuilder<BuildSystemDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
        var browse = new SqlEnvironmentBrowse(unused);
        var actor = new Actor(Guid.NewGuid(), false);
        await RejectAsync(403, () => browse.ListAsync(null!, new(), Ct));
        await RejectAsync(403, () => browse.ListAsync(new Actor(Guid.Empty, true), new(), Ct));
        await RejectAsync(400, () => browse.ListAsync(actor, null!, Ct));
        await RejectAsync(400, () => browse.ListAsync(actor, new(0), Ct));
        await RejectAsync(400, () => browse.ListAsync(actor, new(101), Ct));
        await RejectAsync(400, () => browse.ListAsync(actor, new(25, ""), Ct));
        await RejectAsync(400, () => browse.ListAsync(actor, new(25, new string('A', 513)), Ct));
        await RejectAsync(400, () => browse.ListAsync(actor, new(25, "not+base64"), Ct));
        await RejectAsync(400, () => browse.ListAsync(actor, new(25, "AA"), Ct));
        var unknown = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { Version = 1, EnvironmentId = Guid.NewGuid(), ActorId = actor.UserId, Administrator = false, Extra = 1 })))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await RejectAsync(400, () => browse.ListAsync(actor, new(25, unknown), Ct));
    }

    [SqlServerFact]
    public async Task Keyset_pages_cover_exact_owner_history_once_and_bind_cursor_to_actor_and_role()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var before = await StateAsync(sql);
        var seen = new List<Guid>(); string? cursor = null; var pages = 0;
        do
        {
            var page = await ListAsync(sql, seed.Owner, new(2, cursor));
            Assert.InRange(page.Items.Count, 1, 2);
            Assert.True(page.ObservedAt > DateTimeOffset.MinValue);
            Assert.All(page.Items, item => { Assert.Equal(seed.Project, item.ProjectId); Assert.Equal("Requested", item.State); });
            seen.AddRange(page.Items.Select(item => item.Id)); cursor = page.NextCursor;
            pages++; Assert.InRange(pages, 1, 3);
        } while (cursor is not null);
        Assert.Equal(3, pages);
        Assert.Equal(seed.OwnerIds.OrderBy(id => id.ToString("D"), StringComparer.Ordinal), seen);
        Assert.Equal(5, seen.Distinct().Count());
        var first = await ListAsync(sql, seed.Owner, new(2));
        Assert.NotNull(first.NextCursor);
        await RejectAsync(400, () => ListAsync(sql, seed.Foreign, new(2, first.NextCursor)));
        await RejectAsync(400, () => ListAsync(sql, new Actor(seed.Owner.UserId, true), new(2, first.NextCursor)));
        await RejectAsync(400, () => ListAsync(sql, seed.Owner, new(2, first.NextCursor + "=")));
        Assert.Equal(before, await StateAsync(sql));
    }

    [SqlServerFact]
    public async Task Current_membership_revocation_applies_on_second_page_and_foreign_owner_is_isolated()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        Assert.Empty((await ListAsync(sql, seed.Foreign)).Items);
        var first = await ListAsync(sql, seed.Owner, new(2));
        Assert.NotNull(first.NextCursor);
        await using (var db = sql.NewContext())
            await db.ProjectMemberships.Where(m => m.ProjectId == seed.Project && m.UserId == seed.Owner.UserId).ExecuteDeleteAsync();
        Assert.Empty((await ListAsync(sql, seed.Owner, new(2, first.NextCursor))).Items);
        Assert.Null((await ListAsync(sql, seed.Owner, new(2, first.NextCursor))).NextCursor);
        Assert.Empty((await ListAsync(sql, seed.Owner)).Items);
    }

    [SqlServerFact]
    public async Task Administrator_sees_all_owners_without_membership_and_display_name_remains_bounded_metadata()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        const string hostile = "<img src=x onerror=alert(1)>";
        await using (var db = sql.NewContext())
        {
            await db.TestEnvironments.Where(e => e.Id == seed.OwnerIds[0]).ExecuteUpdateAsync(s => s.SetProperty(e => e.DisplayName, new string('N', 1024)));
            await db.TestEnvironments.Where(e => e.Id == seed.ForeignIds[0]).ExecuteUpdateAsync(s => s.SetProperty(e => e.DisplayName, hostile));
        }
        var ids = new List<Guid>(); var items = new List<EnvironmentView>(); string? cursor = null;
        do
        {
            var page = await ListAsync(sql, seed.Admin, new(3, cursor));
            Assert.InRange(page.Items.Count, 1, 3);
            items.AddRange(page.Items); ids.AddRange(page.Items.Select(item => item.Id)); cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(seed.OwnerIds.Concat(seed.ForeignIds).OrderBy(id => id.ToString("D"), StringComparer.Ordinal), ids);
        Assert.Equal(new string('N', 120), items.Single(item => item.Id == seed.OwnerIds[0]).DisplayName);
        Assert.Equal(hostile, items.Single(item => item.Id == seed.ForeignIds[0]).DisplayName);
        Assert.All(items, item => Assert.InRange(item.DisplayName.Length, 1, 120));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(await ListAsync(sql, seed.Admin)));
        Assert.Equal(new[] { "Items", "NextCursor", "ObservedAt" }, json.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        foreach (var item in json.RootElement.GetProperty("Items").EnumerateArray())
            Assert.Equal(new[] { "DisplayName", "ExpiresAt", "Id", "ProjectId", "State" }, item.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
    }
}
// EOF
