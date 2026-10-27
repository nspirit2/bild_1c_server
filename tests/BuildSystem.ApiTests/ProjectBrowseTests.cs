using System.Net;
using System.Net.Http.Json;
using BuildSystem.Application;
using BuildSystem.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BuildSystem.ApiTests;
public sealed class ProjectBrowseFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IProjectBrowse>(); services.AddSingleton<IProjectBrowse, Stub>();
            services.RemoveAll<IAgentControl>(); services.AddScoped<IAgentControl, StubAgentControl>();
        });
    }
    private sealed class Stub : IProjectBrowse
    {
        public Task<ProjectBrowsePage> ListAsync(Actor actor, ProjectBrowseQuery query, CancellationToken ct = default)
        {
            if (actor.UserId != Guid.Parse("11111111-1111-1111-1111-111111111111")) throw new InvalidOperationException("Unexpected actor");
            if (query != new ProjectBrowseQuery() && query != new ProjectBrowseQuery(2, "test-cursor")) throw new InvalidOperationException("Unexpected query");
            return Task.FromResult(new ProjectBrowsePage([new(Guid.NewGuid(), "Тестовый проект")], null, DateTimeOffset.UtcNow));
        }
    }
}
public sealed class ProjectBrowseTests : IClassFixture<ProjectBrowseFactory>
{
    private readonly ProjectBrowseFactory factory;
    public ProjectBrowseTests(ProjectBrowseFactory factory) => this.factory = factory;
    private HttpClient Client() => factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
    [Fact]
    public async Task AnonymousAndMachineCannotListProjects()
    {
        using var client = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/projects/browse")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid-test-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/projects/browse")).StatusCode);
    }
    [Theory]
    [InlineData("")]
    [InlineData("?limit=2&cursor=test-cursor")]
    public async Task HumanCanBrowseWithForwardedQuery(string query)
    {
        using var client = Client(); client.DefaultRequestHeaders.Add("X-Test-User", "yes");
        var response = await client.GetAsync("/api/projects/browse" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<ProjectBrowsePage>();
        Assert.Equal("Тестовый проект", Assert.Single(page!.Items).Name);
    }
    [Theory]
    [InlineData(0, null)]
    [InlineData(101, null)]
    [InlineData(25, "malformed")]
    public async Task InvalidQueryRejectedBeforeSql(int limit, string? cursor)
    {
        using var db = new BuildSystemDbContext(new DbContextOptionsBuilder<BuildSystemDbContext>().UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => new SqlProjectBrowse(db).ListAsync(new Actor(Guid.NewGuid(), false), new ProjectBrowseQuery(limit, cursor), default));
        Assert.Equal(400, error.StatusCode);
    }
}
// end
