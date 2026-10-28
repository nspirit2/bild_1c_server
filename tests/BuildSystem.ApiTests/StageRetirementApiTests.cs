using System.Net;
using System.Net.Http.Json;
using BuildSystem.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BuildSystem.ApiTests;
public sealed class RetirementApiFactory : ApiFactory
{
    public RetirementStub Stub { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(s => { s.RemoveAll<IStageRetirement>(); s.AddSingleton<IStageRetirement>(Stub); });
    }
    public sealed class RetirementStub : IStageRetirement
    {
        public int Calls;
        public Task<StageRetirementReceipt> RequestAsync(Actor actor, Guid environmentId, StageRetirementRequest input, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (input.ExpectedJobId == Guid.Empty || input.ExpectedArtifactId == Guid.Empty)
                throw new RequestRejectedException(400, "InvalidStageRetirement");
            return Task.FromResult(new StageRetirementReceipt(Guid.NewGuid(), environmentId, input.ExpectedJobId, input.ExpectedArtifactId, "Requested", DateTimeOffset.UtcNow));
        }
    }
}
// Only HTTP/application-port contract proof, not SQL persistence or cleanup proof.
public sealed class StageRetirementApiTests : IClassFixture<RetirementApiFactory>
{
    private readonly RetirementApiFactory factory;
    public StageRetirementApiTests(RetirementApiFactory factory) => this.factory = factory;
    private sealed record Csrf(string Token);
    private HttpClient Client(bool authenticated = true)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        if (authenticated) client.DefaultRequestHeaders.Add("X-Test-User", "yes");
        return client;
    }
    private static string Route(Guid id) => $"/api/test-environments/{id}/stage-retirement-intents";
    private static object Input() => new { expectedJobId = Guid.NewGuid(), expectedArtifactId = Guid.NewGuid() };
    private static async Task Token(HttpClient client)
    {
        var token = await client.GetFromJsonAsync<Csrf>("/api/auth/csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token!.Token);
    }
    [Fact]
    public async Task AnonymousCannotRequestRetirementEvenWithCsrf()
    {
        using var client = Client(false); await Token(client);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Route(Guid.NewGuid()), Input())).StatusCode);
    }
    [Fact]
    public async Task HumanPostRequiresCsrf()
    {
        using var client = Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Route(Guid.NewGuid()), Input())).StatusCode);
    }
    [Fact]
    public async Task ReceiptContainsOnlyBoundedIntentMetadataNotCleanupAuthority()
    {
        using var client = Client(); await Token(client); var id = Guid.NewGuid();
        var response = await client.PostAsJsonAsync(Route(id), Input());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(new[] { "artifactId", "createdAt", "environmentId", "jobId", "retirementId", "state" }, document.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(id, document.GetProperty("environmentId").GetGuid());
        Assert.Equal("Requested", document.GetProperty("state").GetString());
    }
    [Fact]
    public async Task UnknownCleanupAuthorityFieldRejected()
    {
        using var client = Client(); await Token(client);
        var response = await client.PostAsJsonAsync(Route(Guid.NewGuid()), new { expectedJobId = Guid.NewGuid(), expectedArtifactId = Guid.NewGuid(), releaseQuota = true });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
    [Fact]
    public async Task EmptyExpectedIdentityRejected()
    {
        using var client = Client(); await Token(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Route(Guid.NewGuid()), new { expectedJobId = Guid.Empty, expectedArtifactId = Guid.NewGuid() })).StatusCode);
    }
}
// end
