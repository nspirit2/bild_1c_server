using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BuildSystem.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace BuildSystem.ApiTests;

// Offline HTTP boundary only: throwing ports never contact metadata SQL or a target executor.
public sealed class TargetCreationPermitHttpContractTests
{
    private const string Admin = "/api/admin/target-creation-permits";
    private const string Start = "/api/agents/target-creation-permit-starts";
    private const string Id = "22222222-2222-2222-2222-222222222222";
    private const string Token = "permit-machine-token-canary";
    private const string Canary = "permit-secret-ciphertext-exception-canary";
    private const string Issue = "{\"operationId\":\"" + Id + "\",\"expectedOperationRevision\":1,\"credentialId\":\"" + Id + "\",\"endpointMappingId\":\"" + Id + "\"}";
    private const string Begin = "{\"permitId\":\"" + Id + "\",\"expectedPermitRevision\":1,\"attemptId\":\"" + Id + "\"}";
    private sealed class Port : ITargetCreationPermits, ITargetCreationPermitAgentStarts
    {
        public int Calls;
        public Exception Failure = new InvalidOperationException(Canary);
        public readonly List<(Guid Permit, Guid Attempt)> Generated = [];
        private Task<TargetCreationPermitReceipt> Fail() { Calls++; throw Failure; }
        public Task<TargetCreationPermitReceipt> IssueAsync(Actor a, Guid o, long r, Guid p, Guid t, CancellationToken ct) => throw new Xunit.Sdk.XunitException("Legacy issuance must not be exposed");
        public Task<TargetCreationPermitReceipt> IssueWithCredentialAsync(Actor a, Guid o, long r, Guid p, Guid t, Guid c, Guid m, CancellationToken ct)
        {
            Assert.True(a.IsAdministrator); Assert.Equal(1, r);
            Assert.All(new[] { o, c, m }, value => Assert.Equal(Guid.Parse(Id), value));
            Assert.NotEqual(Guid.Empty, p); Assert.NotEqual(Guid.Empty, t); Assert.NotEqual(p, t);
            Generated.Add((p, t)); return Fail();
        }
        public Task<TargetCreationPermitReceipt> RecordStartAsync(Actor a, Guid p, long r, Guid t, CancellationToken ct) => throw new Xunit.Sdk.XunitException("Administrator start must not be exposed");
        public Task<TargetCreationPermitReceipt> RevokeAsync(Actor a, Guid p, long r, CancellationToken ct) => Fail();
        public Task<TargetCreationPermitReceipt> GetAsync(Actor a, Guid p, CancellationToken ct) => Fail();
        public Task<TargetCreationPermitReceipt> RecordAgentStartAsync(string token, Guid p, long r, Guid t, CancellationToken ct)
        { Assert.Equal(Token, token); Assert.Equal(1, r); Assert.Equal(Guid.Parse(Id), p); Assert.Equal(p, t); return Fail(); }
    }
    public sealed class AdminAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e)
        : AuthenticationHandler<AuthenticationSchemeOptions>(o, l, e)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("X-Permit-Admin")) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new[] { new Claim(ClaimTypes.NameIdentifier, Id), new Claim(ClaimTypes.Role, "Administrator") };
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }
    private sealed class Factory(bool? enabled = true, bool activationFailure = false, string? disabledGate = null) : ApiFactory
    {
        public readonly Port Port = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder); builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, c) =>
            {
                var values = new Dictionary<string, string?> { ["TargetCreationPermit:Enabled"] = "true", ["TargetCreationPermit:AgentStartEnabled"] = "true", ["TargetCreationPermit:CredentialBindingEnabled"] = "true", ["TargetCreationOperation:Enabled"] = "true" };
                if (enabled.HasValue) values["TargetCreationPermitHttp:Enabled"] = enabled.Value.ToString();
                if (disabledGate is not null) values[disabledGate] = "false";
                c.AddInMemoryCollection(values);
            });
            builder.ConfigureServices(s =>
            {
                s.RemoveAll<ITargetCreationPermits>(); s.RemoveAll<ITargetCreationPermitAgentStarts>();
                s.AddSingleton<ITargetCreationPermits>(_ => activationFailure ? throw new InvalidOperationException(Canary) : Port);
                s.AddSingleton<ITargetCreationPermitAgentStarts>(_ => activationFailure ? throw new InvalidOperationException(Canary) : Port);
                s.AddAuthentication(o => { o.DefaultAuthenticateScheme = "PermitAdmin"; o.DefaultChallengeScheme = "PermitAdmin"; o.DefaultForbidScheme = "PermitAdmin"; })
                    .AddScheme<AuthenticationSchemeOptions, AdminAuthentication>("PermitAdmin", _ => { });
            });
        }
        public HttpClient Client(bool admin = false, bool bearer = true, bool https = true)
        {
            var c = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(https ? "https://localhost" : "http://localhost"), AllowAutoRedirect = false });
            if (admin) c.DefaultRequestHeaders.Add("X-Permit-Admin", "yes");
            if (bearer) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            return c;
        }
    }
    private static StringContent Body(string body) => new(body, Encoding.UTF8, "application/json");
    private static async Task Csrf(HttpClient c)
    { var j = await c.GetFromJsonAsync<JsonElement>("/api/auth/csrf"); c.DefaultRequestHeaders.Add("X-CSRF-TOKEN", j.GetProperty("token").GetString()); }
    private static void Headers(HttpResponseMessage r)
    {
        Assert.True(r.Headers.CacheControl?.NoStore); Assert.Contains("no-cache", r.Headers.Pragma.Select(p => p.Name));
        Assert.Equal("nosniff", Assert.Single(r.Headers.GetValues("X-Content-Type-Options")));
    }
    private static async Task Error(HttpResponseMessage r, HttpStatusCode status, string? code = null)
    {
        Assert.Equal(status, r.StatusCode); Headers(r); var text = await r.Content.ReadAsStringAsync();
        foreach (var secret in new[] { Canary, Token, "InvalidOperationException", "stackTrace", "<html", Id }) Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        using var j = JsonDocument.Parse(text); Assert.Equal(new[] { "code" }, j.RootElement.EnumerateObject().Select(p => p.Name));
        if (code is not null) Assert.Equal(code, j.RootElement.GetProperty("code").GetString());
    }
    [Fact]
    public async Task Default_off_rejects_valid_machine_and_admin_without_port_calls()
    {
        foreach (var gate in new[] { "TargetCreationPermitHttp:Enabled", "TargetCreationPermit:Enabled", "TargetCreationOperation:Enabled", "TargetCreationPermit:CredentialBindingEnabled", "TargetCreationPermit:AgentStartEnabled" })
        {
            using var f = new Factory(enabled: gate == "TargetCreationPermitHttp:Enabled" ? null : true, disabledGate: gate == "TargetCreationPermitHttp:Enabled" ? null : gate);
            using var c = f.Client(admin: true); await Csrf(c);
            using (var r = await c.PostAsync(Start, Body(Begin))) await Error(r, HttpStatusCode.ServiceUnavailable, "TargetCreationPermitUnavailable");
            if (gate != "TargetCreationPermit:AgentStartEnabled")
            { using var r = await c.PostAsync(Admin + "/issue", Body(Issue)); await Error(r, HttpStatusCode.ServiceUnavailable, "TargetCreationPermitUnavailable"); }
            Assert.Equal(0, f.Port.Calls);
        }
    }
    [Fact]
    public async Task Valid_machine_and_admin_requests_sanitize_service_and_activation_failures_in_Development()
    {
        foreach (var activation in new[] { false, true })
        {
            using var f = new Factory(activationFailure: activation); using var c = f.Client(admin: true); await Csrf(c);
            foreach (var pair in new[] { (Start, Begin), (Admin + "/issue", Issue), (Admin + "/" + Id + "/revoke", "{\"expectedPermitRevision\":2}") })
            { using var r = await c.PostAsync(pair.Item1, Body(pair.Item2)); await Error(r, HttpStatusCode.ServiceUnavailable, "TargetCreationPermitUnavailable"); }
            using var get = await c.GetAsync(Admin + "/" + Id); await Error(get, HttpStatusCode.ServiceUnavailable);
            Assert.Equal(activation ? 0 : 4, f.Port.Calls);
        }
    }
    [Fact]
    public async Task Issuance_generates_fresh_server_permit_and_attempt_ids_only()
    {
        using var f = new Factory(); using var c = f.Client(admin: true); await Csrf(c);
        for (var i = 0; i < 2; i++) { using var r = await c.PostAsync(Admin + "/issue", Body(Issue)); await Error(r, HttpStatusCode.ServiceUnavailable); }
        Assert.Equal(4, f.Port.Generated.SelectMany(p => new[] { p.Permit, p.Attempt }).Distinct().Count());
    }
    [Fact]
    public async Task Https_is_required_on_both_prefixes_before_redirect_even_with_forwarded_proto()
    {
        using var f = new Factory(); using var c = f.Client(admin: true, https: false); c.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        foreach (var pair in new[] { (Start, Begin), (Admin + "/issue", Issue) })
        { using var r = await c.PostAsync(pair.Item1, Body(pair.Item2)); await Error(r, HttpStatusCode.BadRequest, "TargetCreationPermitHttpsRequired"); Assert.Null(r.Headers.Location); }
        Assert.Equal(0, f.Port.Calls);
    }
    [Fact]
    public async Task Only_machine_start_is_exempt_from_csrf_and_cookie_is_not_machine_authority()
    {
        using var f = new Factory(); using var admin = f.Client(admin: true, bearer: false);
        using (var r = await admin.PostAsync(Admin + "/issue", Body(Issue))) { Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode); Headers(r); }
        using (var r = await admin.PostAsync(Start, Body(Begin))) await Error(r, HttpStatusCode.Unauthorized, "AgentUnauthorized");
        using var anonymous = f.Client(bearer: false); await Csrf(anonymous);
        using (var r = await anonymous.PostAsync(Admin + "/issue", Body(Issue))) { Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode); Headers(r); }
        Assert.Equal(0, f.Port.Calls);
    }
    [Fact]
    public async Task Every_origin_and_nonexact_bearer_is_rejected_before_port()
    {
        using var f = new Factory();
        foreach (var origin in new[] { "null", "https://localhost" })
        { using var c = f.Client(); c.DefaultRequestHeaders.Add("Origin", origin); using var r = await c.PostAsync(Start, Body(Begin)); await Error(r, HttpStatusCode.Forbidden, "TargetCreationPermitForbidden"); }
        foreach (var header in new[] { "Basic token", "Bearer", "bearer " + Token, "Bearer " + Token + " extra", "Bearer " + Token + ",Bearer " + Token })
        { using var c = f.Client(bearer: false); c.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", header); using var r = await c.PostAsync(Start, Body(Begin)); await Error(r, HttpStatusCode.Unauthorized, "AgentUnauthorized"); }
        using var duplicate = f.Client(bearer: false); duplicate.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", new[] { "Bearer " + Token, "Bearer " + Token });
        using (var r = await duplicate.PostAsync(Start, Body(Begin))) await Error(r, HttpStatusCode.Unauthorized, "AgentUnauthorized");
        Assert.Equal(0, f.Port.Calls);
    }
    [Fact]
    public async Task Exact_body_members_canonical_nonzero_D_guids_and_lexical_revision_are_required()
    {
        using var f = new Factory(); using var c = f.Client(admin: true); await Csrf(c);
        foreach (var pair in new[] { (Start, Begin), (Admin + "/issue", Issue) })
        {
            var revision = pair.Item1 == Start ? "expectedPermitRevision" : "expectedOperationRevision";
            var idName = pair.Item1 == Start ? "permitId" : "operationId";
            foreach (var body in new[] { "", "null", "[]", "{}", pair.Item2 + "{}", pair.Item2[..^1],
                pair.Item2[..^1] + ",\"unknown\":1}", pair.Item2[..^1] + ",\"" + idName + "\":\"" + Id + "\"}",
                pair.Item2.Replace("\"" + idName + "\":\"" + Id + "\",", ""), pair.Item2.Replace(idName, "WrongCase"),
                pair.Item2.Replace(Id, Guid.Empty.ToString("D")), pair.Item2.Replace(Id, Guid.Parse(Id).ToString("N")), pair.Item2.Replace(Id, " " + Id), pair.Item2.Replace(Id, "\\ud800"),
                pair.Item2.Replace(":1", ":2"), pair.Item2.Replace(":1", ":1.0"), pair.Item2.Replace(":1", ":1e0"), pair.Item2.Replace(":1", ":\"1\""), pair.Item2.Replace(":1", ":null") })
            { using var r = await c.PostAsync(pair.Item1, Body(body)); await Error(r, HttpStatusCode.BadRequest, "InvalidTargetCreationPermit"); }
        }
        foreach (var member in new[] { "permitId", "attemptId" })
        { using var r = await c.PostAsync(Admin + "/issue", Body(Issue[..^1] + ",\"" + member + "\":\"" + Id + "\"}")); await Error(r, HttpStatusCode.BadRequest); }
        Assert.Equal(0, f.Port.Calls);
    }
    [Fact]
    public async Task Revoke_requires_exact_one_member_revision_one_or_two_and_canonical_route_id()
    {
        using var f = new Factory(); using var c = f.Client(admin: true); await Csrf(c);
        foreach (var body in new[] { "{}", "null", "{\"expectedPermitRevision\":0}", "{\"expectedPermitRevision\":3}", "{\"expectedPermitRevision\":1.0}", "{\"expectedPermitRevision\":1,\"extra\":1}", "{\"expectedPermitRevision\":1,\"expectedPermitRevision\":1}" })
        { using var r = await c.PostAsync(Admin + "/" + Id + "/revoke", Body(body)); await Error(r, HttpStatusCode.BadRequest); }
        foreach (var id in new[] { Guid.Empty.ToString("D"), Guid.Parse(Id).ToString("N") })
        { using var r = await c.GetAsync(Admin + "/" + id); await Error(r, HttpStatusCode.BadRequest); }
        using (var r = await c.GetAsync(Admin + "/not-an-id")) { Assert.Equal(HttpStatusCode.NotFound, r.StatusCode); Headers(r); }
        Assert.Equal(0, f.Port.Calls);
    }
    private sealed class Chunked(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }
    [Fact]
    public async Task Four_KiB_known_and_chunked_limit_utf8_and_media_type_are_enforced()
    {
        using var f = new Factory(); using var c = f.Client(admin: true); await Csrf(c);
        foreach (var pair in new[] { (Start, Begin), (Admin + "/issue", Issue), (Admin + "/" + Id + "/revoke", "{\"expectedPermitRevision\":1}") })
        {
            var bytes = Encoding.UTF8.GetBytes(pair.Item2 + new string(' ', 4097 - Encoding.UTF8.GetByteCount(pair.Item2)));
            using var known = new ByteArrayContent(bytes); known.Headers.ContentType = new("application/json");
            using var chunked = new Chunked(bytes); chunked.Headers.ContentType = new("application/json");
            foreach (var body in new HttpContent[] { known, chunked }) { using var r = await c.PostAsync(pair.Item1, body); await Error(r, HttpStatusCode.RequestEntityTooLarge, "TargetCreationPermitBodyTooLarge"); }
            using var invalid = new ByteArrayContent([0x7b, 0x22, 0xff, 0x22, 0x3a, 0x31, 0x7d]); invalid.Headers.ContentType = new("application/json");
            using (var r = await c.PostAsync(pair.Item1, invalid)) await Error(r, HttpStatusCode.BadRequest);
            using (var r = await c.PostAsync(pair.Item1, new StringContent(pair.Item2, Encoding.UTF8, "text/plain"))) await Error(r, HttpStatusCode.BadRequest);
        }
        Assert.Equal(0, f.Port.Calls);
        using var boundary = await c.PostAsync(Start, Body(Begin + new string(' ', 4096 - Encoding.UTF8.GetByteCount(Begin)))); await Error(boundary, HttpStatusCode.ServiceUnavailable); Assert.Equal(1, f.Port.Calls);
    }
    [Fact]
    public async Task Prefix_unknown_method_and_preflight_errors_remain_no_store()
    {
        using var f = new Factory(); using var c = f.Client(admin: true); await Csrf(c);
        foreach (var route in new[] { Admin + "/unknown/deep", Start + "/unknown" }) { using var r = await c.GetAsync(route); Assert.Equal(HttpStatusCode.NotFound, r.StatusCode); Headers(r); }
        using (var r = await c.GetAsync(Start)) { Assert.Equal(HttpStatusCode.MethodNotAllowed, r.StatusCode); Headers(r); }
        using var request = new HttpRequestMessage(HttpMethod.Options, Start); request.Headers.Add("Origin", "https://browser.invalid"); request.Headers.Add("Access-Control-Request-Method", "POST");
        using (var r = await c.SendAsync(request)) { Assert.NotEqual(HttpStatusCode.OK, r.StatusCode); Headers(r); }
        Assert.Equal(0, f.Port.Calls);
    }
}
