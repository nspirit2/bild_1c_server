using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BuildSystem.ApiTests;

// Offline HTTP/source contracts, not browser execution, real login, or secret-erasure proof.
public sealed class TargetCredentialsUiTests
{
    private const string Page = "/admin/target-credentials";

    private sealed class Factory : ApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "CredentialsUiTest";
                options.DefaultChallengeScheme = "CredentialsUiTest";
                options.DefaultForbidScheme = "CredentialsUiTest";
            }).AddScheme<AuthenticationSchemeOptions, StagingReleaseAuthentication>(
                "CredentialsUiTest", _ => { }));
        }
    }

    private static HttpClient Client(Factory factory, string? role = "Administrator")
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        if (role is not null) client.DefaultRequestHeaders.Add("X-Test-Role", role);
        return client;
    }

    private static void NoStore(HttpResponseMessage response) => Assert.True(response.Headers.CacheControl?.NoStore);

    [Theory]
    [InlineData(Page, "text/html")]
    [InlineData(Page + "/app.js", "text/javascript")]
    [InlineData(Page + "/styles.css", "text/css")]
    public async Task OnlyAdministratorCanFetchEmbeddedAssetsWithHardenedHeaders(string route, string mediaType)
    {
        using var factory = new Factory();
        using var anonymous = Client(factory, null);
        using var developer = Client(factory, "Developer");
        using var administrator = Client(factory);
        using var deniedAnonymous = await anonymous.GetAsync(route);
        Assert.Equal(HttpStatusCode.Unauthorized, deniedAnonymous.StatusCode); NoStore(deniedAnonymous);
        using var deniedDeveloper = await developer.GetAsync(route);
        Assert.Equal(HttpStatusCode.Forbidden, deniedDeveloper.StatusCode); NoStore(deniedDeveloper);
        using var response = await administrator.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoStore(response);
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
        Assert.False(string.IsNullOrWhiteSpace(await response.Content.ReadAsStringAsync()));
        var policy = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'none'", policy);
        Assert.Contains("script-src 'self'", policy);
        Assert.Contains("style-src 'self'", policy);
        Assert.Contains("connect-src 'self'", policy);
        Assert.Equal("'self'", Assert.Single(policy.Split(';').Select(part => part.Trim()),
            part => part.StartsWith("connect-src ", StringComparison.Ordinal))["connect-src ".Length..]);
        Assert.Contains("frame-ancestors 'none'", policy);
        Assert.Contains("form-action 'self'", policy);
        Assert.DoesNotContain("unsafe-inline", policy);
        Assert.DoesNotContain("unsafe-eval", policy);
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public async Task HtmlIncludesFourEmptyAutocompleteOffPasswordFieldsAndExternalAssetsOnly()
    {
        using var factory = new Factory(); using var client = Client(factory);
        var html = await client.GetStringAsync(Page);
        foreach (var id in new[] { "clusterUser", "clusterPassword", "sqlUser", "sqlPassword" })
        {
            var inputs = Regex.Matches(html, @"<input\b[^>]*>", RegexOptions.IgnoreCase)
                .Select(match => match.Value)
                .Where(input => Attribute(input, "id") == id).ToArray();
            var input = Assert.Single(inputs);
            Assert.Null(Attribute(input, "name"));
            Assert.Equal("password", Attribute(input, "type"));
            Assert.Equal("off", Attribute(input, "autocomplete"));
            Assert.True(Attribute(input, "value") is null or "", $"{id} must start blank");
        }
        var form = Assert.Single(Regex.Matches(html, @"<form\b[^>]*>", RegexOptions.IgnoreCase)
            .Select(match => match.Value), element => Attribute(element, "id") == "importForm");
        Assert.Equal("post", Attribute(form, "method"));
        Assert.Equal("/api/admin/target-credentials", Attribute(form, "action"));
        var submit = Assert.Single(Regex.Matches(html, @"<button\b[^>]*>", RegexOptions.IgnoreCase)
            .Select(match => match.Value), element => Attribute(element, "id") == "importSubmit");
        Assert.Matches(@"\bdisabled(?:\s|=|>)", submit);
        Assert.Contains(Page + "/app.js", html);
        Assert.Contains(Page + "/styles.css", html);
        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<style>", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"(?i)\bon\w+\s*=", html);
    }

    private static string? Attribute(string element, string name)
    {
        var match = Regex.Match(element, $@"\b{Regex.Escape(name)}\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Success ? match.Groups[1].Value :
            match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value : null;
    }

    [Fact]
    public async Task ScriptHasNoPersistentStorageHtmlInjectionEvalLoggingOrQuerySeeding()
    {
        using var factory = new Factory(); using var client = Client(factory);
        var script = await client.GetStringAsync(Page + "/app.js");
        foreach (var forbidden in new[] { "localStorage", "sessionStorage", "innerHTML", "outerHTML",
            "insertAdjacentHTML", "document.write", "eval(", "console.", "location.search",
            "URLSearchParams", "http://", "https://" })
            Assert.DoesNotContain(forbidden, script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("textContent", script);
    }

    [Fact]
    public async Task ScriptPinsSameOriginCredentialRoutesAndExplicitCsrfHeader()
    {
        using var factory = new Factory(); using var client = Client(factory);
        var script = await client.GetStringAsync(Page + "/app.js");
        Assert.Contains("/api/auth/csrf", script);
        Assert.Contains("X-CSRF-TOKEN", script);
        Assert.Contains("/api/admin/target-credentials", script);
        Assert.Contains("/revoke", script);
        foreach (var key in new[] { "credentialId", "canonicalClusterId", "canonicalSqlServerId",
            "endpointMappingId", "clusterUser", "clusterPassword", "sqlUser", "sqlPassword",
            "revision", "state" })
            Assert.Contains(key, script);
        Assert.Matches(@"const\s+bindingKeys\s*=\s*\[\s*""credentialId"",\s*""canonicalClusterId"",\s*""canonicalSqlServerId"",\s*""endpointMappingId""\s*\]", script);
        Assert.Matches(@"const\s+metadataKeys\s*=\s*\[\s*\.\.\.bindingKeys,\s*""state"",\s*""revision"",\s*""createdAt"",\s*""revokedAt""\s*\]", script);
        Assert.Contains("for (const key of metadataKeys)", script);
        Assert.Contains("credentials: \"same-origin\"", script);
        Assert.Contains("cache: \"no-store\"", script);
        Assert.DoesNotContain("Object.keys(", script);
        Assert.DoesNotContain("Object.entries(", script);
        Assert.DoesNotContain("JSON.stringify(metadata)", script);
    }

    [Fact]
    public async Task ScriptDeclaresSecretClearingAfterImportAttempt()
    {
        using var factory = new Factory(); using var client = Client(factory);
        var script = await client.GetStringAsync(Page + "/app.js");
        Assert.Matches(@"function\s+clearSecrets\s*\(\)\s*\{[^}]*for\s*\(const id of secretIds\)[^}]*\.value\s*=\s*""""", script);
        Assert.Matches(@"const\s+secretIds\s*=\s*\[\s*""clusterUser"",\s*""clusterPassword"",\s*""sqlUser"",\s*""sqlPassword""\s*\]", script);
        var importStart = script.IndexOf("byId(\"importForm\").addEventListener", StringComparison.Ordinal);
        Assert.True(importStart >= 0);
        var importEnd = script.IndexOf("function wellFormed", importStart, StringComparison.Ordinal);
        Assert.True(importEnd > importStart);
        var handler = script[importStart..importEnd];
        var capture = handler.IndexOf("payload[key] = byId(key).value;", StringComparison.Ordinal);
        Assert.True(capture >= 0);
        var firstClear = handler.IndexOf("clearSecrets();", capture, StringComparison.Ordinal);
        var firstAwait = handler.IndexOf("await ", StringComparison.Ordinal);
        Assert.True(firstClear >= capture && firstAwait > firstClear, "DOM secrets must clear before first await");
        Assert.Matches(@"finally\s*\{\s*clearSecrets\(\);", handler);
        // Source inspection does not prove browser behavior or managed-memory erasure.
    }

    [Theory]
    [InlineData(Page + "/index.html")]
    [InlineData(Page + "/unknown")]
    public async Task NonAllowlistedAssetPathsAreNotServed(string route)
    {
        using var factory = new Factory(); using var client = Client(factory);
        using var response = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); NoStore(response);
    }

    [Fact]
    public async Task EarlyRequestSizeRejectionIsStillNoncacheable()
    {
        using var factory = new Factory(); using var client = Client(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, Page)
        { Content = new ByteArrayContent(new byte[16 * 1024 + 1]) };
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode); NoStore(response);
    }

    [Fact]
    public async Task QueryParametersCannotPopulateHtmlOrBecomeInlineScript()
    {
        using var factory = new Factory(); using var client = Client(factory);
        const string marker = "query-secret-must-not-seed";
        using var response = await client.GetAsync(Page + "?clusterPassword=" + marker);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoStore(response);
        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(marker, html);
        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
    }
}
