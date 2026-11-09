using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BuildSystem.ApiTests;

// Offline boundary: a canary port proves parsing and gates precede metadata SQL activation.
public sealed class TargetCreationPermitAgentReadContractTests
{
    private const string Prefix = "/api/agents/target-creation-permits";
    private const string Id = "22222222-2222-2222-2222-222222222222";
    private const string Route = Prefix + "/" + Id;
    private const string Token = "permit-read-machine-token-canary";
    private const string Canary = "read-secret-ciphertext-exception-canary";
    private sealed class Port : ITargetCreationPermitAgentReads
    {
        public int Calls;
        public Exception Failure = new InvalidOperationException(Canary);
        public TargetCreationPermitReceipt? Receipt;
        public Task<TargetCreationPermitReceipt> GetForAgentAsync(string token, Guid permit, CancellationToken ct)
        {
            Assert.Equal(Token, token); Assert.Equal(Guid.Parse(Id), permit); Calls++;
            return Receipt is { } receipt ? Task.FromResult(receipt) : throw Failure;
        }
    }
    private sealed class Factory(string? disabled = null, bool activateFailure = false, bool omitReadGate = false) : ApiFactory
    {
        public readonly Port Port = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder); builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, c) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["TargetCreationPermitHttp:Enabled"] = "true", ["TargetCreationPermit:Enabled"] = "true",
                    ["TargetCreationPermit:CredentialBindingEnabled"] = "true", ["TargetCreationOperation:Enabled"] = "true"
                };
                if (!omitReadGate) values["TargetCreationPermit:AgentReadEnabled"] = "true";
                if (disabled is not null) values[disabled] = "false";
                c.AddInMemoryCollection(values);
            });
            builder.ConfigureServices(s =>
            {
                s.RemoveAll<ITargetCreationPermitAgentReads>();
                s.AddSingleton<ITargetCreationPermitAgentReads>(_ => activateFailure ? throw new InvalidOperationException(Canary) : Port);
            });
        }
        public HttpClient Client(bool bearer = true, bool https = true)
        {
            var c = CreateClient(new() { BaseAddress = new Uri(https ? "https://localhost" : "http://localhost"), AllowAutoRedirect = false });
            if (bearer) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            return c;
        }
    }
    private static void Headers(HttpResponseMessage r)
    {
        Assert.True(r.Headers.CacheControl?.NoStore); Assert.Contains("no-cache", r.Headers.Pragma.Select(x => x.Name));
        Assert.Equal("nosniff", Assert.Single(r.Headers.GetValues("X-Content-Type-Options")));
    }
    private static async Task Error(HttpResponseMessage r, HttpStatusCode status, string? code = null)
    {
        Assert.Equal(status, r.StatusCode); Headers(r); var text = await r.Content.ReadAsStringAsync();
        foreach (var secret in new[] { Canary, Token, Id, "InvalidOperationException", "stackTrace", "<html" })
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(text);
        Assert.Equal(new[] { "code" }, document.RootElement.EnumerateObject().Select(x => x.Name));
        if (code is not null) Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
    }
    [Fact]
    public async Task Read_is_default_off_and_each_required_http_gate_blocks_before_port_activation()
    {
        Assert.False(new TargetCreationPermitOptions().AgentReadEnabled);
        foreach (var gate in new[] { "TargetCreationPermitHttp:Enabled", "TargetCreationPermit:Enabled", "TargetCreationPermit:AgentReadEnabled", "TargetCreationPermit:CredentialBindingEnabled", "TargetCreationOperation:Enabled", "omitted" })
        {
            using var f = new Factory(disabled: gate == "omitted" ? null : gate, omitReadGate: gate == "omitted");
            using var c = f.Client(); using var r = await c.GetAsync(Route);
            await Error(r, HttpStatusCode.ServiceUnavailable, "TargetCreationPermitUnavailable"); Assert.Equal(0, f.Port.Calls);
        }
    }
    [Fact]
    public async Task Exact_known_permit_reaches_only_read_port_and_service_or_activation_failures_are_safe()
    {
        using (var f = new Factory())
        {
            var pin = Guid.Parse(Id); var reservation = Guid.NewGuid(); var name = "bs_test_" + reservation.ToString("N");
            var operation = new TargetCreationOperationReceipt(pin, pin, pin, pin, reservation, pin, pin, pin,
                pin, pin, name, name, TargetCreationOperationState.Registered, 1, null, null, null, false);
            var now = DateTimeOffset.UtcNow;
            f.Port.Receipt = new(pin, pin, Guid.NewGuid(), 1, now, now.AddMinutes(5),
                TargetCreationPermitState.Prepared, 1, null, null, false, operation) { CredentialId = pin, EndpointMappingId = pin };
            using var c = f.Client();
            for (var i = 0; i < 2; i++)
            {
                using var r = await c.GetAsync(Route); Assert.Equal(HttpStatusCode.OK, r.StatusCode); Headers(r);
                var text = await r.Content.ReadAsStringAsync(); Assert.DoesNotContain(Token, text); Assert.DoesNotContain(Canary, text);
                var receipt = JsonSerializer.Deserialize<TargetCreationPermitReceipt>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                Assert.Equal(f.Port.Receipt, receipt);
                using var json = JsonDocument.Parse(text); Assert.Equal(0, json.RootElement.GetProperty("state").GetInt32());
                Assert.Equal(0, json.RootElement.GetProperty("operation").GetProperty("state").GetInt32());
            }
            Assert.Equal(2, f.Port.Calls);
        }
        foreach (var activation in new[] { false, true })
        {
            using var f = new Factory(activateFailure: activation); using var c = f.Client();
            using var r = await c.GetAsync(Route); await Error(r, HttpStatusCode.ServiceUnavailable, "TargetCreationPermitUnavailable");
            Assert.Equal(activation ? 0 : 1, f.Port.Calls);
        }
        foreach (var rejection in new[] { (503, "TargetCreationPermitAgentReadDisabled"), (409, "TargetCreationPermitNotReady"), (401, "AgentUnauthorized"), (403, "TargetCreationPermitForbidden"), (404, "TargetCreationPermitNotFound") })
        {
            using var f = new Factory(); f.Port.Failure = new RequestRejectedException(rejection.Item1, rejection.Item2, Canary);
            using var c = f.Client(); using var r = await c.GetAsync(Route);
            await Error(r, (HttpStatusCode)rejection.Item1, rejection.Item2); Assert.Equal(1, f.Port.Calls);
        }
    }
    [Fact]
    public async Task Https_precedes_redirect_for_both_prefixes_even_with_forwarded_proto()
    {
        using var f = new Factory(); using var c = f.Client(https: false); c.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        foreach (var route in new[] { Route, "/api/admin/target-creation-permits/" + Id })
        {
            using var r = await c.GetAsync(route); await Error(r, HttpStatusCode.BadRequest, "TargetCreationPermitHttpsRequired");
            Assert.Null(r.Headers.Location);
        }
        Assert.Equal(0, f.Port.Calls);
    }
    [Fact]
    public async Task Origin_cookie_only_and_nonexact_bearer_never_authorize_machine_read()
    {
        using var f = new Factory();
        foreach (var origin in new[] { "null", "https://localhost" })
        {
            using var c = f.Client(); c.DefaultRequestHeaders.Add("Origin", origin); using var r = await c.GetAsync(Route);
            await Error(r, HttpStatusCode.Forbidden, "TargetCreationPermitForbidden");
        }
        foreach (var auth in new[] { "", "Basic token", "Bearer", "bearer " + Token, "Bearer " + Token + " extra", "Bearer " + Token + ",Bearer " + Token, "Bearer café" })
        {
            using var c = f.Client(bearer: false); c.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", ".AspNetCore.Identity.Application=not-machine-authority");
            if (auth.Length != 0) c.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth);
            using var r = await c.GetAsync(Route); await Error(r, HttpStatusCode.Unauthorized, "AgentUnauthorized");
        }
        using var duplicate = f.Client(bearer: false); duplicate.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", new[] { "Bearer " + Token, "Bearer " + Token });
        using (var r = await duplicate.GetAsync(Route)) await Error(r, HttpStatusCode.Unauthorized, "AgentUnauthorized");
        Assert.Equal(0, f.Port.Calls);
    }
    [Fact]
    public async Task Query_and_every_nonempty_get_body_are_rejected_without_read_calls()
    {
        using var f = new Factory(); using var c = f.Client();
        foreach (var query in new[] { "?latest=true", "?permitId=" + Id, "?token=" + Token })
        { using var r = await c.GetAsync(Route + query); await Error(r, HttpStatusCode.BadRequest, "InvalidTargetCreationPermit"); }
        foreach (var body in new[] { " ", "{}", "null", new string('x', 4097) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Route) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            using var r = await c.SendAsync(request); await Error(r, HttpStatusCode.BadRequest, "InvalidTargetCreationPermit");
        }
        Assert.Equal(0, f.Port.Calls);
    }
    [Fact]
    public async Task No_list_latest_adoption_or_noncanonical_route_and_other_methods_remain_no_store()
    {
        using var f = new Factory(); using var c = f.Client();
        foreach (var route in new[] { Prefix, Prefix + "/latest", Prefix + "/adopt", Prefix + "/not-an-id", Route + "/unknown" })
        { using var r = await c.GetAsync(route); Assert.Equal(HttpStatusCode.NotFound, r.StatusCode); Headers(r); }
        foreach (var id in new[] { Guid.Empty.ToString("D"), Guid.Parse(Id).ToString("N") })
        { using var r = await c.GetAsync(Prefix + "/" + id); await Error(r, HttpStatusCode.BadRequest, "InvalidTargetCreationPermit"); }
        using (var r = await c.PostAsync(Route, new StringContent("{}"))) { Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode); Headers(r); } // Global CSRF rejects POST before method routing.
        Assert.Equal(0, f.Port.Calls);
    }
}
