using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BuildSystem.Agent;
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

// Production HTTP/authentication/SQL services plus the real agent journal/coordinator/outbox.
// Only the target executor is fake: no live RAC, target SQL, restore work or production TLS network.
public sealed class SqlTargetObservationRoundTripTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly string Hash = new('A', 64);
    private const string MachineRoute = "/api/agents/target-observation-receipts";
    private sealed record Seed(Actor Admin, Actor Owner, Guid Project, Guid Pool, OrderReceipt Order,
        AgentCredential Credential, RestoreIntentReceipt Restore, TargetObservationEndpointMapping Mapping);

    // Minimal local copy of the existing private stage/Identity fixtures; preparation is not HTTP under test.
    private static async Task<Seed> PrepareAsync(SqlServerTestDatabase sql, bool stageOnly = false)
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
            SupportsDt = true, SupportsBak = true, IsolationVerified = !stageOnly,
            ScheduledJobsSuppressionVerified = !stageOnly, ResourceLimitVerified = !stageOnly });
        db.ProjectPools.Add(new ProjectPool { ProjectId = project, PoolId = pool, Enabled = true });
        db.EnvironmentQuotas.Add(new EnvironmentQuota { SubjectId = owner.UserId, MaxActiveEnvironments = 5,
            MaxConcurrentProvisioning = 5, MaxStorageBytes = 100000, MaxTtlDays = 30 });
        await db.SaveChangesAsync();
        var control = new SqlAgentControl(db);
        var ticket = await control.CreateEnrollmentAsync(admin, new(agent, "observation-http-worker", pool), Ct);
        await control.RegisterAsync(new(agent, ticket.EnrollmentId, ticket.Token, "observation-http-worker"), Ct);
        var credential = await control.ApproveAsync(admin, agent, Ct);
        var order = await new SqlEnvironmentOrders(db).CreateAsync(owner, "observation-http-order",
            new(project, "observation-http", stageOnly ? "archive-stage-only" : "archive", new(version), 7), Ct);
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
        private readonly string keys = Path.Combine(Path.GetTempPath(), "bs-observation-roundtrip-keys-" + Guid.NewGuid().ToString("N"));
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
                        !Path.GetFileName(resolved).StartsWith("bs-observation-roundtrip-keys-", StringComparison.Ordinal))
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
        Assert.True(cookie.Contains("secure", StringComparison.OrdinalIgnoreCase), "Session cookie must be Secure.");
        Assert.True(cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase), "Session cookie must be HttpOnly.");
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
    private static async Task<int> Audits(SqlServerTestDatabase sql)
    {
        await using var db = sql.NewContext(); return await db.AuditEvents.CountAsync();
    }
    private static async Task<string> Lifecycle(SqlServerTestDatabase sql, bool includeAssignments = true)
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

    private sealed class JournalFixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "bs-observation-roundtrip-journal-" + Guid.NewGuid().ToString("N"));
        public AgentJournal Journal { get; private set; }
        public JournalFixture() => Journal = new AgentJournal(directory);
        public void Reopen() { Journal.Dispose(); Journal = new AgentJournal(directory); }
        public void Dispose()
        {
            Journal.Dispose();
            var resolved = Path.GetFullPath(directory);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(root, StringComparison.Ordinal) ||
                !Path.GetFileName(resolved).StartsWith("bs-observation-roundtrip-journal-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected fixture journal cleanup path.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }

    private sealed class Executor(AgentJournal journal) : ITargetObservationExecutor
    {
        public int Calls { get; private set; }
        public Task<ReportTargetObservation> ObserveAsync(TargetObservationAssignment assignment, CancellationToken ct)
        {
            Calls++;
            Assert.Equal(assignment, journal.GetTargetObservationAssignment(assignment.ObservationRequestId));
            Assert.Null(journal.GetTargetObservationReport(assignment.ObservationRequestId));
            Assert.False(assignment.Executable); Assert.True(assignment.ScheduledJobsMustRemainBlocked);
            return Task.FromResult(new ReportTargetObservation(
                Guid.Parse("11111111-1111-4111-8111-111111111111"), assignment,
                Guid.Parse("22222222-2222-4222-8222-222222222222"), Guid.Parse("33333333-3333-4333-8333-333333333333"),
                TargetObservationOutcome.BothObservedAbsent, new DateTimeOffset(2026, 11, 3, 9, 30, 0, TimeSpan.Zero).AddTicks(1234567)));
        }
    }

    // This trusted wrapper never replaces server behavior: it records requests and can lose one committed ACK.
    private sealed class WireHandler(HttpMessageHandler server, bool loseFirstAck = false) : DelegatingHandler(server)
    {
        public List<string> Payloads { get; } = [];
        public List<HttpStatusCode> Statuses { get; } = [];
        public int Reads { get; private set; }
        private bool lost;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("https://localhost", request.RequestUri!.GetLeftPart(UriPartial.Authority));
            var receipt = request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == MachineRoute;
            if (receipt) Payloads.Add(await request.Content!.ReadAsStringAsync(ct));
            if (request.Method == HttpMethod.Get) Reads++;
            var response = await base.SendAsync(request, ct);
            Statuses.Add(response.StatusCode);
            Assert.Equal(request.RequestUri, response.RequestMessage?.RequestUri);
            if (receipt && loseFirstAck && !lost && response.StatusCode == HttpStatusCode.OK)
            {
                lost = true; response.Dispose();
                throw new HttpRequestException("secret-no-leak");
            }
            return response;
        }
    }

    private static TargetObservationHttpClient Transport(WireHandler handler, Seed seed) => new(handler,
        new TargetObservationHttpClientOptions { BaseUri = new Uri("https://localhost/"), AgentId = seed.Credential.AgentId },
        () => seed.Credential.Token);

    private static async Task Evidence(SqlServerTestDatabase sql, Seed seed, ReportTargetObservation report)
    {
        await using var db = sql.NewContext();
        var assignment = Assert.Single(await db.TargetObservations.ToListAsync());
        Assert.Equal(report.Assignment.ObservationRequestId, assignment.ObservationRequestId);
        Assert.Equal(report.Assignment.OperationId, assignment.OperationId);
        Assert.Equal(seed.Owner.UserId, assignment.RequestedBy);
        Assert.False(assignment.Executable); Assert.True(assignment.ScheduledJobsMustRemainBlocked);
        var receipt = Assert.Single(await db.TargetObservationReceipts.ToListAsync());
        Assert.Equal(report.ReceiptId, receipt.ReceiptId); Assert.Equal(assignment.ObservationRequestId, receipt.ObservationRequestId);
        Assert.Equal(report.RacObservationId, receipt.RacObservationId); Assert.Equal(report.SqlObservationId, receipt.SqlObservationId);
        Assert.Equal((int)report.Outcome, receipt.Outcome); Assert.Equal(report.ObservedAt, receipt.ObservedAt);
        Assert.False(receipt.Executable); Assert.True(receipt.ScheduledJobsMustRemainBlocked);
        var requested = Assert.Single(await db.AuditEvents.Where(a => a.Action == "TargetObservationRequested").ToListAsync());
        Assert.Equal(assignment.ObservationRequestId, requested.TargetId); Assert.Equal(seed.Owner.UserId, requested.ActorId);
        var recorded = Assert.Single(await db.AuditEvents.Where(a => a.Action == "TargetObservationRecorded").ToListAsync());
        Assert.Equal(receipt.ReceiptId, recorded.TargetId); Assert.Equal(seed.Credential.AgentId, recorded.ActorId);
    }

    [SqlServerFact]
    public async Task Explicit_stage_only_with_all_target_readiness_false_roundtrips_without_promotion()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql, stageOnly: true);
        using var factory = new CookieFactory(sql.ConnectionString, seed);
        using var owner = factory.Client(); await Login(factory, owner, seed.Owner);
        var assignment = await Request(owner, seed);
        using var local = new JournalFixture();
        var wire = new WireHandler(factory.Server.CreateHandler()); using var transport = Transport(wire, seed);
        var report = await new TargetObservationCoordinator(local.Journal, transport, new Executor(local.Journal)).ObserveAsync(assignment.ObservationRequestId);
        Assert.Equal(1, await new TargetObservationOutboxDelivery(local.Journal, transport).DeliverRequestAsync(assignment.ObservationRequestId));
        await using var db = sql.NewContext();
        var pool = await db.EnvironmentPools.SingleAsync();
        Assert.False(pool.IsolationVerified); Assert.False(pool.ResourceLimitVerified); Assert.False(pool.ScheduledJobsSuppressionVerified);
        var job = await db.EnvironmentJobs.SingleAsync(); Assert.Equal("ObserveArchiveStage", job.Kind); Assert.Equal("StageCompleted", job.State);
        Assert.Equal("Requested", (await db.TestEnvironments.SingleAsync()).State);
        Assert.False(assignment.Executable); Assert.True(assignment.ScheduledJobsMustRemainBlocked);
        Assert.Single(await db.TargetObservationReceipts.ToListAsync());
        Assert.False(report.Executable);
    }

    [SqlServerFact]
    public async Task Read_observe_durable_outbox_ack_and_reopened_history_execute_only_once()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        using var factory = new CookieFactory(sql.ConnectionString, seed);
        using var owner = factory.Client(); await Login(factory, owner, seed.Owner);
        var assignment = await Request(owner, seed); var before = await Lifecycle(sql); var audits = await Audits(sql);
        using var local = new JournalFixture();
        var wire = new WireHandler(factory.Server.CreateHandler()); using var transport = Transport(wire, seed);
        var executor = new Executor(local.Journal);
        var report = await new TargetObservationCoordinator(local.Journal, transport, executor).ObserveAsync(assignment.ObservationRequestId);
        Assert.Equal(assignment, report.Assignment); Assert.Equal(1, executor.Calls); Assert.Equal(1, wire.Reads);
        Assert.Equal(report, Assert.Single(local.Journal.PendingTargetObservations())); Assert.Empty(wire.Payloads);
        await using (var db = sql.NewContext()) Assert.Empty(await db.TargetObservationReceipts.ToListAsync());
        // Reopen before sending proves delivery uses durable SQLite evidence, not executor memory.
        local.Reopen(); Assert.Equal(report, Assert.Single(local.Journal.PendingTargetObservations()));
        Assert.Equal(1, await new TargetObservationOutboxDelivery(local.Journal, transport).DeliverAsync());
        Assert.Empty(local.Journal.PendingTargetObservations());
        local.Reopen(); var retryExecutor = new Executor(local.Journal);
        Assert.Equal(report, await new TargetObservationCoordinator(local.Journal, transport, retryExecutor).ObserveAsync(assignment.ObservationRequestId));
        Assert.Equal(0, retryExecutor.Calls); Assert.Equal(1, executor.Calls); Assert.Equal(2, wire.Reads);
        Assert.Equal(report, local.Journal.GetTargetObservationReport(assignment.ObservationRequestId));
        Assert.Empty(local.Journal.PendingTargetObservations());
        Assert.Equal(0, await new TargetObservationOutboxDelivery(local.Journal, transport).DeliverAsync());
        Assert.Single(wire.Payloads); Assert.All(wire.Statuses, status => Assert.Equal(HttpStatusCode.OK, status));
        await Evidence(sql, seed, report);
        Assert.Equal(audits + 1, await Audits(sql)); Assert.Equal(before, await Lifecycle(sql));
    }

    [SqlServerFact]
    public async Task Lost_committed_ack_retries_exact_payload_with_one_receipt_and_one_audit()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        using var factory = new CookieFactory(sql.ConnectionString, seed);
        using var owner = factory.Client(); await Login(factory, owner, seed.Owner);
        var assignment = await Request(owner, seed); var before = await Lifecycle(sql); var audits = await Audits(sql);
        using var local = new JournalFixture();
        var firstWire = new WireHandler(factory.Server.CreateHandler(), loseFirstAck: true);
        using var firstTransport = Transport(firstWire, seed); var executor = new Executor(local.Journal);
        var report = await new TargetObservationCoordinator(local.Journal, firstTransport, executor).ObserveAsync(assignment.ObservationRequestId);
        var failure = await Assert.ThrowsAsync<TargetObservationOutboxException>(() =>
            new TargetObservationOutboxDelivery(local.Journal, firstTransport).DeliverAsync());
        Assert.Equal("Target observation outbox delivery failed.", failure.Message); Assert.Null(failure.InnerException);
        Assert.DoesNotContain("secret-no-leak", failure.ToString());
        Assert.Equal(HttpStatusCode.OK, firstWire.Statuses.Last());
        await Evidence(sql, seed, report); Assert.Equal(audits + 1, await Audits(sql));
        local.Reopen(); Assert.Equal(report, Assert.Single(local.Journal.PendingTargetObservations()));
        var retryWire = new WireHandler(factory.Server.CreateHandler()); using var retryTransport = Transport(retryWire, seed);
        var retryExecutor = new Executor(local.Journal);
        Assert.Equal(report, await new TargetObservationCoordinator(local.Journal, retryTransport, retryExecutor).ObserveAsync(assignment.ObservationRequestId));
        Assert.Equal(0, retryExecutor.Calls); Assert.Equal(1, retryWire.Reads); Assert.Equal(1, executor.Calls);
        Assert.Equal(report, Assert.Single(local.Journal.PendingTargetObservations()));
        Assert.Equal(1, await new TargetObservationOutboxDelivery(local.Journal, retryTransport).DeliverAsync());
        Assert.Equal(Assert.Single(firstWire.Payloads), Assert.Single(retryWire.Payloads));
        local.Reopen(); Assert.Empty(local.Journal.PendingTargetObservations());
        Assert.Equal(report, local.Journal.GetTargetObservationReport(assignment.ObservationRequestId));
        Assert.Equal(0, await new TargetObservationOutboxDelivery(local.Journal, retryTransport).DeliverAsync());
        await Evidence(sql, seed, report);
        Assert.Equal(audits + 1, await Audits(sql)); Assert.Equal(before, await Lifecycle(sql));
    }

    [SqlServerFact]
    public async Task Revoked_agent_cannot_read_durable_history_or_deliver_pending_report()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        using var factory = new CookieFactory(sql.ConnectionString, seed);
        using var owner = factory.Client(); await Login(factory, owner, seed.Owner);
        var assignment = await Request(owner, seed);
        using var local = new JournalFixture();
        var wire = new WireHandler(factory.Server.CreateHandler()); using var transport = Transport(wire, seed);
        var executor = new Executor(local.Journal);
        var report = await new TargetObservationCoordinator(local.Journal, transport, executor).ObserveAsync(assignment.ObservationRequestId);
        await using (var db = sql.NewContext()) await new SqlAgentControl(db).RevokeAsync(seed.Admin, seed.Credential.AgentId, Ct);
        var before = await Lifecycle(sql); var audits = await Audits(sql);
        local.Reopen(); var retryExecutor = new Executor(local.Journal);
        var deniedRead = await Assert.ThrowsAsync<TargetObservationTransportException>(() => transport.ReadAsync(assignment.ObservationRequestId, Ct));
        Assert.Equal("Target observation transport failed.", deniedRead.Message); Assert.Null(deniedRead.InnerException);
        var deniedHistory = await Assert.ThrowsAsync<TargetObservationCoordinatorException>(() =>
            new TargetObservationCoordinator(local.Journal, transport, retryExecutor).ObserveAsync(assignment.ObservationRequestId));
        Assert.Equal("Target observation coordination failed.", deniedHistory.Message); Assert.Null(deniedHistory.InnerException);
        var deniedDelivery = await Assert.ThrowsAsync<TargetObservationOutboxException>(() =>
            new TargetObservationOutboxDelivery(local.Journal, transport).DeliverAsync());
        Assert.Equal("Target observation outbox delivery failed.", deniedDelivery.Message); Assert.Null(deniedDelivery.InnerException);
        Assert.Equal(1, executor.Calls); Assert.Equal(0, retryExecutor.Calls); Assert.Equal(3, wire.Reads);
        Assert.All(wire.Statuses.Skip(1), status => Assert.Equal(HttpStatusCode.Unauthorized, status));
        Assert.Single(wire.Payloads); local.Reopen();
        Assert.Equal(report, local.Journal.GetTargetObservationReport(assignment.ObservationRequestId));
        Assert.Equal(report, Assert.Single(local.Journal.PendingTargetObservations()));
        await using (var db = sql.NewContext())
        {
            Assert.Single(await db.TargetObservations.ToListAsync()); Assert.Empty(await db.TargetObservationReceipts.ToListAsync());
            Assert.Empty(await db.AuditEvents.Where(a => a.Action == "TargetObservationRecorded").ToListAsync());
        }
        Assert.Equal(audits, await Audits(sql)); Assert.Equal(before, await Lifecycle(sql));
    }
}
