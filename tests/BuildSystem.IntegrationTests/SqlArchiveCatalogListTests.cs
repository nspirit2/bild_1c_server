using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildSystem.IntegrationTests;

public sealed class SqlArchiveCatalogListTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private sealed record Seed(Actor Member, Actor Foreign, Actor Admin, Guid Project, Guid Root, Guid Archive, Guid[] Versions);
    private static async Task<Seed> PrepareAsync(SqlServerTestDatabase sql)
    {
        await sql.ApplyMigrationsAsync();
        await using var db = sql.NewContext();
        var member = new Actor(Guid.NewGuid(), false); var foreign = new Actor(Guid.NewGuid(), false); var admin = new Actor(Guid.NewGuid(), true);
        var project = Guid.NewGuid(); var root = Guid.NewGuid(); var archive = Guid.NewGuid();
        var versions = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        db.Users.AddRange(new BuildSystemUser { Id = member.UserId, UserName = "member" },
            new BuildSystemUser { Id = foreign.UserId, UserName = "foreign" }, new BuildSystemUser { Id = admin.UserId, UserName = "admin" });
        db.Projects.Add(new Project { Id = project, Name = "catalog-project", Enabled = true });
        db.ProjectMemberships.Add(new ProjectMembership { UserId = member.UserId, ProjectId = project });
        db.ArchiveRoots.Add(new ArchiveRoot { Id = root, Enabled = true, AgentScopeId = Guid.NewGuid(), MaxArchiveBytes = 100000, MaxStagingBytes = 100000 });
        db.ArchiveRootProjects.Add(new ArchiveRootProject { RootId = root, ProjectId = project });
        db.DiskArchives.Add(new DiskArchive { Id = archive, RootId = root, RelativePath = "private-source/catalog.dt" });
        foreach (var version in versions)
            db.DiskArchiveVersions.Add(new DiskArchiveVersion { Id = version, ArchiveId = archive, Format = "dt", SizeBytes = 10,
                EstimatedDbBytes = 100, Sha256 = version.ToString("N") + version.ToString("N"), State = "Ready" });
        await db.SaveChangesAsync();
        return new(member, foreign, admin, project, root, archive, versions);
    }
    private static async Task<ArchiveCatalogPage> ListAsync(SqlServerTestDatabase sql, Seed seed, Actor? actor = null, ArchiveCatalogQuery? query = null, Guid? project = null)
    {
        await using var db = sql.NewContext();
        return await new SqlArchiveCatalogBrowse(db).ListAsync(actor ?? seed.Member, project ?? seed.Project, query ?? new(), Ct);
    }
    private static async Task RejectAsync(int status, Func<Task> action) =>
        Assert.Equal(status, (await Assert.ThrowsAsync<RequestRejectedException>(action)).StatusCode);
    private static async Task<string> StateAsync(SqlServerTestDatabase sql)
    {
        await using var db = sql.NewContext();
        return JsonSerializer.Serialize(new {
            Projects = await db.Projects.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            Memberships = await db.ProjectMemberships.AsNoTracking().OrderBy(x => x.UserId).ThenBy(x => x.ProjectId).ToListAsync(),
            Roots = await db.ArchiveRoots.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            RootProjects = await db.ArchiveRootProjects.AsNoTracking().OrderBy(x => x.RootId).ThenBy(x => x.ProjectId).ToListAsync(),
            Archives = await db.DiskArchives.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            Versions = await db.DiskArchiveVersions.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            Audits = await db.AuditEvents.AsNoTracking().OrderBy(x => x.Id).ToListAsync()
        });
    }

    [SqlServerFact]
    public async Task Member_and_admin_can_list_but_foreign_and_missing_project_are_not_found()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        Assert.Equal(5, (await ListAsync(sql, seed)).Items.Count);
        Assert.Equal(5, (await ListAsync(sql, seed, seed.Admin)).Items.Count);
        await RejectAsync(404, () => ListAsync(sql, seed, seed.Foreign));
        await RejectAsync(404, () => ListAsync(sql, seed, project: Guid.NewGuid()));
    }

    [SqlServerFact]
    public async Task Root_acl_enabled_root_ready_positive_sizes_and_recognized_formats_are_required()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        await using var db = sql.NewContext();
        await db.DiskArchiveVersions.Where(x => x.Id == seed.Versions[0]).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Discovered"));
        await db.DiskArchiveVersions.Where(x => x.Id == seed.Versions[1]).ExecuteUpdateAsync(s => s.SetProperty(x => x.SizeBytes, 0L));
        await db.DiskArchiveVersions.Where(x => x.Id == seed.Versions[2]).ExecuteUpdateAsync(s => s.SetProperty(x => x.EstimatedDbBytes, 0L));
        await db.DiskArchiveVersions.Where(x => x.Id == seed.Versions[3]).ExecuteUpdateAsync(s => s.SetProperty(x => x.Format, "zip"));
        await db.DiskArchiveVersions.Where(x => x.Id == seed.Versions[4]).ExecuteUpdateAsync(s => s.SetProperty(x => x.Format, "bak"));
        var visible = Assert.Single((await ListAsync(sql, seed)).Items);
        Assert.Equal(seed.Versions[4], visible.ArchiveVersionId); Assert.Equal("bak", visible.Format);
        await db.ArchiveRoots.Where(x => x.Id == seed.Root).ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false));
        Assert.Empty((await ListAsync(sql, seed, seed.Admin)).Items);
        await db.ArchiveRoots.Where(x => x.Id == seed.Root).ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, true));
        await db.ArchiveRootProjects.Where(x => x.RootId == seed.Root && x.ProjectId == seed.Project).ExecuteDeleteAsync();
        Assert.Empty((await ListAsync(sql, seed)).Items);
        Assert.Empty((await ListAsync(sql, seed, seed.Admin)).Items);
    }

    [SqlServerFact]
    public async Task Disabled_project_is_hidden_even_from_admin_and_revocation_is_effective_on_next_read()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        await using var db = sql.NewContext();
        await db.ProjectMemberships.Where(x => x.UserId == seed.Member.UserId && x.ProjectId == seed.Project).ExecuteDeleteAsync();
        await RejectAsync(404, () => ListAsync(sql, seed));
        Assert.Equal(5, (await ListAsync(sql, seed, seed.Admin)).Items.Count);
        await db.Projects.Where(x => x.Id == seed.Project).ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false));
        await RejectAsync(404, () => ListAsync(sql, seed, seed.Admin));
    }

    [SqlServerFact]
    public async Task Keyset_pages_cover_tied_metadata_once_and_cursors_are_project_actor_role_bound()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var before = await StateAsync(sql); var ids = new List<Guid>(); string? cursor = null; var pages = 0;
        do {
            var page = await ListAsync(sql, seed, query: new(2, cursor));
            Assert.InRange(page.Items.Count, 1, 2); ids.AddRange(page.Items.Select(x => x.ArchiveVersionId));
            cursor = page.NextCursor; pages++; Assert.InRange(pages, 1, 3);
        } while (cursor is not null);
        Assert.Equal(3, pages); Assert.Equal(5, ids.Distinct().Count());
        Assert.Equal(seed.Versions.OrderBy(x => x.ToString("D"), StringComparer.Ordinal), ids);
        var first = await ListAsync(sql, seed, query: new(2)); Assert.NotNull(first.NextCursor);
        await RejectAsync(400, () => ListAsync(sql, seed, seed.Admin, new(2, first.NextCursor)));
        await RejectAsync(400, () => ListAsync(sql, seed, new Actor(seed.Member.UserId, true), new(2, first.NextCursor)));
        await RejectAsync(400, () => ListAsync(sql, seed, query: new(2, first.NextCursor), project: Guid.NewGuid()));
        Assert.Equal(before, await StateAsync(sql));
    }

    [SqlServerFact]
    public async Task Invalid_limits_and_cursors_are_rejected_before_sql_and_do_not_change_persisted_state()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql); var before = await StateAsync(sql);
        var invalid = new[] { new ArchiveCatalogQuery(0), new ArchiveCatalogQuery(101), new ArchiveCatalogQuery(25, "invalid"), new ArchiveCatalogQuery(25, ""), new ArchiveCatalogQuery(25, new string('A', 513)) };
        await using var unused = new BuildSystemDbContext(new DbContextOptionsBuilder<BuildSystemDbContext>().UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
        foreach (var query in invalid) {
            await RejectAsync(400, () => new SqlArchiveCatalogBrowse(unused).ListAsync(seed.Member, seed.Project, query, Ct));
            await RejectAsync(400, () => ListAsync(sql, seed, query: query));
        }
        Assert.Equal(before, await StateAsync(sql));
    }

    [SqlServerFact]
    public async Task Serialized_page_is_an_exact_safe_allowlist_and_read_has_no_side_effects()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql); var before = await StateAsync(sql);
        var page = await ListAsync(sql, seed, query: new(2));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(page));
        Assert.Equal(new[] { "Items", "NextCursor", "ObservedAt" }, json.RootElement.EnumerateObject().Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal));
        foreach (var row in json.RootElement.GetProperty("Items").EnumerateArray())
            Assert.Equal(new[] { "ArchiveVersionId", "EstimatedDbBytes", "Format", "SizeBytes" }, row.EnumerateObject().Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal));
        var text = JsonSerializer.Serialize(page);
        foreach (var secret in new[] { "private-source", "RelativePath", "Sha256", "RootId", "AgentScopeId", "Token", "Snapshot", "UserName" }) Assert.DoesNotContain(secret, text);
        Assert.True(page.ObservedAt > DateTimeOffset.MinValue); Assert.Equal(before, await StateAsync(sql));
    }
}
// end
