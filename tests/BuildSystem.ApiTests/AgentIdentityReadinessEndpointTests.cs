using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace BuildSystem.ApiTests;

public sealed class AgentIdentityReadinessEndpointTests : IClassFixture<AgentApiFactory>
{
    private const string Path = "/api/agent/readiness-identity";
    private static readonly Guid ApprovedAgentId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private readonly AgentApiFactory factory;

    public AgentIdentityReadinessEndpointTests(AgentApiFactory factory) => this.factory = factory;

    private HttpClient Client(string scheme = "https") => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri($"{scheme}://localhost"),
        AllowAutoRedirect = false
    });

    [Fact]
    public async Task ApprovedHttpsBearerSeesOnlyIdentityAndFixedNegativeReadiness()
    {
        using var client = Client();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid-test-token");

        using var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet?.ToLowerInvariant());
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("no-cache", response.Headers.Pragma.ToString());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Object, json.RootElement.ValueKind);
        Assert.Equal(new[] { "agentId", "executable", "freshAuthority", "readinessComplete" },
            json.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(ApprovedAgentId, json.RootElement.GetProperty("agentId").GetGuid());
        Assert.False(json.RootElement.GetProperty("executable").GetBoolean());
        Assert.False(json.RootElement.GetProperty("freshAuthority").GetBoolean());
        Assert.False(json.RootElement.GetProperty("readinessComplete").GetBoolean());
    }

    [Fact]
    public async Task AnonymousInvalidBearerAndHumanTestUserCannotReadIdentity()
    {
        using var client = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Path)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "invalid-test-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Path)).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        client.DefaultRequestHeaders.Add("X-Test-User", "yes");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Path)).StatusCode);
    }

    [Fact]
    public async Task PlainHttpCannotReadIdentityEvenWithValidBearer()
    {
        using var client = Client("http");
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid-test-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Path)).StatusCode);
    }

    [Fact]
    public async Task ReadinessIdentityHasNoPostOrPutHandler()
    {
        using var client = Client();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid-test-token");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PutAsync(Path, null)).StatusCode);

        // Existing antiforgery middleware can reject an unmatched POST before routing returns 405.
        var postStatus = (await client.PostAsync(Path, null)).StatusCode;
        Assert.True(postStatus is HttpStatusCode.BadRequest or HttpStatusCode.MethodNotAllowed);
    }
}
// end
