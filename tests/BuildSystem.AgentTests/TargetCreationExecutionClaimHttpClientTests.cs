using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BuildSystem.Agent;
using Xunit;

namespace BuildSystem.AgentTests;

// Fake handlers only: delivery metadata is not proof of ongoing SQL authority or permission to run jobs.
[Collection("Agent CLI subprocesses")]
public sealed class TargetCreationExecutionClaimHttpClientTests
{
    private const string Canary = "secret-canary";
    private static readonly DateTimeOffset Issued = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static TargetCreationIntent Intent()
    {
        var r = Guid.NewGuid();
        return new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 17, r, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "bs_test_" + r.ToString("N"), "bs_test_" + r.ToString("N"));
    }
    private static TargetCreationPermitBinding Binding(TargetCreationIntent i) =>
        new(Guid.NewGuid(), i.OperationId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Issued, Issued.AddMinutes(5))
        { CredentialId = Guid.NewGuid(), EndpointMappingId = Guid.NewGuid() };
    private static TargetCreationExecutionClaimHttpClientOptions Options(bool enabled = true, int timeout = 30) =>
        new() { BaseUri = new Uri("https://claims.example/"), Enabled = enabled, TimeoutSeconds = timeout };
    private static TargetCreationExecutionClaimHttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        Func<string>? provider = null, bool enabled = true, int timeout = 30) => new(new Handler(send), Options(enabled, timeout), provider ?? (() => "token"));
    private static TargetCreationExecutionClaimHttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> send) => Client((r, _) => Task.FromResult(send(r)));
    private static string Json(TargetCreationIntent i, TargetCreationPermitBinding b) => JsonSerializer.Serialize(new
    {
        claimId = Guid.NewGuid(), permitId = b.PermitId, operationId = i.OperationId, attemptId = b.AttemptId, agentId = b.AgentId,
        credentialId = b.CredentialId, endpointMappingId = b.EndpointMappingId, issuedAt = b.IssuedAt.AddSeconds(1), expiresAt = b.StartBefore,
        newlyIssued = true, executable = true, scheduledJobsMustRemainBlocked = true, retryAllowed = false
    });
    private static HttpResponseMessage Reply(HttpRequestMessage r, string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { RequestMessage = r, Content = new StringContent(json, Encoding.UTF8, "application/json") };
        response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true }; return response;
    }
    private static string Change(string json, string field, string value)
    { var root = JsonNode.Parse(json)!.AsObject(); root[field] = JsonNode.Parse(value); return root.ToJsonString(); }
    private static async Task Failure(Func<Task> action)
    {
        var e = await Assert.ThrowsAsync<TargetCreationExecutionClaimHttpException>(action);
        Assert.Equal("TargetCreationExecutionClaimUnavailable", e.Message); Assert.Null(e.InnerException);
        Assert.DoesNotContain(Canary, e.ToString()); Assert.DoesNotContain("https://", e.ToString());
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => send(r, ct); }

    [Fact]
    public void Options_default_off_and_thirty_seconds()
    { var o = new TargetCreationExecutionClaimHttpClientOptions { BaseUri = new Uri("https://claims.example/") }; Assert.False(o.Enabled); Assert.Equal(30, o.TimeoutSeconds); }

    [Fact]
    public async Task Historical_receipt_has_exact_pins_and_no_serialization_or_identifier_diagnostics()
    {
        var i = Intent(); var b = Binding(i); var json = Json(i, b);
        using var client = Client(r => Reply(r, json)); ITargetCreationExecutionClaimTransport transport = client;
        var grant = await transport.ClaimAsync(i, b, default);
        Assert.Equal(JsonNode.Parse(json)!["claimId"]!.GetValue<Guid>(), grant.ClaimId);
        Assert.Equal(i, grant.TargetIntent); Assert.Equal(b, grant.ScopeBinding);
        Assert.Equal(b.IssuedAt.AddSeconds(1), grant.IssuedAt); Assert.Equal(b.StartBefore, grant.ExpiresAt);
        Assert.True(grant.NewlyIssued); Assert.True(grant.ServerIssued); Assert.True(grant.Executable); Assert.True(grant.ScheduledJobsMustRemainBlocked); Assert.False(grant.RetryAllowed);
        Assert.True(typeof(TargetCreationExecutionClaimGrant).IsSealed); Assert.Empty(typeof(TargetCreationExecutionClaimGrant).GetConstructors());
        foreach (var id in new[] { grant.ClaimId, b.PermitId, b.OperationId, b.AttemptId, b.AgentId, b.CredentialId!.Value, b.EndpointMappingId!.Value })
            Assert.DoesNotContain(id.ToString(), grant.ToString());
        Assert.DoesNotContain(i.InfobaseName, grant.ToString());
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(grant));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize<object>(grant));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize<TargetCreationExecutionClaimGrant>(null!));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TargetCreationExecutionClaimGrant>(json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TargetCreationExecutionClaimGrant>("null"));
    }

    [Fact]
    public async Task Exact_post_fresh_bearer_and_second_call_reaches_server_without_local_cache_or_retry()
    {
        var i = Intent(); var b = Binding(i); var calls = 0; var tokens = 0;
        using var client = Client(async (r, ct) =>
        {
            calls++; Assert.Equal(HttpMethod.Post, r.Method); Assert.Equal("https://claims.example/api/agents/target-creation-execution-claims", r.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", r.Headers.Authorization!.Scheme); Assert.Equal("rotation-" + calls, r.Headers.Authorization.Parameter); Assert.False(r.Headers.Contains("Cookie"));
            Assert.Equal("application/json", r.Content!.Headers.ContentType!.MediaType);
            using var body = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)); var root = body.RootElement;
            Assert.Equal(new[] { "permitId", "expectedPermitRevision", "attemptId", "credentialId", "endpointMappingId" }, root.EnumerateObject().Select(p => p.Name));
            Assert.Equal(b.PermitId, root.GetProperty("permitId").GetGuid()); Assert.Equal(2, root.GetProperty("expectedPermitRevision").GetInt64());
            Assert.Equal(b.AttemptId, root.GetProperty("attemptId").GetGuid()); Assert.Equal(b.CredentialId, root.GetProperty("credentialId").GetGuid()); Assert.Equal(b.EndpointMappingId, root.GetProperty("endpointMappingId").GetGuid());
            return Reply(r, Json(i, b), calls == 1 ? HttpStatusCode.OK : HttpStatusCode.Conflict);
        }, () => "rotation-" + ++tokens);
        await client.ClaimAsync(i, b, default); await Failure(() => client.ClaimAsync(i, b, default)); Assert.Equal(2, calls); Assert.Equal(2, tokens);
    }

    [Theory]
    [InlineData("http://claims.example/")] [InlineData("https://claims.example/path")] [InlineData("https://claims.example/?secret-canary")]
    [InlineData("https://claims.example/#secret-canary")] [InlineData("https://user:secret-canary@claims.example/")] [InlineData("/relative")]
    public void Bad_options_are_sanitized_even_disabled(string uri)
    {
        var e = Assert.Throws<TargetCreationExecutionClaimHttpException>(() => new TargetCreationExecutionClaimHttpClient(new Handler((_, _) => throw new Exception()), new() { BaseUri = new Uri(uri, UriKind.RelativeOrAbsolute) }, () => Canary));
        Assert.Equal("InvalidTargetCreationExecutionClaimHttpOptions", e.Message); Assert.Null(e.InnerException); Assert.DoesNotContain(uri, e.ToString());
    }
    [Theory] [InlineData(0)] [InlineData(61)]
    public void Timeout_is_bounded(int timeout)
    {
        var error = Assert.Throws<TargetCreationExecutionClaimHttpException>(() => new TargetCreationExecutionClaimHttpClient(new Handler((_, _) => throw new Exception()), Options(false, timeout), () => Canary));
        Assert.Equal("InvalidTargetCreationExecutionClaimHttpOptions", error.Message); Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task Disabled_precedes_bad_pins_provider_precancel_and_disposal()
    {
        var i = Intent(); var b = Binding(i); var calls = 0; using var ct = new CancellationTokenSource(); ct.Cancel();
        var client = Client((_, _) => { calls++; throw new Exception(Canary); }, () => { calls++; throw new Exception(Canary); }, enabled: false);
        client.Dispose(); await Failure(() => client.ClaimAsync(i with { OperationId = Guid.Empty }, b with { CredentialId = null }, ct.Token)); Assert.Equal(0, calls);
    }
    [Fact]
    public async Task Invalid_expected_pins_never_send()
    {
        var i = Intent(); var b = Binding(i); var sends = 0; var providers = 0;
        using var client = Client((_, _) => { sends++; throw new Exception(Canary); }, () => { providers++; return "token"; });
        foreach (var bad in new[] { b with { PermitId = Guid.Empty }, b with { OperationId = Guid.NewGuid() }, b with { AttemptId = Guid.Empty }, b with { AgentId = Guid.Empty }, b with { CredentialId = null }, b with { EndpointMappingId = Guid.Empty }, b with { IssuedAt = b.IssuedAt.ToOffset(TimeSpan.FromHours(1)) } })
            await Failure(() => client.ClaimAsync(i, bad, default));
        await Failure(() => client.ClaimAsync(i with { FencingToken = 0 }, b, default)); Assert.Equal(0, sends); Assert.Equal(0, providers);
    }
    [Fact]
    public async Task Precancel_does_not_send_and_disposed_client_is_sanitized()
    {
        var i = Intent(); var b = Binding(i); var sends = 0; using var ct = new CancellationTokenSource(); ct.Cancel();
        var client = Client((_, _) => { sends++; throw new Exception(Canary); });
        var e = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ClaimAsync(i, b, ct.Token)); Assert.Equal(ct.Token, e.CancellationToken); Assert.Null(e.InnerException);
        client.Dispose(); await Failure(() => client.ClaimAsync(i, b, default)); Assert.Equal(0, sends);
    }

    [Theory]
    [InlineData(201)] [InlineData(204)] [InlineData(302)] [InlineData(307)] [InlineData(401)] [InlineData(409)] [InlineData(500)]
    public async Task Only_200_and_no_error_body_read_or_retry(int status)
    {
        var i = Intent(); var b = Binding(i); var calls = 0; var content = new SpyContent([], true);
        using var client = Client(r => { calls++; var response = Reply(r, "", (HttpStatusCode)status); response.Content = content; response.Headers.Location = new Uri("https://evil.example/"); return response; });
        await Failure(() => client.ClaimAsync(i, b, default)); Assert.Equal(1, calls); Assert.Equal(0, content.Reads); Assert.True(content.Disposed);
    }
    [Fact]
    public async Task All_thirteen_fields_required_unique_case_sensitive_and_nonnull()
    {
        var i = Intent(); var b = Binding(i); var json = Json(i, b);
        foreach (var p in JsonNode.Parse(json)!.AsObject())
        {
            var missing = JsonNode.Parse(json)!.AsObject(); missing.Remove(p.Key);
            var casing = JsonNode.Parse(json)!.AsObject(); casing.Remove(p.Key); casing[char.ToUpperInvariant(p.Key[0]) + p.Key[1..]] = p.Value!.DeepClone();
            foreach (var body in new[] { missing.ToJsonString(), casing.ToJsonString(), json[..^1] + ",\"" + p.Key + "\":" + p.Value!.ToJsonString() + "}", Change(json, p.Key, "null") })
            { using var client = Client(r => Reply(r, body)); await Failure(() => client.ClaimAsync(i, b, default)); }
        }
    }
    [Fact]
    public async Task Every_guid_pin_is_canonical_nonempty_and_matches_expected()
    {
        var i = Intent(); var b = Binding(i); var json = Json(i, b);
        foreach (var field in new[] { "claimId", "permitId", "operationId", "attemptId", "agentId", "credentialId", "endpointMappingId" })
        {
            var value = JsonNode.Parse(json)![field]!.GetValue<string>();
            var wrong = new[] { Guid.Empty.ToString(), value.Replace("-", ""), " " + value }.ToList(); if (field != "claimId") wrong.Add(Guid.NewGuid().ToString());
            foreach (var v in wrong) { using var client = Client(r => Reply(r, Change(json, field, JsonSerializer.Serialize(v)))); await Failure(() => client.ClaimAsync(i, b, default)); }
        }
    }
    [Fact]
    public async Task Safety_flags_and_closed_shape_are_strict()
    {
        var i = Intent(); var b = Binding(i); var json = Json(i, b);
        foreach (var field in new[] { "newlyIssued", "executable", "scheduledJobsMustRemainBlocked", "retryAllowed" })
            foreach (var value in new[] { field == "retryAllowed" ? "true" : "false", "1", "\"true\"" })
            { using var client = Client(r => Reply(r, Change(json, field, value))); await Failure(() => client.ClaimAsync(i, b, default)); }
        foreach (var body in new[] { "", "null", "[]", "{}", "42", "\"text\"", json + " {}", Change(json, "password", "\"secret-canary\"") })
        { using var client = Client(r => Reply(r, body)); await Failure(() => client.ClaimAsync(i, b, default)); }
    }
    [Fact]
    public async Task Utc_window_is_binding_relative_not_local_clock_and_accepts_trailing_Z()
    {
        var i = Intent(); var b = Binding(i); var json = Json(i, b);
        foreach (var field in new[] { "issuedAt", "expiresAt" })
            foreach (var wrong in new[] { "123", "\"bad\"", JsonSerializer.Serialize(b.IssuedAt.ToOffset(TimeSpan.FromHours(1))), JsonSerializer.Serialize(b.IssuedAt.AddSeconds(-1)), JsonSerializer.Serialize(b.StartBefore.AddSeconds(1)), JsonSerializer.Serialize(field == "issuedAt" ? b.StartBefore : b.IssuedAt) })
            { using var client = Client(r => Reply(r, Change(json, field, wrong))); await Failure(() => client.ClaimAsync(i, b, default)); }
        using var valid = Client(r => Reply(r, json.Replace("+00:00", "Z", StringComparison.Ordinal))); await valid.ClaimAsync(i, b, default);
        using var boundary = Client(r => Reply(r, Change(json, "issuedAt", JsonSerializer.Serialize(b.IssuedAt)))); await boundary.ClaimAsync(i, b, default);
    }
    [Theory]
    [InlineData("application/json")] [InlineData("application/json; charset=utf-8")] [InlineData("application/json; charset=\"UTF-8\"")]
    public async Task Utf8_json_media_types_accepted(string type)
    { var i = Intent(); var b = Binding(i); using var client = Client(r => { var response = Reply(r, Json(i, b)); response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(type); return response; }); await client.ClaimAsync(i, b, default); }
    [Fact]
    public async Task Unsafe_metadata_is_rejected_before_body_read()
    {
        var i = Intent(); var b = Binding(i);
        foreach (var bad in new[] { "cache", "encoding", "type", "charset", "destination" })
        {
            var content = new SpyContent([], true); content.Headers.ContentType = new("application/json");
            using var client = Client(r => { var response = Reply(r, ""); response.Content = content;
                if (bad == "cache") response.Headers.CacheControl = null; if (bad == "encoding") content.Headers.ContentEncoding.Add("gzip");
                if (bad == "type") content.Headers.ContentType = new("text/plain"); if (bad == "charset") content.Headers.ContentType.CharSet = "utf-16";
                if (bad == "destination") response.RequestMessage = new(HttpMethod.Post, "https://evil.example/"); return response; });
            await Failure(() => client.ClaimAsync(i, b, default)); Assert.Equal(0, content.Reads); Assert.True(content.Disposed);
        }
    }
    [Fact]
    public async Task Malformed_utf8_and_actual_unknown_length_overflow_are_rejected()
    {
        var i = Intent(); var b = Binding(i); var json = Json(i, b);
        foreach (var bytes in new[] { new byte[] { 0x7b, 0xff, 0x7d }, Encoding.UTF8.GetBytes(json.Replace("claimId", "\\ud800", StringComparison.Ordinal)), Encoding.UTF8.GetBytes(json + new string(' ', 32769 - Encoding.UTF8.GetByteCount(json))) })
        {
            var content = new SpyContent(bytes); content.Headers.ContentType = new("application/json");
            using var client = Client(r => { var response = Reply(r, ""); response.Content = content; return response; });
            await Failure(() => client.ClaimAsync(i, b, default)); Assert.True(content.Disposed);
        }
    }
    [Fact]
    public async Task Exact_32KiB_body_is_accepted()
    {
        var i = Intent(); var b = Binding(i); var json = Json(i, b);
        var content = new SpyContent(Encoding.UTF8.GetBytes(json + new string(' ', 32768 - Encoding.UTF8.GetByteCount(json)))); content.Headers.ContentType = new("application/json");
        using var client = Client(r => { var response = Reply(r, ""); response.Content = content; return response; }); await client.ClaimAsync(i, b, default);
    }
    [Theory] [InlineData("")] [InlineData("Bearer token")] [InlineData("white space")] [InlineData("token\r\nInjected: value")]
    public async Task Bad_bearer_never_sent(string token)
    { var i = Intent(); var b = Binding(i); var sends = 0; using var client = Client((_, _) => { sends++; throw new Exception(Canary); }, () => token); await Failure(() => client.ClaimAsync(i, b, default)); Assert.Equal(0, sends); }
    [Fact]
    public async Task Provider_and_send_errors_are_sanitized()
    {
        var i = Intent(); var b = Binding(i); using var provider = Client((_, _) => throw new Exception(), () => throw new Exception(Canary)); await Failure(() => provider.ClaimAsync(i, b, default));
        using var send = Client((_, _) => throw new HttpRequestException(Canary)); await Failure(() => send.ClaimAsync(i, b, default));
    }
    [Fact]
    public async Task Timeout_covers_send_even_when_handler_ignores_cancellation()
    {
        var i = Intent(); var b = Binding(i); var blocked = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = Client((_, _) => blocked.Task, timeout: 1);
        try { await Failure(() => client.ClaimAsync(i, b, default).WaitAsync(TimeSpan.FromSeconds(30))); }
        finally { blocked.TrySetCanceled(); }
    }
    [Fact]
    public async Task Timeout_covers_body_read_even_when_stream_ignores_cancellation()
    {
        var i = Intent(); var b = Binding(i); var stream = new BlockingStream(); var content = new StreamContent(stream); content.Headers.ContentType = new("application/json");
        using var client = Client((r, _) => { var response = Reply(r, ""); response.Content = content; return Task.FromResult(response); }, timeout: 1);
        try { await Failure(() => client.ClaimAsync(i, b, default).WaitAsync(TimeSpan.FromSeconds(30))); Assert.True(stream.Disposed); }
        finally { stream.Blocked.TrySetResult(0); }
    }
    [Fact]
    public async Task Timeout_covers_stream_open_ignoring_cancellation()
    {
        var i = Intent(); var b = Binding(i); var content = new BlockingOpenContent(); content.Headers.ContentType = new("application/json");
        using var client = Client((r, _) => { var response = Reply(r, ""); response.Content = content; return Task.FromResult(response); }, timeout: 1);
        try { await Failure(() => client.ClaimAsync(i, b, default).WaitAsync(TimeSpan.FromSeconds(30))); Assert.True(content.Disposed); }
        finally { content.Blocked.TrySetResult(new MemoryStream()); }
    }
    private sealed class BlockingOpenContent : HttpContent
    {
        public bool Disposed; public readonly TaskCompletionSource<Stream> Blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override Task<Stream> CreateContentReadStreamAsync() => Blocked.Task;
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class SpyContent(byte[] bytes, bool throwOnRead = false) : HttpContent
    {
        public int Reads; public bool Disposed;
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) { Reads++; if (throwOnRead) throw new Exception(Canary); return stream.WriteAsync(bytes).AsTask(); }
        protected override Task<Stream> CreateContentReadStreamAsync() { Reads++; if (throwOnRead) throw new Exception(Canary); return Task.FromResult<Stream>(new MemoryStream(bytes)); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class BlockingStream : Stream
    {
        public bool Disposed; public readonly TaskCompletionSource<int> Blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(Blocked.Task);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
