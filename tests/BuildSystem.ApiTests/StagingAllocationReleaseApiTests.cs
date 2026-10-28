using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Contracts;
using BuildSystem.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace BuildSystem.ApiTests;

// Replaces only the application port and authentication; no real Identity/SQL proof.
public sealed class StagingReleaseApiFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IStagingAllocationRelease>();
            services.AddScoped<IStagingAllocationRelease, StagingReleaseStub>();
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "TestAdmin";
                options.DefaultChallengeScheme = "TestAdmin";
                options.DefaultForbidScheme = "TestAdmin";
            }).AddScheme<AuthenticationSchemeOptions, StagingReleaseAuthentication>("TestAdmin", _ => { });
        });
    }

    private sealed class StagingReleaseStub : IStagingAllocationRelease
    {
        public Task<StagingAllocationReleaseReceipt> ReleaseAsync(Actor actor, Guid resultId, ReleaseStagingAllocation input, CancellationToken ct)
        {
            Assert.True(actor.IsAdministrator);
            Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), actor.UserId);
            return Task.FromResult(new StagingAllocationReleaseReceipt(Guid.NewGuid(), resultId, input.ExpectedReservationId, 123, DateTimeOffset.UtcNow));
        }
    }
}

public sealed class StagingReleaseAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var role = Request.Headers["X-Test-Role"].ToString();
        if (role is not ("Administrator" or "Developer")) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, "11111111-1111-1111-1111-111111111111"),
            new Claim(ClaimTypes.Name, "release-test-user"), new Claim(ClaimTypes.Role, role) };
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
    }
}

public sealed class StagingAllocationReleaseApiTests : IClassFixture<StagingReleaseApiFactory>
{
    private readonly StagingReleaseApiFactory factory;
    public StagingAllocationReleaseApiTests(StagingReleaseApiFactory factory) => this.factory = factory;
    private HttpClient Client(string? role = "Administrator")
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        if (role is not null) client.DefaultRequestHeaders.Add("X-Test-Role", role);
        return client;
    }
    private static string Route(Guid resultId) => $"/api/admin/stage-cleanup-results/{resultId:D}/release-staging";
    private static ReleaseStagingAllocation Input() => new(Guid.NewGuid(), Guid.NewGuid());
    private sealed record CsrfResponse(string Token);
    private static async Task Csrf(HttpClient client)
    {
        var csrf = await client.GetFromJsonAsync<CsrfResponse>("/api/auth/csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf!.Token);
    }

    [Fact]
    public async Task AnonymousWithValidCsrfGets401()
    {
        using var client = Client(null); await Csrf(client);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Route(Guid.NewGuid()), Input())).StatusCode);
    }
    [Fact]
    public async Task DeveloperWithValidCsrfGets403()
    {
        using var client = Client("Developer"); await Csrf(client);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Route(Guid.NewGuid()), Input())).StatusCode);
    }
    [Fact]
    public async Task AdministratorWithoutCsrfGets400()
    {
        using var client = Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Route(Guid.NewGuid()), Input())).StatusCode);
    }
    [Fact]
    public async Task AdministratorWithValidCsrfGetsExactFiveFieldReceipt()
    {
        using var client = Client(); await Csrf(client);
        var resultId = Guid.NewGuid(); var input = Input();
        var response = await client.PostAsJsonAsync(Route(resultId), input);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "releaseId", "releasedAt", "releasedBytes", "reservationId", "resultId" }, json.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(resultId, json.GetProperty("resultId").GetGuid());
        Assert.Equal(input.ExpectedReservationId, json.GetProperty("reservationId").GetGuid());
        Assert.Equal(123, json.GetProperty("releasedBytes").GetInt64());
    }
    [Fact]
    public async Task UnknownBodyFieldGets400()
    {
        using var client = Client(); await Csrf(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Route(Guid.NewGuid()), new
        { expectedAuthorizationId = Guid.NewGuid(), expectedReservationId = Guid.NewGuid(), releasedBytes = 999999 })).StatusCode);
    }
    [Fact]
    public async Task DisabledServiceRejectsBeforeAnySqlAccess()
    {
        using var db = new BuildSystemDbContext(new DbContextOptionsBuilder<BuildSystemDbContext>().UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
        var ex = await Assert.ThrowsAsync<RequestRejectedException>(async () => await new SqlStagingAllocationRelease(db, new StagingAllocationReleaseOptions())
            .ReleaseAsync(new Actor(Guid.NewGuid(), true), Guid.NewGuid(), Input(), default));
        Assert.Equal(409, ex.StatusCode); Assert.Equal("StagingReleaseDisabled", ex.Code);
    }
}
// end
