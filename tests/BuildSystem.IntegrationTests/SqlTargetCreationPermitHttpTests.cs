using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Seed = BuildSystem.IntegrationTests.SqlTargetCreationOperationTests.Seed;

namespace BuildSystem.IntegrationTests;

// Real SQL metadata and Identity cookies only; no RAC, target SQL or execution worker.
public sealed class SqlTargetCreationPermitHttpTests
{
    private const string Admin = "/api/admin/target-creation-permits";
    private const string Start = "/api/agents/target-creation-permit-starts";
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly TargetCredentialInput Secrets = new("permit-cluster-user-canary", "permit-cluster-password-canary", "permit-sql-user-canary", "permit-sql-password-canary");
    private static TargetCreationPermitOptions Gates => new() { Enabled = true, AgentStartEnabled = true, CredentialBindingEnabled = true };
    private sealed record Fixture(Seed Seed, TargetCreationOperationReceipt Operation, TargetCredentialBinding Binding, ITargetCredentialProtector Protector);
    private static async Task<Fixture> Prepare(SqlServerTestDatabase sql)
    {
        var seed = await SqlTargetCreationOperationTests.PrepareAsync(sql); await using var db = sql.NewContext();
        var operation = await new SqlTargetCreationOperations(db, seed.Options).RegisterAsync(seed.Admin, seed.Order.EnvironmentId, seed.Restore.RestoreIntentId, Ct);
        var binding = new TargetCredentialBinding(Guid.NewGuid(), operation.CanonicalClusterId, operation.CanonicalSqlServerId, Guid.NewGuid());
        var protector = new DataProtectionTargetCredentialProtector(new EphemeralDataProtectionProvider());
        await new SqlTargetCredentials(db, new() { Enabled = true }, protector).StoreAsync(seed.Admin, binding, Secrets, Ct);
        return new(seed, operation, binding, protector);
    }
    private sealed class Factory(SqlServerTestDatabase sql, Fixture f) : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing"); builder.UseContentRoot(AppContext.BaseDirectory);
            builder.UseSetting("ConnectionStrings:BuildSystem", sql.ConnectionString);
            builder.UseSetting("Security:DataProtectionDirectory", Path.Combine(Path.GetTempPath(), "buildsystem-permit-http-test-keys"));
            builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TargetCreationPermitHttp:Enabled"] = "true", ["TargetCreationPermit:Enabled"] = "true",
                ["TargetCreationPermit:AgentStartEnabled"] = "true", ["TargetCreationPermit:CredentialBindingEnabled"] = "true",
                ["TargetCreationOperation:Enabled"] = "true", ["TargetCredentials:Enabled"] = "true"
            }));
            builder.ConfigureServices(s => { s.AddSingleton(f.Seed.Options); s.AddSingleton(Gates); s.AddSingleton(new TargetCredentialOptions { Enabled = true }); s.AddSingleton(f.Protector); });
        }
        public HttpClient Client(string? token = null)
        {
            var c = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
            if (token is not null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return c;
        }
        public async Task<HttpClient> Administrator()
        {
            const string password = "Fixture-Only-Aa1!"; string userName;
            await using (var scope = Services.CreateAsyncScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<BuildSystemUser>>();
                var user = await users.FindByIdAsync(f.Seed.Admin.UserId.ToString()); Assert.NotNull(user); userName = user.UserName!;
                // Seed has null Email: unique-email validation must be satisfied before AddPasswordAsync.
                Assert.True((await users.SetEmailAsync(user, "permit-http-" + user.Id.ToString("N") + "@fixture.example.invalid")).Succeeded);
                user.EmailConfirmed = true; Assert.True((await users.AddPasswordAsync(user, password)).Succeeded);
            }
            var c = Client(); await Csrf(c);
            using (var login = await c.PostAsJsonAsync("/api/auth/login", new { userName, password })) Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
            // Antiforgery tokens are identity-bound; fetch a fresh token after authentication.
            c.DefaultRequestHeaders.Remove("X-CSRF-TOKEN"); await Csrf(c); return c;
        }
    }
    private static async Task Csrf(HttpClient c)
    { var j = await c.GetFromJsonAsync<JsonElement>("/api/auth/csrf"); c.DefaultRequestHeaders.Add("X-CSRF-TOKEN", j.GetProperty("token").GetString()); }
    private static object Issue(Fixture f, Guid? credential = null, Guid? mapping = null) => new
    { operationId = f.Operation.OperationId, expectedOperationRevision = 1, credentialId = credential ?? f.Binding.CredentialId, endpointMappingId = mapping ?? f.Binding.EndpointMappingId };
    private static object Begin(JsonElement permit, long revision = 1, Guid? attempt = null) => new
    { permitId = permit.GetProperty("permitId").GetGuid(), expectedPermitRevision = revision, attemptId = attempt ?? permit.GetProperty("attemptId").GetGuid() };
    private static void Headers(HttpResponseMessage r)
    { Assert.True(r.Headers.CacheControl?.NoStore); Assert.Contains("no-cache", r.Headers.Pragma.Select(p => p.Name)); Assert.Equal("nosniff", Assert.Single(r.Headers.GetValues("X-Content-Type-Options"))); }
    private static void Redacted(string text)
    {
        foreach (var value in new[] { Secrets.ClusterUser, Secrets.ClusterPassword, Secrets.SqlUser, Secrets.SqlPassword, "ciphertext", "Exception" })
            Assert.DoesNotContain(value, text, StringComparison.OrdinalIgnoreCase);
    }
    private static async Task<JsonElement> Receipt(HttpResponseMessage r, HttpStatusCode status)
    {
        Assert.Equal(status, r.StatusCode); Headers(r); var text = await r.Content.ReadAsStringAsync(); Redacted(text);
        using var j = JsonDocument.Parse(text); var root = j.RootElement;
        Assert.False(root.GetProperty("executable").GetBoolean()); Assert.True(root.GetProperty("scheduledJobsMustRemainBlocked").GetBoolean()); return root.Clone();
    }
    private static async Task Reject(HttpResponseMessage r, HttpStatusCode status, string? code = null)
    {
        using (r)
        {
            Assert.Equal(status, r.StatusCode); Headers(r); var text = await r.Content.ReadAsStringAsync(); Redacted(text);
            using var j = JsonDocument.Parse(text); Assert.Equal(new[] { "code" }, j.RootElement.EnumerateObject().Select(p => p.Name));
            if (code is not null) Assert.Equal(code, j.RootElement.GetProperty("code").GetString());
        }
    }
    private static async Task<JsonElement> Issue(HttpClient c, Fixture f)
    {
        using var response = await c.PostAsJsonAsync(Admin + "/issue", Issue(f)); var permit = await Receipt(response, HttpStatusCode.Created);
        Assert.Equal(Admin + "/" + permit.GetProperty("permitId").GetGuid().ToString("D"), response.Headers.Location?.OriginalString);
        Assert.Equal(f.Operation.OperationId, permit.GetProperty("operationId").GetGuid()); Assert.Equal(1, permit.GetProperty("revision").GetInt64());
        Assert.Equal(f.Binding.CredentialId, permit.GetProperty("credentialId").GetGuid()); Assert.Equal(f.Binding.EndpointMappingId, permit.GetProperty("endpointMappingId").GetGuid());
        Assert.NotEqual(Guid.Empty, permit.GetProperty("permitId").GetGuid()); Assert.NotEqual(Guid.Empty, permit.GetProperty("attemptId").GetGuid());
        Assert.NotEqual(permit.GetProperty("permitId").GetGuid(), permit.GetProperty("attemptId").GetGuid()); return permit;
    }
    private static async Task<string> Snapshot(SqlServerTestDatabase sql)
    {
        await using var db = sql.NewContext(); await db.TargetCreationPermits.LoadAsync(); await db.TargetCreationOperations.LoadAsync(); await db.TargetCredentials.LoadAsync();
        await db.AuditEvents.LoadAsync(); await db.RestoreIntents.LoadAsync(); await db.EnvironmentJobs.LoadAsync(); await db.AgentLeases.LoadAsync();
        await db.StagedArtifacts.LoadAsync(); await db.TestEnvironments.LoadAsync(); await db.EnvironmentReservations.LoadAsync();
        return JsonSerializer.Serialize(db.ChangeTracker.Entries().Select(e => e.Metadata.Name + ":" + JsonSerializer.Serialize(e.Properties.OrderBy(p => p.Metadata.Name).ToDictionary(p => p.Metadata.Name, p => p.CurrentValue))).OrderBy(s => s));
    }

    [SqlServerFact]
    public async Task Issue_get_and_assigned_agent_start_preserve_explicit_pins_and_only_record_nonexecuting_metadata()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql);
        using var factory = new Factory(sql, f); using var admin = await factory.Administrator(); var permit = await Issue(admin, f);
        var route = Admin + "/" + permit.GetProperty("permitId").GetGuid();
        using (var response = await admin.GetAsync(route)) { var read = await Receipt(response, HttpStatusCode.OK); Assert.Equal(permit.GetProperty("attemptId").GetGuid(), read.GetProperty("attemptId").GetGuid()); }
        using var agent = factory.Client(f.Seed.Credential.Token);
        using (var response = await agent.PostAsJsonAsync(Start, Begin(permit)))
        { var started = await Receipt(response, HttpStatusCode.OK); Assert.Equal(2, started.GetProperty("revision").GetInt64()); Assert.Equal(f.Binding.CredentialId, started.GetProperty("credentialId").GetGuid()); }
        await using var db = sql.NewContext(); var row = await db.TargetCreationPermits.SingleAsync(); Assert.Equal(permit.GetProperty("permitId").GetGuid(), row.PermitId);
        Assert.Equal(permit.GetProperty("attemptId").GetGuid(), row.AttemptId); Assert.Equal(2, row.Revision);
        // Existing credential resolver observes the same binding and protector, without delivering secrets over another HTTP surface.
        using var resolved = await new SqlTargetCreationCredentialResolver(db, Gates, f.Seed.Options, f.Protector, new() { Enabled = true }, new() { Enabled = true })
            .ResolveAsync(f.Seed.Credential.Token, row.PermitId, 2, row.AttemptId, f.Binding.CredentialId, f.Binding.EndpointMappingId, Ct);
        Assert.False(resolved.Executable); Assert.False(resolved.RetryAllowed);
    }

    [SqlServerFact]
    public async Task Revocation_blocks_new_start_but_preserves_historical_started_receipt_and_original_issue_ids()
    {
        foreach (var started in new[] { false, true })
        {
            await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql);
            using var factory = new Factory(sql, f); using var admin = await factory.Administrator(); var permit = await Issue(admin, f);
            using var agent = factory.Client(f.Seed.Credential.Token);
            if (started) { using var start = await agent.PostAsJsonAsync(Start, Begin(permit)); await Receipt(start, HttpStatusCode.OK); }
            var before = await Snapshot(sql); await Reject(await admin.PostAsJsonAsync(Admin + "/issue", Issue(f)), HttpStatusCode.Conflict); Assert.Equal(before, await Snapshot(sql));
            using (var revoke = await admin.PostAsJsonAsync(Admin + "/" + permit.GetProperty("permitId").GetGuid() + "/revoke", new { expectedPermitRevision = started ? 2 : 1 })) await Receipt(revoke, HttpStatusCode.OK);
            before = await Snapshot(sql);
            if (started)
            {
                using var replay = await agent.PostAsJsonAsync(Start, Begin(permit)); var historical = await Receipt(replay, HttpStatusCode.OK);
                Assert.Equal(3, historical.GetProperty("revision").GetInt64()); Assert.False(historical.GetProperty("newlyRecorded").GetBoolean());
            }
            else await Reject(await agent.PostAsJsonAsync(Start, Begin(permit)), HttpStatusCode.Conflict);
            Assert.Equal(before, await Snapshot(sql));
            await using var db = sql.NewContext(); var row = await db.TargetCreationPermits.SingleAsync(); Assert.Equal(permit.GetProperty("permitId").GetGuid(), row.PermitId); Assert.Equal(permit.GetProperty("attemptId").GetGuid(), row.AttemptId);
        }
    }

    [SqlServerFact]
    public async Task Real_cookie_requires_csrf_and_does_not_authorize_agent_start_wrong_token_or_assignment()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql); var other = await SqlTargetCreationOperationTests.PrepareAsync(sql);
        using var factory = new Factory(sql, f); using var admin = await factory.Administrator(); var permit = await Issue(admin, f); var before = await Snapshot(sql);
        admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        using (var response = await admin.PostAsJsonAsync(Admin + "/issue", Issue(f))) { Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Headers(response); }
        await Reject(await admin.PostAsJsonAsync(Start, Begin(permit)), HttpStatusCode.Unauthorized, "AgentUnauthorized");
        using var invalid = factory.Client("invalid-token"); await Reject(await invalid.PostAsJsonAsync(Start, Begin(permit)), HttpStatusCode.Unauthorized, "AgentUnauthorized");
        using var wrong = factory.Client(other.Credential.Token); await Reject(await wrong.PostAsJsonAsync(Start, Begin(permit)), HttpStatusCode.Forbidden, "TargetCreationPermitForbidden");
        Assert.Equal(before, await Snapshot(sql));
    }

    [SqlServerFact]
    public async Task Current_database_administrator_role_and_owner_membership_are_rechecked_after_cookie_issue()
    {
        foreach (var withdrawal in new[] { "role", "membership" })
        {
            await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql);
            using var factory = new Factory(sql, f); using var admin = await factory.Administrator(); var permit = await Issue(admin, f);
            await using var db = sql.NewContext();
            if (withdrawal == "role") await db.UserRoles.Where(r => r.UserId == f.Seed.Admin.UserId).ExecuteDeleteAsync();
            else await db.ProjectMemberships.Where(m => m.UserId == f.Seed.Owner.UserId && m.ProjectId == f.Seed.Project).ExecuteDeleteAsync();
            var before = await Snapshot(sql);
            if (withdrawal == "role")
            {
                await Reject(await admin.GetAsync(Admin + "/" + permit.GetProperty("permitId").GetGuid()), HttpStatusCode.Forbidden);
                await Reject(await admin.PostAsJsonAsync(Admin + "/" + permit.GetProperty("permitId").GetGuid() + "/revoke", new { expectedPermitRevision = 1 }), HttpStatusCode.Forbidden);
            }
            else
            { using var agent = factory.Client(f.Seed.Credential.Token); await Reject(await agent.PostAsJsonAsync(Start, Begin(permit)), HttpStatusCode.Conflict); }
            Assert.Equal(before, await Snapshot(sql));
        }
    }

    [SqlServerFact]
    public async Task Strict_input_wrong_binding_revoked_credentials_and_legacy_metadata_never_create_or_replace_permits()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql);
        using var factory = new Factory(sql, f); using var admin = await factory.Administrator(); var before = await Snapshot(sql);
        var valid = JsonSerializer.Serialize(Issue(f));
        foreach (var body in new[] { "{}", valid.Replace(":1", ":2"), valid.Replace(":1", ":1.0"), valid.Replace(f.Operation.OperationId.ToString(), Guid.Empty.ToString()), valid[..^1] + ",\"permitId\":\"" + Guid.NewGuid() + "\"}", valid[..^1] + ",\"attemptId\":\"" + Guid.NewGuid() + "\"}" })
            await Reject(await admin.PostAsync(Admin + "/issue", new StringContent(body, Encoding.UTF8, "application/json")), HttpStatusCode.BadRequest);
        await Reject(await admin.PostAsJsonAsync(Admin + "/issue", Issue(f, mapping: Guid.NewGuid())), HttpStatusCode.Conflict);
        Assert.Equal(before, await Snapshot(sql));
        await using var db = sql.NewContext();
        await new SqlTargetCredentials(db, new() { Enabled = true }, f.Protector).RevokeAsync(f.Seed.Admin, f.Binding.CredentialId, 1, Ct);
        before = await Snapshot(sql); await Reject(await admin.PostAsJsonAsync(Admin + "/issue", Issue(f)), HttpStatusCode.Conflict); Assert.Equal(before, await Snapshot(sql));
        // Legacy permits remain possible only through the internal port; HTTP never upgrades/replaces them.
        var legacy = await new SqlTargetCreationPermits(db, Gates, f.Seed.Options).IssueAsync(f.Seed.Admin, f.Operation.OperationId, 1, Guid.NewGuid(), Guid.NewGuid(), Ct);
        before = await Snapshot(sql); await Reject(await admin.PostAsJsonAsync(Admin + "/issue", Issue(f)), HttpStatusCode.Conflict); Assert.Equal(before, await Snapshot(sql));
        Assert.Null(legacy.CredentialId); Assert.Null(legacy.EndpointMappingId);
    }
}
