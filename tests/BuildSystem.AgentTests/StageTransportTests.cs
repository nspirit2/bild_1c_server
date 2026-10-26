using System.Net;
using System.Net.Http.Json;
using System.Text;
using BuildSystem.Agent;
using BuildSystem.Contracts;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class StageTransportTests
{
    private static readonly string Token = new('A', 64);
    private static StageLeaseGrant Grant() => new(Guid.NewGuid(), 1, DateTimeOffset.UtcNow.AddMinutes(2), "ArchiveStageOnly",
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "archive.dt", new string('B', 64), 100, "dt"));

    [Fact]
    public async Task DedicatedRouteAndBearerAndNoSnapshot()
    {
        var grant = Grant();
        using var transport = new AgentTransport(new Uri("https://server.test/"), Token, new Handler(request =>
        {
            Assert.Equal("/api/agent/stage/poll", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme); Assert.Equal(Token, request.Headers.Authorization.Parameter);
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Null(request.Content);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(grant) };
        }));
        Assert.Equal(grant, await transport.PollStageAsync(default));
    }

    [Fact]
    public async Task NoContentIsNoJob()
    {
        using var transport = Create(new(HttpStatusCode.NoContent));
        Assert.Null(await transport.PollStageAsync(default));
    }

    [Theory]
    [InlineData(302)]
    [InlineData(401)]
    [InlineData(409)]
    [InlineData(500)]
    public async Task RejectionIsSanitizedAndNeverFollowsRedirect(int status)
    {
        using var transport = Create(new((HttpStatusCode)status) { Content = new StringContent("secret-token-in-body") });
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => transport.PollStageAsync(default));
        Assert.Equal((HttpStatusCode)status, error.StatusCode); Assert.DoesNotContain("secret", error.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("not-json")]
    [InlineData("{\"snapshotJson\":\"must-not-be-accepted\"}")]
    public async Task InvalidOrUnknownWireContentIsSanitized(string body)
    {
        using var transport = Create(new(HttpStatusCode.OK) { Content = new StringContent(body) });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.PollStageAsync(default));
        Assert.DoesNotContain(body.Length == 0 ? "sentinel-never" : body, error.ToString());
    }

    [Fact]
    public async Task ReceiptBodyIsBoundedEvenWithoutContentLength()
    {
        using var transport = Create(new(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(new string(' ', 16385)))) });
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.PollStageAsync(default));
    }

    [Fact]
    public async Task HeartbeatUsesDedicatedRoute()
    {
        var heartbeat = new LeaseHeartbeat(Guid.NewGuid(), 1); var status = new LeaseStatus(DateTimeOffset.UtcNow.AddMinutes(2));
        using var transport = new AgentTransport(new Uri("https://server.test/"), Token, new Handler(request =>
        {
            Assert.Equal("/api/agent/stage/heartbeat", request.RequestUri!.AbsolutePath);
            Assert.NotNull(request.Content);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(status) };
        }));
        Assert.Equal(status, await transport.HeartbeatStageAsync(heartbeat, default));
    }

    private static AgentTransport Create(HttpResponseMessage response) => new(new Uri("https://server.test/"), Token, new Handler(_ => response));
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }
}
// end
