using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Contracts;
using BuildSystem.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BuildSystem.IntegrationTests;

// Real Identity cookies, antiforgery, API middleware and SQL application service. Only disposable
// metadata SQL is touched: TestServer simulates HTTPS and never performs restore/1C/cleanup work.
public sealed class SqlRestoreIntentCookieTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly string Hash = new('A', 64);
    private sealed record Seed(Actor Owner, Actor Foreign, OrderReceipt Order, AgentCredential Credential,
        Guid ArtifactId, RestoreIntentOptions Options);

    private static async Task<Seed> PrepareAsync(SqlServerTestDatabase sql)
    {
        await sql.ApplyMigrationsAsync();
        var admin = new Actor(Guid.NewGuid(), true); var owner = new Actor(Guid.NewGuid(), false);
        var foreign = new Actor(Guid.NewGuid(), false);
        var project = Guid.NewGuid(); var pool = Guid.NewGuid(); var root = Guid.NewGuid();
        var archive = Guid.NewGuid(); var version = Guid.NewGuid(); var agent = Guid.NewGuid();
        var cluster = Guid.NewGuid(); var server = Guid.NewGuid();
        await using var db = sql.NewContext();
        db.Users.AddRange(new BuildSystemUser { Id = admin.UserId, UserName = "admin", NormalizedUserName = "ADMIN" },
            new BuildSystemUser { Id = owner.UserId, UserName = "owner", NormalizedUserName = "OWNER" },
            new BuildSystemUser { Id = foreign.UserId, UserName = "foreign", NormalizedUserName = "FOREIGN" });
        db.Projects.Add(new Project { Id = project, Name = "restore-cookie", IsolationPolicyId = "isolated", PreparationProfileVersion = "v1" });
        db.ProjectMemberships.Add(new ProjectMembership { UserId = owner.UserId, ProjectId = project });
        db.ArchiveRoots.Add(new ArchiveRoot { Id = root, Enabled = true, AgentScopeId = agent,
            IsolationPolicyId = "isolated", MaxArchiveBytes = 10000, MaxStagingBytes = 10000 });
        db.ArchiveRootProjects.Add(new ArchiveRootProject { RootId = root, ProjectId = project });
        db.DiskArchives.Add(new DiskArchive { Id = archive, RootId = root, RelativePath = "restore-cookie.dt" });
        db.DiskArchiveVersions.Add(new DiskArchiveVersion { Id = version, ArchiveId = archive,
            Sha256 = Hash, SizeBytes = 10, Format = "dt", State = "Ready", EstimatedDbBytes = 100 });
        db.EnvironmentPools.Add(new EnvironmentPool { Id = pool, Enabled = true, ClusterId = cluster,
            SqlServerId = server, AgentScopeId = agent, IsolationPolicyId = "isolated", CapacityBytes = 100000,
            MaxEnvironmentBytes = 10000, MaxStagingBytes = 10000, MaxConcurrentProvisioning = 5,
            SupportsDt = true, SupportsBak = true, IsolationVerified = true,
            ScheduledJobsSuppressionVerified = true, ResourceLimitVerified = true });
        db.ProjectPools.Add(new ProjectPool { ProjectId = project, PoolId = pool, Enabled = true });
        db.EnvironmentQuotas.Add(new EnvironmentQuota { SubjectId = owner.UserId, MaxActiveEnvironments = 5,
            MaxConcurrentProvisioning = 5, MaxStorageBytes = 100000, MaxTtlDays = 30 });
        await db.SaveChangesAsync();
        var control = new SqlAgentControl(db);
        var ticket = await control.CreateEnrollmentAsync(admin, new(agent, "restore-cookie-worker", pool), Ct);
        await control.RegisterAsync(new(agent, ticket.EnrollmentId, ticket.Token, "restore-cookie-worker"), Ct);
        var credential = await control.ApproveAsync(admin, agent, Ct);
        var order = await new SqlEnvironmentOrders(db).CreateAsync(owner, "restore-cookie-order",
            new(project, "restore-cookie", "archive", new(version), 7), Ct);
        var grant = Assert.IsType<StageLeaseGrant>(await new SqlArchiveStageControl(db,
            new ArchiveStageOptions { Enabled = true }).PollAsync(credential.Token, Ct));
        var artifact = Guid.NewGuid();
        await new SqlArtifactReceipts(db).ReportAsync(credential.Token,
            new(grant.JobId, grant.FencingToken, grant.Plan.ReservationId, version, artifact, Hash, 10, "dt"), Ct);
        await new SqlStageCompletion(db, new ArchiveStageOptions { Enabled = true })
            .CompleteAsync(credential.Token, new(grant.JobId, grant.FencingToken, artifact), Ct);
        return new(owner, foreign, order, credential, artifact, new RestoreIntentOptions { Enabled = true,
            CanonicalMappings = new() { [pool] = new(cluster, server) } });
    }

    private sealed record Csrf(string Token);
    private sealed class CookieFactory : WebApplicationFactory<global::Program>
    {
        private readonly string connection;
        private readonly RestoreIntentOptions options;
        private readonly string keys = Path.Combine(Path.GetTempPath(), "bs-restore-cookie-keys-" + Guid.NewGuid().ToString("N"));
        public CookieFactory(string connection, RestoreIntentOptions options)
        {
            this.connection = connection; this.options = options;
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(keys);
            else Directory.CreateDirectory(keys, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing"); builder.UseContentRoot(AppContext.BaseDirectory);
            builder.UseSetting("ConnectionStrings:BuildSystem", connection);
            builder.UseSetting("Security:DataProtectionDirectory", keys);
            // Override configuration data only: authentication, antiforgery and IRestoreIntents stay real.
            builder.ConfigureServices(services => services.AddSingleton(options));
        }
        public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true });
        protected override void Dispose(bool disposing)
        {
            try { base.Dispose(disposing); }
            finally
            {
                if (disposing)
                {
                    var resolved = Path.GetFullPath(keys);
                    var expected = Path.GetFullPath(Path.GetTempPath()) +
                        (Path.EndsInDirectorySeparator(Path.GetTempPath()) ? "" : Path.DirectorySeparatorChar);
                    if (!resolved.StartsWith(expected, StringComparison.Ordinal) ||
                        !Path.GetFileName(resolved).StartsWith("bs-restore-cookie-keys-", StringComparison.Ordinal))
                        throw new InvalidOperationException("Unexpected fixture key cleanup path.");
                    if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
                }
            }
        }
    }
    private static async Task<string> IdentityPassword(CookieFactory factory, Actor actor)
    {
        // Credentials remain local to this disposable fixture; never include them in failures or logs.
        var password = "Fixture-Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<BuildSystemUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        if (!await roles.RoleExistsAsync("Developer") &&
            !(await roles.CreateAsync(new IdentityRole<Guid>("Developer"))).Succeeded)
            throw new InvalidOperationException("Fixture role creation failed.");
        var user = await users.FindByIdAsync(actor.UserId.ToString()) ?? throw new InvalidOperationException("Fixture user missing.");
        user.Email = user.UserName + "@fixture.invalid"; user.EmailConfirmed = true;
        if (!(await users.UpdateSecurityStampAsync(user)).Succeeded || !(await users.UpdateAsync(user)).Succeeded ||
            !(await users.AddPasswordAsync(user, password)).Succeeded || !(await users.AddToRoleAsync(user, "Developer")).Succeeded)
            throw new InvalidOperationException("Fixture identity setup failed.");
        return password;
    }
    private static async Task<string> Token(HttpClient client)
    {
        var value = await client.GetFromJsonAsync<Csrf>("/api/auth/csrf"); Assert.NotNull(value); return value.Token;
    }
    private static void SetToken(HttpClient client, string? token)
    {
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        if (token is not null) client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
    }
    private static async Task<string> Login(HttpClient client, string name, string password)
    {
        var anonymous = await Token(client); SetToken(client, anonymous);
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { userName = name, password });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var values));
        var cookie = values.Single(v => v.StartsWith("__Host-BuildSystem=", StringComparison.Ordinal));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        using var me = await client.GetAsync("/api/auth/me"); Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var principal = await me.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Developer", principal.GetProperty("roles").EnumerateArray().Select(v => v.GetString()));
        SetToken(client, await Token(client)); return anonymous;
    }
    private static string Route(Seed seed) => $"/api/test-environments/{seed.Order.EnvironmentId}/restore-intents";
    private static RequestRestoreIntent Input(Seed seed) => new(seed.Order.JobId, seed.ArtifactId);
    private static async Task<string> LifecycleAsync(SqlServerTestDatabase sql)
    {
        await using var db = sql.NewContext();
        await db.EnvironmentJobs.LoadAsync(); await db.AgentLeases.LoadAsync(); await db.StagedArtifacts.LoadAsync();
        await db.TestEnvironments.LoadAsync(); await db.EnvironmentReservations.LoadAsync();
        await db.EnvironmentQuotas.LoadAsync(); await db.EnvironmentRequests.LoadAsync();
        await db.StageRetirementIntents.LoadAsync(); await db.StageCleanupAuthorizations.LoadAsync();
        await db.StageCleanupResults.LoadAsync(); await db.StagingAllocationReleases.LoadAsync();
        return JsonSerializer.Serialize(db.ChangeTracker.Entries().Select(e => e.Metadata.Name + ":" +
            JsonSerializer.Serialize(e.Properties.OrderBy(p => p.Metadata.Name)
                .ToDictionary(p => p.Metadata.Name, p => p.CurrentValue))).OrderBy(s => s).ToArray());
    }

    [SqlServerFact]
    public async Task Owner_real_developer_cookie_with_fresh_csrf_replays_one_nonexecuting_intent_without_lifecycle_changes()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        using var factory = new CookieFactory(sql.ConnectionString, seed.Options);
        var password = await IdentityPassword(factory, seed.Owner);
        using var client = factory.Client(); await Login(client, "owner", password);
        var before = await LifecycleAsync(sql); int audits;
        await using (var db = sql.NewContext()) audits = await db.AuditEvents.CountAsync();
        using var first = await client.PostAsJsonAsync(Route(seed), Input(seed)); Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var receipt = await first.Content.ReadFromJsonAsync<RestoreIntentReceipt>(); Assert.NotNull(receipt);
        Assert.False(receipt.Executable); Assert.NotEqual(Guid.Empty, receipt.RestoreIntentId);
        Assert.NotEqual(Guid.Empty, receipt.TargetIncarnation); Assert.Equal(seed.Order.JobId, receipt.StageJobId);
        Assert.Equal(seed.Order.EnvironmentId, receipt.EnvironmentId); Assert.Equal(seed.ArtifactId, receipt.ArtifactId);
        using var replay = await client.PostAsJsonAsync(Route(seed), Input(seed)); Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(receipt, await replay.Content.ReadFromJsonAsync<RestoreIntentReceipt>());
        await using (var db = sql.NewContext())
        {
            Assert.Single(await db.RestoreIntents.ToListAsync());
            Assert.Equal(audits + 1, await db.AuditEvents.CountAsync());
            Assert.Single(await db.AuditEvents.Where(a => a.Action == "RestoreIntentRequested").ToListAsync());
        }
        Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Owner_cookie_without_csrf_or_with_prelogin_token_cannot_create_intent_even_with_agent_bearer()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        using var factory = new CookieFactory(sql.ConnectionString, seed.Options);
        var password = await IdentityPassword(factory, seed.Owner);
        using var client = factory.Client(); var anonymous = await Login(client, "owner", password);
        var before = await LifecycleAsync(sql); int audits;
        await using (var db = sql.NewContext()) audits = await db.AuditEvents.CountAsync();
        SetToken(client, null);
        using (var missing = await client.PostAsJsonAsync(Route(seed), Input(seed))) Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        SetToken(client, anonymous);
        using (var old = await client.PostAsJsonAsync(Route(seed), Input(seed))) Assert.Equal(HttpStatusCode.BadRequest, old.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.Credential.Token);
        SetToken(client, null);
        using (var mixed = await client.PostAsJsonAsync(Route(seed), Input(seed))) Assert.Equal(HttpStatusCode.BadRequest, mixed.StatusCode);
        await using (var db = sql.NewContext())
        {
            Assert.Empty(await db.RestoreIntents.ToListAsync()); Assert.Equal(audits, await db.AuditEvents.CountAsync());
        }
        Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Anonymous_foreign_developer_and_agent_bearer_cannot_request_owners_intent()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        using var factory = new CookieFactory(sql.ConnectionString, seed.Options);
        var password = await IdentityPassword(factory, seed.Foreign);
        using var foreign = factory.Client(); await Login(foreign, "foreign", password);
        var before = await LifecycleAsync(sql); int audits;
        await using (var db = sql.NewContext()) audits = await db.AuditEvents.CountAsync();
        using (var denied = await foreign.PostAsJsonAsync(Route(seed), Input(seed))) Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        using var anonymous = factory.Client(); SetToken(anonymous, await Token(anonymous));
        using (var denied = await anonymous.PostAsJsonAsync(Route(seed), Input(seed))) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var machine = factory.Client(); SetToken(machine, await Token(machine));
        machine.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.Credential.Token);
        using (var denied = await machine.PostAsJsonAsync(Route(seed), Input(seed))) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        await using (var db = sql.NewContext())
        {
            Assert.Empty(await db.RestoreIntents.ToListAsync()); Assert.Equal(audits, await db.AuditEvents.CountAsync());
        }
        Assert.Equal(before, await LifecycleAsync(sql));
    }
}
// end
