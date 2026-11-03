using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BuildSystem.Agent;
using BuildSystem.Contracts;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class TargetObservationHttpClientTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly Guid Agent = Guid.NewGuid();
    private static TargetObservationAssignment Assignment() => new(Guid.NewGuid(), Guid.NewGuid(), Agent,
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static ReportTargetObservation Report() => new(Guid.NewGuid(), Assignment(), Guid.NewGuid(),
        Guid.NewGuid(), TargetObservationOutcome.BothObservedAbsent, DateTimeOffset.UtcNow);
    private static TargetObservationHttpClientOptions Options(int timeout = 30) => new()
        { BaseUri = new Uri("https://observation.example/"), AgentId = Agent, TimeoutSeconds = timeout };
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, Web);
    private static HttpResponseMessage Reply(HttpRequestMessage request, string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static TargetObservationHttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action,
        Func<string>? credential = null, int timeout = 30) =>
        new(new Handler(action), Options(timeout), credential ?? (() => "secret-token"));
    private static TargetObservationHttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> action) =>
        Client((request, _) => Task.FromResult(action(request)));
    private static string Change(string json, string name, JsonNode? value)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        root[name] = value;
        return root.ToJsonString();
    }
    private static async Task AssertFailure(Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<TargetObservationTransportException>(action);
        Assert.Equal("Target observation transport failed.", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Read_uses_fixed_absolute_route_and_fresh_authorization()
    {
        var assignment = Assignment();
        using var client = Client(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://observation.example/api/agents/target-observations/" + assignment.ObservationRequestId.ToString("D"), request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("secret-token", request.Headers.Authorization.Parameter);
            Assert.Null(request.Content);
            Assert.False(request.Headers.Contains("Cookie"));
            return Reply(request, Json(assignment));
        });
        Assert.Equal(assignment, await client.ReadAsync(assignment.ObservationRequestId, default));
    }

    [Theory]
    [InlineData("http://observation.example/")]
    [InlineData("https://observation.example/path/")]
    [InlineData("https://observation.example/?secret=value")]
    [InlineData("https://observation.example/#secret")]
    [InlineData("https://user:secret@observation.example/")]
    [InlineData("/relative")]
    public void Untrusted_base_is_rejected_without_echoing_configuration(string address)
    {
        var exception = Assert.Throws<ArgumentException>(() => new TargetObservationHttpClient(new Handler((_, _) =>
            throw new InvalidOperationException()), new() { BaseUri = new Uri(address, UriKind.RelativeOrAbsolute), AgentId = Agent }, () => "secret"));
        Assert.DoesNotContain(address, exception.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public void Timeout_configuration_is_bounded(int timeout) => Assert.Throws<ArgumentException>(() =>
        new TargetObservationHttpClient(Options(timeout), () => "secret"));

    [Fact]
    public void Empty_agent_is_rejected() => Assert.Throws<ArgumentException>(() => new TargetObservationHttpClient(
        new() { BaseUri = new Uri("https://observation.example/"), AgentId = Guid.Empty }, () => "secret"));

    [Theory]
    [InlineData(201)]
    [InlineData(204)]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    [InlineData(401)]
    [InlineData(500)]
    public async Task Only_200_is_accepted(int status)
    {
        using var client = Client(request => Reply(request, "secret-server-body", (HttpStatusCode)status));
        await AssertFailure(() => client.ReadAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Changed_response_destination_is_rejected()
    {
        var assignment = Assignment();
        using var client = Client(_ => Reply(new HttpRequestMessage(HttpMethod.Get, "https://other.example/"), Json(assignment)));
        await AssertFailure(() => client.ReadAsync(assignment.ObservationRequestId, default));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Bearer token")]
    [InlineData("bearer-token")]
    [InlineData("token\r\nInjected: header")]
    [InlineData("white space")]
    [InlineData("nonasciié")]
    [InlineData("control\u007f")]
    public async Task Invalid_credentials_never_reach_handler(string credential)
    {
        var calls = 0;
        using var client = Client((_, _) => { calls++; throw new Exception("secret"); }, () => credential);
        await AssertFailure(() => client.ReadAsync(Guid.NewGuid(), default));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Oversize_credential_is_rejected()
    {
        using var client = Client((_, _) => throw new Exception("secret"), () => new string('a', 257));
        await AssertFailure(() => client.ReadAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Credential_provider_failures_are_sanitized()
    {
        using var client = Client((_, _) => throw new Exception("unexpected"), () => throw new Exception("secret-token https://secret.example"));
        await AssertFailure(() => client.ReadAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Credentials_rotate_per_request_without_shared_headers()
    {
        var assignment = Assignment();
        var credentials = new List<string?>();
        var number = 0;
        using var client = Client((request, _) =>
        {
            credentials.Add(request.Headers.Authorization!.Parameter);
            return Task.FromResult(Reply(request, Json(assignment)));
        }, () => "rotation-" + Interlocked.Increment(ref number));
        await client.ReadAsync(assignment.ObservationRequestId, default);
        await client.ReadAsync(assignment.ObservationRequestId, default);
        Assert.Equal(new[] { "rotation-1", "rotation-2" }, credentials);
    }

    [Theory]
    [InlineData("executable", "true")]
    [InlineData("scheduledJobsMustRemainBlocked", "false")]
    [InlineData("executable", "null")]
    [InlineData("infobaseName", "\"wrong\"")]
    [InlineData("sqlDatabaseName", "\"wrong\"")]
    [InlineData("unknown", "123")]
    public async Task Assignment_raw_safety_computed_and_unknown_properties_are_validated(string property, string value)
    {
        var assignment = Assignment();
        using var client = Client(request => Reply(request, Change(Json(assignment), property, JsonNode.Parse(value))));
        await AssertFailure(() => client.ReadAsync(assignment.ObservationRequestId, default));
    }

    [Theory]
    [InlineData("executable")]
    [InlineData("scheduledJobsMustRemainBlocked")]
    public async Task Assignment_requires_explicit_safety_flags(string property)
    {
        var assignment = Assignment();
        var json = JsonNode.Parse(Json(assignment))!.AsObject();
        json.Remove(property);
        using var client = Client(request => Reply(request, json.ToJsonString()));
        await AssertFailure(() => client.ReadAsync(assignment.ObservationRequestId, default));
    }

    [Theory]
    [InlineData("observationRequestId")]
    [InlineData("restoreIntentId")]
    [InlineData("agentId")]
    [InlineData("operationId")]
    [InlineData("environmentId")]
    [InlineData("stageJobId")]
    [InlineData("reservationId")]
    [InlineData("targetIncarnationId")]
    [InlineData("canonicalClusterId")]
    [InlineData("canonicalSqlServerId")]
    [InlineData("racEndpointMappingId")]
    [InlineData("sqlEndpointMappingId")]
    public async Task Every_assignment_pin_is_required(string property)
    {
        var assignment = Assignment();
        var json = JsonNode.Parse(Json(assignment))!.AsObject();
        json.Remove(property);
        using var client = Client(request => Reply(request, json.ToJsonString()));
        await AssertFailure(() => client.ReadAsync(assignment.ObservationRequestId, default));
    }

    [Fact]
    public async Task Wrong_request_agent_or_nonpositive_fence_is_rejected()
    {
        var assignment = Assignment();
        foreach (var invalid in new[] { assignment with { ObservationRequestId = Guid.NewGuid() },
            assignment with { AgentId = Guid.NewGuid() }, assignment with { FencingToken = 0 }, assignment with { FencingToken = -1 } })
        {
            using var client = Client(request => Reply(request, Json(invalid)));
            await AssertFailure(() => client.ReadAsync(assignment.ObservationRequestId, default));
        }
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"x\":1,\"X\":2}")]
    [InlineData("{\"unknown\":{\"x\":1,\"X\":2}}")]
    public async Task Malformed_missing_or_duplicate_json_is_rejected(string json)
    {
        using var client = Client(request => Reply(request, json));
        await AssertFailure(() => client.ReadAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Valid_assignment_with_case_insensitive_duplicate_pin_is_rejected()
    {
        var assignment = Assignment();
        var json = Json(assignment);
        json = json[..^1] + ",\"AGENTID\":\"" + Agent + "\"}";
        using var client = Client(request => Reply(request, json));
        await AssertFailure(() => client.ReadAsync(assignment.ObservationRequestId, default));
    }

    [Fact]
    public async Task Report_sends_complete_pins_and_accepts_only_correlated_recorded_receipt()
    {
        var report = Report();
        using var client = Client(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://observation.example/api/agents/target-observation-receipts", request.RequestUri!.AbsoluteUri);
            var posted = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            Assert.False(posted["executable"]!.GetValue<bool>());
            Assert.True(posted["scheduledJobsMustRemainBlocked"]!.GetValue<bool>());
            Assert.False(posted["assignment"]!["executable"]!.GetValue<bool>());
            Assert.True(posted["assignment"]!["scheduledJobsMustRemainBlocked"]!.GetValue<bool>());
            Assert.Equal(report, JsonSerializer.Deserialize<ReportTargetObservation>(posted.ToJsonString(), Web));
            return Reply(request, Json(new TargetObservationReceipt(report.ReceiptId, report.Assignment.ObservationRequestId, "Recorded")));
        });
        var receipt = await client.ReportAsync(report, default);
        Assert.Equal(report.ReceiptId, receipt.ReceiptId);
    }

    [Theory]
    [InlineData("receiptId", "\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("observationRequestId", "\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("state", "\"recorded\"")]
    [InlineData("state", "\"Rejected\"")]
    [InlineData("executable", "true")]
    [InlineData("scheduledJobsMustRemainBlocked", "false")]
    [InlineData("unknown", "true")]
    public async Task Mismatched_or_unsafe_ack_is_rejected(string property, string value)
    {
        var report = Report();
        var ack = new TargetObservationReceipt(report.ReceiptId, report.Assignment.ObservationRequestId, "Recorded");
        using var client = Client(request => Reply(request, Change(Json(ack), property, JsonNode.Parse(value))));
        await AssertFailure(() => client.ReportAsync(report, default));
    }

    [Theory]
    [InlineData("executable")]
    [InlineData("scheduledJobsMustRemainBlocked")]
    public async Task Ack_requires_explicit_safety_flags(string property)
    {
        var report = Report();
        var ack = JsonNode.Parse(Json(new TargetObservationReceipt(report.ReceiptId, report.Assignment.ObservationRequestId, "Recorded")))!.AsObject();
        ack.Remove(property);
        using var client = Client(request => Reply(request, ack.ToJsonString()));
        await AssertFailure(() => client.ReportAsync(report, default));
    }

    [Fact]
    public async Task Invalid_reports_are_rejected_before_sending()
    {
        var report = Report();
        var calls = 0;
        using var client = Client((_, _) => { calls++; throw new Exception("unexpected"); });
        foreach (var invalid in new[] { report with { ReceiptId = Guid.Empty }, report with { RacObservationId = Guid.Empty },
            report with { SqlObservationId = Guid.Empty }, report with { SqlObservationId = report.RacObservationId },
            report with { Outcome = (TargetObservationOutcome)99 }, report with { ObservedAt = default },
            report with { Assignment = report.Assignment with { AgentId = Guid.NewGuid() } },
            report with { Assignment = report.Assignment with { FencingToken = 0 } },
            report with { Assignment = report.Assignment with { ReservationId = Guid.Empty } } })
            await AssertFailure(() => client.ReportAsync(invalid, default));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Oversize_content_length_is_rejected_before_reading()
    {
        using var client = Client(request =>
        {
            var response = Reply(request, "{}");
            response.Content.Headers.ContentLength = 65537;
            return response;
        });
        await AssertFailure(() => client.ReadAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Chunked_response_is_bounded_and_stream_disposed()
    {
        var stream = new CountingStream(100_000);
        using var client = Client(request => new HttpResponseMessage(HttpStatusCode.OK)
            { RequestMessage = request, Content = new StreamContent(stream) });
        await AssertFailure(() => client.ReadAsync(Guid.NewGuid(), default));
        Assert.Equal(65537, stream.BytesRead);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Handler_and_stream_faults_are_sanitized()
    {
        using var client = Client((_, _) => throw new HttpRequestException("secret-token https://secret.example"));
        await AssertFailure(() => client.ReadAsync(Guid.NewGuid(), default));
        using var streamClient = Client(request => new HttpResponseMessage(HttpStatusCode.OK)
            { RequestMessage = request, Content = new StreamContent(new CountingStream(100, true)) });
        await AssertFailure(() => streamClient.ReadAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Caller_cancellation_is_sanitized_and_preserves_token()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = Client(async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            throw new Exception("secret");
        });
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => client.ReadAsync(Guid.NewGuid(), cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("secret", exception.ToString());
    }

    [Fact]
    public async Task Deadline_bounds_handler_even_when_handler_ignores_cancellation()
    {
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = Client((_, _) => pending.Task, timeout: 1);
        await AssertFailure(() => client.ReadAsync(Guid.NewGuid(), default));
        pending.SetResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    [Fact]
    public async Task Disposed_client_never_sends_request()
    {
        var calls = 0;
        var client = Client((_, _) => { calls++; throw new Exception("unexpected"); });
        client.Dispose();
        await AssertFailure(() => client.ReadAsync(Guid.NewGuid(), default));
        Assert.Equal(0, calls);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            action(request, cancellationToken);
    }

    private sealed class CountingStream(int length, bool fail = false) : Stream
    {
        public int BytesRead { get; private set; }
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (fail) throw new IOException("secret-token https://secret.example");
            var read = Math.Min(count, length - BytesRead);
            Array.Fill(buffer, (byte)' ', offset, read);
            BytesRead += read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (fail) throw new IOException("secret-token https://secret.example");
            var read = Math.Min(buffer.Length, length - BytesRead);
            buffer.Span[..read].Fill((byte)' ');
            BytesRead += read;
            return ValueTask.FromResult(read);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
