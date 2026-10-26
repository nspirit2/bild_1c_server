using System.Net;
using System.Net.Http.Json;
using BuildSystem.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BuildSystem.ApiTests;

public sealed class StageStatusFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IStageStatus>();
            services.AddSingleton<IStageStatus, StatusStub>();
            services.RemoveAll<IAgentControl>();
            services.AddScoped<IAgentControl, StubAgentControl>();
        });
    }
    private sealed class StatusStub : IStageStatus
    {
        public Task<StageStatusView?> GetAsync(Actor actor, Guid environmentId, CancellationToken ct)
        {
            if (actor.UserId != Guid.Parse("11111111-1111-1111-1111-111111111111")) throw new InvalidOperationException("Unexpected actor");
            return Task.FromResult<StageStatusView?>(environmentId == Guid.Empty ? null : new(environmentId,
                Guid.NewGuid(), Guid.NewGuid(), "StageCompleted", "Requested", "Pending", "Held", 100,
                true, Guid.NewGuid(), "ContentVerified", "Completed", DateTimeOffset.UtcNow, "StageCompleted", DateTimeOffset.UtcNow));
        }
    }
}

public sealed class StageStatusEndpointTests : IClassFixture<StageStatusFactory>
{
    private readonly StageStatusFactory factory;
    public StageStatusEndpointTests(StageStatusFactory factory) => this.factory = factory;
    private HttpClient Client() => factory.CreateClient(new WebApplicationFactoryClientOptions
    { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

    [Fact]
    public async Task AnonymousAndMachineBearerCannotReadHumanStageStatus()
    {
        using var client = Client();
        var path = $"/api/test-environments/{Guid.NewGuid()}/stage-status";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid-test-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task AuthenticatedHumanCanReadMetadataWithoutCsrfButNotMutateStatus()
    {
        using var client = Client();
        client.DefaultRequestHeaders.Add("X-Test-User", "yes");
        var id = Guid.NewGuid(); var path = $"/api/test-environments/{id}/stage-status";
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var value = await response.Content.ReadFromJsonAsync<StageStatusView>();
        Assert.Equal(id, value!.EnvironmentId);
        Assert.Equal("StageCompleted", value.ReconciliationCode);
        Assert.True(value.Unsettled);
        Assert.Equal("Held", value.ReservationState);
        Assert.NotEqual(HttpStatusCode.OK, (await client.PostAsJsonAsync(path, new { reset = true })).StatusCode);
    }

    [Fact]
    public async Task InaccessibleOrMissingStageReturnsNotFound()
    {
        using var client = Client();
        client.DefaultRequestHeaders.Add("X-Test-User", "yes");
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/test-environments/{Guid.Empty}/stage-status")).StatusCode);
    }
}
// end
