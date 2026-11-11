using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildSystem.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BuildSystem.ApiTests;

// HTTP contract only: fake application reads, not database authorization or physical execution.
public sealed class TargetCreationExecutionEvidenceEndpointTests
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
                services.RemoveAll<ITargetCreationExecutionEvidenceReads>();
                services.AddSingleton<ITargetCreationExecutionEvidenceReads>(Reads);
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = realCookie ? IdentityConstants.ApplicationScheme : "EvidenceTest";
                    options.DefaultChallengeScheme = realCookie ? IdentityConstants.ApplicationScheme : "EvidenceTest";
                    options.DefaultForbidScheme = realCookie ? IdentityConstants.ApplicationScheme : "EvidenceTest";
                }).AddScheme<AuthenticationSchemeOptions, StagingReleaseAuthentication>("EvidenceTest", _ => { });
            });
        }
    }
    private sealed class ReadsStub : ITargetCreationExecutionEvidenceReads
    {
        public int Calls;
        public Actor? Actor;
        public Guid Id;
        public RequestRejectedException? Rejection;
        public TargetCreationExecutionHistoricalClaim? Claim = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Parse("2020-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2020-01-01T00:01:00Z"));
        public Task<TargetCreationExecutionEvidenceView> GetAsync(Actor actor, Guid operationId, CancellationToken ct)
        {
            Calls++; Actor = actor; Id = operationId;
            if (Rejection is not null) throw Rejection;
            return Task.FromResult(new TargetCreationExecutionEvidenceView(operationId, Claim is not null, Claim));
        }
    }
    private static string Route(string id) => $"/api/admin/target-creation-operations/{id}/execution-evidence";
    private static string Route(Guid id) => Route(id.ToString("D"));
    private static HttpClient Client(Factory factory, string? role = "Administrator")
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        if (role is not null) client.DefaultRequestHeaders.Add("X-Test-Role", role);
        return client;
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

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("11111111111111111111111111111111")]
    [InlineData("{11111111-1111-1111-1111-111111111111}")]
    [InlineData("AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA")]
    [InlineData("%2011111111-1111-1111-1111-111111111111%20")]
    public async Task MalformedOrNoncanonicalIdNeverCallsReads(string id)
    {
        using var factory = new Factory(); using var client = Client(factory);
        var response = await client.GetAsync(Route(id));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("InvalidTargetCreation", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(0, factory.Reads.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExactInertEvidenceIncludesExpiredClaimOrUnknownOutcomeWithoutClaimAndNeedsNoCsrf(bool recorded)
    {
        using var factory = new Factory(); using var client = Client(factory);
        if (!recorded) factory.Reads.Claim = null;
        var id = Guid.NewGuid(); var response = await client.GetAsync(Route(id));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("no-cache", response.Headers.Pragma.Select(p => p.Name));
        Assert.Equal(1, factory.Reads.Calls); Assert.Equal(id, factory.Reads.Id);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), factory.Reads.Actor!.UserId);
        Assert.True(factory.Reads.Actor.IsAdministrator);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "claimRecorded", "executable", "freshAuthority", "historicalClaim", "operationId",
            "physicalOutcomeKnown", "reconciliationRequired", "retryAllowed", "scheduledJobsMustRemainBlocked" },
            json.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(id, json.GetProperty("operationId").GetGuid());
        Assert.Equal(recorded, json.GetProperty("claimRecorded").GetBoolean());
        foreach (var name in new[] { "executable", "freshAuthority", "retryAllowed", "physicalOutcomeKnown" })
            Assert.False(json.GetProperty(name).GetBoolean());
        foreach (var name in new[] { "scheduledJobsMustRemainBlocked", "reconciliationRequired" })
            Assert.True(json.GetProperty(name).GetBoolean());
        var claim = json.GetProperty("historicalClaim");
        if (!recorded) Assert.Equal(JsonValueKind.Null, claim.ValueKind);
        else
        {
            Assert.Equal(new[] { "agentId", "attemptId", "claimId", "credentialId", "endpointMappingId", "expiresAt", "issuedAt", "permitId" },
                claim.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
            var expected = JsonSerializer.SerializeToElement(factory.Reads.Claim, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            foreach (var property in expected.EnumerateObject())
                Assert.Equal(property.Value.GetRawText(), claim.GetProperty(property.Name).GetRawText());
            Assert.True(claim.GetProperty("expiresAt").GetDateTimeOffset() < DateTimeOffset.UtcNow);
        }
    }

    [Theory]
    [InlineData(403, "Forbidden")]
    [InlineData(404, "TargetCreationNotFound")]
    [InlineData(409, "TargetCreationDisabled")]
    public async Task CurrentRoleDenialAndReadFailuresUseExistingSafeMiddleware(int status, string code)
    {
        using var factory = new Factory(); using var client = Client(factory);
        factory.Reads.Rejection = new RequestRejectedException(status, code);
        var response = await client.GetAsync(Route(Guid.NewGuid()));
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "code" }, json.EnumerateObject().Select(p => p.Name));
        Assert.Equal(code, json.GetProperty("code").GetString()); Assert.Equal(1, factory.Reads.Calls);
    }

    [Fact]
    public async Task DisabledDiagnosticsDoesNotRegisterEvidenceRouteOrCallReads()
    {
        using var factory = new Factory(false); using var client = Client(factory);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Route(Guid.NewGuid()))).StatusCode);
        Assert.Equal(0, factory.Reads.Calls);
    }
}
