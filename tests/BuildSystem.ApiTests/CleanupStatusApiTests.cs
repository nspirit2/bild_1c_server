using System.Net;
using System.Net.Http.Json;
using BuildSystem.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
namespace BuildSystem.ApiTests;
public sealed class CleanupStatusApiFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(s => { s.RemoveAll<ICleanupStatus>(); s.AddSingleton<ICleanupStatus, StatusStub>(); });
    }
    private sealed class StatusStub : ICleanupStatus
    {
        public Task<CleanupStatusView?> GetAsync(Actor actor, Guid environmentId, CancellationToken ct)
            => Task.FromResult<CleanupStatusView?>(environmentId == Guid.Empty ? null : new(environmentId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "StagingReleased", 10, 0, 10, DateTimeOffset.UtcNow));
    }
}
public sealed class CleanupStatusApiTests : IClassFixture<CleanupStatusApiFactory>
{
    private readonly CleanupStatusApiFactory factory;
    public CleanupStatusApiTests(CleanupStatusApiFactory factory) => this.factory = factory;
    private HttpClient Client(bool authenticated = true)
    {
        var c = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        if (authenticated) c.DefaultRequestHeaders.Add("X-Test-User", "yes");
        return c;
    }
    private static string Route(Guid id) => $"/api/test-environments/{id}/cleanup-status";
    [Fact]
    public async Task AnonymousCannotReadCleanupMetadata()
    {
        using var c = Client(false); Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(Route(Guid.NewGuid()))).StatusCode);
    }
    [Fact]
    public async Task MissingScopeReturnsNotFound()
    {
        using var c = Client(); Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync(Route(Guid.Empty))).StatusCode);
    }
    [Fact]
    public async Task ReadNeedsNoCsrfAndReportsHistoricalAndNetBytesSeparately()
    {
        using var c = Client(); var id = Guid.NewGuid(); var r = await c.GetAsync(Route(id)); Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var v = await r.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(new[] { "artifactId", "authorizationId", "cleanupState", "environmentId", "heldStagingBytes", "jobId", "observedAt", "releaseId", "releasedStagingBytes", "reservedStagingBytes", "resultId", "retirementId" }.Order(StringComparer.Ordinal), v.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(id, v.GetProperty("environmentId").GetGuid()); Assert.Equal(10, v.GetProperty("reservedStagingBytes").GetInt64());
        Assert.Equal(0, v.GetProperty("heldStagingBytes").GetInt64()); Assert.Equal("StagingReleased", v.GetProperty("cleanupState").GetString());
    }
}
// end
