using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildSystem.IntegrationTests;

public sealed class SqlProjectBrowseTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private sealed record Seed(Actor Member, Actor Foreign, Actor Admin, Guid[] Projects, Guid Hidden, Guid Disabled);
    private static async Task<Seed> PrepareAsync(SqlServerTestDatabase sql)
    {
        await sql.ApplyMigrationsAsync();
        await using var db = sql.NewContext();
        var member = new Actor(Guid.NewGuid(), false); var foreign = new Actor(Guid.NewGuid(), false); var admin = new Actor(Guid.NewGuid(), true);
        db.Users.AddRange(new BuildSystemUser { Id = member.UserId, UserName = "member" },
            new BuildSystemUser { Id = foreign.UserId, UserName = "foreign" }, new BuildSystemUser { Id = admin.UserId, UserName = "admin" });
        var projects = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray(); var hidden = Guid.NewGuid(); var disabled = Guid.NewGuid();
        foreach (var id in projects)
        {
            db.Projects.Add(new Project { Id = id, Name = "same-name", Enabled = true });
            db.ProjectMemberships.Add(new ProjectMembership { UserId = member.UserId, ProjectId = id });
        }
        db.Projects.AddRange(new Project { Id = hidden, Name = "foreign-project", Enabled = true },
            new Project { Id = disabled, Name = "disabled-project", Enabled = false });
        db.ProjectMemberships.Add(new ProjectMembership { UserId = member.UserId, ProjectId = disabled });
        await db.SaveChangesAsync();
        return new(member, foreign, admin, projects, hidden, disabled);
    }
    private static async Task<ProjectBrowsePage> ListAsync(SqlServerTestDatabase sql, Seed seed, Actor? actor = null, ProjectBrowseQuery? query = null)
    {
        await using var db = sql.NewContext();
        return await new SqlProjectBrowse(db).ListAsync(actor ?? seed.Member, query ?? new(), Ct);
    }
    private static async Task RejectAsync(int status, Func<Task> action) =>
        Assert.Equal(status, (await Assert.ThrowsAsync<RequestRejectedException>(action)).StatusCode);
    private static async Task<string> StateAsync(SqlServerTestDatabase sql)
    {
        await using var db = sql.NewContext();
        return JsonSerializer.Serialize(new {
            Projects = await db.Projects.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            Memberships = await db.ProjectMemberships.AsNoTracking().OrderBy(x => x.UserId).ThenBy(x => x.ProjectId).ToListAsync(),
            Audits = await db.AuditEvents.AsNoTracking().OrderBy(x => x.Id).ToListAsync()
        });
    }

    [SqlServerFact]
    public async Task Member_lists_exact_enabled_memberships_admin_lists_enabled_and_revocation_applies()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var member = await ListAsync(sql, seed);
        Assert.Equal(seed.Projects.OrderBy(x => x.ToString("D"), StringComparer.Ordinal), member.Items.Select(x => x.ProjectId));
        Assert.Empty((await ListAsync(sql, seed, seed.Foreign)).Items);
        var admin = await ListAsync(sql, seed, seed.Admin);
        Assert.Equal(6, admin.Items.Count); Assert.Contains(admin.Items, x => x.ProjectId == seed.Hidden);
        Assert.DoesNotContain(admin.Items, x => x.ProjectId == seed.Disabled);
        await using var db = sql.NewContext();
        await db.ProjectMemberships.Where(x => x.UserId == seed.Member.UserId && x.ProjectId == seed.Projects[0]).ExecuteDeleteAsync();
        await db.Projects.Where(x => x.Id == seed.Projects[1]).ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false));
        var remaining = (await ListAsync(sql, seed)).Items;
        Assert.Equal(3, remaining.Count);
        Assert.DoesNotContain(remaining, x => x.ProjectId == seed.Projects[0] || x.ProjectId == seed.Projects[1]);
        Assert.Equal(5, (await ListAsync(sql, seed, seed.Admin)).Items.Count);
    }

    [SqlServerFact]
    public async Task Keyset_pages_cover_five_equal_name_projects_once_and_cursor_binds_actor_and_role()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var ids = new List<Guid>(); string? cursor = null; var pages = 0;
        do {
            var page = await ListAsync(sql, seed, query: new(2, cursor));
            Assert.InRange(page.Items.Count, 1, 2); Assert.All(page.Items, x => Assert.Equal("same-name", x.Name));
            ids.AddRange(page.Items.Select(x => x.ProjectId)); cursor = page.NextCursor;
            pages++; Assert.InRange(pages, 1, 3);
        } while (cursor is not null);
        Assert.Equal(3, pages); Assert.Equal(5, ids.Distinct().Count());
        Assert.Equal(seed.Projects.OrderBy(x => x.ToString("D"), StringComparer.Ordinal), ids);
        var first = await ListAsync(sql, seed, query: new(2)); Assert.NotNull(first.NextCursor);
        await RejectAsync(400, () => ListAsync(sql, seed, seed.Foreign, new(2, first.NextCursor)));
        await RejectAsync(400, () => ListAsync(sql, seed, new Actor(seed.Member.UserId, true), new(2, first.NextCursor)));
    }

    [SqlServerFact]
    public async Task Long_and_hostile_names_are_bounded_literal_metadata_with_exact_two_field_items()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        const string hostile = "<img src=x onerror=alert(1)><script>alert('x')</script>";
        await using var db = sql.NewContext();
        await db.Projects.Where(x => x.Id == seed.Projects[0]).ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, new string('N', 1024)));
        await db.Projects.Where(x => x.Id == seed.Projects[1]).ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, hostile));
        var page = await ListAsync(sql, seed);
        Assert.Equal(new string('N', 256), page.Items.Single(x => x.ProjectId == seed.Projects[0]).Name);
        Assert.Equal(hostile, page.Items.Single(x => x.ProjectId == seed.Projects[1]).Name);
        Assert.All(page.Items, x => Assert.InRange(x.Name.Length, 0, 256));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(page));
        Assert.Equal(new[] { "Items", "NextCursor", "ObservedAt" }, json.RootElement.EnumerateObject().Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal));
        foreach (var row in json.RootElement.GetProperty("Items").EnumerateArray())
            Assert.Equal(new[] { "Name", "ProjectId" }, row.EnumerateObject().Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal));
        var literal = json.RootElement.GetProperty("Items").EnumerateArray().Single(x => x.GetProperty("ProjectId").GetGuid() == seed.Projects[1]);
        Assert.Equal(hostile, literal.GetProperty("Name").GetString());
        // JSON literal metadata only; browser DOM escaping is verified separately by UI tests.
    }

    [SqlServerFact]
    public async Task Reads_preserve_state_and_audit_and_invalid_queries_fail_before_sql_access()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var before = await StateAsync(sql); var page = await ListAsync(sql, seed, query: new(2));
        Assert.True(page.ObservedAt > DateTimeOffset.MinValue);
        Assert.Equal(before, await StateAsync(sql));
        var invalid = new[] { new ProjectBrowseQuery(0), new ProjectBrowseQuery(101), new ProjectBrowseQuery(25, "invalid"), new ProjectBrowseQuery(25, ""), new ProjectBrowseQuery(25, new string('A', 513)) };
        await using var unused = new BuildSystemDbContext(new DbContextOptionsBuilder<BuildSystemDbContext>().UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
        foreach (var query in invalid) {
            await RejectAsync(400, () => new SqlProjectBrowse(unused).ListAsync(seed.Member, query, Ct));
            await RejectAsync(400, () => ListAsync(sql, seed, query: query));
        }
        Assert.Equal(before, await StateAsync(sql));
    }
}
// end
