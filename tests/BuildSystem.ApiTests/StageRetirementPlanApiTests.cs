using System.Net;
using System.Net.Http.Headers;
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
public sealed class RetirementPlanFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(s => {
            s.RemoveAll<IAgentControl>(); s.AddScoped<IAgentControl, StubAgentControl>();
            s.RemoveAll<IStageRetirementPlans>(); s.AddScoped<IStageRetirementPlans, PlanStub>();
        });
    }
    private sealed class PlanStub : IStageRetirementPlans
    {
        public Task<StageRetirementPlan?> PollAsync(string credential, CancellationToken ct)
        {
            if (credential != "valid-test-token") throw new InvalidOperationException("Unexpected credential");
            return Task.FromResult<StageRetirementPlan?>(null);
        }
    }
}
public sealed class StageRetirementPlanApiTests : IClassFixture<RetirementPlanFactory>
{
    private readonly RetirementPlanFactory factory;
    public StageRetirementPlanApiTests(RetirementPlanFactory factory) => this.factory = factory;
    private HttpClient Client(string scheme = "https") => factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(scheme + "://localhost"), AllowAutoRedirect = false });
    [Fact]
    public async Task HumanCookiePrincipalCannotPollRetirementPlan()
    {
        using var client = Client(); client.DefaultRequestHeaders.Add("X-Test-User", "yes");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/agent/stage/retirement-plan", null)).StatusCode);
    }
    [Fact]
    public async Task AgentPollRequiresHttpsButNoHumanCsrf()
    {
        using var client = Client(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "valid-test-token");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/agent/stage/retirement-plan", null)).StatusCode);
        using var http = Client("http"); http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "valid-test-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsync("/api/agent/stage/retirement-plan", null)).StatusCode);
    }
    [Fact]
    public async Task DefaultDisabledRejectsBeforeDatabaseAccess()
    {
        using var db = new BuildSystemDbContext(new DbContextOptionsBuilder<BuildSystemDbContext>().UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
        var service = new SqlStageRetirementPlans(db, new StageRetirementOptions());
        var error = await Assert.ThrowsAsync<RequestRejectedException>(async () => await service.PollAsync("credential", default));
        Assert.Equal(409, error.StatusCode); Assert.Equal("RetirementDisabled", error.Code);
    }
}
// end
