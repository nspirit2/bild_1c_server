using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BuildSystem.ApiTests;

public sealed class TargetObservationDeliveryApiTests
{
    private sealed class Factory : ApiFactory
    {
        public readonly DeliveryStub Delivery = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAgentControl>();
                services.AddSingleton<IAgentControl, StubAgentControl>();
                services.RemoveAll<ITargetObservationDelivery>();
                services.AddSingleton<ITargetObservationDelivery>(Delivery);
            });
        }
    }

    private sealed class DeliveryStub : ITargetObservationDelivery
    {
        public int Calls;
        public string? Credential;
        public Guid Id;
        public RequestRejectedException? Rejection;
        public Task<TargetObservationAssignment> ReadAsync(string credential, Guid observationRequestId, CancellationToken ct)
        {
            Calls++;
            Credential = credential;
            Id = observationRequestId;
            if (Rejection is not null) throw Rejection;
            return Task.FromResult(TargetObservationApiFactory.Assignment(Guid.NewGuid(), Guid.NewGuid()) with
            { ObservationRequestId = observationRequestId });
        }
    }

    private static HttpClient Client(Factory factory) => factory.CreateClient(new WebApplicationFactoryClientOptions
    { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
    private static string Route(Guid id) => $"/api/agents/target-observations/{id}";

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("Bearer", "wrong-test-token", false)]
    [InlineData("AgentToken", "valid-test-token", false)]
    [InlineData(null, null, true)]
    public async Task AnonymousWrongMachineOrHumanCookieCannotRead(string? scheme, string? token, bool human)
    {
        using var factory = new Factory();
        using var client = Client(factory);
        if (scheme is not null) client.DefaultRequestHeaders.Authorization = new(scheme, token);
        if (human)
        {
            client.DefaultRequestHeaders.Add("X-Test-User", "yes");
            client.DefaultRequestHeaders.Add("Cookie", "__Host-BuildSystem=untrusted-test-cookie");
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Route(Guid.NewGuid()))).StatusCode);
        Assert.Equal(0, factory.Delivery.Calls);
    }

    [Fact]
    public async Task AddressedBearerReadForwardsExactCredentialAndIdWithoutCsrf()
    {
        using var factory = new Factory();
        using var client = Client(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid-test-token");
        var id = Guid.NewGuid();
        var response = await client.GetAsync(Route(id));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, factory.Delivery.Calls);
        Assert.Equal("valid-test-token", factory.Delivery.Credential);
        Assert.Equal(id, factory.Delivery.Id);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, json.GetProperty("observationRequestId").GetGuid());
        Assert.False(json.GetProperty("executable").GetBoolean());
        Assert.True(json.GetProperty("scheduledJobsMustRemainBlocked").GetBoolean());
    }

    [Fact]
    public async Task ServiceRejectionOnlyReturnsSafeCode()
    {
        using var factory = new Factory();
        using var client = Client(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid-test-token");
        factory.Delivery.Rejection = new RequestRejectedException(409, "TargetObservationDeliveryMismatch");
        var response = await client.GetAsync(Route(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(1, factory.Delivery.Calls);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "code" }, json.EnumerateObject().Select(p => p.Name));
        Assert.Equal("TargetObservationDeliveryMismatch", json.GetProperty("code").GetString());
    }
}
// end
