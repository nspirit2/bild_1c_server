using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using BuildSystem.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace BuildSystem.ApiTests;

// HTTP contract only. Current database roles and persisted history are covered by SQL tests.
public sealed class TargetRegistrationEvidenceEndpointTests
{
    private sealed class Factory(bool enabled = true, bool realCookie = false) : ApiFactory
    {
        public readonly ReadsStub Reads = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("TargetCreationDiagnostics:Enabled", enabled.ToString());
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITargetRegistrationEvidenceReads>();
                services.AddSingleton<ITargetRegistrationEvidenceReads>(Reads);
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = realCookie ? IdentityConstants.ApplicationScheme : "RegistrationEvidenceTest";
                    options.DefaultChallengeScheme = realCookie ? IdentityConstants.ApplicationScheme : "RegistrationEvidenceTest";
                    options.DefaultForbidScheme = realCookie ? IdentityConstants.ApplicationScheme : "RegistrationEvidenceTest";
                }).AddScheme<AuthenticationSchemeOptions, RegistrationEvidenceAuthentication>("RegistrationEvidenceTest", _ => { });
            });
        }
    }

    private sealed class ReadsStub : ITargetRegistrationEvidenceReads
    {
        public int Calls;
        public Actor? Actor;
        public Guid Id;
        public bool Recorded;
        public RequestRejectedException? Rejection;
        public Task<TargetRegistrationEvidenceView> GetAsync(Actor actor, Guid operationId, CancellationToken ct)
        {
            Calls++; Actor = actor; Id = operationId;
            if (Rejection is not null) throw Rejection;
            return Task.FromResult(new TargetRegistrationEvidenceView(operationId, Recorded, null));
        }
    }

    private static string Route(string id) => $"/api/admin/target-creation-operations/{id}/registration-evidence";
    private static string Route(Guid id) => Route(id.ToString("D"));
    private static HttpClient Client(Factory factory, string? role = "Administrator")
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        if (role is not null) client.DefaultRequestHeaders.Add("X-Test-Role", role);
        return client;
    }
    private static void NoCache(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("no-cache", response.Headers.Pragma.Select(p => p.Name));
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("Developer", HttpStatusCode.Forbidden)]
    public async Task OnlyAdministratorCanRead(string? role, HttpStatusCode expected)
    {
        using var factory = new Factory(); using var client = Client(factory, role);
        Assert.Equal(expected, (await client.GetAsync(Route(Guid.NewGuid()))).StatusCode);
        Assert.Equal(0, factory.Reads.Calls);
    }

    [Fact]
    public async Task BearerIsNotAnAdministratorCookie()
    {
        using var factory = new Factory(realCookie: true); using var client = Client(factory, null);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "not-an-admin-cookie");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Route(Guid.NewGuid()))).StatusCode);
        Assert.Equal(0, factory.Reads.Calls);
    }

    [Fact]
    public async Task DisabledDiagnosticsDoesNotRegisterRoute()
    {
        using var factory = new Factory(false); using var client = Client(factory);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Route(Guid.NewGuid()))).StatusCode);
        Assert.Equal(0, factory.Reads.Calls);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("11111111111111111111111111111111")]
    [InlineData("{11111111-1111-1111-1111-111111111111}")]
    [InlineData("AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA")]
    public async Task InvalidOrNoncanonicalIdNeverCallsReads(string id)
    {
        using var factory = new Factory(); using var client = Client(factory);
        var response = await client.GetAsync(Route(id));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); NoCache(response);
        Assert.Equal("InvalidTargetCreation", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(0, factory.Reads.Calls);
    }

    [Fact]
    public async Task ForgedAdministratorWithEmptyIdNeverCallsReads()
    {
        using var factory = new Factory(); using var client = Client(factory);
        client.DefaultRequestHeaders.Add("X-Test-Empty-Id", "true");
        var response = await client.GetAsync(Route(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); NoCache(response);
        Assert.Equal("Forbidden", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(0, factory.Reads.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadPortViewIsSerializedWithoutFreshAuthorityOrUnexpectedFields(bool recorded)
    {
        using var factory = new Factory(); using var client = Client(factory);
        factory.Reads.Recorded = recorded;
        var id = Guid.NewGuid(); var response = await client.GetAsync(Route(id));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoCache(response);
        Assert.Equal(1, factory.Reads.Calls); Assert.Equal(id, factory.Reads.Id);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), factory.Reads.Actor!.UserId);
        Assert.True(factory.Reads.Actor.IsAdministrator);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "evidenceRecorded", "executable", "freshAuthority", "historicalEvidence", "operationId",
            "ownershipEstablished", "reportedEndpointMappingsAreAttested", "requiresIndependentVerification", "restoreAllowed",
            "retryAllowed", "scheduledJobsMustRemainBlocked" },
            json.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(id, json.GetProperty("operationId").GetGuid());
        Assert.Equal(recorded, json.GetProperty("evidenceRecorded").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("historicalEvidence").ValueKind);
        foreach (var name in new[] { "executable", "freshAuthority", "ownershipEstablished", "reportedEndpointMappingsAreAttested", "restoreAllowed", "retryAllowed" })
            Assert.False(json.GetProperty(name).GetBoolean());
        foreach (var name in new[] { "requiresIndependentVerification", "scheduledJobsMustRemainBlocked" })
            Assert.True(json.GetProperty(name).GetBoolean());
    }

    [Theory]
    [InlineData(403, "TargetCreationForbidden")]
    [InlineData(404, "TargetCreationNotFound")]
    [InlineData(503, "TargetRegistrationEvidenceHistoryUnavailable")]
    public async Task CurrentRoleRejectionAndUnavailableUseSafeErrorPipeline(int status, string code)
    {
        using var factory = new Factory(); using var client = Client(factory);
        factory.Reads.Rejection = new RequestRejectedException(status, code);
        var response = await client.GetAsync(Route(Guid.NewGuid()));
        Assert.Equal((HttpStatusCode)status, response.StatusCode); NoCache(response);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "code" }, json.EnumerateObject().Select(p => p.Name));
        Assert.Equal(code, json.GetProperty("code").GetString());
        Assert.Equal(1, factory.Reads.Calls); Assert.True(factory.Reads.Actor!.IsAdministrator);
    }
}

public sealed class RegistrationEvidenceAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var role = Request.Headers["X-Test-Role"].ToString();
        if (role is not ("Administrator" or "Developer")) return Task.FromResult(AuthenticateResult.NoResult());
        var id = Request.Headers.ContainsKey("X-Test-Empty-Id") ? Guid.Empty.ToString("D") : "11111111-1111-1111-1111-111111111111";
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, id), new Claim(ClaimTypes.Role, role) };
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
    }
}
