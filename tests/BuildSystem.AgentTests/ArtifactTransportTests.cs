using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BuildSystem.Agent;
using BuildSystem.Contracts;
using Xunit;

namespace BuildSystem.AgentTests;

// Handler injection checks request/response behavior, not TLS or production redirect enforcement.
public sealed class ArtifactTransportTests
{
    private const string Token = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Secret = "RAW_SERVER_SECRET";
    private static ReportStagedArtifact Report() => new(Guid.NewGuid(), 1, Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), new string('b', 64), 42, "dt");

    [Fact]
    public async Task Sends_metadata_to_origin_with_bearer_and_returns_verified_receipt()
    {
        var report = Report();
        using var transport = new AgentTransport(new Uri("https://agent.example/"), Token,
            new Handler(async (request, ct) =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("https://agent.example/api/agent/artifacts/staged", request.RequestUri!.AbsoluteUri);
                Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
                Assert.Equal(Token, request.Headers.Authorization.Parameter);
                Assert.Equal(report, await request.Content!.ReadFromJsonAsync<ReportStagedArtifact>(ct));
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new StagedArtifactReceipt(report.ArtifactId, "ContentVerified")) };
            }));
        Assert.Equal(new StagedArtifactReceipt(report.ArtifactId, "ContentVerified"),
            await transport.ReportArtifactAsync(report, default));
    }

    [Theory]
    [InlineData(201)] [InlineData(204)] [InlineData(302)] [InlineData(400)]
    [InlineData(401)] [InlineData(403)] [InlineData(404)] [InlineData(409)]
    [InlineData(410)] [InlineData(422)] [InlineData(429)] [InlineData(500)]
    public async Task Requires_exact_200_and_never_exposes_response_body(int status)
    {
        using var transport = Transport((HttpStatusCode)status, Secret);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => transport.ReportArtifactAsync(Report(), default));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.DoesNotContain(Secret, error.ToString());
        Assert.DoesNotContain(Token, error.ToString());
    }

    [Theory]
    [InlineData("")] [InlineData("null")] [InlineData("RAW_SERVER_SECRET not json")]
    [InlineData("{}")] [InlineData("{\"artifactId\":\"RAW_SERVER_SECRET\"}")]
    public async Task Invalid_receipt_is_typed_and_sanitized(string body)
    {
        using var transport = Transport(HttpStatusCode.OK, body);
        await AssertMismatch(transport, Report());
    }

    [Theory]
    [InlineData(true, "ContentVerified")] [InlineData(false, "Ready")]
    [InlineData(false, "contentverified")] [InlineData(false, "RAW_SERVER_SECRET")]
    public async Task Receipt_requires_same_id_and_exact_verified_state(bool wrongId, string state)
    {
        var report = Report();
        using var transport = Transport(HttpStatusCode.OK, JsonSerializer.Serialize(
            new StagedArtifactReceipt(wrongId ? Guid.NewGuid() : report.ArtifactId, state)));
        await AssertMismatch(transport, report);
    }

    [Fact]
    public async Task Oversize_chunked_receipt_is_rejected_after_at_most_limit_plus_one_bytes()
    {
        var stream = new CountingStream(100_000);
        using var transport = new AgentTransport(new Uri("https://agent.example/"), Token,
            new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(stream) })));
        await AssertMismatch(transport, Report());
        Assert.Equal(16 * 1024 + 1, stream.BytesRead);
    }

    [Fact]
    public async Task Invalid_metadata_is_rejected_before_send()
    {
        using var transport = new AgentTransport(new Uri("https://agent.example/"), Token,
            new Handler((_, _) => throw new InvalidOperationException("Must not send")));
        await Assert.ThrowsAsync<ArgumentException>(() => transport.ReportArtifactAsync(Report() with { Sha256 = Secret }, default));
    }

    [Theory]
    [InlineData("http://agent.example/")] [InlineData("https://agent.example/path")]
    [InlineData("https://user:pass@agent.example/")] [InlineData("https://agent.example/?token=secret")]
    public void Origin_validation_is_preserved(string origin) =>
        Assert.Throws<ArgumentException>(() => new AgentTransport(new Uri(origin), Token, new Handler((_, _) => throw new Exception())));

    private static AgentTransport Transport(HttpStatusCode status, string body) => new(new Uri("https://agent.example/"), Token,
        new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") })));

    private static async Task AssertMismatch(AgentTransport transport, ReportStagedArtifact report)
    {
        var error = await Assert.ThrowsAsync<ArtifactReceiptException>(() => transport.ReportArtifactAsync(report, default));
        Assert.Equal("ReceiptMismatch", error.Code);
        Assert.DoesNotContain(Secret, error.ToString());
        Assert.DoesNotContain(Token, error.ToString());
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    private sealed class CountingStream(int length) : Stream
    {
        public int BytesRead { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length, length - BytesRead);
            buffer.Span[..count].Fill((byte)' ');
            BytesRead += count;
            return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
// end
