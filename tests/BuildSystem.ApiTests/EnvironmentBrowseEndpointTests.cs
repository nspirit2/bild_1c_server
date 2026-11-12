using System.Net;
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

public sealed class EnvironmentBrowseFactory : ApiFactory
{
    public static readonly Guid UserId = Guid.NewGuid();
    public List<(Actor Actor, EnvironmentBrowseQuery Query)> Calls { get; } = [];
    public bool Reject { get; set; }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEnvironmentBrowse>();
            services.AddScoped<IEnvironmentBrowse>(_ => new Browse(this));
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
    private sealed class Browse(EnvironmentBrowseFactory owner) : IEnvironmentBrowse
    {
        public Task<EnvironmentBrowsePage> ListAsync(Actor actor, EnvironmentBrowseQuery query, CancellationToken ct = default)
        {
            owner.Calls.Add((actor, query));
            if (owner.Reject) throw new RequestRejectedException(400, "InvalidEnvironmentBrowseQuery");
            return Task.FromResult(new EnvironmentBrowsePage(
                [new EnvironmentView(Guid.NewGuid(), Guid.NewGuid(), "Metadata only", "Requested", null)], "opaque_cursor", DateTimeOffset.UnixEpoch));
        }
    }
}

public sealed class EnvironmentBrowseEndpointTests
{
    private const string Route = "/api/test-environments/browse";
    private static void Headers(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cookie_get_forwards_actor_and_bounded_query_with_exact_metadata(bool administrator)
    {
        using var factory = new EnvironmentBrowseFactory(); using var client = factory.Client(administrator: administrator);
        using var response = await client.GetAsync(Route + "?limit=2&cursor=opaque_cursor");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Headers(response);
        Assert.Equal((new Actor(EnvironmentBrowseFactory.UserId, administrator), new EnvironmentBrowseQuery(2, "opaque_cursor")), Assert.Single(factory.Calls));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "items", "nextCursor", "observedAt" }, json.RootElement.EnumerateObject().Select(x => x.Name).Order().ToArray());
        var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(new[] { "displayName", "expiresAt", "id", "projectId", "state" }, item.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.Equal("Requested", item.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("expiresAt").ValueKind);
    }
    [Fact]
    public async Task Default_query_and_application_cursor_rejection_are_preserved()
    {
        using var factory = new EnvironmentBrowseFactory(); using var client = factory.Client();
        using var response = await client.GetAsync(Route); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new EnvironmentBrowseQuery(), Assert.Single(factory.Calls).Query);
        factory.Reject = true;
        using var rejected = await client.GetAsync(Route + "?cursor=bad_actor_cursor");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode); Headers(rejected);
        using var json = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        Assert.Equal("InvalidEnvironmentBrowseQuery", json.RootElement.GetProperty("code").GetString());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Anonymous_and_bearer_only_cannot_reach_port(bool bearer)
    {
        using var factory = new EnvironmentBrowseFactory(); using var client = factory.Client(authenticated: false);
        if (bearer) client.DefaultRequestHeaders.Authorization = new("Bearer", "not-human-cookie-authority");
        using var response = await client.GetAsync(Route);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); Headers(response); Assert.Empty(factory.Calls);
    }
    [Theory]
    [InlineData("?unknown=1")]
    [InlineData("?limit=1&limit=2")]
    [InlineData("?cursor=a&cursor=b")]
    [InlineData("?Limit=2")]
    [InlineData("?limit=")]
    [InlineData("?limit=0")]
    [InlineData("?limit=101")]
    [InlineData("?limit=-1")]
    [InlineData("?limit=%2B1")]
    [InlineData("?limit=%201")]
    [InlineData("?limit=1.0")]
    [InlineData("?limit=9999999999999999999")]
    [InlineData("?limit=%D9%A1")]
    [InlineData("?cursor=")]
    [InlineData("?cursor=a%3D")]
    [InlineData("?cursor=%20")]
    public async Task Invalid_http_query_never_reaches_port(string query)
    {
        using var factory = new EnvironmentBrowseFactory(); using var client = factory.Client();
        using var response = await client.GetAsync(Route + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Headers(response); Assert.Empty(factory.Calls);
    }
    [Fact]
    public async Task Oversized_cursor_is_rejected_before_port()
    {
        using var factory = new EnvironmentBrowseFactory(); using var client = factory.Client();
        using var response = await client.GetAsync(Route + "?cursor=" + new string('a', 513));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Empty(factory.Calls);
    }
}