using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace BuildSystem.ApiTests;

public sealed class StageOperatorUiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;
    public StageOperatorUiTests(ApiFactory factory) => this.factory = factory;
    private HttpClient Client() => factory.CreateClient(new WebApplicationFactoryClientOptions
    { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

    [Theory]
    [InlineData("/stage", "text/html")]
    [InlineData("/stage/", "text/html")]
    [InlineData("/stage/app.js", "text/javascript")]
    [InlineData("/stage/styles.css", "text/css")]
    public async Task PublicShellResourcesAreEmbeddedAndCarrySecurityHeaders(string route, string type)
    {
        using var client = Client();
        var response = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(type, response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.NotEmpty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PublicHtmlHasNoInlineCodeOrInjectedExecutionAuthority()
    {
        using var client = Client();
        var html = await client.GetStringAsync("/stage");
        Assert.Contains("/stage/app.js", html);
        Assert.Contains("/stage/styles.css", html);
        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<style>", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SnapshotJson", html);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/stage-operations")).StatusCode);
    }

    [Fact]
    public async Task JavaScriptUsesSameOriginSafeDomWithoutTokenStorage()
    {
        using var client = Client();
        var js = await client.GetStringAsync("/stage/app.js");
        Assert.Contains("textContent", js);
        Assert.Contains("AbortController", js);
        foreach (var unsafeText in new[] { "innerHTML", "outerHTML", "insertAdjacentHTML", "eval(", "localStorage", "sessionStorage", "document.write", "http://", "https://" })
            Assert.DoesNotContain(unsafeText, js);
        foreach (var mutation in new[] { "/stage/complete", "/api/admin/", "/api/agent/", "/reset", "/reassign" })
            Assert.DoesNotContain(mutation, js);
    }

    [Fact]
    public async Task RetentionExplanationAddsNoCleanupAuthorityOrMutationRoute()
    {
        using var client = Client();
        var html = await client.GetStringAsync("/stage");
        var js = await client.GetStringAsync("/stage/app.js");
        Assert.Contains("detail-retention", html);
        Assert.Contains("detail-retention", js);
        Assert.Contains("/cleanup-status", js);
        var mutationSource = js.Replace("/cleanup-status", "", StringComparison.Ordinal);
        foreach (var route in new[] { "/cleanup", "/retire", "/release", "/cancel", "/delete" })
            Assert.DoesNotContain(route, mutationSource);
        Assert.DoesNotContain("id=\"cleanup\"", html);
        Assert.DoesNotContain("id=\"release\"", html);
    }

    [Theory]
    [InlineData("/stage/secret")]
    [InlineData("/stage/index.html")]
    public async Task OnlyAllowlistedResourcesAreServed(string route)
    {
        using var client = Client();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(route)).StatusCode);
    }
}
// end
