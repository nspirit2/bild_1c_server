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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BuildSystem.IntegrationTests;

// Real Identity cookies, CSRF, AgentOnly authentication and SQL services over HTTPS TestServer.
// Only disposable metadata SQL is touched: no IIS/network E2E, real IB/RAC/SQL targets or restore work.
public sealed class SqlTargetObservationHttpTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly string Hash = new('A', 64);
    private const string MachineRoute = "/api/agents/target-observation-receipts";
    private sealed record Seed(Actor Admin, Actor Owner, Guid Project, Guid Pool, OrderReceipt Order,
        AgentCredential Credential, RestoreIntentReceipt Restore, TargetObservationEndpointMapping Mapping);

    // Minimal local copy of the existing private stage/Identity fixtures; preparation is not HTTP under test.
    private static async Task<Seed> PrepareAsync(SqlServerTestDatabase sql)
    {
        await sql.ApplyMigrationsAsync();
        var admin = new Actor(Guid.NewGuid(), true); var owner = new Actor(Guid.NewGuid(), false);
        var project = Guid.NewGuid(); var pool = Guid.NewGuid(); var root = Guid.NewGuid();
        var archive = Guid.NewGuid(); var version = Guid.NewGuid(); var agent = Guid.NewGuid();
        var cluster = Guid.NewGuid(); var server = Guid.NewGuid();
        await using var db = sql.NewContext();
        db.Users.AddRange(new BuildSystemUser { Id = admin.UserId, UserName = "admin", NormalizedUserName = "ADMIN" },
            new BuildSystemUser { Id = owner.UserId, UserName = "owner", NormalizedUserName = "OWNER" });
        db.Projects.Add(new Project { Id = project, Name = "observation-http", IsolationPolicyId = "isolated", PreparationProfileVersion = "v1" });
        db.ProjectMemberships.Add(new ProjectMembership { UserId = owner.UserId, ProjectId = project });
        db.ArchiveRoots.Add(new ArchiveRoot { Id = root, Enabled = true, AgentScopeId = agent,
            IsolationPolicyId = "isolated", MaxArchiveBytes = 10000, MaxStagingBytes = 10000 });
        db.ArchiveRootProjects.Add(new ArchiveRootProject { RootId = root, ProjectId = project });
        db.DiskArchives.Add(new DiskArchive { Id = archive, RootId = root, RelativePath = "observation-http.dt" });
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
        var ticket = await control.CreateEnrollmentAsync(admin, new(agent, "observation-http-worker", pool), Ct);
        await control.RegisterAsync(new(agent, ticket.EnrollmentId, ticket.Token, "observation-http-worker"), Ct);
        var credential = await control.ApproveAsync(admin, agent, Ct);
        var order = await new SqlEnvironmentOrders(db).CreateAsync(owner, "observation-http-order",
            new(project, "observation-http", "archive", new(version), 7), Ct);
        var grant = Assert.IsType<StageLeaseGrant>(await new SqlArchiveStageControl(db,
            new ArchiveStageOptions { Enabled = true }).PollAsync(credential.Token, Ct));
        var artifact = Guid.NewGuid();
        await new SqlArtifactReceipts(db).ReportAsync(credential.Token,
            new(grant.JobId, grant.FencingToken, grant.Plan.ReservationId, version, artifact, Hash, 10, "dt"), Ct);
        await new SqlStageCompletion(db, new ArchiveStageOptions { Enabled = true })
            .CompleteAsync(credential.Token, new(grant.JobId, grant.FencingToken, artifact), Ct);
        var restore = await new SqlRestoreIntents(db, new RestoreIntentOptions { Enabled = true,
            CanonicalMappings = new() { [pool] = new(cluster, server) } })
            .RequestAsync(owner, order.EnvironmentId, new(order.JobId, artifact), Ct);
        return new(admin, owner, project, pool, order, credential, restore, new(Guid.NewGuid(), Guid.NewGuid()));
    }

    private sealed class CookieFactory : WebApplicationFactory<global::Program>
    {
        private readonly string connection;
        private readonly Seed seed;
        private readonly string keys = Path.Combine(Path.GetTempPath(), "bs-observation-http-keys-" + Guid.NewGuid().ToString("N"));
        public CookieFactory(string connection, Seed seed)
        {
            this.connection = connection; this.seed = seed;
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(keys);
            else Directory.CreateDirectory(keys, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing"); builder.UseContentRoot(AppContext.BaseDirectory);
            builder.UseSetting("ConnectionStrings:BuildSystem", connection);
            builder.UseSetting("Security:DataProtectionDirectory", keys);
            // Data only, consumed by the production factory's final IConfiguration binding.
            // No authentication, authorization or application service is replaced.
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TargetObservation:Enabled"] = "true",
                    [$"TargetObservation:EndpointMappings:{seed.Pool}:RacEndpointMappingId"] = seed.Mapping.RacEndpointMappingId.ToString(),
                    [$"TargetObservation:EndpointMappings:{seed.Pool}:SqlEndpointMappingId"] = seed.Mapping.SqlEndpointMappingId.ToString()
                }));
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
                        !Path.GetFileName(resolved).StartsWith("bs-observation-http-keys-", StringComparison.Ordinal))
                        throw new InvalidOperationException("Unexpected fixture key cleanup path.");
                    if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
                }
            }
        }
    }

    private sealed record Csrf(string Token);
    private static async Task<string> Token(HttpClient client)
    {
        var value = await client.GetFromJsonAsync<Csrf>("/api/auth/csrf"); Assert.NotNull(value); return value.Token;
    }
    private static void SetToken(HttpClient client, string? token)
    {
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        if (token is not null) client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
    }
    private static async Task Login(CookieFactory factory, HttpClient client, Actor owner)
    {
        // Random credentials stay local to the disposable fixture; never include them in failure output.
        var password = "Fixture-Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<BuildSystemUser>>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            if (!await roles.RoleExistsAsync("Developer") &&
                !(await roles.CreateAsync(new IdentityRole<Guid>("Developer"))).Succeeded)
                throw new InvalidOperationException("Fixture role creation failed.");
            var user = await users.FindByIdAsync(owner.UserId.ToString()) ?? throw new InvalidOperationException("Fixture user missing.");
            user.Email = "owner@fixture.invalid"; user.EmailConfirmed = true;
            if (!(await users.UpdateSecurityStampAsync(user)).Succeeded || !(await users.UpdateAsync(user)).Succeeded ||
                !(await users.AddPasswordAsync(user, password)).Succeeded || !(await users.AddToRoleAsync(user, "Developer")).Succeeded)
                throw new InvalidOperationException("Fixture identity setup failed.");
        }
        SetToken(client, await Token(client));
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { userName = "owner", password });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var values));
        var cookie = values.Single(v => v.StartsWith("__Host-BuildSystem=", StringComparison.Ordinal));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        using var me = await client.GetAsync("/api/auth/me"); Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var principal = await me.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Developer", principal.GetProperty("roles").EnumerateArray().Select(v => v.GetString()));
        SetToken(client, await Token(client));
    }
    private static string Route(Seed seed) => $"/api/test-environments/{seed.Order.EnvironmentId}/target-observations";
    private static object Input(Seed seed) => new { expectedRestoreIntentId = seed.Restore.RestoreIntentId };
    private static async Task<TargetObservationAssignment> Request(HttpClient client, Seed seed)
    {
        using var response = await client.PostAsJsonAsync(Route(seed), Input(seed));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<TargetObservationAssignment>(await response.Content.ReadFromJsonAsync<TargetObservationAssignment>());
    }
    private static ReportTargetObservation Report(TargetObservationAssignment assignment) => new(Guid.NewGuid(), assignment,
        Guid.NewGuid(), Guid.NewGuid(), TargetObservationOutcome.BothObservedAbsent,
        new DateTimeOffset(2026, 11, 3, 9, 30, 0, TimeSpan.Zero).AddTicks(1234567));
    private static HttpClient Machine(CookieFactory factory, Seed seed)
    {
        var client = factory.Client();
        // AgentToken is the server authentication scheme; its existing wire format is Bearer.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.Credential.Token);
        return client;
    }
    private static async Task<int> Audits(SqlServerTestDatabase sql)
    {
        await using var db = sql.NewContext(); return await db.AuditEvents.CountAsync();
    }
    private static async Task<string> Lifecycle(SqlServerTestDatabase sql, bool includeAssignments = false)
    {
        await using var db = sql.NewContext();
        await db.EnvironmentJobs.LoadAsync(); await db.AgentLeases.LoadAsync(); await db.StagedArtifacts.LoadAsync();
        await db.TestEnvironments.LoadAsync(); await db.EnvironmentReservations.LoadAsync();
        await db.EnvironmentQuotas.LoadAsync(); await db.EnvironmentRequests.LoadAsync();
        await db.StageRetirementIntents.LoadAsync(); await db.StageCleanupAuthorizations.LoadAsync();
        await db.StageCleanupResults.LoadAsync(); await db.StagingAllocationReleases.LoadAsync();
        await db.RestoreIntents.LoadAsync();
        if (includeAssignments) await db.TargetObservations.LoadAsync();
        return JsonSerializer.Serialize(db.ChangeTracker.Entries().Select(e => e.Metadata.Name + ":" +
            JsonSerializer.Serialize(e.Properties.OrderBy(p => p.Metadata.Name)
                .ToDictionary(p => p.Metadata.Name, p => p.CurrentValue))).OrderBy(s => s).ToArray());
    }

    [SqlServerFact]
    public async Task Owner_real_cookie_with_csrf_replays_one_nonexecuting_assignment_with_server_derived_names()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        using var factory = new CookieFactory(sql.ConnectionString, seed);
        using var owner = factory.Client(); await Login(factory, owner, seed.Owner);
        var before = await Lifecycle(sql); var audits = await Audits(sql);
        var first = await Request(owner, seed); var replay = await Request(owner, seed);
        Assert.Equal(first, replay); Assert.False(first.Executable); Assert.True(first.ScheduledJobsMustRemainBlocked);
        Assert.NotEqual(Guid.Empty, first.ObservationRequestId); Assert.NotEqual(Guid.Empty, first.OperationId);
        Assert.Equal(seed.Restore.RestoreIntentId, first.RestoreIntentId);
        Assert.Equal(seed.Order.EnvironmentId, first.EnvironmentId); Assert.Equal(seed.Order.JobId, first.StageJobId);
        Assert.Equal(seed.Credential.AgentId, first.AgentId); Assert.Equal(seed.Restore.TargetIncarnation, first.TargetIncarnationId);
        Assert.Equal(seed.Restore.ReservationId, first.ReservationId);
        Assert.Equal(seed.Restore.CanonicalClusterId, first.CanonicalClusterId);
        Assert.Equal(seed.Restore.CanonicalSqlServerId, first.CanonicalSqlServerId);
        Assert.Equal(seed.Mapping.RacEndpointMappingId, first.RacEndpointMappingId);
        Assert.Equal(seed.Mapping.SqlEndpointMappingId, first.SqlEndpointMappingId);
        Assert.Equal("bs_test_" + first.ReservationId.ToString("N"), first.InfobaseName);
        Assert.Equal(first.InfobaseName, first.SqlDatabaseName);
        await using (var db = sql.NewContext())
        {
            var row = Assert.Single(await db.TargetObservations.ToListAsync());
            Assert.False(row.Executable); Assert.True(row.ScheduledJobsMustRemainBlocked);
            Assert.Equal(seed.Owner.UserId, row.RequestedBy);
            Assert.Single(await db.AuditEvents.Where(a => a.Action == "TargetObservationRequested").ToListAsync());
            Assert.Empty(await db.TargetObservationReceipts.ToListAsync());
        }
        Assert.Equal(audits + 1, await Audits(sql)); Assert.Equal(before, await Lifecycle(sql));
    }

    [SqlServerFact]
    public async Task AgentToken_report_replays_one_recorded_ack_without_lifecycle_mutation_and_auth_surfaces_stay_separate()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        using var factory = new CookieFactory(sql.ConnectionString, seed);
        using var owner = factory.Client(); await Login(factory, owner, seed.Owner);
        var assignment = await Request(owner, seed); var report = Report(assignment);
        var before = await Lifecycle(sql, includeAssignments: true); var audits = await Audits(sql);
        using var machine = Machine(factory, seed); // Deliberately no CSRF cookie/header on the machine request.
        using var first = await machine.PostAsJsonAsync(MachineRoute, report); Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var ack = await first.Content.ReadFromJsonAsync<TargetObservationReceipt>(); Assert.NotNull(ack);
        Assert.Equal("Recorded", ack.State); Assert.Equal(report.ReceiptId, ack.ReceiptId);
        Assert.Equal(assignment.ObservationRequestId, ack.ObservationRequestId);
        Assert.False(ack.Executable); Assert.True(ack.ScheduledJobsMustRemainBlocked);
        using var replay = await machine.PostAsJsonAsync(MachineRoute, report); Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(ack, await replay.Content.ReadFromJsonAsync<TargetObservationReceipt>());
        using (var denied = await owner.PostAsJsonAsync(MachineRoute, report)) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        SetToken(machine, await Token(machine)); // Valid anonymous CSRF does not turn a machine token into Identity.
        using (var denied = await machine.PostAsJsonAsync(Route(seed), Input(seed))) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        await using (var db = sql.NewContext())
        {
            var row = Assert.Single(await db.TargetObservationReceipts.ToListAsync());
            Assert.Equal(report.ReceiptId, row.ReceiptId); Assert.Equal(report.RacObservationId, row.RacObservationId);
            Assert.Equal(report.SqlObservationId, row.SqlObservationId); Assert.Equal(report.ObservedAt, row.ObservedAt);
            Assert.False(row.Executable); Assert.True(row.ScheduledJobsMustRemainBlocked);
            Assert.Single(await db.AuditEvents.Where(a => a.Action == "TargetObservationRecorded").ToListAsync());
            Assert.Equal(assignment.ObservationRequestId, Assert.Single(await db.TargetObservations.ToListAsync()).ObservationRequestId);
        }
        Assert.Equal(audits + 1, await Audits(sql)); Assert.Equal(before, await Lifecycle(sql, includeAssignments: true));
    }

    [SqlServerFact]
    public async Task Missing_csrf_and_current_membership_or_agent_revocation_block_otherwise_exact_replays()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        using var factory = new CookieFactory(sql.ConnectionString, seed);
        using var owner = factory.Client(); await Login(factory, owner, seed.Owner);
        var before = await Lifecycle(sql); var audits = await Audits(sql);
        SetToken(owner, null);
        using (var denied = await owner.PostAsJsonAsync(Route(seed), Input(seed))) Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        await using (var db = sql.NewContext()) Assert.Empty(await db.TargetObservations.ToListAsync());
        Assert.Equal(audits, await Audits(sql)); Assert.Equal(before, await Lifecycle(sql));
        SetToken(owner, await Token(owner));
        var assignment = await Request(owner, seed); var report = Report(assignment);
        using var machine = Machine(factory, seed);
        using (var recorded = await machine.PostAsJsonAsync(MachineRoute, report)) Assert.Equal(HttpStatusCode.OK, recorded.StatusCode);
        SetToken(owner, null);
        using (var denied = await owner.PostAsJsonAsync(Route(seed), Input(seed))) Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        SetToken(owner, await Token(owner));
        await using (var db = sql.NewContext())
            await db.ProjectMemberships.Where(m => m.UserId == seed.Owner.UserId && m.ProjectId == seed.Project).ExecuteDeleteAsync();
        audits = await Audits(sql); before = await Lifecycle(sql);
        using (var denied = await owner.PostAsJsonAsync(Route(seed), Input(seed))) Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Equal(audits, await Audits(sql)); Assert.Equal(before, await Lifecycle(sql));
        // Restore membership so the machine denial proves current token revocation, not unrelated ACL drift.
        await using (var db = sql.NewContext())
        {
            db.ProjectMemberships.Add(new ProjectMembership { UserId = seed.Owner.UserId, ProjectId = seed.Project });
            await db.SaveChangesAsync();
            await new SqlAgentControl(db).RevokeAsync(seed.Admin, seed.Credential.AgentId, Ct);
        }
        audits = await Audits(sql); before = await Lifecycle(sql);
        using (var denied = await machine.PostAsJsonAsync(MachineRoute, report)) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal(audits, await Audits(sql)); Assert.Equal(before, await Lifecycle(sql));
        await using var check = sql.NewContext();
        Assert.Single(await check.TargetObservations.ToListAsync()); Assert.Single(await check.TargetObservationReceipts.ToListAsync());
    }
}
// end
