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

public sealed class StageOperationsFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IStageOperations>(); services.AddSingleton<IStageOperations, Stub>();
            services.RemoveAll<IAgentControl>(); services.AddScoped<IAgentControl, StubAgentControl>();
        });
    }
    private sealed class Stub : IStageOperations
    {
        public Task<StageOperationsPage> ListAsync(Actor actor, StageOperationsQuery query, CancellationToken ct)
        {
            if (actor.UserId != Guid.Parse("11111111-1111-1111-1111-111111111111")) throw new InvalidOperationException("Unexpected actor");
            if (query == new StageOperationsQuery(25, null, "Cancelled"))
            {
                var row = new StageOperationView(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                    "Cancelled", "Cancelled", "Released", 100, false, DateTimeOffset.UnixEpoch)
                { CancellationCode = "CancelledBeforeFirstLease" };
                return Task.FromResult(new StageOperationsPage([row], null, DateTimeOffset.UnixEpoch));
            }
            if (query == new StageOperationsQuery(25, null, "Pending"))
                return Task.FromResult(new StageOperationsPage([new StageOperationView(Guid.NewGuid(), Guid.NewGuid(),
                    Guid.NewGuid(), "Pending", "Requested", "Held", 100, true, DateTimeOffset.UnixEpoch)],
                    null, DateTimeOffset.UnixEpoch));
            if (query.Limit != 25 || query.Cursor is not null || query.State is not null)
            {
                if (query != new StageOperationsQuery(2, "test-cursor", "Leased")) throw new InvalidOperationException("Unexpected query");
            }
            return Task.FromResult(new StageOperationsPage([], null, DateTimeOffset.UtcNow));
        }
    }
}

public sealed class StageOperationsEndpointTests : IClassFixture<StageOperationsFactory>
{
    private readonly StageOperationsFactory factory;
    public StageOperationsEndpointTests(StageOperationsFactory factory) => this.factory = factory;
    private HttpClient Client() => factory.CreateClient(new WebApplicationFactoryClientOptions
    { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

    [Fact]
    public async Task AnonymousAndMachineIdentityCannotListHumanStageOperations()
    {
        using var client = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/stage-operations")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid-test-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/stage-operations")).StatusCode);
    }

    [Theory]
    [InlineData("/api/stage-operations")]
    [InlineData("/api/stage-operations?limit=2&cursor=test-cursor&state=Leased")]
    public async Task HumanReadForwardsBoundedQueryAndNeedsNoCsrf(string path)
    {
        using var client = Client(); client.DefaultRequestHeaders.Add("X-Test-User", "yes");
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<StageOperationsPage>();
        Assert.Empty(page!.Items); Assert.Null(page.NextCursor);
    }

    [Theory]
    [InlineData(0, null, null)]
    [InlineData(101, null, null)]
    [InlineData(25, "malformed", null)]
    [InlineData(25, null, "Ready")]
    public async Task InvalidApplicationQueryRejectedBeforeDatabaseAccess(int limit, string? cursor, string? state)
    {
        using var db = new BuildSystemDbContext(new DbContextOptionsBuilder<BuildSystemDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => new SqlStageOperations(db)
            .ListAsync(new Actor(Guid.NewGuid(), false), new StageOperationsQuery(limit, cursor, state), default));
        Assert.Equal(400, error.StatusCode);
    }

    [Fact]
    public async Task CancellationFilterReturnsOnlyThePublicConfirmationMarker()
    {
        using var client = Client(); client.DefaultRequestHeaders.Add("X-Test-User", "yes");
        using var response = await client.GetAsync("/api/stage-operations?state=Cancelled");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var row = Assert.Single(payload.GetProperty("items").EnumerateArray());
        Assert.Equal("CancelledBeforeFirstLease", row.GetProperty("cancellationCode").GetString());
        Assert.Equal(new[] { "cancellationCode", "createdAt", "environmentId", "environmentState", "jobId",
            "jobState", "requestId", "reservationState", "stagingBytes", "unsettled" },
            row.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task NonCancellationRowKeepsItsExistingWireShape()
    {
        using var client = Client(); client.DefaultRequestHeaders.Add("X-Test-User", "yes");
        using var response = await client.GetAsync("/api/stage-operations?state=Pending");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var row = Assert.Single(payload.GetProperty("items").EnumerateArray());
        Assert.False(row.TryGetProperty("cancellationCode", out _));
    }

    [Fact]
    public async Task NonNumericLimitIsHttpBadRequest()
    {
        using var client = Client(); client.DefaultRequestHeaders.Add("X-Test-User", "yes");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/stage-operations?limit=bad")).StatusCode);
    }
}
// end
