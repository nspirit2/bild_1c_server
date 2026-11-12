using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using BuildSystem.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BuildSystem.ApiTests;

// HTTP contracts only: SQL owner/current-membership filtering is not implemented by this fake.
public sealed class EnvironmentHistoryFactory : ApiFactory
{
    public static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly EnvironmentView Environment = new(Guid.NewGuid(), Guid.NewGuid(), "Metadata only", "Requested", null);
    public static readonly RequestView Request = new(Guid.NewGuid(), Environment.Id, Guid.NewGuid(), "Requested", Environment.DisplayName, Environment.ProjectId, DateTimeOffset.UnixEpoch);
    public List<(string Method, Actor Actor, Guid? Id)> Calls { get; } = [];
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEnvironmentOrders>();
            services.AddScoped<IEnvironmentOrders>(_ => new ReadOrders(Calls));
            services.Configure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
                options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
                options.DefaultForbidScheme = IdentityConstants.ApplicationScheme;
            });
        });
    }
    public HttpClient Client(bool authenticated = true, bool administrator = false)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        if (!authenticated) return client;
        var options = Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
        var identity = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, UserId.ToString()),
            new(ClaimTypes.Name, "reader"), new(ClaimTypes.Role, administrator ? "Administrator" : "Developer")], IdentityConstants.ApplicationScheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), new AuthenticationProperties
        { IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5) }, IdentityConstants.ApplicationScheme);
        client.DefaultRequestHeaders.Add("Cookie", $"{options.Cookie.Name}={options.TicketDataFormat.Protect(ticket)}");
        return client;
    }
    private sealed class ReadOrders(List<(string Method, Actor Actor, Guid? Id)> calls) : IEnvironmentOrders
    {
        public Task<OrderReceipt> CreateAsync(Actor actor, string key, CreateEnvironmentRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("Read requests must never create or dispatch orders.");
        public Task<IReadOnlyList<EnvironmentView>> ListEnvironmentsAsync(Actor actor, CancellationToken ct)
        { calls.Add(("list", actor, null)); return Task.FromResult<IReadOnlyList<EnvironmentView>>([Environment]); }
        public Task<EnvironmentView?> GetEnvironmentAsync(Actor actor, Guid id, CancellationToken ct)
        { calls.Add(("environment", actor, id)); return Task.FromResult(id == Environment.Id ? Environment : null); }
        public Task<RequestView?> GetRequestAsync(Actor actor, Guid id, CancellationToken ct)
        { calls.Add(("request", actor, id)); return Task.FromResult(id == Request.Id ? Request : null); }
    }
}

public sealed class EnvironmentHistoryReadTests
{
    private static readonly string[] EnvironmentFields = ["displayName", "expiresAt", "id", "projectId", "state"];
    private static void Headers(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cookie_reads_forward_actor_and_exact_metadata_without_csrf(bool administrator)
    {
        using var factory = new EnvironmentHistoryFactory();
        using var client = factory.Client(administrator: administrator); // No CSRF bootstrap or header.
        var env = EnvironmentHistoryFactory.Environment;
        var request = EnvironmentHistoryFactory.Request;
        using var list = await client.GetAsync("/api/test-environments");
        using var detail = await client.GetAsync($"/api/test-environments/{env.Id}");
        using var receipt = await client.GetAsync($"/api/environment-requests/{request.Id}");
        foreach (var response in new[] { list, detail, receipt })
        { Assert.Equal(HttpStatusCode.OK, response.StatusCode); Headers(response); }
        using var listJson = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        using var detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        using var requestJson = JsonDocument.Parse(await receipt.Content.ReadAsStringAsync());
        Assert.Equal(EnvironmentFields, Assert.Single(listJson.RootElement.EnumerateArray()).EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(EnvironmentFields, detailJson.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(new[] { "createdAt", "displayName", "environmentId", "id", "jobId", "projectId", "state" },
            requestJson.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(env, await detail.Content.ReadFromJsonAsync<EnvironmentView>());
        Assert.Equal(request, await receipt.Content.ReadFromJsonAsync<RequestView>());
        Assert.Equal("Requested", detailJson.RootElement.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, detailJson.RootElement.GetProperty("expiresAt").ValueKind);
        Assert.Equal("Requested", requestJson.RootElement.GetProperty("state").GetString());
        Assert.Equal(new[] { ("list", new Actor(EnvironmentHistoryFactory.UserId, administrator), (Guid?)null),
            ("environment", new Actor(EnvironmentHistoryFactory.UserId, administrator), (Guid?)env.Id),
            ("request", new Actor(EnvironmentHistoryFactory.UserId, administrator), (Guid?)request.Id) }, factory.Calls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Anonymous_or_bearer_only_cannot_reach_read_port(bool bearer)
    {
        using var factory = new EnvironmentHistoryFactory();
        using var client = factory.Client(authenticated: false);
        if (bearer) client.DefaultRequestHeaders.Authorization = new("Bearer", "agent-token-not-human-authority");
        foreach (var path in new[] { "/api/test-environments", $"/api/test-environments/{Guid.NewGuid()}", $"/api/environment-requests/{Guid.NewGuid()}" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); Headers(response);
        }
        Assert.Empty(factory.Calls);
    }
    [Theory]
    [InlineData("test-environments", "environment")]
    [InlineData("environment-requests", "request")]
    public async Task Missing_is_404_and_invalid_guid_never_reaches_port(string route, string method)
    {
        using var factory = new EnvironmentHistoryFactory();
        using var client = factory.Client();
        var missing = Guid.NewGuid();
        using var absent = await client.GetAsync($"/api/{route}/{missing}");
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode); Headers(absent);
        var call = Assert.Single(factory.Calls);
        Assert.Equal((method, new Actor(EnvironmentHistoryFactory.UserId, false), (Guid?)missing), call);
        using var invalid = await client.GetAsync($"/api/{route}/not-a-guid");
        Assert.Equal(HttpStatusCode.NotFound, invalid.StatusCode); Headers(invalid);
        Assert.Single(factory.Calls);
    }
}
