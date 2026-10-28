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
public sealed class CleanupResultApiFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(s => { s.RemoveAll<IAgentControl>(); s.AddScoped<IAgentControl, StubAgentControl>(); s.RemoveAll<IStageCleanupResults>(); s.AddScoped<IStageCleanupResults, CleanupStub>(); });
    }
    private sealed class CleanupStub : IStageCleanupResults
    {
        public Task<StageCleanupResultReceipt> ReportAsync(string credential, ReportStageCleanup input, CancellationToken ct)
        {
            if (credential != "valid-test-token") throw new InvalidOperationException("Unexpected credential");
            if (input.RetirementId == Guid.Empty || input.ArtifactId == Guid.Empty) throw new RequestRejectedException(400, "InvalidCleanupResult");
            return Task.FromResult(new StageCleanupResultReceipt(input.ResultId, input.AuthorizationId, input.ArtifactId, "DeletedReported", DateTimeOffset.UtcNow));
        }
    }
}
public sealed class StageCleanupResultApiTests : IClassFixture<CleanupResultApiFactory>
{
    private readonly CleanupResultApiFactory factory;
    public StageCleanupResultApiTests(CleanupResultApiFactory factory) => this.factory = factory;
    private HttpClient Client(string scheme = "https", bool agent = true)
    {
        var c = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(scheme+"://localhost"), AllowAutoRedirect = false });
        if (agent) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "valid-test-token");
        return c;
    }
    private static ReportStageCleanup Input() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
    private const string Route = "/api/agent/stage/cleanup-result";
    [Fact]
    public async Task HumanCannotReportAgentCleanupResult()
    {
        using var c = Client(agent:false); c.DefaultRequestHeaders.Add("X-Test-User", "yes");
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync(Route, Input())).StatusCode);
    }
    [Fact]
    public async Task HttpsAgentReportNeedsNoHumanCsrfAndReturnsBoundedEnvelope()
    {
        using var c = Client(); var response = await c.PostAsJsonAsync(Route, Input());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(new[] { "artifactId", "authorizationId", "receivedAt", "resultId", "state" }, json.EnumerateObject().Select(p => p.Name).Order().ToArray());
        using var http = Client("http"); Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync(Route, Input())).StatusCode);
    }
    [Fact]
    public async Task UnknownPathFieldRejected()
    {
        using var c = Client(); Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync(Route, new { resultId = Guid.NewGuid(), authorizationId = Guid.NewGuid(), retirementId = Guid.NewGuid(), jobId = Guid.NewGuid(), artifactId = Guid.NewGuid(), fencingToken = 1, path = "arbitrary" })).StatusCode);
    }
    [Fact]
    public async Task DisabledBeforeDatabaseAccess()
    {
        using var db = new BuildSystemDbContext(new DbContextOptionsBuilder<BuildSystemDbContext>().UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
        var e = await Assert.ThrowsAsync<RequestRejectedException>(async () => await new SqlStageCleanupResults(db, new StageCleanupResultOptions()).ReportAsync("credential", Input(), default));
        Assert.Equal(409, e.StatusCode); Assert.Equal("CleanupResultDisabled", e.Code);
    }
}
// end
