using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BuildSystem.Agent;
using BuildSystem.Agent.Execution;
using Xunit;

namespace BuildSystem.AgentTests;

// A trusted-handler contract suite, not evidence of TLS or real-server interoperability.
public sealed class TargetRegistrationEvidenceHttpClientTests
{
    private const string Secret = "private-transport-canary";
    private static readonly Guid Claim = Guid.Parse("12345678-1234-4234-8234-123456789abc");
    private static readonly Guid Operation = Guid.Parse("abcdef01-1234-4234-8234-123456789abc");
    private static TargetRegistrationEvidenceReport Report() => new(Claim,
        Guid.Parse("22345678-1234-4234-8234-123456789abc"), Guid.Parse("32345678-1234-4234-8234-123456789abc"),
        Guid.Parse("42345678-1234-4234-8234-123456789abc"), Guid.Parse("52345678-1234-4234-8234-123456789abc"),
        Guid.Parse("62345678-1234-4234-8234-123456789abc"), InfobaseRegistrationReadback.ObservedProfileId,
        "target-db", "sql.example\\INSTANCE", "target-db", 12, "2026-11-01T12:34:56.1234567+03:00", "ONLINE");
    private static TargetRegistrationEvidenceHttpClientOptions Options(int timeout = 30) =>
        new() { Enabled = true, BaseUri = new Uri("https://evidence.example/"), TimeoutSeconds = timeout };
    private static Dictionary<string, object?> Response() => new()
    {
        ["claimId"] = Claim.ToString("D"), ["operationId"] = Operation.ToString("D"), ["status"] = "Recorded",
        ["acceptedAt"] = "2026-11-01T09:34:56.1234567+00:00", ["executable"] = false,
        ["ownershipEstablished"] = false, ["restoreAllowed"] = false, ["retryAllowed"] = false,
        ["freshAuthority"] = false, ["requiresIndependentVerification"] = true, ["scheduledJobsMustRemainBlocked"] = true
    };
    private static HttpResponseMessage Reply(HttpRequestMessage request, byte[]? bytes = null, HttpContent? content = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request,
            Content = content ?? new ByteArrayContent(bytes ?? JsonSerializer.SerializeToUtf8Bytes(Response())) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        response.Headers.Pragma.Add(new NameValueHeaderValue("no-cache"));
        return response;
    }
    private static async Task Reject(Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<TargetRegistrationEvidenceTransportException>(action);
        Assert.Equal("TargetRegistrationEvidenceTransportUnavailable", error.Code);
        Assert.Equal(error.Code, error.Message); Assert.Equal(error.Code, error.ToString()); Assert.Null(error.InnerException);
        Assert.DoesNotContain(Secret, error.ToString());
    }

    [Fact]
    public async Task PostsExactlyThirteenHistoricalFieldsToFixedRouteAndReturnsInertElevenFieldReceipt()
    {
        byte[]? captured = null; var providers = 0;
        using var handler = new Handler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://evidence.example/api/agent/target-creation/registration-evidence", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme); Assert.Equal(Secret, request.Headers.Authorization.Parameter);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            captured = (await request.Content.ReadAsByteArrayAsync(ct)).ToArray();
            return Reply(request);
        });
        using var client = new TargetRegistrationEvidenceHttpClient(handler, Options(), () => { providers++; return Secret; });
        var receipt = await client.ReportAsync(Report(), default);
        Assert.Equal(1, providers); Assert.Equal(1, handler.Calls); Assert.NotNull(captured); Assert.InRange(captured.Length, 1, 8192);
        using var json = JsonDocument.Parse(captured);
        Assert.Equal(new[] { "claimId", "reportedInfobaseId", "racObservationId", "sqlObservationId", "racEndpointMappingId",
            "sqlEndpointMappingId", "registrationProfileId", "infobaseName", "sqlServer", "databaseName", "sqlDatabaseId", "sqlCreateDate", "sqlState" },
            json.RootElement.EnumerateObject().Select(p => p.Name));
        var expected = JsonSerializer.SerializeToElement(Report(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        foreach (var property in expected.EnumerateObject()) Assert.Equal(property.Value.GetRawText(), json.RootElement.GetProperty(property.Name).GetRawText());
        Assert.Equal(6, json.RootElement.EnumerateObject().Count(p => p.Name.EndsWith("Id", StringComparison.Ordinal) && p.Value.ValueKind == JsonValueKind.String && Guid.TryParse(p.Value.GetString(), out _)));
        Assert.NotEqual(Report().RacObservationId, Report().SqlObservationId);
        Assert.Equal(Claim, receipt.ClaimId); Assert.Equal(Operation, receipt.OperationId); Assert.Equal("Recorded", receipt.Status);
        Assert.NotEqual(default, receipt.AcceptedAt); Assert.Equal(TimeSpan.Zero, receipt.AcceptedAt.Offset);
        Assert.False(receipt.Executable); Assert.False(receipt.OwnershipEstablished); Assert.False(receipt.RestoreAllowed);
        Assert.False(receipt.RetryAllowed); Assert.False(receipt.FreshAuthority);
        Assert.True(receipt.RequiresIndependentVerification); Assert.True(receipt.ScheduledJobsMustRemainBlocked);
        Assert.Equal(11, JsonSerializer.SerializeToElement(receipt).EnumerateObject().Count());
        Assert.DoesNotContain(Secret, receipt.ToString()); Assert.DoesNotContain(Report().SqlServer, receipt.ToString());
    }

    [Theory]
    [InlineData("2026-11-01T09:34:56Z")] [InlineData("2026-11-01T09:34:56.1Z")]
    [InlineData("2026-11-01T09:34:56.1234567Z")] [InlineData("2026-11-01T09:34:56+00:00")]
    public async Task AcceptsSystemTextJsonUtcTimestampFractionVariants(string timestamp)
    {
        var body = Response(); body["acceptedAt"] = timestamp;
        using var handler = new Handler((r, _) => Task.FromResult(Reply(r, JsonSerializer.SerializeToUtf8Bytes(body))));
        using var client = new TargetRegistrationEvidenceHttpClient(handler, Options(), () => Secret);
        Assert.Equal(TimeSpan.Zero, (await client.ReportAsync(Report(), default)).AcceptedAt.Offset);
    }

    [Fact]
    public async Task DefaultOffDoesNotCallProviderOrHandlerEvenWithIgnoredInvalidOptions()
    {
        Assert.False(new TargetRegistrationEvidenceHttpClientOptions().Enabled);
        Assert.Null(new TargetRegistrationEvidenceHttpClientOptions().BaseUri);
        Assert.Equal(30, new TargetRegistrationEvidenceHttpClientOptions().TimeoutSeconds);
        using var handler = new Handler((_, _) => throw new InvalidOperationException(Secret)); var providers = 0;
        using var client = new TargetRegistrationEvidenceHttpClient(handler,
            new() { BaseUri = new Uri("http://invalid/path"), TimeoutSeconds = -1 }, () => { providers++; throw new Exception(Secret); });
        await Reject(() => client.ReportAsync(Report(), default)); Assert.Equal(0, providers); Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("http://evidence.example/")] [InlineData("https://evidence.example/path")]
    [InlineData("https://evidence.example/?query=secret")] [InlineData("https://user:secret@evidence.example/")]
    [InlineData("https://evidence.example/#secret")] [InlineData("relative")]
    public void InvalidEnabledBaseUriIsFixedFailure(string uri)
    {
        using var handler = new Handler((r, _) => Task.FromResult(Reply(r)));
        var error = Assert.Throws<TargetRegistrationEvidenceTransportException>(() => new TargetRegistrationEvidenceHttpClient(handler,
            new() { Enabled = true, BaseUri = new Uri(uri, UriKind.RelativeOrAbsolute) }, () => Secret));
        Assert.Equal("TargetRegistrationEvidenceTransportUnavailable", error.ToString()); Assert.Null(error.InnerException);
    }

    [Theory] [InlineData(0)] [InlineData(-1)] [InlineData(121)]
    public void InvalidTimeoutIsFixedFailure(int seconds)
    {
        using var handler = new Handler((r, _) => Task.FromResult(Reply(r)));
        Assert.Throws<TargetRegistrationEvidenceTransportException>(() => new TargetRegistrationEvidenceHttpClient(handler, Options(seconds), () => Secret));
    }

    [Fact]
    public async Task RejectsMalformedReportBeforeCredentialProviderAndHttp()
    {
        var r = Report(); var invalid = new[] { r with { ClaimId = Guid.Empty }, r with { ReportedInfobaseId = Guid.Empty },
            r with { RacObservationId = Guid.Empty }, r with { SqlObservationId = r.RacObservationId },
            r with { RacEndpointMappingId = Guid.Empty }, r with { SqlEndpointMappingId = Guid.Empty },
            r with { RegistrationProfileId = null! }, r with { RegistrationProfileId = Secret }, r with { InfobaseName = null! },
            r with { InfobaseName = "" }, r with { InfobaseName = new string('a', 129) }, r with { InfobaseName = "bad\nname" },
            r with { SqlServer = null! }, r with { SqlServer = new string('s', 256) }, r with { SqlServer = "bad\rserver" },
            r with { DatabaseName = null! }, r with { DatabaseName = "" }, r with { DatabaseName = new string('d', 129) },
            r with { SqlDatabaseId = 0 }, r with { SqlCreateDate = null! }, r with { SqlCreateDate = "not-a-date" },
            r with { SqlCreateDate = "2026-11-01T12:34:56+25:00" }, r with { SqlCreateDate = "2026-11-01T12:34:56.0000000-00:00" },
            r with { SqlCreateDate = "0001-01-01T01:00:00.0000000+14:00" }, r with { SqlState = null! }, r with { SqlState = "OFFLINE" } };
        var providers = 0; using var handler = new Handler((q, _) => Task.FromResult(Reply(q)));
        using var client = new TargetRegistrationEvidenceHttpClient(handler, Options(), () => { providers++; return Secret; });
        foreach (var report in invalid) await Reject(() => client.ReportAsync(report, default));
        await Reject(() => client.ReportAsync(null!, default)); Assert.Equal(0, providers); Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ReceiptRequiresExactNamesTypesGuidsStatusTimestampAndAllSevenSafetyFlags()
    {
        var bad = new List<Dictionary<string, object?>>();
        foreach (var key in Response().Keys)
        {
            var missing = Response(); missing.Remove(key); bad.Add(missing);
            var wrongCase = Response(); wrongCase[key.ToUpperInvariant()] = wrongCase[key]; wrongCase.Remove(key); bad.Add(wrongCase);
            var nullValue = Response(); nullValue[key] = null; bad.Add(nullValue);
        }
        foreach (var key in new[] { "executable", "ownershipEstablished", "restoreAllowed", "retryAllowed", "freshAuthority", "requiresIndependentVerification", "scheduledJobsMustRemainBlocked" })
        {
            var inverted = Response(); inverted[key] = !(bool)inverted[key]!; bad.Add(inverted);
            var stringValue = Response(); stringValue[key] = "false"; bad.Add(stringValue);
            var number = Response(); number[key] = 0; bad.Add(number);
        }
        foreach (var (key, value) in new[] { ("claimId", Guid.NewGuid().ToString("D")), ("claimId", Claim.ToString("D").ToUpperInvariant()),
            ("operationId", Guid.Empty.ToString("D")), ("operationId", Operation.ToString("N")), ("status", "recorded"),
            ("acceptedAt", "0001-01-01T00:00:00+00:00"), ("acceptedAt", "2026-11-01T10:34:56+01:00"),
            ("acceptedAt", "2026-11-01"), ("acceptedAt", "2026-11-01T09:34:56.Z"),
            ("acceptedAt", "2026-11-01T09:34:56.+00:00"), ("acceptedAt", "2026-11-01T09:34:56.12345678Z") })
        { var changed = Response(); changed[key] = value; bad.Add(changed); }
        var extra = Response(); extra["authority"] = Secret; bad.Add(extra);
        foreach (var body in bad)
        {
            using var handler = new Handler((q, _) => Task.FromResult(Reply(q, JsonSerializer.SerializeToUtf8Bytes(body))));
            using var client = new TargetRegistrationEvidenceHttpClient(handler, Options(), () => Secret);
            await Reject(() => client.ReportAsync(Report(), default)); Assert.Equal(1, handler.Calls);
        }
    }

    [Theory] [InlineData(201)] [InlineData(204)] [InlineData(301)] [InlineData(302)] [InlineData(307)] [InlineData(400)] [InlineData(401)] [InlineData(500)]
    public async Task Non200AndRedirectNeverReadErrorBodyOrAutomaticallyRetry(int status)
    {
        var body = new TrackingContent(Array.Empty<byte>(), throwsRead: true); var providers = 0;
        using var handler = new Handler((r, _) => { var reply = Reply(r, content: body); reply.StatusCode = (HttpStatusCode)status;
            reply.Headers.Location = new Uri("https://other.example/secret"); return Task.FromResult(reply); });
        using var client = new TargetRegistrationEvidenceHttpClient(handler, Options(), () => { providers++; return Secret; });
        await Reject(() => client.ReportAsync(Report(), default)); Assert.Equal(0, body.Opens); Assert.True(body.Disposed);
        Assert.Equal(1, handler.Calls); Assert.Equal(1, providers);
    }

    [Theory] [InlineData("type")] [InlineData("cache")] [InlineData("pragma")] [InlineData("encoding")] [InlineData("cookie")] [InlineData("length")]
    public async Task InvalidResponseHeadersRejectedBeforeOpeningBody(string defect)
    {
        var body = new TrackingContent(Array.Empty<byte>(), throwsRead: true);
        using var handler = new Handler((r, _) => { var reply = Reply(r, content: body);
            switch (defect) { case "type": body.Headers.ContentType = new("text/plain"); break;
                case "cache": reply.Headers.CacheControl = new() { NoStore = false }; break;
                case "pragma": reply.Headers.Pragma.Clear(); break; case "encoding": body.Headers.ContentEncoding.Add("gzip"); break;
                case "cookie": reply.Headers.TryAddWithoutValidation("Set-Cookie", Secret); break; case "length": body.Headers.ContentLength = 2049; break; }
            return Task.FromResult(reply); });
        using var client = new TargetRegistrationEvidenceHttpClient(handler, Options(), () => Secret);
        await Reject(() => client.ReportAsync(Report(), default)); Assert.Equal(0, body.Opens); Assert.True(body.Disposed);
    }

    [Fact]
    public async Task StrictUtf8ClosedJsonAndBoundedResponseRejectAdversarialBodies()
    {
        var json = JsonSerializer.Serialize(Response());
        var bodies = new[] { Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(json)).ToArray(), new byte[] { 0xff, 0xfe },
            Encoding.UTF8.GetBytes(json[..^1]), Encoding.UTF8.GetBytes(json[..^1] + ",\"claimId\":\"" + Claim + "\"}"),
            Encoding.UTF8.GetBytes(json + "{}"), Encoding.UTF8.GetBytes("[]"), Encoding.UTF8.GetBytes(new string(' ', 2049) + json) };
        foreach (var bytes in bodies)
        {
            var body = new TrackingContent(bytes); using var handler = new Handler((r, _) => Task.FromResult(Reply(r, content: body)));
            using var client = new TargetRegistrationEvidenceHttpClient(handler, Options(), () => Secret);
            await Reject(() => client.ReportAsync(Report(), default)); Assert.True(body.Disposed); Assert.InRange(body.BytesRead, 0, 2049);
        }
    }

    [Fact]
    public async Task CallerCancellationAndOneSecondDeadlineAreFixedFailuresWithCooperativeHandlerCleanup()
    {
        foreach (var callerCancels in new[] { true, false })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var exited = false;
            using var handler = new Handler(async (_, ct) => { entered.TrySetResult(); try { await Task.Delay(Timeout.Infinite, ct); }
                finally { exited = true; } throw new InvalidOperationException(Secret); });
            using var client = new TargetRegistrationEvidenceHttpClient(handler, Options(1), () => Secret);
            using var cancellation = new CancellationTokenSource(); var pending = client.ReportAsync(Report(), cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30)); if (callerCancels) cancellation.Cancel();
            await Reject(async () => await pending.WaitAsync(TimeSpan.FromSeconds(30))); Assert.True(exited); Assert.Equal(1, handler.Calls);
        }
    }

    [Fact]
    public async Task DisposeOwnsHandlerCancelsInflightAndPreventsFurtherCredentialAccess()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var providers = 0;
        var handler = new Handler(async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); throw new Exception(Secret); });
        var client = new TargetRegistrationEvidenceHttpClient(handler, Options(), () => { providers++; return Secret; });
        var pending = client.ReportAsync(Report(), default); await entered.Task.WaitAsync(TimeSpan.FromSeconds(30)); client.Dispose();
        await Reject(async () => await pending.WaitAsync(TimeSpan.FromSeconds(30))); Assert.True(handler.Disposals > 0);
        await Reject(() => client.ReportAsync(Report(), default)); Assert.Equal(1, providers); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ResponseDisposalFailureCannotReturnParsedSuccessOrLeakSecret()
    {
        using var handler = new Handler((r, _) => { var response = new ThrowingResponse { RequestMessage = r,
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(Response())) };
            response.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
            response.Headers.CacheControl = new() { NoStore = true }; response.Headers.Pragma.Add(new("no-cache"));
            return Task.FromResult<HttpResponseMessage>(response); });
        using var client = new TargetRegistrationEvidenceHttpClient(handler, Options(), () => Secret);
        await Reject(() => client.ReportAsync(Report(), default));
    }

    [Fact]
    public async Task FailureHasNoAutomaticRetryButNewExplicitCallCanSucceed()
    {
        var attempts = 0; using var handler = new Handler((r, _) =>
        { if (++attempts == 1) throw new InvalidOperationException(Secret); return Task.FromResult(Reply(r)); });
        var providers = 0; using var client = new TargetRegistrationEvidenceHttpClient(handler, Options(), () => { providers++; return Secret; });
        await Reject(() => client.ReportAsync(Report(), default)); Assert.Equal(1, handler.Calls); Assert.Equal(1, providers);
        Assert.Equal(Operation, (await client.ReportAsync(Report(), default)).OperationId); Assert.Equal(2, handler.Calls); Assert.Equal(2, providers);
    }

    [Theory] [InlineData("provider")] [InlineData("backwards")] [InlineData("send")] [InlineData("read")] [InlineData("cleanup")]
    public async Task WholeBudgetIncludesProviderSendReadAndCleanupAndRejectsBackwardsClock(string phase)
    {
        var clock = new ManualClock(); TrackingContent? body = null;
        void Expire() => clock.Ticks = 31 * TimeSpan.TicksPerSecond;
        using var handler = new Handler((r, _) =>
        {
            body = new TrackingContent(JsonSerializer.SerializeToUtf8Bytes(Response()), afterRead: phase == "read" ? Expire : null);
            var reply = phase == "cleanup" ? new CallbackResponse(Expire) : new HttpResponseMessage();
            reply.StatusCode = HttpStatusCode.OK; reply.RequestMessage = r; reply.Content = body;
            body.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
            reply.Headers.CacheControl = new() { NoStore = true }; reply.Headers.Pragma.Add(new("no-cache"));
            if (phase == "send") Expire(); return Task.FromResult(reply);
        });
        using var client = new TargetRegistrationEvidenceHttpClient(handler, Options(), () =>
        { if (phase == "provider") Expire(); if (phase == "backwards") clock.Ticks = -1; return Secret; }, clock);
        await Reject(() => client.ReportAsync(Report(), default));
        if (body is not null) Assert.True(body.Disposed);
        Assert.Equal(phase is "provider" or "backwards" ? 0 : 1, handler.Calls);
    }

    [Fact]
    public void NullReferencesAlwaysFailEvenDisabledAndHandlerDisposeIsSanitized()
    {
        using var handler = new Handler((r, _) => Task.FromResult(Reply(r)));
        Assert.Throws<TargetRegistrationEvidenceTransportException>(() => new TargetRegistrationEvidenceHttpClient(handler, null!, () => Secret));
        Assert.Throws<TargetRegistrationEvidenceTransportException>(() => new TargetRegistrationEvidenceHttpClient(handler, new(), null!));
        Assert.Throws<TargetRegistrationEvidenceTransportException>(() => new TargetRegistrationEvidenceHttpClient(null!, new(), () => Secret));
        using var client = new TargetRegistrationEvidenceHttpClient(new ThrowingHandler(), Options(), () => Secret);
        client.Dispose();
    }

    [Fact]
    public async Task TimerCleanupIsWithinWholeBudgetAndInvalidCredentialsNeverReachHttp()
    {
        using var handler = new Handler((r, _) => Task.FromResult(Reply(r)));
        using (var client = new TargetRegistrationEvidenceHttpClient(handler, Options(), () => Secret, new ManualClock(expireOnTimerDispose: true)))
            await Reject(() => client.ReportAsync(Report(), default));
        foreach (var credential in new[] { "", "Bearer secret", "bad\r\nheader", "two tokens", "bad,token", "bad;token", new string('x', 257), null! })
        {
            using var unused = new Handler((r, _) => Task.FromResult(Reply(r)));
            using var client = new TargetRegistrationEvidenceHttpClient(unused, Options(), () => credential);
            await Reject(() => client.ReportAsync(Report(), default)); Assert.Equal(0, unused.Calls);
        }
    }

    private sealed class ManualClock(bool expireOnTimerDispose = false) : TimeProvider
    {
        public long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new CleanupTimer(TimeProvider.System.CreateTimer(callback, state, dueTime, period), () => { if (expireOnTimerDispose) Ticks = 31 * TimeSpan.TicksPerSecond; });
    }
    private sealed class CleanupTimer(ITimer inner, Action cleanup) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);
        public void Dispose() { inner.Dispose(); cleanup(); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
    private sealed class CallbackResponse(Action callback) : HttpResponseMessage
    {
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) callback(); }
    }
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new InvalidOperationException(Secret);
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) throw new InvalidOperationException(Secret); }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls; public int Disposals;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return send(request, cancellationToken); }
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }
    private sealed class ThrowingResponse : HttpResponseMessage
    {
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) throw new InvalidOperationException(Secret); }
    }
    private sealed class TrackingContent(byte[] bytes, bool throwsRead = false, Action? afterRead = null) : HttpContent
    {
        public int Opens; public int BytesRead; public bool Disposed;
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new InvalidOperationException(Secret);
        protected override Task<Stream> CreateContentReadStreamAsync() { Opens++; if (throwsRead) throw new InvalidOperationException(Secret);
            return Task.FromResult<Stream>(new ReadStream(bytes, count => { BytesRead += count; afterRead?.Invoke(); })); }
        protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
    }
    private sealed class ReadStream(byte[] bytes, Action<int> count) : MemoryStream(bytes, writable: false)
    {
        private bool readingMemory;
        public override int Read(byte[] buffer, int offset, int length)
        { var read = base.Read(buffer, offset, length); if (!readingMemory) count(read); return read; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read;
            readingMemory = true;
            try { read = base.Read(buffer.Span); }
            finally { readingMemory = false; }
            count(read);
            return ValueTask.FromResult(read);
        }
    }
}
