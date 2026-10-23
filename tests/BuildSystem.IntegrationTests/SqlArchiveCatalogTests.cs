using BuildSystem.Application;
using BuildSystem.Contracts;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildSystem.IntegrationTests;

public sealed class SqlArchiveCatalogTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private sealed record Seed(Guid Root, Guid Pool, Actor Admin, AgentCredential Credential);
    private static ArchiveInventoryEntry Entry(string path = "daily/sample.dt", char hash = 'a', long size = 100) => new(path, new string(hash, 64), size, "dt");
    private static ArchiveInventoryReport Report(Seed seed, params ArchiveInventoryEntry[] entries) => new(seed.Root, entries);
    private static async Task<Seed> PrepareAsync(SqlServerTestDatabase sql)
    {
        await sql.ApplyMigrationsAsync();
        await using var db = sql.NewContext();
        var admin = new Actor(Guid.NewGuid(), true);
        var pool = Guid.NewGuid();
        var agent = Guid.NewGuid();
        var root = Guid.NewGuid();
        db.EnvironmentPools.Add(new EnvironmentPool { Id = pool, Enabled = true });
        db.ArchiveRoots.Add(new ArchiveRoot { Id = root, Enabled = true, AgentScopeId = agent, MaxArchiveBytes = 1000 });
        await db.SaveChangesAsync();
        var control = new SqlAgentControl(db);
        var ticket = await control.CreateEnrollmentAsync(admin, new CreateEnrollment(agent, "scanner", pool), Ct);
        await control.RegisterAsync(new RegisterAgent(agent, ticket.EnrollmentId, ticket.Token, "scanner"), Ct);
        return new(root, pool, admin, await control.ApproveAsync(admin, agent, Ct));
    }
    private static async Task Reject(string code, Func<Task> action) =>
        Assert.Equal(code, (await Assert.ThrowsAsync<RequestRejectedException>(action)).Code);

    [SqlServerFact]
    public async Task Approved_current_credential_and_exact_enabled_root_binding_are_required()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        await using var db = sql.NewContext();
        var catalog = new SqlArchiveCatalog(db);
        await Reject("AgentUnauthorized", () => catalog.ReportAsync("invalid", Report(seed, Entry()), Ct));
        await db.Agents.Where(x => x.Id == seed.Credential.AgentId).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Pending"));
        await Reject("AgentUnauthorized", () => catalog.ReportAsync(seed.Credential.Token, Report(seed, Entry()), Ct));
        await db.Agents.Where(x => x.Id == seed.Credential.AgentId).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Approved"));
        await db.ArchiveRoots.Where(x => x.Id == seed.Root).ExecuteUpdateAsync(s => s.SetProperty(x => x.AgentScopeId, Guid.NewGuid()));
        await Reject("ArchiveRootForbidden", () => catalog.ReportAsync(seed.Credential.Token, Report(seed, Entry()), Ct));
        await db.ArchiveRoots.Where(x => x.Id == seed.Root).ExecuteUpdateAsync(s => s.SetProperty(x => x.AgentScopeId, seed.Credential.AgentId).SetProperty(x => x.Enabled, false));
        await Reject("ArchiveRootForbidden", () => catalog.ReportAsync(seed.Credential.Token, Report(seed, Entry()), Ct));
        await db.ArchiveRoots.Where(x => x.Id == seed.Root).ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, true));
        var control = new SqlAgentControl(db);
        var rotated = await control.RotateAsync(seed.Admin, seed.Credential.AgentId, Ct);
        await Reject("AgentUnauthorized", () => catalog.ReportAsync(seed.Credential.Token, Report(seed, Entry()), Ct));
        Assert.Single((await catalog.ReportAsync(rotated.Token, Report(seed, Entry()), Ct)).Entries);
        await control.RevokeAsync(seed.Admin, rotated.AgentId, Ct);
        await Reject("AgentUnauthorized", () => catalog.ReportAsync(rotated.Token, Report(seed, Entry()), Ct));
    }

    [SqlServerFact]
    public async Task Partial_inventory_is_idempotent_and_replacement_invalidates_ready_without_promoting_discovery()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        await using var db = sql.NewContext();
        var catalog = new SqlArchiveCatalog(db);
        var first = (await catalog.ReportAsync(seed.Credential.Token, Report(seed, Entry("daily\\sample.dt"), Entry("other.dt")), Ct)).Entries[0];
        Assert.Equal("Discovered", first.State);
        Assert.Equal(first, Assert.Single((await catalog.ReportAsync(seed.Credential.Token, Report(seed, Entry()), Ct)).Entries));
        Assert.Equal("daily/sample.dt", (await db.DiskArchives.SingleAsync(x => x.Id == first.ArchiveId)).RelativePath);
        Assert.All(await db.DiskArchiveVersions.ToListAsync(), x => { Assert.Equal(0, x.EstimatedDbBytes); Assert.Equal(new string('A', 64), x.Sha256); });
        await db.DiskArchiveVersions.Where(x => x.Id == first.ArchiveVersionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Ready"));
        Assert.Equal("Ready", Assert.Single((await catalog.ReportAsync(seed.Credential.Token, Report(seed, Entry()), Ct)).Entries).State);
        var replacement = Assert.Single((await catalog.ReportAsync(seed.Credential.Token, Report(seed, Entry(hash: 'b')), Ct)).Entries);
        Assert.Equal(first.ArchiveId, replacement.ArchiveId);
        Assert.NotEqual(first.ArchiveVersionId, replacement.ArchiveVersionId);
        Assert.Equal("Discovered", replacement.State);
        Assert.Equal("Unavailable", (await db.DiskArchiveVersions.SingleAsync(x => x.Id == first.ArchiveVersionId)).State);
        await db.DiskArchiveVersions.Where(x => x.Id == replacement.ArchiveVersionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Ready"));
        Assert.Equal("Unavailable", Assert.Single((await catalog.ReportAsync(seed.Credential.Token, Report(seed, Entry()), Ct)).Entries).State);
        Assert.Equal("Unavailable", (await db.DiskArchiveVersions.SingleAsync(x => x.Id == replacement.ArchiveVersionId)).State);
        Assert.Equal("Discovered", (await db.DiskArchiveVersions.SingleAsync(x => x.ArchiveId != first.ArchiveId)).State);
    }

    [SqlServerFact]
    public async Task Accent_width_and_kana_distinct_names_remain_distinct_archives()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        await using var db = sql.NewContext();
        var catalog = new SqlArchiveCatalog(db);
        var names = new[] { "cafe.dt", "café.dt", "Ａ.dt", "A.dt", "カ.dt", "か.dt" };
        var receipt = await catalog.ReportAsync(seed.Credential.Token, Report(seed, names.Select(x => Entry(x)).ToArray()), Ct);
        Assert.Equal(names.Length, receipt.Entries.Select(x => x.ArchiveId).Distinct().Count());
        Assert.Equal(names.Length, await db.DiskArchives.CountAsync());
        var replay = Assert.Single((await catalog.ReportAsync(seed.Credential.Token, Report(seed, Entry("CAFE.dt")), Ct)).Entries);
        Assert.Equal(receipt.Entries[0], replay);
    }

    [SqlServerFact]
    public async Task Invalid_batch_or_conflicting_fingerprint_rolls_back_all_entries_and_clears_tracker()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        await using var db = sql.NewContext();
        var catalog = new SqlArchiveCatalog(db);
        await catalog.ReportAsync(seed.Credential.Token, Report(seed, Entry()), Ct);
        await Reject("ArchiveFingerprintConflict", () => catalog.ReportAsync(seed.Credential.Token,
            Report(seed, Entry("new.dt"), Entry(size: 101)), Ct));
        Assert.Empty(db.ChangeTracker.Entries());
        await Reject("ArchiveTooLarge", () => catalog.ReportAsync(seed.Credential.Token,
            Report(seed, Entry("new.dt"), Entry("huge.dt", size: 1001)), Ct));
        foreach (var path in new[] { "../bad.dt", "/bad.dt", "C:\\bad.dt", "folder//bad.dt", "folder./bad.dt", "folder /bad.dt", "NUL.dt", "COM1/file.dt", "bad:stream.dt", "bad\n.dt", "bad.bak" })
            await Assert.ThrowsAsync<RequestRejectedException>(() => catalog.ReportAsync(seed.Credential.Token, Report(seed, Entry(path)), Ct));
        await Reject("InvalidArchiveInventory", () => catalog.ReportAsync(seed.Credential.Token, new(seed.Root, null!), Ct));
        await Reject("InvalidArchiveEntry", () => catalog.ReportAsync(seed.Credential.Token, Report(seed, new ArchiveInventoryEntry[] { null! }), Ct));
        await Reject("InvalidArchiveInventory", () => catalog.ReportAsync(seed.Credential.Token, Report(seed, Enumerable.Repeat(Entry(), 101).ToArray()), Ct));
        await Reject("DuplicateArchivePath", () => catalog.ReportAsync(seed.Credential.Token, Report(seed, Entry(), Entry("daily\\sample.dt")), Ct));
        Assert.Equal(1, await db.DiskArchives.CountAsync());
        Assert.Equal(1, await db.DiskArchiveVersions.CountAsync());
        Assert.Equal(1, await db.AuditEvents.CountAsync(x => x.Action == "ArchiveInventoryReported"));
        Assert.Single((await catalog.ReportAsync(seed.Credential.Token, Report(seed, Entry("new.dt")), Ct)).Entries);
    }
}
