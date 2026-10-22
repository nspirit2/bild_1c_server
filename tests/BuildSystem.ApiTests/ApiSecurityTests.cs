using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using BuildSystem.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace BuildSystem.ApiTests;

// These tests replace only the application port. They do not prove SQL persistence or real Identity login.
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:BuildSystem", "Server=unused;Database=unused;Integrated Security=true;TrustServerCertificate=false");
        builder.UseSetting("Security:DataProtectionDirectory", Path.Combine(Path.GetTempPath(), "buildsystem-api-test-keys"));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEnvironmentOrders>();
            services.AddSingleton<IEnvironmentOrders, StubOrders>();
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "Test";
                options.DefaultChallengeScheme = "Test";
                options.DefaultForbidScheme = "Test";
            }).AddScheme<AuthenticationSchemeOptions, TestAuthentication>("Test", _ => { });
        });
    }
}

public sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.ContainsKey("X-Test-User")) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, "11111111-1111-1111-1111-111111111111"),
            new Claim(ClaimTypes.Name, "test-user"), new Claim(ClaimTypes.Role, "Developer") };
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
    }
}

public sealed class StubOrders : IEnvironmentOrders
{
    public Task<OrderReceipt> CreateAsync(Actor actor, string key, CreateEnvironmentRequest request, CancellationToken ct)
    {
        OrderInput.Validate(key, request);
        return Task.FromResult(new OrderReceipt(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
    }
    public Task<RequestView?> GetRequestAsync(Actor actor, Guid id, CancellationToken ct) => Task.FromResult<RequestView?>(null);
    public Task<IReadOnlyList<EnvironmentView>> ListEnvironmentsAsync(Actor actor, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<EnvironmentView>>([]);
    public Task<EnvironmentView?> GetEnvironmentAsync(Actor actor, Guid id, CancellationToken ct) => Task.FromResult<EnvironmentView?>(null);
}

public sealed class ApiSecurityTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;
    public ApiSecurityTests(ApiFactory factory) => this.factory = factory;
    private HttpClient Client(bool authenticated = true)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        if (authenticated) client.DefaultRequestHeaders.Add("X-Test-User", "yes");
        return client;
    }
    private static object ValidOrder() => new { projectId = Guid.NewGuid(), displayName = "Test base",
        mode = "archive", source = new { archiveVersionId = Guid.NewGuid() }, ttlDays = 7 };
    private static async Task Csrf(HttpClient client)
    {
        var json = await client.GetFromJsonAsync<CsrfResponse>("/api/auth/csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", json!.Token);
    }
    private sealed record CsrfResponse(string Token);

    [Fact]
    public async Task AnonymousCannotReadEnvironments()
    {
        using var client = Client(false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/test-environments")).StatusCode);
    }
    [Fact]
    public async Task MutationWithoutCsrfIsRejectedBeforeApplicationService()
    {
        using var client = Client();
        client.DefaultRequestHeaders.Add("Idempotency-Key", "test-key");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/environment-requests", ValidOrder())).StatusCode);
    }
    [Fact]
    public async Task ValidCsrfAndInputReturnAcceptedLocation()
    {
        using var client = Client();
        await Csrf(client);
        client.DefaultRequestHeaders.Add("Idempotency-Key", "test-key");
        var response = await client.PostAsJsonAsync("/api/environment-requests", ValidOrder());
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.StartsWith("/api/environment-requests/", response.Headers.Location!.OriginalString);
    }
    [Fact]
    public async Task UnknownJsonPropertiesCannotInjectOwnerOrServer()
    {
        using var client = Client();
        await Csrf(client);
        client.DefaultRequestHeaders.Add("Idempotency-Key", "test-key");
        var response = await client.PostAsJsonAsync("/api/environment-requests", new
        { projectId = Guid.NewGuid(), displayName = "Test", mode = "archive", source = new { archiveVersionId = Guid.NewGuid() }, ttlDays = 7, ownerId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
    [Fact]
    public async Task MissingIdempotencyKeyIsRejected()
    {
        using var client = Client();
        await Csrf(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/environment-requests", ValidOrder())).StatusCode);
    }
    [Fact]
    public async Task DeveloperCannotCreateUsersOrGrantMembership()
    {
        using var client = Client();
        await Csrf(client);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/admin/users",
            new { email = "someone@example.invalid", password = "NotARealSecret123!" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/admin/project-memberships",
            new { userId = Guid.NewGuid(), projectId = Guid.NewGuid() })).StatusCode);
    }
    [Fact]
    public async Task AnonymousCannotAccessAdminEvenWithCsrf()
    {
        using var client = Client(false);
        await Csrf(client);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/admin/users",
            new { email = "someone@example.invalid", password = "NotARealSecret123!" })).StatusCode);
    }
    [Fact]
    public async Task LoginAndLogoutRequireCsrf()
    {
        using var anonymous = Client(false);
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostAsJsonAsync("/api/auth/login",
            new { userName = "test", password = "test" })).StatusCode);
        using var authenticated = Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await authenticated.PostAsJsonAsync("/api/auth/logout", new { })).StatusCode);
    }
    [Fact]
    public async Task NoPublicRegistrationEndpoint()
    {
        using var client = Client(false);
        await Csrf(client);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/auth/register", new { })).StatusCode);
    }
}
// end
