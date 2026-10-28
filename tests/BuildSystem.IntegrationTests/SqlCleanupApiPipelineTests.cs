using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildSystem.Agent;
using BuildSystem.Application;
using BuildSystem.Contracts;
using BuildSystem.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BuildSystem.IntegrationTests;

// Real API middleware, AgentToken authentication and disposable SQL. TestServer HTTPS is simulated,
// not TLS/network proof; separate loopback TLS tests cover the transport handshake.
public sealed class SqlCleanupApiPipelineTests
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
        return new(admin, owner, project, root, pool, version, order, credential, grant, authorization);
    }
    private static ReportStageCleanup Report(Seed seed) => new(Guid.NewGuid(), seed.Authorization.AuthorizationId,
        seed.Authorization.Plan.RetirementId, seed.Order.JobId, seed.Authorization.Plan.ArtifactId, seed.Grant.FencingToken);
    private const string Route = "/api/agent/stage/cleanup-result";
    private sealed class PipelineFactory : WebApplicationFactory<global::Program>
    {
        private readonly string connection;
        private readonly bool enabled;
        private readonly int? httpsPort;
        private readonly string keys = Path.Combine(Path.GetTempPath(), "bs-cleanup-api-keys-" + Guid.NewGuid().ToString("N"));
        public PipelineFactory(string connection, bool enabled = true, int? httpsPort = null)
        {
            this.connection = connection; this.enabled = enabled; this.httpsPort = httpsPort;
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(keys);
            else Directory.CreateDirectory(keys, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseContentRoot(AppContext.BaseDirectory);
            builder.UseSetting("ConnectionStrings:BuildSystem", connection);
            builder.UseSetting("Security:DataProtectionDirectory", keys);
            builder.UseSetting("StageCleanupResult:Enabled", enabled ? "true" : "false");
            if (httpsPort is int port)
                builder.ConfigureServices(s => s.Configure<Microsoft.AspNetCore.HttpsPolicy.HttpsRedirectionOptions>(o => o.HttpsPort = port));
        }
        public HttpClient Client(string? token, string scheme = "https")
        {
            var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(scheme + "://localhost"), AllowAutoRedirect = false, HandleCookies = false });
            if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }
        protected override void Dispose(bool disposing)
        {
            try { base.Dispose(disposing); }
            finally { if (disposing && Directory.Exists(keys)) Directory.Delete(keys, recursive: true); }
        }
    }
    [SqlServerFact]
    public async Task Actual_https_pipeline_authenticates_agent_without_csrf_and_replays_one_receipt()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        using var factory = new PipelineFactory(sql.ConnectionString);
        using var client = factory.Client(seed.Credential.Token); var report = Report(seed);
        int before; await using (var db = sql.NewContext()) before = await db.AuditEvents.CountAsync();
        using var first = await client.PostAsJsonAsync(Route, report);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var receipt = await first.Content.ReadFromJsonAsync<StageCleanupResultReceipt>();
        Assert.NotNull(receipt); Assert.Equal(report.ResultId, receipt.ResultId); Assert.Equal("DeletedReported", receipt.State);
        using var replay = await client.PostAsJsonAsync(Route, report);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(receipt, await replay.Content.ReadFromJsonAsync<StageCleanupResultReceipt>());
        // Bearer alone is never a human principal, even on an admin route. No fake auth scheme exists.
        using var admin = await client.PostAsJsonAsync("/api/admin/agents/" + seed.Credential.AgentId + "/revoke", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, admin.StatusCode);
        await using (var db = sql.NewContext())
        {
            Assert.Single(await db.StageCleanupResults.ToListAsync()); Assert.Equal(before + 1, await db.AuditEvents.CountAsync());
            Assert.Equal("Held", (await db.EnvironmentReservations.SingleAsync()).State);
            Assert.True((await db.TestEnvironments.SingleAsync()).HasUnsettledProvisioningOperation);
        }
    }
    [SqlServerFact]
    public async Task Http_missing_bad_bearer_and_unknown_json_are_rejected_before_result_insert()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        using var factory = new PipelineFactory(sql.ConnectionString); var report = Report(seed);
        foreach (var client in new[] { factory.Client(seed.Credential.Token, "http"), factory.Client(null), factory.Client(new string('0', 64)) })
            using (client)
            using (var response = await client.PostAsJsonAsync(Route, report)) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var duplicateBearer = factory.Client(null);
        Assert.True(duplicateBearer.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", new[] { "Bearer " + seed.Credential.Token, "Bearer " + seed.Credential.Token }));
        using (var duplicate = await duplicateBearer.PostAsJsonAsync(Route, report)) Assert.Equal(HttpStatusCode.Unauthorized, duplicate.StatusCode);
        using var valid = factory.Client(seed.Credential.Token);
        using var invalidBody = await valid.PostAsJsonAsync(Route, new { report.ResultId, report.AuthorizationId, report.RetirementId, report.JobId, report.ArtifactId, report.FencingToken, path = "not-accepted" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidBody.StatusCode);
        // With an explicitly configured HTTPS port, middleware redirects before AgentToken auth.
        // The client must not follow; either safe rejection policy leaves SQL untouched.
        using var redirectFactory = new PipelineFactory(sql.ConnectionString, httpsPort: 443);
        using var http = redirectFactory.Client(seed.Credential.Token, "http");
        using var redirect = await http.PostAsJsonAsync(Route, report);
        Assert.Equal(HttpStatusCode.TemporaryRedirect, redirect.StatusCode);
        Assert.Equal("https://localhost" + Route, redirect.Headers.Location?.AbsoluteUri);
        await using var db = sql.NewContext(); Assert.Empty(await db.StageCleanupResults.ToListAsync());
    }
    [SqlServerFact]
    public async Task Rotation_and_revocation_are_enforced_by_real_agent_authentication_and_result_service()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        using var factory = new PipelineFactory(sql.ConnectionString); var report = Report(seed);
        AgentCredential rotated;
        await using (var db = sql.NewContext()) rotated = await new SqlAgentControl(db).RotateAsync(seed.Admin, seed.Credential.AgentId, Ct);
        using var oldClient = factory.Client(seed.Credential.Token);
        using (var old = await oldClient.PostAsJsonAsync(Route, report)) Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        using var current = factory.Client(rotated.Token);
        using (var accepted = await current.PostAsJsonAsync(Route, report)) Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        await using (var db = sql.NewContext()) await new SqlAgentControl(db).RevokeAsync(seed.Admin, rotated.AgentId, Ct);
        using (var revoked = await current.PostAsJsonAsync(Route, report)) Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        await using (var db = sql.NewContext()) Assert.Single(await db.StageCleanupResults.ToListAsync());
    }
    [SqlServerFact]
    public async Task Disabled_real_pipeline_returns_known_code_and_actual_transport_keeps_it_retryable()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        using var factory = new PipelineFactory(sql.ConnectionString, enabled: false); var report = Report(seed);
        using var client = factory.Client(seed.Credential.Token);
        using (var disabled = await client.PostAsJsonAsync(Route, report))
        {
            Assert.Equal(HttpStatusCode.Conflict, disabled.StatusCode);
            var body = await disabled.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("CleanupResultDisabled", body.GetProperty("code").GetString());
        }
        // Internal constructor injection is test-only: production uses normal HTTPS platform trust.
        using var transport = (AgentTransport)Activator.CreateInstance(typeof(AgentTransport), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: new object[] { new Uri("https://localhost"), seed.Credential.Token, factory.Server.CreateHandler() }, culture: null)!;
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => transport.ReportCleanupResultAsync(report, Ct));
        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
        Assert.IsNotType<CleanupResultConflictException>(error);
        await using var db = sql.NewContext(); Assert.Empty(await db.StageCleanupResults.ToListAsync());
    }
}
// end
