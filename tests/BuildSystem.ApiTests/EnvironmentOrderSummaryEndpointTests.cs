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

public sealed class EnvironmentOrderSummaryFactory : ApiFactory
{
    public static readonly Guid UserId = Guid.NewGuid();
    public static readonly Guid EnvironmentId = Guid.NewGuid();
    public List<(Actor Actor, Guid EnvironmentId)> Calls { get; } = [];
    public bool Missing { get; set; }
    public bool Inconsistent { get; set; }
    public string JobKind { get; set; } = "ProvisionArchive";
    public string Format { get; set; } = "dt";
    public int CreateCalls { get; private set; }
    public EnvironmentOrderSummaryView Fixture => new(EnvironmentId, Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "Metadata only", "Requested", Guid.Parse("22222222-2222-2222-2222-222222222222"), "Pending",
        DateTimeOffset.UnixEpoch, 1, Guid.Parse("33333333-3333-3333-3333-333333333333"), JobKind, "Pending",
        new(Guid.Parse("44444444-4444-4444-4444-444444444444"), Format, new string('a', 64), 1234));
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEnvironmentOrderSummary>();
            services.AddScoped<IEnvironmentOrderSummary>(_ => new Summary(this));
            services.RemoveAll<IEnvironmentOrders>();
            services.AddScoped<IEnvironmentOrders>(_ => new Orders(this));
            services.Configure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
                options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
                options.DefaultForbidScheme = IdentityConstants.ApplicationScheme;
            });
        });
    }
    public HttpClient Client(bool authenticated = true, bool administrator = false, string? actorId = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        if (!authenticated) return client;
        var options = Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
        var identity = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, actorId ?? UserId.ToString()),
            new(ClaimTypes.Name, "reader"), new(ClaimTypes.Role, administrator ? "Administrator" : "Developer")], IdentityConstants.ApplicationScheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), new AuthenticationProperties
        { IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5) }, IdentityConstants.ApplicationScheme);
        client.DefaultRequestHeaders.Add("Cookie", $"{options.Cookie.Name}={options.TicketDataFormat.Protect(ticket)}");
        return client;
    }
    private sealed class Summary(EnvironmentOrderSummaryFactory owner) : IEnvironmentOrderSummary
    {
        public Task<EnvironmentOrderSummaryView?> GetAsync(Actor actor, Guid environmentId, CancellationToken ct)
        {
            owner.Calls.Add((actor, environmentId));
            if (owner.Inconsistent) throw new RequestRejectedException(409, "EnvironmentOrderSummaryInconsistent",
                $"Internal detail {environmentId}: /private/archive/source.bak must never leave the port.");
            return Task.FromResult<EnvironmentOrderSummaryView?>(owner.Missing ? null : owner.Fixture);
        }
    }
    private sealed class Orders(EnvironmentOrderSummaryFactory owner) : IEnvironmentOrders
    {
        public Task<OrderReceipt> CreateAsync(Actor actor, string key, CreateEnvironmentRequest request, CancellationToken ct)
        { owner.CreateCalls++; throw new InvalidOperationException("Read must never create an order."); }
        public Task<RequestView?> GetRequestAsync(Actor actor, Guid id, CancellationToken ct) => throw new InvalidOperationException();
        public Task<IReadOnlyList<EnvironmentView>> ListEnvironmentsAsync(Actor actor, CancellationToken ct) => throw new InvalidOperationException();
        public Task<EnvironmentView?> GetEnvironmentAsync(Actor actor, Guid id, CancellationToken ct) => throw new InvalidOperationException();
    }
}

public sealed class EnvironmentOrderSummaryEndpointTests
{
    private static string Route(string? id = null) => $"/api/test-environments/{id ?? EnvironmentOrderSummaryFactory.EnvironmentId.ToString()}/order-summary";
    private static void Headers(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }
    [Theory]
    [InlineData(false, "ProvisionArchive", "dt")]
    [InlineData(true, "ProvisionArchive", "bak")]
    [InlineData(false, "ObserveArchiveStage", "bak")]
    [InlineData(true, "ObserveArchiveStage", "dt")]
    public async Task Cookie_get_without_csrf_returns_exact_summary_and_never_creates(bool administrator, string kind, string format)
    {
        using var factory = new EnvironmentOrderSummaryFactory { JobKind = kind, Format = format };
        using var client = factory.Client(administrator: administrator);
        using var response = await client.GetAsync(Route());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Headers(response);
        Assert.Equal((new Actor(EnvironmentOrderSummaryFactory.UserId, administrator), EnvironmentOrderSummaryFactory.EnvironmentId), Assert.Single(factory.Calls));
        Assert.Equal(0, factory.CreateCalls);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var root = json.RootElement;
        Assert.Equal(new[] { "createdAt", "displayName", "environmentId", "environmentState", "jobId", "jobKind", "jobState", "projectId", "requestId", "requestState", "source", "ttlDays" }, root.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.Equal(factory.Fixture.EnvironmentId, root.GetProperty("environmentId").GetGuid());
        Assert.Equal(factory.Fixture.ProjectId, root.GetProperty("projectId").GetGuid());
        Assert.Equal(factory.Fixture.RequestId, root.GetProperty("requestId").GetGuid());
        Assert.Equal(factory.Fixture.JobId, root.GetProperty("jobId").GetGuid());
        Assert.Equal("Metadata only", root.GetProperty("displayName").GetString());
        Assert.Equal("Requested", root.GetProperty("environmentState").GetString());
        Assert.Equal("Pending", root.GetProperty("requestState").GetString());
        Assert.Equal("Pending", root.GetProperty("jobState").GetString());
        Assert.Equal(kind, root.GetProperty("jobKind").GetString());
        Assert.Equal(DateTimeOffset.UnixEpoch, root.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(1, root.GetProperty("ttlDays").GetInt32());
        var source = root.GetProperty("source");
        Assert.Equal(new[] { "archiveVersionId", "format", "sha256", "sizeBytes" }, source.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.Equal(factory.Fixture.Source.ArchiveVersionId, source.GetProperty("archiveVersionId").GetGuid());
        Assert.Equal(format, source.GetProperty("format").GetString());
        Assert.Equal(new string('a', 64), source.GetProperty("sha256").GetString());
        Assert.Equal(1234, source.GetProperty("sizeBytes").GetInt64());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Anonymous_or_bearer_only_is_unauthorized_without_port(bool bearer)
    {
        using var factory = new EnvironmentOrderSummaryFactory(); using var client = factory.Client(authenticated: false);
        if (bearer) client.DefaultRequestHeaders.Authorization = new("Bearer", "not-human-cookie-authority");
        using var response = await client.GetAsync(Route());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); Headers(response); Assert.Empty(factory.Calls);
    }
    [Fact]
    public async Task Missing_or_invisible_summary_is_not_found()
    {
        using var factory = new EnvironmentOrderSummaryFactory { Missing = true }; using var client = factory.Client();
        using var response = await client.GetAsync(Route());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); Headers(response); Assert.Single(factory.Calls);
    }
    [Theory]
    [InlineData("not-a-guid", HttpStatusCode.NotFound)]
    [InlineData("00000000-0000-0000-0000-000000000000", HttpStatusCode.BadRequest)]
    public async Task Invalid_environment_id_never_reaches_port(string id, HttpStatusCode status)
    {
        using var factory = new EnvironmentOrderSummaryFactory(); using var client = factory.Client();
        using var response = await client.GetAsync(Route(id));
        Assert.Equal(status, response.StatusCode); Headers(response); Assert.Empty(factory.Calls);
        if (status == HttpStatusCode.BadRequest)
            Assert.Equal("{\"code\":\"InvalidEnvironmentOrderSummary\"}", await response.Content.ReadAsStringAsync());
    }
    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Invalid_cookie_actor_is_forbidden_without_port(string actorId)
    {
        using var factory = new EnvironmentOrderSummaryFactory(); using var client = factory.Client(actorId: actorId);
        using var response = await client.GetAsync(Route());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); Headers(response); Assert.Empty(factory.Calls);
    }
    [Fact]
    public async Task Inconsistent_summary_is_safe_conflict_with_only_fixed_code()
    {
        using var factory = new EnvironmentOrderSummaryFactory { Inconsistent = true }; using var client = factory.Client();
        using var response = await client.GetAsync(Route());
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Headers(response); Assert.Single(factory.Calls);
        Assert.Equal("{\"code\":\"EnvironmentOrderSummaryInconsistent\"}", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, factory.CreateCalls);
    }
}
