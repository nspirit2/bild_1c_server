using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BuildSystem.ApiTests;

public sealed class TargetObservationApiFactory : ApiFactory
{
    public ObservationStub Observations { get; } = new();
    public ReceiptStub Receipts { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ITargetObservations>();
            services.AddSingleton<ITargetObservations>(Observations);
            services.RemoveAll<ITargetObservationReceipts>();
            services.AddSingleton<ITargetObservationReceipts>(Receipts);
            services.RemoveAll<IAgentControl>();
            services.AddSingleton<IAgentControl, StubAgentControl>();
        });
    }

    public sealed class ObservationStub : ITargetObservations
    {
        public int Calls;
        public Actor? Actor;
        public Guid EnvironmentId;
        public Guid RestoreIntentId;
        public RequestRejectedException? Rejection;
        public Task<TargetObservationAssignment> RequestAsync(Actor actor, Guid environmentId,
            Guid expectedRestoreIntentId, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Actor = actor;
            EnvironmentId = environmentId;
            RestoreIntentId = expectedRestoreIntentId;
            if (Rejection is not null) throw Rejection;
            if (expectedRestoreIntentId == Guid.Empty) throw new RequestRejectedException(400, "InvalidTargetObservation");
            return Task.FromResult(Assignment(environmentId, expectedRestoreIntentId));
        }
    }

    public sealed class ReceiptStub : ITargetObservationReceipts
    {
        public int Calls;
        public string? Credential;
        public ReportTargetObservation? Report;
        public RequestRejectedException? Rejection;
        public Task<TargetObservationReceipt> ReportAsync(string credential, ReportTargetObservation report, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Credential = credential;
            Report = report;
            if (Rejection is not null) throw Rejection;
            return Task.FromResult(new TargetObservationReceipt(report.ReceiptId, report.Assignment.ObservationRequestId, "Recorded"));
        }
    }

    public static TargetObservationAssignment Assignment(Guid environmentId, Guid restoreIntentId) => new(
        Guid.NewGuid(), restoreIntentId, Guid.NewGuid(), Guid.NewGuid(), environmentId, Guid.NewGuid(), 7,
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
}

// HTTP/application-port proofs use the existing fake human scheme, not real Identity cookies or SQL.
// AgentToken authentication itself remains real; only its credential-validation application port is replaced.
public sealed class TargetObservationApiTests : IClassFixture<TargetObservationApiFactory>
{
    private readonly TargetObservationApiFactory factory;
    public TargetObservationApiTests(TargetObservationApiFactory factory) => this.factory = factory;
    private const string MachineRoute = "/api/agents/target-observation-receipts";
    private static string HumanRoute(Guid id) => $"/api/test-environments/{id}/target-observations";
    private HttpClient Client(bool human = false)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        if (human) client.DefaultRequestHeaders.Add("X-Test-User", "yes");
        return client;
    }
    private static async Task Csrf(HttpClient client)
    {
        var json = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", json.GetProperty("token").GetString());
    }
    private static ReportTargetObservation Report() => new(Guid.NewGuid(),
        TargetObservationApiFactory.Assignment(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid(), Guid.NewGuid(),
        TargetObservationOutcome.BothObservedAbsent, DateTimeOffset.Parse("2026-11-03T10:00:00Z"));

    [Fact]
    public async Task HumanRouteForwardsAuthenticatedActorEnvironmentAndExactRestoreIntentPin()
    {
        using var client = Client(true);
        await Csrf(client);
        var id = Guid.NewGuid();
        var input = new RequestTargetObservation(Guid.NewGuid());
        var before = factory.Observations.Calls;
        var response = await client.PostAsJsonAsync(HumanRoute(id), input);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before + 1, factory.Observations.Calls);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), factory.Observations.Actor!.UserId);
        Assert.False(factory.Observations.Actor.IsAdministrator);
        Assert.Equal(id, factory.Observations.EnvironmentId);
        Assert.Equal(input.ExpectedRestoreIntentId, factory.Observations.RestoreIntentId);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, json.GetProperty("environmentId").GetGuid());
        Assert.Equal(input.ExpectedRestoreIntentId, json.GetProperty("restoreIntentId").GetGuid());
        Assert.False(json.GetProperty("executable").GetBoolean());
        Assert.True(json.GetProperty("scheduledJobsMustRemainBlocked").GetBoolean());
    }

    [Fact]
    public async Task HumanRequiresCsrfBeforeService()
    {
        using var client = Client(true);
        var before = factory.Observations.Calls;
        var response = await client.PostAsJsonAsync(HumanRoute(Guid.NewGuid()), new RequestTargetObservation(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("InvalidCsrfToken", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(before, factory.Observations.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnonymousOrValidMachineCredentialCannotAuthenticateHumanRoute(bool machine)
    {
        using var client = Client();
        if (machine) client.DefaultRequestHeaders.Authorization = new("Bearer", "valid-test-token");
        await Csrf(client);
        var before = factory.Observations.Calls;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(HumanRoute(Guid.NewGuid()),
            new RequestTargetObservation(Guid.NewGuid()))).StatusCode);
        Assert.Equal(before, factory.Observations.Calls);
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("Bearer", "wrong-test-token", false)]
    [InlineData("AgentToken", "valid-test-token", false)]
    [InlineData(null, null, true)]
    public async Task MachineRequiresActualBearerCredentialNotHumanAuthority(string? scheme, string? token, bool human)
    {
        using var client = Client(human);
        if (scheme is not null) client.DefaultRequestHeaders.Authorization = new(scheme, token);
        if (human)
        {
            // Ambient human-shaped authority and a cookie must not satisfy the explicit machine scheme.
            client.DefaultRequestHeaders.Add("Cookie", "__Host-BuildSystem=untrusted-test-cookie");
            await Csrf(client);
        }
        var before = factory.Receipts.Calls;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(MachineRoute, Report())).StatusCode);
        Assert.Equal(before, factory.Receipts.Calls);
    }

    [Fact]
    public async Task ExactMachineRouteForwardsCredentialAndAllPinsWithoutCsrf()
    {
        using var client = Client();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid-test-token");
        var report = Report();
        var before = factory.Receipts.Calls;
        var response = await client.PostAsJsonAsync(MachineRoute, report);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before + 1, factory.Receipts.Calls);
        Assert.Equal("valid-test-token", factory.Receipts.Credential);
        Assert.Equal(report, factory.Receipts.Report);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(report.ReceiptId, json.GetProperty("receiptId").GetGuid());
        Assert.Equal(report.Assignment.ObservationRequestId, json.GetProperty("observationRequestId").GetGuid());
        Assert.Equal(new[] { "executable", "observationRequestId", "receiptId", "scheduledJobsMustRemainBlocked", "state" },
            json.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.False(json.GetProperty("executable").GetBoolean());
        Assert.True(json.GetProperty("scheduledJobsMustRemainBlocked").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedJsonRejectedBeforeEitherService(bool machine)
    {
        using var client = Client(!machine);
        if (machine) client.DefaultRequestHeaders.Authorization = new("Bearer", "valid-test-token");
        else await Csrf(client);
        var before = machine ? factory.Receipts.Calls : factory.Observations.Calls;
        using var content = new StringContent("{\"invalid\":", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(machine ? MachineRoute : HumanRoute(Guid.NewGuid()), content)).StatusCode);
        Assert.Equal(before, machine ? factory.Receipts.Calls : factory.Observations.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownAuthorityFieldRejectedBeforeService(bool machine)
    {
        using var client = Client(!machine);
        if (machine) client.DefaultRequestHeaders.Authorization = new("Bearer", "valid-test-token");
        else await Csrf(client);
        var body = machine ? JsonSerializer.SerializeToNode(Report(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject()
            : new System.Text.Json.Nodes.JsonObject { ["expectedRestoreIntentId"] = Guid.NewGuid().ToString() };
        body["ownerId"] = Guid.NewGuid();
        var before = machine ? factory.Receipts.Calls : factory.Observations.Calls;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(machine ? MachineRoute : HumanRoute(Guid.NewGuid()), body)).StatusCode);
        Assert.Equal(before, machine ? factory.Receipts.Calls : factory.Observations.Calls);
    }

    [Fact]
    public async Task GetterOnlyAuthorityFlagsCannotBecomeExecutionPermission()
    {
        using var client = Client();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid-test-token");
        var body = JsonSerializer.SerializeToNode(Report(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        body["executable"] = true;
        body["scheduledJobsMustRemainBlocked"] = false;
        body["assignment"]!["executable"] = true;
        body["assignment"]!["scheduledJobsMustRemainBlocked"] = false;
        var response = await client.PostAsJsonAsync(MachineRoute, body);
        // Known getter-only JSON members are ignored, not unknown-member rejections.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(factory.Receipts.Report!.Executable);
        Assert.True(factory.Receipts.Report.ScheduledJobsMustRemainBlocked);
        Assert.False(factory.Receipts.Report.Assignment.Executable);
        Assert.True(factory.Receipts.Report.Assignment.ScheduledJobsMustRemainBlocked);
    }

    [Theory]
    [InlineData(false, 404, "TargetObservationNotFound")]
    [InlineData(true, 409, "TargetObservationReceiptConflict")]
    public async Task ExistingRejectionMiddlewareReturnsOnlySafeCode(bool machine, int status, string code)
    {
        using var client = Client(!machine);
        if (machine) client.DefaultRequestHeaders.Authorization = new("Bearer", "valid-test-token");
        else await Csrf(client);
        if (machine) factory.Receipts.Rejection = new RequestRejectedException(status, code);
        else factory.Observations.Rejection = new RequestRejectedException(status, code);
        try
        {
            var response = machine ? await client.PostAsJsonAsync(MachineRoute, Report())
                : await client.PostAsJsonAsync(HumanRoute(Guid.NewGuid()), new RequestTargetObservation(Guid.NewGuid()));
            Assert.Equal(status, (int)response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(new[] { "code" }, json.EnumerateObject().Select(p => p.Name));
            Assert.Equal(code, json.GetProperty("code").GetString());
        }
        finally { factory.Observations.Rejection = null; factory.Receipts.Rejection = null; }
    }
}

public sealed class TargetObservationConfigurationTests
{
    private sealed class ConfigurationFactory(Dictionary<string, string?> settings) : ApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
        }
    }

    [Fact]
    public async Task DefaultOffRealServiceRejectsBeforeDatabaseAccess()
    {
        using var factory = new ConfigurationFactory([]);
        var options = factory.Services.GetRequiredService<TargetObservationOptions>();
        Assert.False(options.Enabled);
        Assert.Empty(options.EndpointMappings);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-User", "yes");
        var json = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", json.GetProperty("token").GetString());
        var response = await client.PostAsJsonAsync($"/api/test-environments/{Guid.NewGuid()}/target-observations",
            new RequestTargetObservation(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("TargetObservationDisabled", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public void LateOperatorConfigurationBindsEnabledAndExactEndpointMapping()
    {
        var pool = Guid.NewGuid();
        var rac = Guid.NewGuid();
        var sql = Guid.NewGuid();
        using var factory = new ConfigurationFactory(new()
        {
            ["TargetObservation:Enabled"] = "true",
            [$"TargetObservation:EndpointMappings:{pool}:RacEndpointMappingId"] = rac.ToString(),
            [$"TargetObservation:EndpointMappings:{pool}:SqlEndpointMappingId"] = sql.ToString()
        });
        var options = factory.Services.GetRequiredService<TargetObservationOptions>();
        Assert.True(options.Enabled);
        Assert.Equal(new TargetObservationEndpointMapping(rac, sql), options.EndpointMappings[pool]);
    }

    [Theory]
    [InlineData("not-a-guid", "RacEndpointMappingId", "11111111-1111-1111-1111-111111111111")]
    [InlineData("00000000-0000-0000-0000-000000000000", "RacEndpointMappingId", "11111111-1111-1111-1111-111111111111")]
    [InlineData("22222222-2222-2222-2222-222222222222", "RacEndpointMappingId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("22222222-2222-2222-2222-222222222222", "RacEndpointMappingId", "not-a-guid")]
    [InlineData("22222222-2222-2222-2222-222222222222", "RacEndpointMappingId", "")]
    [InlineData("22222222-2222-2222-2222-222222222222", "UnknownAuthority", "true")]
    public void InvalidConfigurationFailsStartupEvenWhenDisabled(string pool, string field, string value)
    {
        using var factory = new ConfigurationFactory(new()
        {
            ["TargetObservation:Enabled"] = "false",
            [$"TargetObservation:EndpointMappings:{pool}:{field}"] = value,
            [$"TargetObservation:EndpointMappings:{pool}:SqlEndpointMappingId"] = Guid.NewGuid().ToString()
        });
        Assert.ThrowsAny<InvalidOperationException>(() => { _ = factory.Services; });
    }

    [Fact]
    public void NormalizedDuplicatePoolKeysFailStartup()
    {
        var pool = Guid.NewGuid();
        var settings = new Dictionary<string, string?>();
        foreach (var key in new[] { pool.ToString("D"), pool.ToString("N") })
        {
            settings[$"TargetObservation:EndpointMappings:{key}:RacEndpointMappingId"] = Guid.NewGuid().ToString();
            settings[$"TargetObservation:EndpointMappings:{key}:SqlEndpointMappingId"] = Guid.NewGuid().ToString();
        }
        using var factory = new ConfigurationFactory(settings);
        Assert.ThrowsAny<ArgumentException>(() => { _ = factory.Services; });
    }

    [Fact]
    public void MoreThan1024MappingsFailStartup()
    {
        var settings = new Dictionary<string, string?>();
        for (var i = 0; i < 1025; i++)
        {
            var pool = Guid.NewGuid();
            settings[$"TargetObservation:EndpointMappings:{pool}:RacEndpointMappingId"] = Guid.NewGuid().ToString();
            settings[$"TargetObservation:EndpointMappings:{pool}:SqlEndpointMappingId"] = Guid.NewGuid().ToString();
        }
        using var factory = new ConfigurationFactory(settings);
        Assert.ThrowsAny<InvalidOperationException>(() => { _ = factory.Services; });
    }
}
// end
