using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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

// Real API middleware, AgentToken authentication and disposable SQL. TestServer HTTPS is simulated,
// not TLS/network proof; separate loopback TLS tests cover the transport handshake.
public sealed class SqlCleanupAdminCookieTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly string Hash = new('A', 64);
    private sealed record Seed(Actor Admin, Actor Owner, Guid Project, Guid Root, Guid Pool, Guid Version,
        OrderReceipt Order, AgentCredential Credential, StageLeaseGrant Grant, StageCleanupAuthorization Authorization);
    private static Guid ArtifactId(Guid reservation, Guid version)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"archive-stage-v1:{reservation:N}:{version:N}"));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x80); bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes.AsSpan(0, 16), bigEndian: true);
    }
    private static EnvironmentPool Pool(Guid id, Guid agent) => new() { Id = id, AgentScopeId = agent, Enabled = true,
        ClusterId = Guid.NewGuid(), SqlServerId = Guid.NewGuid(), IsolationPolicyId = "isolated", CapacityBytes = 100000,
        MaxEnvironmentBytes = 10000, MaxStagingBytes = 10000, MaxConcurrentProvisioning = 5, SupportsDt = true,
        SupportsBak = true, IsolationVerified = true, ScheduledJobsSuppressionVerified = true, ResourceLimitVerified = true };
    private static async Task<Seed> PrepareAsync(SqlServerTestDatabase sql)
    {
        await sql.ApplyMigrationsAsync();
        var admin = new Actor(Guid.NewGuid(), true); var owner = new Actor(Guid.NewGuid(), false);
        var project = Guid.NewGuid(); var root = Guid.NewGuid(); var pool = Guid.NewGuid();
        var archive = Guid.NewGuid(); var version = Guid.NewGuid(); var agent = Guid.NewGuid();
        await using var db = sql.NewContext();
        db.Users.AddRange(new BuildSystemUser { Id = admin.UserId, UserName = "admin", NormalizedUserName = "ADMIN" },
            new BuildSystemUser { Id = owner.UserId, UserName = "owner", NormalizedUserName = "OWNER" });
        db.Projects.Add(new Project { Id = project, Name = "cleanup-result", IsolationPolicyId = "isolated", PreparationProfileVersion = "v1" });
        db.ProjectMemberships.Add(new ProjectMembership { UserId = owner.UserId, ProjectId = project });
        db.ArchiveRoots.Add(new ArchiveRoot { Id = root, AgentScopeId = agent, Enabled = true, IsolationPolicyId = "isolated", MaxArchiveBytes = 10000, MaxStagingBytes = 10000 });
        db.ArchiveRootProjects.Add(new ArchiveRootProject { RootId = root, ProjectId = project });
        db.DiskArchives.Add(new DiskArchive { Id = archive, RootId = root, RelativePath = "cleanup-result.dt" });
        db.DiskArchiveVersions.Add(new DiskArchiveVersion { Id = version, ArchiveId = archive, Sha256 = Hash, SizeBytes = 10, Format = "dt", State = "Ready", EstimatedDbBytes = 100 });
        db.EnvironmentPools.Add(Pool(pool, agent)); db.ProjectPools.Add(new ProjectPool { ProjectId = project, PoolId = pool, Enabled = true });
        db.EnvironmentQuotas.Add(new EnvironmentQuota { SubjectId = owner.UserId, MaxActiveEnvironments = 5, MaxConcurrentProvisioning = 5, MaxStorageBytes = 100000, MaxTtlDays = 30 });
        await db.SaveChangesAsync();
        var control = new SqlAgentControl(db); var ticket = await control.CreateEnrollmentAsync(admin, new(agent, "result-worker", pool), Ct);
        await control.RegisterAsync(new(agent, ticket.EnrollmentId, ticket.Token, "result-worker"), Ct);
        var credential = await control.ApproveAsync(admin, agent, Ct);
        var order = await new SqlEnvironmentOrders(db).CreateAsync(owner, "cleanup-result-order", new(project, "cleanup", "archive", new(version), 7), Ct);
        var grant = Assert.IsType<StageLeaseGrant>(await new SqlArchiveStageControl(db, new ArchiveStageOptions { Enabled = true }).PollAsync(credential.Token, Ct));
        var artifact = ArtifactId(grant.Plan.ReservationId, version);
        await new SqlArtifactReceipts(db).ReportAsync(credential.Token, new(grant.JobId, grant.FencingToken, grant.Plan.ReservationId, version, artifact, Hash, 10, "dt"), Ct);
        await new SqlStageCompletion(db, new ArchiveStageOptions { Enabled = true }).CompleteAsync(credential.Token, new(grant.JobId, grant.FencingToken, artifact), Ct);
        var retirement = await new SqlStageRetirement(db).RequestAsync(owner, order.EnvironmentId, new(order.JobId, artifact), Ct);
        var authorization = await new SqlStageCleanup(db, new StageCleanupOptions { Enabled = true }).ClaimAsync(credential.Token, new(retirement.RetirementId, artifact), Ct);
        await new SqlStageCleanupResults(db, new StageCleanupResultOptions { Enabled = true }).ReportAsync(credential.Token,
            new(Guid.NewGuid(), authorization.AuthorizationId, retirement.RetirementId, order.JobId, artifact, grant.FencingToken), Ct);
        return new(admin, owner, project, root, pool, version, order, credential, grant, authorization);
    }

    private sealed record Csrf(string Token);
    private sealed class CookieFactory : WebApplicationFactory<global::Program>
    {
        private readonly string connection;
        private readonly string keys = Path.Combine(Path.GetTempPath(), "bs-cleanup-cookie-keys-" + Guid.NewGuid().ToString("N"));
        public CookieFactory(string connection)
        {
            this.connection = connection;
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(keys);
            else Directory.CreateDirectory(keys, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing"); builder.UseContentRoot(AppContext.BaseDirectory);
            builder.UseSetting("ConnectionStrings:BuildSystem", connection);
            builder.UseSetting("Security:DataProtectionDirectory", keys);
            builder.UseSetting("StageCleanupResult:Enabled", "true");
            builder.UseSetting("StagingAllocationRelease:Enabled", "true");
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
                    var expected = Path.GetFullPath(Path.GetTempPath()) + (Path.EndsInDirectorySeparator(Path.GetTempPath()) ? "" : Path.DirectorySeparatorChar);
                    if (!resolved.StartsWith(expected, StringComparison.Ordinal) || !Path.GetFileName(resolved).StartsWith("bs-cleanup-cookie-keys-", StringComparison.Ordinal))
                        throw new InvalidOperationException("Unexpected fixture key cleanup path.");
                    if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
                }
            }
        }
    }
    private static async Task<string> IdentityPassword(CookieFactory factory, Actor actor, string role)
    {
        // Never log or assert the generated credential. Only throw safe fixed failure labels.
        var password = "Fixture-Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<BuildSystemUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        if (!await roles.RoleExistsAsync(role) && !(await roles.CreateAsync(new IdentityRole<Guid>(role))).Succeeded)
            throw new InvalidOperationException("Fixture role creation failed.");
        var user = await users.FindByIdAsync(actor.UserId.ToString()) ?? throw new InvalidOperationException("Fixture user missing.");
        // Seeded application-port users had no email/security stamp; initialize valid Identity metadata.
        user.Email = user.UserName + "@fixture.invalid"; user.EmailConfirmed = true;
        // Initialize omitted seed stamps through Identity before its normal validation path.
        if (!(await users.UpdateSecurityStampAsync(user)).Succeeded || !(await users.UpdateAsync(user)).Succeeded ||
            !(await users.AddPasswordAsync(user, password)).Succeeded || !(await users.AddToRoleAsync(user, role)).Succeeded)
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
    private static async Task<string> Login(HttpClient client, string name, string password, string role)
    {
        var anonymous = await Token(client); SetToken(client, anonymous);
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { userName = name, password });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var values));
        var authCookie = values.Single(v => v.StartsWith("__Host-BuildSystem=", StringComparison.Ordinal));
        Assert.Contains("secure", authCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", authCookie, StringComparison.OrdinalIgnoreCase);
        using var me = await client.GetAsync("/api/auth/me"); Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var principal = await me.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(role, principal.GetProperty("roles").EnumerateArray().Select(v => v.GetString()));
        SetToken(client, await Token(client));
        return anonymous;
    }
    private static string Route(Guid resultId) => $"/api/admin/stage-cleanup-results/{resultId}/release-staging";
    private static ReleaseStagingAllocation Input(Seed seed) => new(seed.Authorization.AuthorizationId, seed.Grant.Plan.ReservationId);
    private static async Task<Guid> ResultId(SqlServerTestDatabase sql)
    { await using var db = sql.NewContext(); return (await db.StageCleanupResults.SingleAsync()).ResultId; }
    private static async Task NoLedger(SqlServerTestDatabase sql)
    { await using var db = sql.NewContext(); Assert.Empty(await db.StagingAllocationReleases.ToListAsync()); }

    [SqlServerFact]
    public async Task Administrator_real_cookie_and_fresh_csrf_release_replays_once_and_status_has_zero_held_bytes()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        using var factory = new CookieFactory(sql.ConnectionString); var password = await IdentityPassword(factory, seed.Admin, "Administrator");
        using var client = factory.Client(); await Login(client, "admin", password, "Administrator"); var id = await ResultId(sql);
        int auditBefore; await using (var db = sql.NewContext()) auditBefore = await db.AuditEvents.CountAsync();
        using var first = await client.PostAsJsonAsync(Route(id), Input(seed)); Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var receipt = await first.Content.ReadFromJsonAsync<StagingAllocationReleaseReceipt>(); Assert.NotNull(receipt);
        using var replay = await client.PostAsJsonAsync(Route(id), Input(seed)); Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(receipt, await replay.Content.ReadFromJsonAsync<StagingAllocationReleaseReceipt>());
        var status = await client.GetFromJsonAsync<CleanupStatusView>($"/api/test-environments/{seed.Order.EnvironmentId}/cleanup-status");
        Assert.NotNull(status); Assert.Equal("StagingReleased", status.CleanupState); Assert.Equal(0, status.HeldStagingBytes);
        await using var verify = sql.NewContext(); Assert.Single(await verify.StagingAllocationReleases.ToListAsync());
        Assert.Equal(auditBefore + 1, await verify.AuditEvents.CountAsync());
        var reservation = await verify.EnvironmentReservations.SingleAsync(); Assert.Equal("Held", reservation.State); Assert.Equal(100, reservation.ReservedBytes); Assert.Equal(10, reservation.StagingBytes);
        Assert.True((await verify.TestEnvironments.SingleAsync()).HasUnsettledProvisioningOperation);
    }
    [SqlServerFact]
    public async Task Administrator_cookie_without_csrf_or_with_pre_login_csrf_cannot_release()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        using var factory = new CookieFactory(sql.ConnectionString); var password = await IdentityPassword(factory, seed.Admin, "Administrator");
        using var client = factory.Client(); var anonymous = await Login(client, "admin", password, "Administrator"); var id = await ResultId(sql);
        SetToken(client, null);
        using (var missing = await client.PostAsJsonAsync(Route(id), Input(seed))) Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        SetToken(client, anonymous);
        using (var old = await client.PostAsJsonAsync(Route(id), Input(seed))) Assert.Equal(HttpStatusCode.BadRequest, old.StatusCode);
        await NoLedger(sql);
    }
    [SqlServerFact]
    public async Task Developer_cookie_with_csrf_and_anonymous_client_cannot_release()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        using var factory = new CookieFactory(sql.ConnectionString); var password = await IdentityPassword(factory, seed.Owner, "Developer");
        using var developer = factory.Client(); await Login(developer, "owner", password, "Developer"); var id = await ResultId(sql);
        using (var denied = await developer.PostAsJsonAsync(Route(id), Input(seed))) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var anonymous = factory.Client(); SetToken(anonymous, await Token(anonymous));
        using (var denied = await anonymous.PostAsJsonAsync(Route(id), Input(seed))) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        await NoLedger(sql);
    }
    [SqlServerFact]
    public async Task Agent_bearer_does_not_exempt_real_admin_cookie_from_csrf_or_allow_cookie_fallback_on_machine_route()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        using var factory = new CookieFactory(sql.ConnectionString); var password = await IdentityPassword(factory, seed.Admin, "Administrator");
        using var client = factory.Client(); await Login(client, "admin", password, "Administrator"); var id = await ResultId(sql);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.Credential.Token); SetToken(client, null);
        using (var denied = await client.PostAsJsonAsync(Route(id), Input(seed))) Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        await NoLedger(sql); SetToken(client, await Token(client));
        using (var allowed = await client.PostAsJsonAsync(Route(id), Input(seed))) Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new string('0', 64));
        using var machine = await client.PostAsJsonAsync("/api/agent/stage/cleanup-result", new ReportStageCleanup(id, seed.Authorization.AuthorizationId,
            seed.Authorization.Plan.RetirementId, seed.Order.JobId, seed.Authorization.Plan.ArtifactId, seed.Grant.FencingToken));
        Assert.Equal(HttpStatusCode.Unauthorized, machine.StatusCode);
        await using var db = sql.NewContext(); Assert.Single(await db.StageCleanupResults.ToListAsync()); Assert.Single(await db.StagingAllocationReleases.ToListAsync());
    }
}
// end
