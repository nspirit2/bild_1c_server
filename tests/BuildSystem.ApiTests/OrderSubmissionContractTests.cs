using System.Net;
using System.Net.Http.Json;
using BuildSystem.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BuildSystem.ApiTests;
public sealed class OrderSubmissionFactory : ApiFactory
{
    public RecordingOrders Orders { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(s => { s.RemoveAll<IEnvironmentOrders>(); s.AddSingleton<IEnvironmentOrders>(Orders); });
    }
    public sealed class RecordingOrders : IEnvironmentOrders
    {
        public string? LastKey { get; private set; }
        public CreateEnvironmentRequest? LastPayload { get; private set; }
        public Guid ActorId { get; private set; }
        private readonly Dictionary<string, (CreateEnvironmentRequest Payload, OrderReceipt Receipt)> receipts = [];
        public Task<OrderReceipt> CreateAsync(Actor actor, string key, CreateEnvironmentRequest request, CancellationToken ct)
        {
            OrderInput.Validate(key, request); LastKey = key; LastPayload = request; ActorId = actor.UserId;
            lock (receipts)
            {
                if (receipts.TryGetValue(key, out var prior))
                {
                    if (prior.Payload != request) throw new RequestRejectedException(409, "IdempotencyConflict");
                    return Task.FromResult(prior.Receipt);
                }
                var receipt = new OrderReceipt(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
                receipts.Add(key, (request, receipt)); return Task.FromResult(receipt);
            }
        }
        public Task<RequestView?> GetRequestAsync(Actor a, Guid id, CancellationToken ct) => Task.FromResult<RequestView?>(null);
        public Task<EnvironmentView?> GetEnvironmentAsync(Actor a, Guid id, CancellationToken ct) => Task.FromResult<EnvironmentView?>(null);
        public Task<IReadOnlyList<EnvironmentView>> ListEnvironmentsAsync(Actor a, CancellationToken ct) => Task.FromResult<IReadOnlyList<EnvironmentView>>([]);
    }
}
public sealed class OrderSubmissionContractTests : IClassFixture<OrderSubmissionFactory>
{
    private readonly OrderSubmissionFactory factory;
    public OrderSubmissionContractTests(OrderSubmissionFactory factory) => this.factory = factory;
    private sealed record TokenResponse(string Token);
    private async Task<HttpClient> Client()
    {
        var c = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        c.DefaultRequestHeaders.Add("X-Test-User", "yes");
        var csrf = await c.GetFromJsonAsync<TokenResponse>("/api/auth/csrf");
        c.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf!.Token); return c;
    }
    [Fact]
    public async Task ExactRepeatForwardsSameKeyAndPayloadAndReturnsSameReceipt()
    {
        using var c = await Client(); var key = Guid.NewGuid().ToString(); c.DefaultRequestHeaders.Add("Idempotency-Key", key);
        var payload = new CreateEnvironmentRequest(Guid.NewGuid(), "UI contract", "archive", new(Guid.NewGuid()), 7);
        var first = await c.PostAsJsonAsync("/api/environment-requests", payload);
        var second = await c.PostAsJsonAsync("/api/environment-requests", payload);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode); Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(await first.Content.ReadFromJsonAsync<OrderReceipt>(), await second.Content.ReadFromJsonAsync<OrderReceipt>());
        Assert.Equal(key, factory.Orders.LastKey); Assert.Equal(payload, factory.Orders.LastPayload);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), factory.Orders.ActorId);
    }
    [Fact]
    public async Task ChangedPayloadUnderSameKeyReturnsConflict()
    {
        using var c = await Client(); c.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var payload = new CreateEnvironmentRequest(Guid.NewGuid(), "Original", "archive", new(Guid.NewGuid()), 7);
        Assert.Equal(HttpStatusCode.Accepted, (await c.PostAsJsonAsync("/api/environment-requests", payload)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/environment-requests", payload with { TtlDays = 8 })).StatusCode);
    }
    [Fact]
    public async Task NestedSourceCannotAcceptPathsOrHashFromBrowser()
    {
        using var c = await Client(); c.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await c.PostAsJsonAsync("/api/environment-requests", new { projectId = Guid.NewGuid(), displayName = "Test", mode = "archive", ttlDays = 7,
            source = new { archiveVersionId = Guid.NewGuid(), relativePath = "not-allowed.dt" } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
// end
