using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BuildSystem.ApiTests;

// Offline HTTP boundary only: no SQL, RAC, target endpoint or execution is used.
public sealed class TargetCreationExecutionClaimEndpointTests
{
    private const string Route = "/api/agents/target-creation-execution-claims";
    private const string Token = "claim-machine-token";
    private const string Canary = "claim-secret-ciphertext-canary";
    private const string Id = "abcdefab-2222-2222-2222-222222222222";
    private const string Valid = "{\"permitId\":\"" + Id + "\",\"expectedPermitRevision\":2,\"attemptId\":\"" + Id + "\",\"credentialId\":\"" + Id + "\",\"endpointMappingId\":\"" + Id + "\"}";
    private static TargetCreationExecutionClaimReceipt Fresh()
    {
        var id = Guid.Parse(Id); var now = DateTimeOffset.UtcNow;
        return new(id, id, id, id, id, id, id, now.AddSeconds(-1), now.AddMinutes(4));
    }
    private sealed class Port : ITargetCreationExecutionClaims
    {
        public int Calls;
        public Exception? Failure;
        public TargetCreationExecutionClaimReceipt Receipt = Fresh();
        public Task<TargetCreationExecutionClaimReceipt> ClaimAsync(string token, Guid permitId, long revision,
            Guid attemptId, Guid credentialId, Guid endpointMappingId, CancellationToken ct)
        {
            Calls++; Assert.Equal(Token, token); Assert.Equal(2, revision);
            Assert.All(new[] { permitId, attemptId, credentialId, endpointMappingId }, id => Assert.Equal(Guid.Parse(Id), id));
            return Failure is null ? Task.FromResult(Receipt) : throw Failure;
        }
    }
    private sealed class Factory(string? disabled = null, bool activationFailure = false) : ApiFactory
    {
        public readonly Port Port = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder); builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, c) =>
            {
                var values = new Dictionary<string, string?>
                { ["TargetCreationExecutionClaimHttp:Enabled"] = "true", ["TargetCreationExecutionClaim:Enabled"] = "true" };
                if (disabled is not null) values[disabled] = "false";
                c.AddInMemoryCollection(values);
            });
            builder.ConfigureServices(s =>
            {
                s.RemoveAll<ITargetCreationExecutionClaims>();
                s.AddSingleton<ITargetCreationExecutionClaims>(_ => activationFailure ? throw new InvalidOperationException(Canary) : Port);
            });
        }
        public HttpClient Client(bool bearer = true, bool https = true)
        {
            var c = CreateClient(new() { BaseAddress = new Uri(https ? "https://localhost" : "http://localhost"), AllowAutoRedirect = false });
            if (bearer) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            return c;
        }
    }
    private static StringContent Body(string text = Valid) => new(text, Encoding.UTF8, "application/json");
    private static void Headers(HttpResponseMessage r)
    {
        Assert.True(r.Headers.CacheControl?.NoStore); Assert.Contains("no-cache", r.Headers.Pragma.Select(x => x.Name));
        Assert.Equal("nosniff", Assert.Single(r.Headers.GetValues("X-Content-Type-Options")));
    }
    private static async Task Error(HttpResponseMessage r, HttpStatusCode status)
    {
        Assert.Equal(status, r.StatusCode); Headers(r);
        var text = await r.Content.ReadAsStringAsync();
        foreach (var secret in new[] { Canary, Token, Id, "InvalidOperationException", "stackTrace", "<html" })
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(text);
        Assert.Equal(new[] { "code" }, document.RootElement.EnumerateObject().Select(x => x.Name));
    }
    [Theory]
    [InlineData("TargetCreationExecutionClaimHttp:Enabled")]
    [InlineData("TargetCreationExecutionClaim:Enabled")]
    public async Task Gates_are_default_off_and_independent(string gate)
    {
        Assert.False(new TargetCreationExecutionClaimHttpOptions().Enabled); Assert.False(new TargetCreationExecutionClaimOptions().Enabled);
        using var f = new Factory(gate); using var c = f.Client(); using var r = await c.PostAsync(Route, Body());
        await Error(r, HttpStatusCode.ServiceUnavailable); Assert.Equal(0, f.Port.Calls);
    }
    [Fact]
    public async Task Fresh_claim_has_only_the_bounded_camelcase_grant_shape_without_csrf_or_cookie()
    {
        using var f = new Factory(); using var c = f.Client(); using var r = await c.PostAsync(Route, Body(Valid.Replace(Id, Id.ToUpperInvariant())));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode); Headers(r); Assert.Equal(1, f.Port.Calls);
        var bytes = await r.Content.ReadAsByteArrayAsync(); Assert.InRange(bytes.Length, 1, 2048);
        using var json = JsonDocument.Parse(bytes); var root = json.RootElement;
        Assert.Equal(new[] { "claimId", "permitId", "operationId", "attemptId", "agentId", "credentialId", "endpointMappingId", "issuedAt", "expiresAt", "newlyIssued", "executable", "scheduledJobsMustRemainBlocked", "retryAllowed" }, root.EnumerateObject().Select(x => x.Name));
        Assert.True(root.GetProperty("newlyIssued").GetBoolean()); Assert.True(root.GetProperty("executable").GetBoolean());
        Assert.True(root.GetProperty("scheduledJobsMustRemainBlocked").GetBoolean()); Assert.False(root.GetProperty("retryAllowed").GetBoolean());
    }
    [Theory]
    [InlineData("Basic token")]
    [InlineData("Bearer claim-machine-token\tother")]
    [InlineData("Bearer claim-machine-token\u00e9")]
    [InlineData("bearer claim-machine-token")]
    [InlineData("Bearer")]
    [InlineData("Bearer claim-machine-token extra")]
    [InlineData("Bearer claim-machine-token,other")]
    public async Task Only_exact_single_machine_bearer_is_authority(string value)
    {
        using var f = new Factory(); using var c = f.Client(false); c.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", value);
        using var r = await c.PostAsync(Route, Body()); await Error(r, HttpStatusCode.Unauthorized); Assert.Equal(0, f.Port.Calls);
    }
    [Fact]
    public async Task Http_origin_query_cookie_and_duplicate_bearer_are_rejected_before_port()
    {
        using var f = new Factory();
        using (var c = f.Client(https: false))
        { c.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https"); using var r = await c.PostAsync(Route, Body()); await Error(r, HttpStatusCode.BadRequest); Assert.Null(r.Headers.Location); }
        using (var c = f.Client())
        { c.DefaultRequestHeaders.Add("Origin", "null"); using var r = await c.PostAsync(Route, Body()); await Error(r, HttpStatusCode.Forbidden); }
        using (var c = f.Client())
        { using var r = await c.PostAsync(Route + "?x=" + Canary, Body()); await Error(r, HttpStatusCode.BadRequest); }
        using (var c = f.Client(false))
        { c.DefaultRequestHeaders.Add("Cookie", "__Host-BuildSystem=fixture"); c.DefaultRequestHeaders.Add("X-Test-User", "yes"); using var r = await c.PostAsync(Route, Body()); await Error(r, HttpStatusCode.Unauthorized); }
        using (var c = f.Client(false))
        { c.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", new[] { "Bearer " + Token, "Bearer " + Token }); using var r = await c.PostAsync(Route, Body()); await Error(r, HttpStatusCode.Unauthorized); }
        Assert.Equal(0, f.Port.Calls);
    }
    [Fact]
    public async Task Strict_exact_five_fields_and_utf8_are_enforced()
    {
        using var f = new Factory(); using var c = f.Client();
        foreach (var body in new[] { "", "null", "[]", "{}", Valid + "{}", Valid[..^1], Valid[..^1] + ",\"permitId\":\"" + Id + "\"}",
            Valid.Replace("permitId", "PermitId"), Valid[..^1] + ",\"unknown\":true}", Valid.Replace(Id, Guid.Empty.ToString("D")),
            Valid.Replace(Id, Guid.Parse(Id).ToString("N")), Valid.Replace(Id, " " + Id), Valid.Replace("\":2", "\":1"),
            Valid.Replace("\":2", "\":2.0"), Valid.Replace("\":2", "\":2e0"), Valid.Replace("\":2", "\":\"2\""), Valid.Replace("\":2", "\":null") })
        { using var r = await c.PostAsync(Route, Body(body)); await Error(r, HttpStatusCode.BadRequest); }
        using var invalid = new ByteArrayContent([0x7b, 0xff, 0x7d]); invalid.Headers.ContentType = new("application/json");
        using (var r = await c.PostAsync(Route, invalid)) await Error(r, HttpStatusCode.BadRequest);
        using var encoded = Body(); encoded.Headers.ContentEncoding.Add("gzip");
        using (var r = await c.PostAsync(Route, encoded)) await Error(r, HttpStatusCode.BadRequest);
        using var charset = Body(); charset.Headers.ContentType!.CharSet = "utf-16";
        using (var r = await c.PostAsync(Route, charset)) await Error(r, HttpStatusCode.BadRequest);
        Assert.Equal(0, f.Port.Calls);
    }
    private sealed class Chunked(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }
    [Fact]
    public async Task Declared_and_actual_body_limits_are_four_KiB_inclusive()
    {
        using var f = new Factory(); using var c = f.Client(); var text = Valid + new string(' ', 4097 - Encoding.UTF8.GetByteCount(Valid));
        using var chunked = new Chunked(Encoding.UTF8.GetBytes(text)); chunked.Headers.ContentType = new("application/json");
        using (var r = await c.PostAsync(Route, chunked)) await Error(r, HttpStatusCode.RequestEntityTooLarge);
        using (var r = await c.PostAsync(Route, Body(text))) await Error(r, HttpStatusCode.RequestEntityTooLarge);
        Assert.Equal(0, f.Port.Calls);
        using var exact = await c.PostAsync(Route, Body(text[..^1])); Assert.Equal(HttpStatusCode.OK, exact.StatusCode); Assert.Equal(1, f.Port.Calls);
    }
    [Fact]
    public async Task Malformed_service_grants_never_escape_as_executable_responses()
    {
        var good = Fresh(); var wrong = Guid.NewGuid();
        foreach (var receipt in new[] { null!, good with { ClaimId = Guid.Empty }, good with { OperationId = Guid.Empty }, good with { AgentId = Guid.Empty },
            good with { PermitId = wrong }, good with { AttemptId = wrong }, good with { CredentialId = wrong }, good with { EndpointMappingId = wrong },
            good with { ExpiresAt = good.IssuedAt }, good with { ExpiresAt = good.IssuedAt.AddTicks(-1) },
            good with { ExpiresAt = good.IssuedAt.AddMinutes(6) },
            good with { IssuedAt = good.IssuedAt.ToOffset(TimeSpan.FromHours(1)) }, good with { ExpiresAt = good.ExpiresAt.ToOffset(TimeSpan.FromHours(1)) } })
        { using var f = new Factory(); f.Port.Receipt = receipt; using var c = f.Client(); using var r = await c.PostAsync(Route, Body()); await Error(r, HttpStatusCode.ServiceUnavailable); Assert.Equal(1, f.Port.Calls); }
    }
    [Theory]
    [InlineData(-60)]
    [InlineData(60)]
    public async Task SQL_clock_is_authoritative_even_when_API_host_clock_is_skewed(int skewMinutes)
    {
        using var f = new Factory(); var sqlNow = DateTimeOffset.UtcNow.AddMinutes(skewMinutes);
        f.Port.Receipt = Fresh() with { IssuedAt = sqlNow, ExpiresAt = sqlNow.AddMinutes(5) };
        using var c = f.Client(); using var r = await c.PostAsync(Route, Body()); Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Service_and_DI_exceptions_are_sanitized_in_development(bool activation)
    {
        using var f = new Factory(activationFailure: activation); f.Port.Failure = new RequestRejectedException(409, Canary, Canary);
        using var c = f.Client(); using var r = await c.PostAsync(Route, Body()); await Error(r, HttpStatusCode.ServiceUnavailable); Assert.Equal(activation ? 0 : 1, f.Port.Calls);
    }
    [Theory]
    [InlineData("GET", "", HttpStatusCode.MethodNotAllowed)]
    [InlineData("POST", "/unknown", HttpStatusCode.BadRequest)] // Unmatched POST retains global CSRF protection.
    [InlineData("OPTIONS", "", HttpStatusCode.MethodNotAllowed)]
    public async Task No_read_unknown_or_preflight_route_can_grant_authority(string method, string suffix, HttpStatusCode status)
    {
        using var f = new Factory(); using var c = f.Client(); using var request = new HttpRequestMessage(new HttpMethod(method), Route + suffix);
        using var r = await c.SendAsync(request); Assert.Equal(status, r.StatusCode); Headers(r); Assert.Equal(0, f.Port.Calls);
    }
    [Fact]
    public async Task Duplicate_claim_is_conflict_not_replayed_authority()
    {
        using var f = new Factory(); using var c = f.Client();
        using (var r = await c.PostAsync(Route, Body())) Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        f.Port.Failure = new RequestRejectedException(409, "TargetCreationExecutionClaimAlreadyIssued", Canary);
        using var second = await c.PostAsync(Route, Body()); await Error(second, HttpStatusCode.Conflict); Assert.Equal(2, f.Port.Calls);
    }
}
