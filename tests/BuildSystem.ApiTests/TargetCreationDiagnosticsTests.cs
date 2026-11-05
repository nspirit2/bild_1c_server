using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BuildSystem.ApiTests;

// HTTP contract tests replace the read port, not proof of SQL authorization or real cookie login.
public sealed class TargetCreationDiagnosticsTests
{
    private const string Prefix = "/api/admin/target-creation-operations";
    private const string UserId = "11111111-1111-1111-1111-111111111111";

    private sealed class Factory(bool? enabled = null, bool realCookie = false, bool replaceReads = true) : ApiFactory
    {
        public readonly ReadsStub Reads = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            if (enabled.HasValue) builder.UseSetting("TargetCreationDiagnostics:Enabled", enabled.Value.ToString());
            builder.ConfigureServices(services =>
            {
                if (replaceReads)
                {
                    services.RemoveAll<ITargetCreationOperationReads>();
                    services.AddSingleton<ITargetCreationOperationReads>(Reads);
                }
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = realCookie ? IdentityConstants.ApplicationScheme : "DiagnosticsTest";
                    options.DefaultChallengeScheme = realCookie ? IdentityConstants.ApplicationScheme : "DiagnosticsTest";
                    options.DefaultForbidScheme = realCookie ? IdentityConstants.ApplicationScheme : "DiagnosticsTest";
                }).AddScheme<AuthenticationSchemeOptions, StagingReleaseAuthentication>("DiagnosticsTest", _ => { });
            });
        }
    }

    private sealed class ReadsStub : ITargetCreationOperationReads
    {
        public int Calls;
        public Actor? Actor;
        public Guid Id;
        public RequestRejectedException? Rejection;
        public TargetCreationOperationReceipt Receipt = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), "bs_test_pin", "bs_test_pin", TargetCreationOperationState.ReconciliationRequired,
            3, Guid.NewGuid(), 1, TargetCreationQuarantineReason.OutcomeUnknown, false);
        public Task<TargetCreationOperationReceipt> GetAsync(Actor actor, Guid operationId, CancellationToken ct)
        {
            Calls++; Actor = actor; Id = operationId;
            if (Rejection is not null) throw Rejection;
            return Task.FromResult(Receipt with { OperationId = operationId });
        }
    }

    private static HttpClient Client(Factory factory, string? role = "Administrator", string scheme = "https")
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri($"{scheme}://localhost"), AllowAutoRedirect = false });
        if (role is not null) client.DefaultRequestHeaders.Add("X-Test-Role", role);
        return client;
    }
    private static string Route(Guid id) => $"{Prefix}/{id:D}";
    private static void NoStore(HttpResponseMessage response) => Assert.True(response.Headers.CacheControl?.NoStore);

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("Developer", HttpStatusCode.Forbidden)]
    public async Task AuthenticationAndExactAdministratorRoleAreRequired(string? role, HttpStatusCode status)
    {
        using var factory = new Factory(); using var client = Client(factory, role);
        var response = await client.GetAsync(Route(Guid.NewGuid()));
        Assert.Equal(status, response.StatusCode); NoStore(response);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, factory.Reads.Calls);
    }

    [Fact]
    public async Task BearerCredentialDoesNotAuthenticateCookieOnlyDiagnostics()
    {
        using var factory = new Factory(realCookie: true); using var client = Client(factory, null);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid-test-token");
        var response = await client.GetAsync(Route(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); NoStore(response);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, factory.Reads.Calls);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("11111111111111111111111111111111")]
    [InlineData("{11111111-1111-1111-1111-111111111111}")]
    [InlineData("AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA")]
    [InlineData("%2011111111-1111-1111-1111-111111111111%20")]
    public async Task OnlyCanonicalLowercaseNonemptyGuidIsAccepted(string id)
    {
        using var factory = new Factory(); using var client = Client(factory);
        var response = await client.GetAsync($"{Prefix}/{id}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); NoStore(response);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InvalidTargetCreation", json.GetProperty("code").GetString());
        Assert.Equal(0, factory.Reads.Calls);
    }

    [Fact]
    public async Task AdministratorReadForwardsActorAndIdAndReturnsExactNonexecutingReceiptWithoutCsrf()
    {
        using var factory = new Factory(); using var client = Client(factory);
        var id = Guid.NewGuid(); var response = await client.GetAsync(Route(id));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoStore(response);
        Assert.Equal(1, factory.Reads.Calls); Assert.Equal(id, factory.Reads.Id);
        Assert.Equal(Guid.Parse(UserId), factory.Reads.Actor!.UserId);
        Assert.True(factory.Reads.Actor.IsAdministrator);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "agentId", "artifactId", "attemptExpectedRevision", "attemptId", "canonicalClusterId",
            "canonicalSqlServerId", "environmentId", "executable", "infobaseName", "newlyRecorded", "operationId",
            "quarantineReason", "reservationId", "restoreIntentId", "revision", "scheduledJobsMustRemainBlocked",
            "sqlDbName", "stageJobId", "state", "targetIncarnationId" },
            json.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        var expected = JsonSerializer.SerializeToElement(factory.Reads.Receipt with { OperationId = id },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var property in expected.EnumerateObject().Where(p => p.Name is not ("state" or "quarantineReason")))
            Assert.Equal(property.Value.GetRawText(), json.GetProperty(property.Name).GetRawText());
        Assert.Equal("ReconciliationRequired", json.GetProperty("state").GetString());
        Assert.Equal("OutcomeUnknown", json.GetProperty("quarantineReason").GetString());
        Assert.False(json.GetProperty("executable").GetBoolean());
        Assert.False(json.GetProperty("newlyRecorded").GetBoolean());
        Assert.True(json.GetProperty("scheduledJobsMustRemainBlocked").GetBoolean());
    }

    [Theory]
    [InlineData(TargetCreationOperationState.Registered)]
    [InlineData(TargetCreationOperationState.AttemptRecorded)]
    public async Task NonQuarantinedStateUsesStringAndNullableReason(TargetCreationOperationState state)
    {
        using var factory = new Factory(); using var client = Client(factory);
        factory.Reads.Receipt = factory.Reads.Receipt with { State = state, QuarantineReason = null };
        var response = await client.GetAsync(Route(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoStore(response);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(state.ToString(), json.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("quarantineReason").ValueKind);
    }

    [Theory]
    [InlineData(403, "Forbidden")]
    [InlineData(404, "TargetCreationNotFound")]
    [InlineData(409, "TargetCreationDisabled")]
    [InlineData(409, "TargetCreationMismatch")]
    public async Task ReadPortRejectionsReturnOnlySafeCodeIncludingStaleAdminDenial(int status, string code)
    {
        using var factory = new Factory(); using var client = Client(factory);
        factory.Reads.Rejection = new RequestRejectedException(status, code);
        var response = await client.GetAsync(Route(Guid.NewGuid()));
        Assert.Equal((HttpStatusCode)status, response.StatusCode); NoStore(response);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "code" }, json.EnumerateObject().Select(p => p.Name));
        Assert.Equal(code, json.GetProperty("code").GetString()); Assert.Equal(1, factory.Reads.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task HostReadDefaultsOffAndRejectsWithoutOpeningSqlConnection(bool? enabled)
    {
        using var factory = new Factory(enabled, replaceReads: false); using var client = Client(factory);
        var response = await client.GetAsync(Route(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); NoStore(response);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("TargetCreationDisabled", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task EnablingDiagnosticsDoesNotEnableOrReplaceMutationPort()
    {
        using var factory = new Factory(true, replaceReads: false); using var client = Client(factory);
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        Assert.True(services.GetRequiredService<TargetCreationDiagnosticsOptions>().Enabled);
        Assert.False(services.GetRequiredService<TargetCreationOperationOptions>().Enabled);
        Assert.Empty(services.GetRequiredService<TargetCreationOperationOptions>().CanonicalMappings);
        var reads = services.GetRequiredService<ITargetCreationOperationReads>();
        var mutations = services.GetRequiredService<ITargetCreationOperations>();
        Assert.NotSame(mutations, reads);
        var actor = new Actor(Guid.Parse(UserId), true);
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() =>
            mutations.RegisterAsync(actor, Guid.NewGuid(), Guid.NewGuid(), default));
        Assert.Equal("TargetCreationDisabled", error.Code);
        error = await Assert.ThrowsAsync<RequestRejectedException>(() =>
            mutations.BeginAttemptAsync(actor, Guid.NewGuid(), 1, Guid.NewGuid(), default));
        Assert.Equal("TargetCreationDisabled", error.Code);
        error = await Assert.ThrowsAsync<RequestRejectedException>(() =>
            mutations.QuarantineAsync(actor, Guid.NewGuid(), 1, TargetCreationQuarantineReason.Cancelled, default));
        Assert.Equal("TargetCreationDisabled", error.Code);
    }

    [Fact]
    public async Task PostHasNoEndpointEvenWithValidCsrf()
    {
        using var factory = new Factory(); using var client = Client(factory);
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        var response = await client.PostAsJsonAsync(Route(Guid.NewGuid()), new { });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode); NoStore(response);
        Assert.Equal(0, factory.Reads.Calls);
    }

    [Theory]
    [InlineData("/admin/creation-diagnostics", "text/html")]
    [InlineData("/admin/creation-diagnostics/app.js", "text/javascript")]
    [InlineData("/admin/creation-diagnostics/styles.css", "text/css")]
    public async Task EmbeddedUiRequiresAdministratorAndReturnsHardenedStream(string route, string contentType)
    {
        using var factory = new Factory();
        using var anonymous = Client(factory, null); using var developer = Client(factory, "Developer");
        using var admin = Client(factory);
        var denied = await anonymous.GetAsync(route);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode); NoStore(denied);
        denied = await developer.GetAsync(route);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); NoStore(denied);
        var response = await admin.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoStore(response);
        Assert.Equal(contentType, response.Content.Headers.ContentType!.MediaType);
        Assert.False(string.IsNullOrWhiteSpace(await response.Content.ReadAsStringAsync()));
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal(0, factory.Reads.Calls);
    }

    [Theory]
    [InlineData("/api/admin/target-creation-operations/11111111-1111-1111-1111-111111111111")]
    [InlineData("/admin/creation-diagnostics")]
    public async Task EarlyRequestSizeFailureStillHasNoStore(string route)
    {
        using var factory = new Factory(); using var client = Client(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, route)
        { Content = new ByteArrayContent(new byte[16 * 1024 + 1]) };
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode); NoStore(response);
        Assert.Equal(0, factory.Reads.Calls);
    }
}
// end
