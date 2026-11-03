using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildSystem.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BuildSystem.ApiTests;

public sealed class RestoreIntentApiFactory : ApiFactory
{
    public RestoreIntentStub Stub { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IRestoreIntents>();
            services.AddSingleton<IRestoreIntents>(Stub);
            services.RemoveAll<IAgentControl>();
            services.AddSingleton<IAgentControl, StubAgentControl>();
        });
    }

    public sealed class RestoreIntentStub : IRestoreIntents
    {
        public int Calls;
        public Actor? LastActor;
        public Guid LastEnvironmentId;
        public RequestRestoreIntent? LastInput;
        public RequestRejectedException? Rejection;
        public Task<RestoreIntentReceipt> RequestAsync(Actor actor, Guid environmentId,
            RequestRestoreIntent input, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            LastActor = actor;
            LastEnvironmentId = environmentId;
            LastInput = input;
            if (Rejection is not null) throw Rejection;
            if (input.ExpectedStageJobId == Guid.Empty || input.ExpectedArtifactId == Guid.Empty)
                throw new RequestRejectedException(400, "InvalidRestoreIntent");
            return Task.FromResult(new RestoreIntentReceipt(Guid.NewGuid(), environmentId,
                input.ExpectedStageJobId, Guid.NewGuid(), input.ExpectedArtifactId, Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), "test_ib", "test_db", DateTimeOffset.UtcNow));
        }
    }
}

// HTTP/application-port proof only: no real Identity login, SQL persistence, restore grant or worker.
public sealed class RestoreIntentApiTests : IClassFixture<RestoreIntentApiFactory>
{
    private readonly RestoreIntentApiFactory factory;
    public RestoreIntentApiTests(RestoreIntentApiFactory factory) => this.factory = factory;
    private sealed record Csrf(string Token);
    private HttpClient Client(bool authenticated = true)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        if (authenticated) client.DefaultRequestHeaders.Add("X-Test-User", "yes");
        return client;
    }
    private static string Route(Guid id) => $"/api/test-environments/{id}/restore-intents";
    private static RequestRestoreIntent Input() => new(Guid.NewGuid(), Guid.NewGuid());
    private static async Task Token(HttpClient client)
    {
        var token = await client.GetFromJsonAsync<Csrf>("/api/auth/csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token!.Token);
    }

    [Fact]
    public async Task OwnerActorAndExactPinsComeFromAuthenticatedRequest()
    {
        using var client = Client();
        await Token(client);
        var environmentId = Guid.NewGuid();
        var input = Input();
        var before = factory.Stub.Calls;
        var response = await client.PostAsJsonAsync(Route(environmentId), input);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before + 1, factory.Stub.Calls);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), factory.Stub.LastActor!.UserId);
        Assert.False(factory.Stub.LastActor.IsAdministrator);
        Assert.Equal(environmentId, factory.Stub.LastEnvironmentId);
        Assert.Equal(input, factory.Stub.LastInput);
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(receipt.GetProperty("executable").GetBoolean());
        Assert.Equal(environmentId, receipt.GetProperty("environmentId").GetGuid());
        Assert.Equal(input.ExpectedStageJobId, receipt.GetProperty("stageJobId").GetGuid());
        Assert.Equal(input.ExpectedArtifactId, receipt.GetProperty("artifactId").GetGuid());
        Assert.Equal(new[] { "artifactId", "canonicalClusterId", "canonicalSqlServerId", "environmentId",
            "executable", "infobaseName", "requestedAt", "reservationId", "restoreIntentId", "sqlDbName",
            "stageJobId", "targetIncarnation" }, receipt.EnumerateObject().Select(x => x.Name).Order().ToArray());
    }

    [Fact]
    public async Task HumanWithoutCsrfNeverReachesService()
    {
        using var client = Client();
        var before = factory.Stub.Calls;
        var response = await client.PostAsJsonAsync(Route(Guid.NewGuid()), Input());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("InvalidCsrfToken", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(before, factory.Stub.Calls);
    }

    [Fact]
    public async Task AnonymousEvenWithCsrfNeverReachesService()
    {
        using var client = Client(false);
        await Token(client);
        var before = factory.Stub.Calls;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Route(Guid.NewGuid()), Input())).StatusCode);
        Assert.Equal(before, factory.Stub.Calls);
    }

    [Fact]
    public async Task ValidAgentTokenEvenWithCsrfCannotUseHumanRoute()
    {
        using var client = Client(false);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid-test-token");
        // Prove this token actually authenticates on the separate machine scheme.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/agent/poll", null)).StatusCode);
        await Token(client);
        var before = factory.Stub.Calls;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Route(Guid.NewGuid()), Input())).StatusCode);
        Assert.Equal(before, factory.Stub.Calls);
    }

    [Theory]
    [InlineData("ownerId")]
    [InlineData("canonicalClusterId")]
    [InlineData("canonicalSqlServerId")]
    [InlineData("canonicalMappings")]
    [InlineData("enabled")]
    [InlineData("executable")]
    [InlineData("releaseHold")]
    public async Task UnknownAuthorityFieldsRejectedBeforeService(string field)
    {
        using var client = Client();
        await Token(client);
        var before = factory.Stub.Calls;
        var body = new Dictionary<string, object>
        {
            ["expectedStageJobId"] = Guid.NewGuid(), ["expectedArtifactId"] = Guid.NewGuid(), [field] = true
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Route(Guid.NewGuid()), body)).StatusCode);
        Assert.Equal(before, factory.Stub.Calls);
    }

    [Fact]
    public async Task MissingPinMapsApplicationRejectionWithoutGrant()
    {
        using var client = Client();
        await Token(client);
        var response = await client.PostAsJsonAsync(Route(Guid.NewGuid()), new { expectedArtifactId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("InvalidRestoreIntent", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(404, "RestoreIntentNotFound")]
    [InlineData(409, "RestoreIntentDisabled")]
    [InlineData(409, "RestoreIntentConflict")]
    public async Task ApplicationRejectionsUseExistingCodeMiddleware(int status, string code)
    {
        using var client = Client();
        await Token(client);
        factory.Stub.Rejection = new RequestRejectedException(status, code);
        try
        {
            var response = await client.PostAsJsonAsync(Route(Guid.NewGuid()), Input());
            Assert.Equal(status, (int)response.StatusCode);
            Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        finally { factory.Stub.Rejection = null; }
    }
}

public sealed class RestoreIntentConfigurationTests
{
    private sealed class ConfigurationFactory(Dictionary<string, string> settings) : ApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(settings.Select(x =>
                    new KeyValuePair<string, string?>(x.Key, x.Value))));
        }
    }

    [Fact]
    public void AbsentConfigurationIsDisabledWithNoCanonicalMappings()
    {
        using var factory = new ConfigurationFactory([]);
        var options = factory.Services.GetRequiredService<RestoreIntentOptions>();
        Assert.False(options.Enabled);
        Assert.Empty(options.CanonicalMappings);
    }

    [Fact]
    public async Task DefaultDisabledRealServiceRejectsBeforeDatabaseAccess()
    {
        using var factory = new ConfigurationFactory([]);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-User", "yes");
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        var response = await client.PostAsJsonAsync($"/api/test-environments/{Guid.NewGuid()}/restore-intents",
            new RequestRestoreIntent(Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("RestoreIntentDisabled", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public void OperatorConfigurationBindsEnabledAndCanonicalMapping()
    {
        var poolId = Guid.NewGuid();
        var clusterId = Guid.NewGuid();
        var sqlId = Guid.NewGuid();
        using var factory = new ConfigurationFactory(new()
        {
            ["RestoreIntent:Enabled"] = "true",
            [$"RestoreIntent:CanonicalMappings:{poolId}:CanonicalClusterId"] = clusterId.ToString(),
            [$"RestoreIntent:CanonicalMappings:{poolId}:CanonicalSqlServerId"] = sqlId.ToString()
        });
        var options = factory.Services.GetRequiredService<RestoreIntentOptions>();
        Assert.True(options.Enabled);
        Assert.Equal(new RestoreIntentCanonicalMapping(clusterId, sqlId), options.CanonicalMappings[poolId]);
    }

    [Theory]
    [InlineData("not-a-guid", "CanonicalClusterId", "11111111-1111-1111-1111-111111111111")]
    [InlineData("00000000-0000-0000-0000-000000000000", "CanonicalClusterId", "11111111-1111-1111-1111-111111111111")]
    [InlineData("22222222-2222-2222-2222-222222222222", "CanonicalClusterId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("22222222-2222-2222-2222-222222222222", "CanonicalClusterId", "not-a-guid")]
    [InlineData("22222222-2222-2222-2222-222222222222", "UnexpectedAuthority", "true")]
    public void MalformedCanonicalConfigurationFailsStartupEvenWhenDisabled(string key, string property, string value)
    {
        using var factory = new ConfigurationFactory(new()
        {
            ["RestoreIntent:Enabled"] = "false",
            [$"RestoreIntent:CanonicalMappings:{key}:{property}"] = value,
            [$"RestoreIntent:CanonicalMappings:{key}:CanonicalSqlServerId"] = Guid.NewGuid().ToString()
        });
        Assert.ThrowsAny<InvalidOperationException>(() => { _ = factory.Services; });
    }
}
// end
