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

public sealed class ArchiveCatalogBrowseFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IArchiveCatalogBrowse>(); services.AddSingleton<IArchiveCatalogBrowse, Stub>();
            services.RemoveAll<IAgentControl>(); services.AddScoped<IAgentControl, StubAgentControl>();
        });
    }
    private sealed class Stub : IArchiveCatalogBrowse
    {
        public Task<ArchiveCatalogPage> ListAsync(Actor actor, Guid projectId, ArchiveCatalogQuery query, CancellationToken ct = default)
        {
            if (actor.UserId != Guid.Parse("11111111-1111-1111-1111-111111111111")) throw new InvalidOperationException("Unexpected actor");
            if (projectId == Guid.Empty) throw new RequestRejectedException(404, "ProjectNotFound");
            if (query != new ArchiveCatalogQuery() && query != new ArchiveCatalogQuery(2, "test-cursor")) throw new InvalidOperationException("Unexpected query");
            return Task.FromResult(new ArchiveCatalogPage([new(projectId, "dt", 100, 1000)], null, DateTimeOffset.UtcNow));
        }
    }
}

public sealed class ArchiveCatalogBrowseTests : IClassFixture<ArchiveCatalogBrowseFactory>
{
    private readonly ArchiveCatalogBrowseFactory factory;
    public ArchiveCatalogBrowseTests(ArchiveCatalogBrowseFactory factory) => this.factory = factory;
    private HttpClient Client() => factory.CreateClient(new WebApplicationFactoryClientOptions
    { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

    [Fact]
    public async Task AnonymousAndMachineCannotReadHumanCatalog()
    {
        using var client = Client(); var path = $"/api/projects/{Guid.NewGuid()}/archive-versions";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid-test-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
    }
    [Theory]
    [InlineData("")]
    [InlineData("?limit=2&cursor=test-cursor")]
    public async Task HumanGetsMinimalMetadataWithForwardedQuery(string query)
    {
        using var client = Client(); client.DefaultRequestHeaders.Add("X-Test-User", "yes");
        var project = Guid.NewGuid(); var response = await client.GetAsync($"/api/projects/{project}/archive-versions{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<ArchiveCatalogPage>();
        Assert.Equal(project, Assert.Single(page!.Items).ArchiveVersionId);
        Assert.Null(page.NextCursor);
    }
    [Fact]
    public async Task InaccessibleProjectReturnsNotFound()
    {
        using var client = Client(); client.DefaultRequestHeaders.Add("X-Test-User", "yes");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{Guid.Empty}/archive-versions")).StatusCode);
    }
    [Theory]
    [InlineData(0, null)]
    [InlineData(101, null)]
    [InlineData(25, "malformed")]
    public async Task InvalidQueryRejectedBeforeSql(int limit, string? cursor)
    {
        using var db = new BuildSystemDbContext(new DbContextOptionsBuilder<BuildSystemDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => new SqlArchiveCatalogBrowse(db)
            .ListAsync(new Actor(Guid.NewGuid(), false), Guid.NewGuid(), new ArchiveCatalogQuery(limit, cursor), default));
        Assert.Equal(400, error.StatusCode);
    }
}
// end
