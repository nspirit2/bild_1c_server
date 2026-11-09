using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Domain;
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

// Real machine credentials, production HTTP boundary, Identity cookies and disposable metadata SQL only.
public sealed class SqlTargetCreationCredentialDeliveryHttpTests
{
    private const string Route = "/api/agents/target-creation-credentials/resolve";
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly TargetCredentialInput Input = new("delivery-cluster-user-canary", "delivery-cluster-password-canary", "delivery-sql-user-canary", "delivery-sql-password-canary");
    private static TargetCreationPermitOptions Gates => new() { Enabled = true, AgentStartEnabled = true, CredentialBindingEnabled = true };
    private sealed record Fixture(Seed Seed, TargetCreationOperationReceipt Operation, TargetCredentialBinding Binding,
        Guid Permit, Guid Attempt, ITargetCredentialProtector Protector);
    private static async Task<Fixture> Prepare(SqlServerTestDatabase sql, bool legacy = false)
    {
        var seed = await SqlTargetCreationOperationTests.PrepareAsync(sql); await using var db = sql.NewContext();
        var operation = await new SqlTargetCreationOperations(db, seed.Options).RegisterAsync(seed.Admin, seed.Order.EnvironmentId, seed.Restore.RestoreIntentId, Ct);
        var binding = new TargetCredentialBinding(Guid.NewGuid(), operation.CanonicalClusterId, operation.CanonicalSqlServerId, Guid.NewGuid());
        var protector = new DataProtectionTargetCredentialProtector(new EphemeralDataProtectionProvider());
        await new SqlTargetCredentials(db, new() { Enabled = true }, protector).StoreAsync(seed.Admin, binding, Input, Ct);
        var permit = Guid.NewGuid(); var attempt = Guid.NewGuid(); var permits = new SqlTargetCreationPermits(db, Gates, seed.Options);
        if (legacy) await permits.IssueAsync(seed.Admin, operation.OperationId, 1, permit, attempt, Ct);
        else await permits.IssueWithCredentialAsync(seed.Admin, operation.OperationId, 1, permit, attempt, binding.CredentialId, binding.EndpointMappingId, Ct);
        await permits.RecordAgentStartAsync(seed.Credential.Token, permit, 1, attempt, Ct);
        return new(seed, operation, binding, permit, attempt, protector);
    }
    private sealed class Factory(SqlServerTestDatabase sql, Fixture f) : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing"); builder.UseContentRoot(AppContext.BaseDirectory);
            builder.UseSetting("ConnectionStrings:BuildSystem", sql.ConnectionString);
            builder.UseSetting("Security:DataProtectionDirectory", Path.Combine(Path.GetTempPath(), "buildsystem-delivery-http-test-keys"));
            builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TargetCreationCredentialDelivery:Enabled"] = "true", ["TargetCreationCredentialResolution:Enabled"] = "true",
                ["TargetCredentials:Enabled"] = "true", ["TargetCreationOperation:Enabled"] = "true",
                ["TargetCreationPermit:Enabled"] = "true", ["TargetCreationPermit:AgentStartEnabled"] = "true", ["TargetCreationPermit:CredentialBindingEnabled"] = "true"
            }));
            builder.ConfigureServices(s =>
            {
                s.AddSingleton(f.Seed.Options); s.AddSingleton(Gates); s.AddSingleton(new TargetCredentialOptions { Enabled = true });
                s.AddSingleton(new TargetCreationCredentialResolutionOptions { Enabled = true });
                s.AddSingleton<ITargetCredentialProtector>(f.Protector);
            });
        }
        public HttpClient Client(string? token = null)
        {
            var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
            if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }
    }
    private static object Pins(Fixture f, Guid? permit = null, Guid? attempt = null, Guid? credential = null, Guid? mapping = null) => new
    {
        permitId = permit ?? f.Permit, expectedPermitRevision = 2, attemptId = attempt ?? f.Attempt,
        credentialId = credential ?? f.Binding.CredentialId, endpointMappingId = mapping ?? f.Binding.EndpointMappingId
    };
    private static void Redacted(string text)
    {
        foreach (var secret in new[] { Input.ClusterUser, Input.ClusterPassword, Input.SqlUser, Input.SqlPassword }) Assert.DoesNotContain(secret, text);
        Assert.DoesNotContain("ciphertext", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", text, StringComparison.OrdinalIgnoreCase);
    }
    private static void Headers(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("no-cache", response.Headers.Pragma.Select(v => v.Name));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }
    private static async Task Reject(HttpClient client, object body, HttpStatusCode status, string? code = null)
    {
        using var response = await client.PostAsJsonAsync(Route, body); Assert.Equal(status, response.StatusCode); Headers(response);
        var text = await response.Content.ReadAsStringAsync(); Redacted(text); using var json = JsonDocument.Parse(text);
        Assert.Equal(new[] { "code" }, json.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        if (code is not null) Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
    }
    private static async Task<string> Snapshot(SqlServerTestDatabase sql)
    {
        await using var db = sql.NewContext(); await db.TargetCreationPermits.LoadAsync(); await db.TargetCreationOperations.LoadAsync();
        await db.TargetCredentials.LoadAsync(); await db.AuditEvents.LoadAsync(); await db.RestoreIntents.LoadAsync(); await db.EnvironmentJobs.LoadAsync();
        await db.AgentLeases.LoadAsync(); await db.StagedArtifacts.LoadAsync(); await db.TestEnvironments.LoadAsync();
        await db.EnvironmentReservations.LoadAsync(); await db.EnvironmentQuotas.LoadAsync(); await db.EnvironmentRequests.LoadAsync(); await db.StagingAllocationReleases.LoadAsync();
        return JsonSerializer.Serialize(db.ChangeTracker.Entries().Select(e => e.Metadata.Name + ":" + JsonSerializer.Serialize(
            e.Properties.OrderBy(p => p.Metadata.Name).ToDictionary(p => p.Metadata.Name, p => p.CurrentValue))).OrderBy(s => s));
    }

    [SqlServerFact]
    public async Task Repeated_https_delivery_returns_exact_pinned_secrets_without_execution_grant_or_metadata_mutations()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql);
        using var factory = new Factory(sql, f); using var client = factory.Client(f.Seed.Credential.Token); var before = await Snapshot(sql);
        string? previous = null;
        for (var i = 0; i < 2; i++)
        {
            using var response = await client.PostAsJsonAsync(Route, Pins(f)); Assert.Equal(HttpStatusCode.OK, response.StatusCode); Headers(response);
            var text = await response.Content.ReadAsStringAsync(); using var json = JsonDocument.Parse(text); var root = json.RootElement;
            Assert.Equal(new[] { "permitId", "expectedPermitRevision", "attemptId", "credentialId", "endpointMappingId", "clusterUser", "clusterPassword", "sqlUser", "sqlPassword", "executable", "scheduledJobsMustRemainBlocked", "retryAllowed" }, root.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.Equal(f.Permit, root.GetProperty("permitId").GetGuid()); Assert.Equal(2, root.GetProperty("expectedPermitRevision").GetInt64());
            Assert.Equal(f.Attempt, root.GetProperty("attemptId").GetGuid()); Assert.Equal(f.Binding.CredentialId, root.GetProperty("credentialId").GetGuid());
            Assert.Equal(f.Binding.EndpointMappingId, root.GetProperty("endpointMappingId").GetGuid());
            Assert.True(root.GetProperty("scheduledJobsMustRemainBlocked").GetBoolean());
            Assert.Equal(Input.ClusterUser, root.GetProperty("clusterUser").GetString()); Assert.Equal(Input.ClusterPassword, root.GetProperty("clusterPassword").GetString());
            Assert.Equal(Input.SqlUser, root.GetProperty("sqlUser").GetString()); Assert.Equal(Input.SqlPassword, root.GetProperty("sqlPassword").GetString());
            Assert.False(root.GetProperty("executable").GetBoolean()); Assert.False(root.GetProperty("retryAllowed").GetBoolean());
            Assert.DoesNotContain("ciphertext", text, StringComparison.OrdinalIgnoreCase);
            if (previous is not null) Assert.Equal(previous, text); previous = text;
        }
        Assert.Equal(before, await Snapshot(sql));
        await using var db = sql.NewContext(); Redacted(JsonSerializer.Serialize(await db.AuditEvents.AsNoTracking().ToListAsync()));
    }

    [SqlServerFact]
    public async Task Current_token_rotation_revocation_and_assignment_are_rechecked_before_missing_credentials()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql); var other = await SqlTargetCreationOperationTests.PrepareAsync(sql);
        using var factory = new Factory(sql, f); using var invalid = factory.Client("invalid-token");
        await Reject(invalid, Pins(f, credential: Guid.NewGuid()), HttpStatusCode.Unauthorized, "AgentUnauthorized");
        using var wrong = factory.Client(other.Credential.Token);
        await Reject(wrong, Pins(f, credential: Guid.NewGuid()), HttpStatusCode.Forbidden, "TargetCreationPermitForbidden");
        await using var db = sql.NewContext(); var rotated = await new SqlAgentControl(db).RotateAsync(f.Seed.Admin, f.Seed.Credential.AgentId, Ct);
        using var old = factory.Client(f.Seed.Credential.Token); await Reject(old, Pins(f), HttpStatusCode.Unauthorized, "AgentUnauthorized");
        using var current = factory.Client(rotated.Token); using (var success = await current.PostAsJsonAsync(Route, Pins(f))) Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        await new SqlAgentControl(db).RevokeAsync(f.Seed.Admin, f.Seed.Credential.AgentId, Ct);
        await Reject(current, Pins(f), HttpStatusCode.Unauthorized, "AgentUnauthorized");
    }

    [SqlServerFact]
    public async Task Credential_and_permit_revocation_expiry_and_current_admission_withdrawal_deny_secret_delivery()
    {
        foreach (var state in new[] { "credential", "permit", "expired", "admission" })
        {
            await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql);
            using var factory = new Factory(sql, f); using var client = factory.Client(f.Seed.Credential.Token); await using var db = sql.NewContext();
            if (state == "credential") await new SqlTargetCredentials(db, new() { Enabled = true }, f.Protector).RevokeAsync(f.Seed.Admin, f.Binding.CredentialId, 1, Ct);
            if (state == "permit") await new SqlTargetCreationPermits(db, Gates, f.Seed.Options).RevokeAsync(f.Seed.Admin, f.Permit, 2, Ct);
            if (state == "expired") await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [TargetCreationPermits] SET [IssuedAt] = DATEADD(day, -1, [IssuedAt]), [StartBefore] = DATEADD(day, -1, [StartBefore]), [StartedAt] = DATEADD(day, -1, [StartedAt]) WHERE [PermitId] = {f.Permit}", Ct);
            if (state == "admission") await db.ProjectMemberships.Where(m => m.UserId == f.Seed.Owner.UserId && m.ProjectId == f.Seed.Project).ExecuteDeleteAsync();
            var before = await Snapshot(sql); await Reject(client, Pins(f), HttpStatusCode.Conflict); Assert.Equal(before, await Snapshot(sql));
        }
    }

    [SqlServerFact]
    public async Task Wrong_pins_nonexistent_legacy_and_real_administrator_cookie_never_authorize_delivery()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql);
        using var factory = new Factory(sql, f); using var client = factory.Client(f.Seed.Credential.Token);
        foreach (var body in new[] { Pins(f, permit: Guid.NewGuid()), Pins(f, attempt: Guid.NewGuid()), Pins(f, credential: Guid.NewGuid()), Pins(f, mapping: Guid.NewGuid()) })
            await Reject(client, body, HttpStatusCode.Conflict);
        var legacy = await Prepare(sql, legacy: true); using var legacyClient = factory.Client(legacy.Seed.Credential.Token);
        await Reject(legacyClient, Pins(legacy), HttpStatusCode.Conflict);
        using var browser = factory.Client(); const string password = "Fixture-Only-Aa1!"; string userName;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<BuildSystemUser>>(); var user = await users.FindByIdAsync(f.Seed.Admin.UserId.ToString());
            Assert.NotNull(user); userName = user.UserName!; Assert.True((await users.AddPasswordAsync(user, password)).Succeeded);
        }
        var csrf = await browser.GetFromJsonAsync<JsonElement>("/api/auth/csrf"); browser.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        using (var login = await browser.PostAsJsonAsync("/api/auth/login", new { userName, password })) Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        browser.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        await Reject(browser, Pins(f), HttpStatusCode.Unauthorized, "AgentUnauthorized");
    }
}
