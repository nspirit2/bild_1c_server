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

public sealed class AgentEndpointTests : IClassFixture<AgentApiFactory>
{
    private readonly AgentApiFactory factory;
    public AgentEndpointTests(AgentApiFactory factory) => this.factory = factory;
    private HttpClient Client(string scheme = "https") => factory.CreateClient(new WebApplicationFactoryClientOptions
    { BaseAddress = new Uri(scheme + "://localhost"), AllowAutoRedirect = false });

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
    public async Task EnrollmentAcceptsMachineJsonWithoutCookieCsrf()
    {
        using var client = Client();
        var response = await client.PostAsJsonAsync("/api/agent/register", new RegisterAgent(Guid.NewGuid(), Guid.NewGuid(), "enrollment-test-token", "test-agent"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }
}
// end
