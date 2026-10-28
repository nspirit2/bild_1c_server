using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BuildSystem.Application;
using BuildSystem.Contracts;
using BuildSystem.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
namespace BuildSystem.ApiTests;
public sealed class CleanupApiFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(s => { s.RemoveAll<IAgentControl>(); s.AddScoped<IAgentControl, StubAgentControl>(); s.RemoveAll<IStageCleanup>(); s.AddScoped<IStageCleanup, CleanupStub>(); });
    }
    private sealed class CleanupStub : IStageCleanup
    {
        public Task<StageCleanupAuthorization> ClaimAsync(string credential, ClaimStageCleanup input, CancellationToken ct)
        {
            if (credential != "valid-test-token") throw new InvalidOperationException("Unexpected credential");
            if (input.ExpectedRetirementId == Guid.Empty || input.ExpectedArtifactId == Guid.Empty) throw new RequestRejectedException(400, "InvalidCleanupClaim");
            return Task.FromResult(new StageCleanupAuthorization(Guid.NewGuid(), new StageRetirementPlan(input.ExpectedRetirementId, Guid.NewGuid(), Guid.NewGuid(), input.ExpectedArtifactId, Guid.NewGuid(), Guid.NewGuid(), 1, new string('A', 64), 10, "dt"), DateTimeOffset.UtcNow));
        }
    }
}
public sealed class StageCleanupApiTests : IClassFixture<CleanupApiFactory>
{
    private readonly CleanupApiFactory factory;
    public StageCleanupApiTests(CleanupApiFactory factory) => this.factory = factory;
    private HttpClient Client(string scheme = "https", bool agent = true)
    {
        var c = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(scheme+"://localhost"), AllowAutoRedirect = false });
        if (agent) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "valid-test-token");
        return c;
    }
    private const string Route = "/api/agent/stage/cleanup-authorization";
    [Fact]
    public async Task HumanCannotClaimAgentCleanupAuthority()
    {
        using var c = Client(agent:false); c.DefaultRequestHeaders.Add("X-Test-User", "yes");
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync(Route, new ClaimStageCleanup(Guid.NewGuid(), Guid.NewGuid()))).StatusCode);
    }
    [Fact]
    public async Task HttpsAgentClaimNeedsNoHumanCsrfAndReturnsBoundedEnvelope()
    {
        using var c = Client(); var response = await c.PostAsJsonAsync(Route, new ClaimStageCleanup(Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(new[] { "authorizationId", "authorizedAt", "plan" }, json.EnumerateObject().Select(p => p.Name).Order().ToArray());
        using var http = Client("http"); Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync(Route, new ClaimStageCleanup(Guid.NewGuid(), Guid.NewGuid()))).StatusCode);
    }
    [Fact]
    public async Task UnknownPathFieldRejected()
    {
        using var c = Client(); Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync(Route, new { expectedRetirementId = Guid.NewGuid(), expectedArtifactId = Guid.NewGuid(), path = "arbitrary" })).StatusCode);
    }
    [Fact]
    public async Task DisabledBeforeDatabaseAccess()
    {
        using var db = new BuildSystemDbContext(new DbContextOptionsBuilder<BuildSystemDbContext>().UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
        var e = await Assert.ThrowsAsync<RequestRejectedException>(async () => await new SqlStageCleanup(db, new StageCleanupOptions()).ClaimAsync("credential", new(Guid.NewGuid(), Guid.NewGuid()), default));
        Assert.Equal(409, e.StatusCode); Assert.Equal("CleanupDisabled", e.Code);
    }
}
// end
