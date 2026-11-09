using System.Net;
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

namespace BuildSystem.IntegrationTests;

// Real Identity cookies, global CSRF, production routes and SQL service; disposable CI metadata SQL only.
// No target SQL/RAC, workers, restore or execution. Every secret below is a fixed dummy test canary.
public sealed class SqlTargetCredentialHttpTests
{
    private const string Route = "/api/admin/target-credentials";
    private const string Password = "Fixture-Only-Aa1!";
    private static readonly string[] Secrets = ["fixture-cluster-user", "fixture-cluster-password", "fixture-sql-user", "fixture-sql-password"];
    private static TargetCredentialBinding Binding() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static object Input(TargetCredentialBinding b) => new { credentialId = b.CredentialId,
        canonicalClusterId = b.CanonicalClusterId, canonicalSqlServerId = b.CanonicalSqlServerId,
        endpointMappingId = b.EndpointMappingId, clusterUser = Secrets[0], clusterPassword = Secrets[1], sqlUser = Secrets[2], sqlPassword = Secrets[3] };
    private static void Redacted(string text)
    {
        foreach (var secret in Secrets) Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain("ciphertext", text, StringComparison.OrdinalIgnoreCase);
    }
    private sealed class CountingProtector : ITargetCredentialProtector
    {
        private readonly DataProtectionTargetCredentialProtector inner = new(new EphemeralDataProtectionProvider());
        public int Calls { get; private set; }
        public string Protect(TargetCredentialScope scope, TargetCredentialSecrets secrets) { Calls++; return inner.Protect(scope, secrets); }
        public TargetCredentialSecrets Unprotect(TargetCredentialScope scope, string ciphertext) { Calls++; return inner.Unprotect(scope, ciphertext); }
    }
    private sealed class CookieFactory : WebApplicationFactory<global::Program>
    {
        private readonly string connection;
        private readonly bool? enabled;
        private readonly string keys = Path.Combine(Path.GetTempPath(), "bs-credential-http-keys-" + Guid.NewGuid().ToString("N"));
        public CountingProtector Protector { get; } = new();
        public CookieFactory(string connection, bool? enabled = true)
        {
            this.connection = connection; this.enabled = enabled;
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(keys);
            else Directory.CreateDirectory(keys, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing"); builder.UseContentRoot(AppContext.BaseDirectory);
            builder.UseSetting("ConnectionStrings:BuildSystem", connection);
            builder.UseSetting("Security:DataProtectionDirectory", keys);
            if (enabled.HasValue) builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
                new Dictionary<string, string?> { ["TargetCredentials:Enabled"] = enabled.Value.ToString() }));
            // Only the credential codec uses ephemeral keys. Identity/auth/CSRF and SQL service stay real.
            builder.ConfigureServices(s => s.AddSingleton<ITargetCredentialProtector>(Protector));
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
                    var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
                    if (!resolved.StartsWith(root, StringComparison.Ordinal) ||
                        !Path.GetFileName(resolved).StartsWith("bs-credential-http-keys-", StringComparison.Ordinal))
                        throw new InvalidOperationException("Unexpected fixture key cleanup path.");
                    if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
                }
            }
        }
    }
    private sealed record Csrf(string Token);
    private static async Task Token(HttpClient client)
    {
        var csrf = await client.GetFromJsonAsync<Csrf>("/api/auth/csrf"); Assert.NotNull(csrf);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN"); client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.Token);
    }
    private static async Task<Guid> Login(CookieFactory factory, HttpClient client, bool admin = true)
    {
        var id = Guid.NewGuid(); var name = "credential-http-" + id.ToString("N");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<BuildSystemUser>>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            var role = admin ? "Administrator" : "Developer";
            if (!await roles.RoleExistsAsync(role) && !(await roles.CreateAsync(new IdentityRole<Guid>(role))).Succeeded)
                throw new InvalidOperationException("Fixture role creation failed.");
            var user = new BuildSystemUser { Id = id, UserName = name, Email = name + "@fixture.invalid", EmailConfirmed = true };
            if (!(await users.CreateAsync(user, Password)).Succeeded || !(await users.AddToRoleAsync(user, role)).Succeeded)
                throw new InvalidOperationException("Fixture identity creation failed.");
        }
        await Token(client);
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = name, password = Password });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        Assert.True(login.Headers.TryGetValues("Set-Cookie", out var cookies));
        var cookie = cookies.Single(v => v.StartsWith("__Host-BuildSystem=", StringComparison.Ordinal));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase); Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Contains(admin ? "Administrator" : "Developer", me.GetProperty("roles").EnumerateArray().Select(v => v.GetString()));
        await Token(client); return id;
    }
    private static async Task<TargetCredentialMetadata> Metadata(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode); Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Redacted(await response.Content.ReadAsStringAsync());
        return Assert.IsType<TargetCredentialMetadata>(await response.Content.ReadFromJsonAsync<TargetCredentialMetadata>());
    }
    private static async Task Empty(SqlServerTestDatabase sql)
    {
        await using var db = sql.NewContext(); Assert.Empty(await db.TargetCredentials.ToListAsync());
        Assert.Empty(await db.AuditEvents.ToListAsync());
    }

    [SqlServerFact]
    public async Task Administrator_cookie_import_get_revoke_returns_metadata_only_and_stores_only_encrypted_secrets()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); await sql.ApplyMigrationsAsync();
        using var factory = new CookieFactory(sql.ConnectionString); using var client = factory.Client(); await Login(factory, client);
        var b = Binding();
        var json = JsonSerializer.Serialize(Input(b));
        var boundaryBody = json + new string(' ', 16384 - Encoding.UTF8.GetByteCount(json));
        Assert.Equal(16384, Encoding.UTF8.GetByteCount(boundaryBody));
        using var imported = await client.PostAsync(Route, new StringContent(boundaryBody, Encoding.UTF8, "application/json"));
        var stored = await Metadata(imported, HttpStatusCode.Created);
        Assert.Equal(b, stored.Binding); Assert.Equal(1, stored.Revision); Assert.False(stored.Executable);
        Assert.Equal(TargetCredentialState.Stored, stored.State);
        using var get = await client.GetAsync($"{Route}/{b.CredentialId}"); Assert.Equal(stored, await Metadata(get, HttpStatusCode.OK));
        string ciphertext;
        await using (var db = sql.NewContext())
        {
            var row = Assert.Single(await db.TargetCredentials.AsNoTracking().ToListAsync()); ciphertext = row.Ciphertext;
            Assert.False(string.IsNullOrWhiteSpace(ciphertext));
            foreach (var secret in Secrets) Assert.DoesNotContain(secret, JsonSerializer.Serialize(row), StringComparison.Ordinal);
            var decrypted = factory.Protector.Unprotect(new(b.CredentialId, b.CanonicalClusterId, b.CanonicalSqlServerId, b.EndpointMappingId), ciphertext);
            Assert.Equal(new TargetCredentialSecrets(Secrets[0], Secrets[1], Secrets[2], Secrets[3]), decrypted);
            Redacted(JsonSerializer.Serialize(await db.AuditEvents.ToListAsync())); Assert.Single(await db.AuditEvents.ToListAsync());
        }
        using var revoke = await client.PostAsJsonAsync($"{Route}/{b.CredentialId}/revoke", new { expectedRevision = 1 });
        var revoked = await Metadata(revoke, HttpStatusCode.OK); Assert.Equal(TargetCredentialState.Revoked, revoked.State);
        Assert.Equal(2, revoked.Revision); Assert.NotNull(revoked.RevokedAt); Assert.False(revoked.Executable);
        using var final = await client.GetAsync($"{Route}/{b.CredentialId}"); Assert.Equal(revoked, await Metadata(final, HttpStatusCode.OK));
        await using var check = sql.NewContext(); Assert.Equal(ciphertext, (await check.TargetCredentials.SingleAsync()).Ciphertext);
        Assert.Equal(2, await check.AuditEvents.CountAsync()); Redacted(JsonSerializer.Serialize(await check.AuditEvents.ToListAsync()));
    }

    [SqlServerFact]
    public async Task Anonymous_and_nonadministrator_denied_and_all_mutations_require_global_cookie_csrf()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); await sql.ApplyMigrationsAsync();
        using var factory = new CookieFactory(sql.ConnectionString); var b = Binding();
        using var anonymous = factory.Client(); await Token(anonymous);
        using var developer = factory.Client(); await Login(factory, developer, false);
        foreach (var (client, status) in new[] { (anonymous, HttpStatusCode.Unauthorized), (developer, HttpStatusCode.Forbidden) })
        {
            using var get = await client.GetAsync($"{Route}/{b.CredentialId}"); Assert.Equal(status, get.StatusCode);
            using var post = await client.PostAsJsonAsync(Route, Input(b)); Assert.Equal(status, post.StatusCode);
            using var revoke = await client.PostAsJsonAsync($"{Route}/{b.CredentialId}/revoke", new { expectedRevision = 1 }); Assert.Equal(status, revoke.StatusCode);
        }
        using var admin = factory.Client(); await Login(factory, admin); admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        using var missing = await admin.PostAsJsonAsync(Route, Input(b)); Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using var missingRevoke = await admin.PostAsJsonAsync($"{Route}/{b.CredentialId}/revoke", new { expectedRevision = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, missingRevoke.StatusCode); Assert.Equal(0, factory.Protector.Calls); await Empty(sql);
    }

    [SqlServerFact]
    public async Task Stale_administrator_cookie_loses_metadata_import_and_revoke_when_database_role_is_deleted()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); await sql.ApplyMigrationsAsync();
        using var factory = new CookieFactory(sql.ConnectionString); using var client = factory.Client(); var id = await Login(factory, client);
        var b = Binding(); using var stored = await client.PostAsJsonAsync(Route, Input(b)); await Metadata(stored, HttpStatusCode.Created);
        await using (var db = sql.NewContext()) await db.UserRoles.Where(r => r.UserId == id).ExecuteDeleteAsync();
        // The existing cookie still contains Administrator; do not refresh/login after removing membership.
        using var get = await client.GetAsync($"{Route}/{b.CredentialId}"); Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
        using var post = await client.PostAsJsonAsync(Route, Input(Binding())); Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        using var revoke = await client.PostAsJsonAsync($"{Route}/{b.CredentialId}/revoke", new { expectedRevision = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, revoke.StatusCode); Assert.Equal(1, factory.Protector.Calls);
        await using var check = sql.NewContext(); var row = Assert.Single(await check.TargetCredentials.ToListAsync());
        Assert.Equal(TargetCredentialState.Stored, row.State); Assert.Equal(1, row.Revision); Assert.Single(await check.AuditEvents.ToListAsync());
    }

    [SqlServerFact]
    public async Task Default_off_routes_return_503_without_secret_codec_io_or_persisted_rows()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); await sql.ApplyMigrationsAsync();
        using var factory = new CookieFactory(sql.ConnectionString, enabled: null); using var client = factory.Client(); await Login(factory, client);
        var b = Binding();
        using var post = await client.PostAsJsonAsync(Route, Input(b)); Assert.Equal(HttpStatusCode.ServiceUnavailable, post.StatusCode);
        using var get = await client.GetAsync($"{Route}/{b.CredentialId}"); Assert.Equal(HttpStatusCode.ServiceUnavailable, get.StatusCode);
        using var revoke = await client.PostAsJsonAsync($"{Route}/{b.CredentialId}/revoke", new { expectedRevision = 1 });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, revoke.StatusCode);
        Redacted(await post.Content.ReadAsStringAsync()); Redacted(await get.Content.ReadAsStringAsync()); Redacted(await revoke.Content.ReadAsStringAsync());
        Assert.Equal(0, factory.Protector.Calls); await Empty(sql);
    }

    private sealed class ChunkedContent(byte[] bytes) : HttpContent
    {
        public int Written { get; private set; }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            for (var offset = 0; offset < bytes.Length; offset += 1024)
            {
                var count = Math.Min(1024, bytes.Length - offset); await stream.WriteAsync(bytes.AsMemory(offset, count)); Written += count;
            }
        }
    }

    [SqlServerFact]
    public async Task Strict_parser_rejects_unknown_duplicate_null_wrongtype_malformed_secrets_and_bounded_oversize_without_inserts()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); await sql.ApplyMigrationsAsync();
        using var factory = new CookieFactory(sql.ConnectionString); using var client = factory.Client(); await Login(factory, client);
        var binding = Binding(); var valid = JsonSerializer.Serialize(Input(binding));
        string? generic = null;
        foreach (var body in new[] { valid[..^1] + ",\"unknown\":\"fixture-sql-password\"}",
            valid[..^1] + ",\"sqlPassword\":\"fixture-sql-password\"}", valid.Replace("\"sqlUser\":\"fixture-sql-user\"", "\"sqlUser\":null"),
            valid.Replace("\"sqlUser\":\"fixture-sql-user\"", "\"sqlUser\":42"), valid.Replace("fixture-sql-password", "fixture-sql-password\\n"),
            valid.Replace(binding.CredentialId.ToString(), " \u00a0" + binding.CredentialId + " "),
            valid.Replace("fixture-sql-password", "\\ud800"),
            valid.Replace("\"sqlUser\":\"fixture-sql-user\",", ""), valid.Replace("fixture-sql-password", ""),
            valid.Replace("fixture-sql-password", new string('x', 4097)), valid + "{}", valid[..^1], "null", "[]" })
        {
            using var response = await client.PostAsync(Route, new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); var text = await response.Content.ReadAsStringAsync(); Redacted(text);
            generic ??= text; Assert.Equal(generic, text);
        }
        foreach (var body in new[] { "{\"expectedRevision\":1,\"unknown\":\"fixture-sql-password\"}",
            "{\"expectedRevision\":1,\"expectedRevision\":1}", "{\"expectedRevision\":null}",
            "{\"expectedRevision\":\"1\"}", "{\"expectedRevision\":0}", "{\"expectedRevision\":-1}",
            "{\"expectedRevision\":9223372036854775808}", "{\"expectedRevision\":1.5}", "{\"expectedRevision\":1" })
        {
            using var response = await client.PostAsync($"{Route}/{Guid.NewGuid()}/revoke", new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Redacted(await response.Content.ReadAsStringAsync());
        }
        using (var malformedUtf8 = new ByteArrayContent([0x7b, 0x22, 0xff, 0x22, 0x3a, 0x31, 0x7d]))
        {
            malformedUtf8.Headers.ContentType = new("application/json");
            using var response = await client.PostAsync(Route, malformedUtf8);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Redacted(await response.Content.ReadAsStringAsync());
        }
        var oversized = Encoding.UTF8.GetBytes(valid[..^1] + ",\"padding\":\"" + new string('x', 17000) + "\"}");
        using (var response = await client.PostAsync(Route, new ByteArrayContent(oversized)))
        { Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode); Redacted(await response.Content.ReadAsStringAsync()); }
        using var chunked = new ChunkedContent(oversized); chunked.Headers.ContentType = new("application/json");
        using (var response = await client.PostAsync(Route, chunked))
        { Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode); Redacted(await response.Content.ReadAsStringAsync()); }
        Assert.Equal(0, factory.Protector.Calls); await Empty(sql);
    }
}
// end
