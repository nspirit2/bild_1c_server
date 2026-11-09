using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BuildSystem.ApiTests;

// Offline machine-delivery boundary: no SQL, target endpoints, executor, or plaintext result construction.
public sealed class TargetCreationCredentialDeliveryContractTests
{
    private const string Prefix = "/api/agents/target-creation-credentials";
    private const string Route = Prefix + "/resolve";
    private const string Canary = "delivery-secret-token-ciphertext-canary";
    private const string Token = "fixture-machine-token";
    private const string Id = "22222222-2222-2222-2222-222222222222";
    private const string Valid = "{\"permitId\":\"" + Id + "\",\"expectedPermitRevision\":2,\"attemptId\":\"" + Id + "\",\"credentialId\":\"" + Id + "\",\"endpointMappingId\":\"" + Id + "\"}";

    private sealed class Resolver : ITargetCreationCredentialResolver
    {
        public int Calls;
        public Exception Failure = new InvalidOperationException(Canary);
        public Task<TargetCreationResolvedCredentials> ResolveAsync(string token, Guid permitId,
            long expectedPermitRevision, Guid attemptId, Guid credentialId, Guid endpointMappingId, CancellationToken ct)
        {
            Calls++;
            Assert.Equal(Token, token); Assert.Equal(2, expectedPermitRevision);
            Assert.All(new[] { permitId, attemptId, credentialId, endpointMappingId }, id => Assert.Equal(Guid.Parse(Id), id));
            throw Failure;
        }
    }
    private sealed class Factory(bool? enabled = true, bool activationFailure = false, bool rejectedActivation = false) : ApiFactory
    {
        public readonly Resolver Port = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder); builder.UseEnvironment("Development");
            // Final configuration, not an early UseSetting masked by appsettings/environment providers.
            builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            { ["TargetCreationCredentialDelivery:Enabled"] = enabled?.ToString() ?? "false" }));
            builder.ConfigureServices(s =>
            {
                s.RemoveAll<ITargetCreationCredentialResolver>();
                if (activationFailure) s.AddSingleton<ITargetCreationCredentialResolver>(_ => throw (rejectedActivation
                    ? (Exception)new RequestRejectedException(409, Canary, Canary) : new InvalidOperationException(Canary)));
                else s.AddSingleton<ITargetCreationCredentialResolver>(Port);
            });
        }
        public HttpClient Client(bool bearer = true, bool https = true)
        {
            var client = CreateClient(new WebApplicationFactoryClientOptions
            { BaseAddress = new Uri(https ? "https://localhost" : "http://localhost"), AllowAutoRedirect = false });
            if (bearer) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            return client;
        }
    }
    private static StringContent Body(string text = Valid) => new(text, Encoding.UTF8, "application/json");
    private static void Headers(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("no-cache", response.Headers.Pragma.Select(v => v.Name));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }
    private static async Task<string> Error(HttpResponseMessage response, HttpStatusCode status, string? code = null)
    {
        Assert.Equal(status, response.StatusCode); Headers(response);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        foreach (var value in new[] { Canary, Token, "InvalidOperationException", "innerException", "<html", "stackTrace", Id })
            Assert.DoesNotContain(value, text, StringComparison.OrdinalIgnoreCase);
        using var json = JsonDocument.Parse(text);
        Assert.Equal(new[] { "code" }, json.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        if (code is not null) Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        return text;
    }

    [Fact]
    public async Task Default_off_does_not_call_resolver_even_with_valid_machine_request()
    {
        using var factory = new Factory(null); using var client = factory.Client();
        using var response = await client.PostAsync(Route, Body());
        await Error(response, HttpStatusCode.ServiceUnavailable, "TargetCreationCredentialDeliveryUnavailable"); Assert.Equal(0, factory.Port.Calls);
    }

    [Fact]
    public async Task Valid_https_bearer_needs_no_cookie_or_csrf_and_sanitizes_unexpected_failure_in_Development()
    {
        foreach (var activation in new[] { false, true })
        {
            using var factory = new Factory(activationFailure: activation); using var client = factory.Client();
            using var response = await client.PostAsync(Route, Body());
            await Error(response, HttpStatusCode.ServiceUnavailable, "TargetCreationCredentialDeliveryUnavailable");
            Assert.Equal(activation ? 0 : 1, factory.Port.Calls);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http_is_rejected_before_redirect_and_does_not_trust_forwarded_proto(bool forwarded)
    {
        using var factory = new Factory(); using var client = factory.Client(https: false);
        if (forwarded) client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        using var response = await client.PostAsync(Route, Body());
        await Error(response, HttpStatusCode.BadRequest, "TargetCreationCredentialHttpsRequired");
        Assert.Null(response.Headers.Location); Assert.Equal(0, factory.Port.Calls);
    }

    [Fact]
    public async Task Cookie_or_authenticated_browser_principal_has_no_machine_authority_and_is_not_a_csrf_error()
    {
        using var factory = new Factory(); using var client = factory.Client(bearer: false);
        client.DefaultRequestHeaders.Add("Cookie", "__Host-BuildSystem=fixture-cookie");
        client.DefaultRequestHeaders.Add("X-Test-User", "yes");
        using var response = await client.PostAsync(Route, Body());
        await Error(response, HttpStatusCode.Unauthorized, "AgentUnauthorized"); Assert.Equal(0, factory.Port.Calls);
    }

    [Theory]
    [InlineData("https://localhost")]
    [InlineData("null")]
    public async Task Any_origin_is_forbidden_before_resolver(string origin)
    {
        using var factory = new Factory(); using var client = factory.Client(); client.DefaultRequestHeaders.Add("Origin", origin);
        using var response = await client.PostAsync(Route, Body());
        await Error(response, HttpStatusCode.Forbidden, "TargetCreationCredentialDeliveryForbidden"); Assert.Equal(0, factory.Port.Calls);
    }

    [Theory]
    [InlineData("Basic fixture-machine-token")]
    [InlineData("Bearer")]
    [InlineData("Bearer fixture-machine-token extra")]
    [InlineData("Bearer fixture-machine-token,Bearer fixture-machine-token")]
    [InlineData("bearer fixture-machine-token")]
    public async Task Malformed_authorization_is_rejected_without_calling_resolver(string authorization)
    {
        using var factory = new Factory(); using var client = factory.Client(bearer: false);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);
        using var response = await client.PostAsync(Route, Body());
        await Error(response, HttpStatusCode.Unauthorized, "AgentUnauthorized"); Assert.Equal(0, factory.Port.Calls);
    }

    [Fact]
    public async Task Duplicate_authorization_and_strict_body_errors_never_call_resolver()
    {
        using var factory = new Factory(); using var client = factory.Client();
        using (var request = new HttpRequestMessage(HttpMethod.Post, Route) { Content = Body() })
        {
            request.Headers.TryAddWithoutValidation("Authorization", new[] { "Bearer " + Token, "Bearer " + Token });
            using var response = await client.SendAsync(request); await Error(response, HttpStatusCode.Unauthorized, "AgentUnauthorized");
        }
        string? neutral = null;
        foreach (var body in new[] { "", "null", "[]", "{}", Valid + "{}", Valid[..^1],
            Valid[..^1] + ",\"permitId\":\"" + Id + "\"}", Valid[..^1] + ",\"unknown\":\"" + Canary + "\"}",
            Valid.Replace("permitId", "PermitId"), Valid.Replace("\"permitId\":\"" + Id + "\",", ""),
            Valid.Replace(Id, " " + Id), Valid.Replace(Id, Guid.Empty.ToString("D")), Valid.Replace(Id, Guid.Parse(Id).ToString("N")),
            Valid.Replace(Id, "\\ud800"), Valid.Replace("\":2", "\":1"), Valid.Replace("\":2", "\":3"),
            Valid.Replace("\":2", "\":2.0"), Valid.Replace("\":2", "\":\"2\""), Valid.Replace("\":2", "\":null") })
        {
            using var response = await client.PostAsync(Route, Body(body)); var text = await Error(response, HttpStatusCode.BadRequest);
            neutral ??= text; Assert.Equal(neutral, text);
        }
        using var invalidUtf8 = new ByteArrayContent([0x7b, 0x22, 0xff, 0x22, 0x3a, 0x31, 0x7d]); invalidUtf8.Headers.ContentType = new("application/json");
        using (var response = await client.PostAsync(Route, invalidUtf8)) await Error(response, HttpStatusCode.BadRequest);
        using (var response = await client.PostAsync(Route, new StringContent(Valid, Encoding.UTF8, "text/plain"))) await Error(response, HttpStatusCode.BadRequest);
        Assert.Equal(0, factory.Port.Calls);
    }

    private sealed class Chunked(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }
    [Fact]
    public async Task Four_KiB_limit_applies_to_known_length_and_chunked_bodies_not_global_sixteen_KiB()
    {
        using var factory = new Factory(); using var client = factory.Client();
        var oversized = Encoding.UTF8.GetBytes(Valid + new string(' ', 4097 - Encoding.UTF8.GetByteCount(Valid)));
        using var chunked = new Chunked(oversized); chunked.Headers.ContentType = new("application/json");
        using (var response = await client.PostAsync(Route, chunked)) await Error(response, HttpStatusCode.RequestEntityTooLarge);
        using var known = new ByteArrayContent(oversized); known.Headers.ContentType = new("application/json");
        using (var response = await client.PostAsync(Route, known)) await Error(response, HttpStatusCode.RequestEntityTooLarge);
        Assert.Equal(0, factory.Port.Calls);
        using (var response = await client.PostAsync(Route, Body(Valid + new string(' ', 4096 - Encoding.UTF8.GetByteCount(Valid)))))
            await Error(response, HttpStatusCode.ServiceUnavailable);
        Assert.Equal(1, factory.Port.Calls);
    }

    [Fact]
    public async Task Prefix_get_unknown_and_preflight_responses_are_no_store_and_do_not_call_resolver()
    {
        using var factory = new Factory(); using var client = factory.Client();
        using (var response = await client.GetAsync(Route)) { Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode); Headers(response); }
        using (var response = await client.GetAsync(Prefix + "/unknown")) { Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); Headers(response); }
        using var request = new HttpRequestMessage(HttpMethod.Options, Route);
        request.Headers.Add("Origin", "https://browser.invalid"); request.Headers.Add("Access-Control-Request-Method", "POST");
        using (var response = await client.SendAsync(request)) { Assert.NotEqual(HttpStatusCode.OK, response.StatusCode); Headers(response); }
        Assert.Equal(0, factory.Port.Calls);
    }

    [Fact]
    public async Task Known_rejection_status_and_code_are_reconstructed_not_reflected_from_exception()
    {
        foreach (var failure in new[]
        {
            new RequestRejectedException(409, Canary, Canary),
            new RequestRejectedException(401, "AgentUnauthorized", Canary)
        })
        {
            using var factory = new Factory(); using var client = factory.Client(); factory.Port.Failure = failure;
            using var response = await client.PostAsync(Route, Body());
            await Error(response, failure.Code == "AgentUnauthorized" ? HttpStatusCode.Unauthorized : HttpStatusCode.ServiceUnavailable,
                failure.Code == "AgentUnauthorized" ? "AgentUnauthorized" : "TargetCreationCredentialDeliveryUnavailable");
            Assert.Equal(1, factory.Port.Calls);
        }
        using var activationFactory = new Factory(activationFailure: true, rejectedActivation: true);
        // A malicious RequestRejectedException during DI activation is still inside the sanitized boundary.
        using var activationClient = activationFactory.Client();
        using var activationResponse = await activationClient.PostAsync(Route, Body());
        await Error(activationResponse, HttpStatusCode.ServiceUnavailable, "TargetCreationCredentialDeliveryUnavailable");
    }
}
