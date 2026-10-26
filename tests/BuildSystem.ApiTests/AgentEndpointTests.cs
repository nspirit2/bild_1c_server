using System.Net;
using System.Net.Http.Json;
using BuildSystem.Application;
using BuildSystem.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BuildSystem.ApiTests;

public sealed class AgentApiFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAgentControl>();
            services.AddScoped<IAgentControl, StubAgentControl>();
            services.RemoveAll<IArchiveCatalog>();
            services.AddScoped<IArchiveCatalog, StubArchiveCatalog>();
            services.RemoveAll<IArtifactReceipts>();
            services.AddScoped<IArtifactReceipts, StubArtifactReceipts>();
            services.RemoveAll<IArchiveStageControl>();
            services.AddScoped<IArchiveStageControl, StubStageControl>();
            services.RemoveAll<IStageCompletion>();
            services.AddScoped<IStageCompletion, StubStageCompletion>();
        });
    }
}
public sealed class StubAgentControl : IAgentControl
{
    private static readonly Guid Agent = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public Task<Guid?> AuthenticateAsync(string token, CancellationToken ct) => Task.FromResult<Guid?>(token == "valid-test-token" ? Agent : null);
    public Task<LeaseGrant?> PollAsync(Guid id, CancellationToken ct) => Task.FromResult<LeaseGrant?>(null);
    public Task SetCapabilitiesAsync(Guid id, AgentCapabilities capabilities, CancellationToken ct) => Task.CompletedTask;
    public Task<LeaseStatus> HeartbeatAsync(Guid id, LeaseHeartbeat value, CancellationToken ct) => Task.FromResult(new LeaseStatus(DateTimeOffset.UtcNow.AddMinutes(1)));
    public Task<EventReceipt> ReportAsync(Guid id, ReportAgentEvent value, CancellationToken ct) => Task.FromResult(new EventReceipt(value.EventId));
    public Task<RegistrationReceipt> RegisterAsync(RegisterAgent value, CancellationToken ct) => Task.FromResult(new RegistrationReceipt(value.AgentId, "Pending"));
    public Task<EnrollmentTicket> CreateEnrollmentAsync(Actor actor, CreateEnrollment value, CancellationToken ct) => throw new NotSupportedException();
    public Task<AgentCredential> ApproveAsync(Actor actor, Guid id, CancellationToken ct) => throw new NotSupportedException();
    public Task RevokeAsync(Actor actor, Guid id, CancellationToken ct) => throw new NotSupportedException();
    public Task<AgentCredential> RotateAsync(Actor actor, Guid id, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<AgentView>> ListAsync(Actor actor, CancellationToken ct) => throw new NotSupportedException();
}

public sealed class StubArchiveCatalog : IArchiveCatalog
{
    public Task<ArchiveInventoryReceipt> ReportAsync(string credential, ArchiveInventoryReport report, CancellationToken ct)
    {
        if (credential != "valid-test-token") throw new InvalidOperationException("Unexpected credential");
        return Task.FromResult(new ArchiveInventoryReceipt(report.RootId, []));
    }
}

public sealed class StubArtifactReceipts : IArtifactReceipts
{
    public Task<StagedArtifactReceipt> ReportAsync(string credential, ReportStagedArtifact report, CancellationToken ct)
    {
        if (credential != "valid-test-token") throw new InvalidOperationException("Unexpected credential");
        return Task.FromResult(new StagedArtifactReceipt(report.ArtifactId, "ContentVerified"));
    }
}

public sealed class StubStageControl : IArchiveStageControl
{
    public Task<StageLeaseGrant?> PollAsync(string credential, CancellationToken ct)
    {
        if (credential != "valid-test-token") throw new InvalidOperationException("Unexpected credential");
        return Task.FromResult<StageLeaseGrant?>(null);
    }
    public Task<LeaseStatus> HeartbeatAsync(string credential, LeaseHeartbeat input, CancellationToken ct)
    {
        if (credential != "valid-test-token") throw new InvalidOperationException("Unexpected credential");
        return Task.FromResult(new LeaseStatus(DateTimeOffset.UtcNow.AddMinutes(1)));
    }
}

public sealed class StubStageCompletion : IStageCompletion
{
    public Task<StageCompletionReceipt> CompleteAsync(string credential, CompleteStage input, CancellationToken ct)
    {
        if (credential != "valid-test-token") throw new InvalidOperationException("Unexpected credential");
        return Task.FromResult(new StageCompletionReceipt(input.JobId, input.FencingToken, input.ArtifactId, "StageCompleted"));
    }
}

public sealed class AgentEndpointTests : IClassFixture<AgentApiFactory>
{
    private readonly AgentApiFactory factory;
    public AgentEndpointTests(AgentApiFactory factory) => this.factory = factory;
    private HttpClient Client(string scheme = "https") => factory.CreateClient(new WebApplicationFactoryClientOptions
    { BaseAddress = new Uri(scheme + "://localhost"), AllowAutoRedirect = false });

    [Fact]
    public async Task CompletionRequiresHttpsAgentAndReturnsExactIdentity()
    {
        var input = new CompleteStage(Guid.NewGuid(), 3, Guid.NewGuid());
        using var client = Client();
        client.DefaultRequestHeaders.Add("X-Test-User", "yes");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/agent/stage/complete", input)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid-test-token");
        var response = await client.PostAsJsonAsync("/api/agent/stage/complete", input);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new StageCompletionReceipt(input.JobId, input.FencingToken, input.ArtifactId, "StageCompleted"),
            await response.Content.ReadFromJsonAsync<StageCompletionReceipt>());
        using var plain = Client("http");
        plain.DefaultRequestHeaders.Authorization = new("Bearer", "valid-test-token");
        Assert.NotEqual(HttpStatusCode.OK, (await plain.PostAsJsonAsync("/api/agent/stage/complete", input)).StatusCode);
    }

    [Theory]
    [InlineData("/api/agent/stage/poll")]
    [InlineData("/api/agent/stage/heartbeat")]
    public async Task StageRoutesRequireHttpsMachineIdentityAndForwardCurrentBearer(string path)
    {
        using var client = Client();
        var heartbeat = new LeaseHeartbeat(Guid.NewGuid(), 1);
        client.DefaultRequestHeaders.Add("X-Test-User", "yes");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(path, heartbeat)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid-test-token");
        Assert.Equal(path.EndsWith("poll") ? HttpStatusCode.NoContent : HttpStatusCode.OK,
            (await client.PostAsJsonAsync(path, heartbeat)).StatusCode);
        using var plain = Client("http");
        plain.DefaultRequestHeaders.Authorization = new("Bearer", "valid-test-token");
        var response = await plain.PostAsJsonAsync(path, heartbeat);
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task CookieUserCannotPollAsAgent()
    {
        using var client = Client();
        client.DefaultRequestHeaders.Add("X-Test-User", "yes");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/agent/poll", null)).StatusCode);
    }
    [Fact]
    public async Task BearerCanPollWithoutCsrfButCannotUseHumanEndpoints()
    {
        using var client = Client();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid-test-token");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/agent/poll", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/test-environments")).StatusCode);
    }
    [Fact]
    public async Task InvalidTokenRejectedWithoutCsrfBypassForHumanApi()
    {
        using var client = Client();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/agent/poll", null)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-User", "yes");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/logout", new { })).StatusCode);
    }
    [Fact]
    public async Task AgentTokenCannotApproveItself()
    {
        using var client = Client();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid-test-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync($"/api/admin/agents/{Guid.NewGuid()}/approve", null)).StatusCode);
    }
    [Fact]
    public async Task InventoryRequiresAgentTokenButDoesNotRequireCookieCsrf()
    {
        using var client = Client();
        var report = new ArchiveInventoryReport(Guid.NewGuid(), []);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/agent/archives/inventory", report)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-User", "yes");
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/agent/archives/inventory", report)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid-test-token");
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/api/agent/archives/inventory", report)).StatusCode);
    }
    [Fact]
    public async Task ArtifactReceiptIsAgentOnlyAndHttpsOnly()
    {
        var report = new ReportStagedArtifact(Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), new string('A', 64), 100, "dt");
        using var client = Client();
        client.DefaultRequestHeaders.Add("X-Test-User", "yes");
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/agent/artifacts/staged", report)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid-test-token");
        var response = await client.PostAsJsonAsync("/api/agent/artifacts/staged", report);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(report.ArtifactId, (await response.Content.ReadFromJsonAsync<StagedArtifactReceipt>())!.ArtifactId);
        using var plain = Client("http");
        plain.DefaultRequestHeaders.Authorization = new("Bearer", "valid-test-token");
        Assert.NotEqual(HttpStatusCode.OK,
            (await plain.PostAsJsonAsync("/api/agent/artifacts/staged", report)).StatusCode);
    }
    [Fact]
    public async Task EnrollmentAcceptsMachineJsonWithoutCookieCsrf()
    {
        using var client = Client();
        var response = await client.PostAsJsonAsync("/api/agent/register", new RegisterAgent(Guid.NewGuid(), Guid.NewGuid(), "enrollment-test-token", "test-agent"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }
}
// end
