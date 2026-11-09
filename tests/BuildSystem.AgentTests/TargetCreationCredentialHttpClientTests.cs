using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BuildSystem.Agent;
using Xunit;

namespace BuildSystem.AgentTests;

// Deliberately fake-handler-only: no listeners, sockets, target processes or RAC.
public sealed class TargetCreationCredentialHttpClientTests
{
    private const string Canary = "secret-canary";
    private const string Token = "machine-token";
    private static TargetCreationCredentialRequest Request() => new(Guid.NewGuid(), 2, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static TargetCreationCredentialHttpClientOptions Options(bool enabled = true, int timeout = 30) =>
        new() { BaseUri = new Uri("https://credentials.example/"), Enabled = enabled, TimeoutSeconds = timeout };
    private static TargetCreationCredentialHttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        Func<string>? provider = null, bool enabled = true, int timeout = 30) =>
        new(new Handler(send), Options(enabled, timeout), provider ?? (() => Token));
    private static TargetCreationCredentialHttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> send) =>
        Client((request, _) => Task.FromResult(send(request)));
    private static string Json(TargetCreationCredentialRequest request) => JsonSerializer.Serialize(new
    {
        permitId = request.PermitId, expectedPermitRevision = request.ExpectedPermitRevision,
        attemptId = request.AttemptId, credentialId = request.CredentialId, endpointMappingId = request.EndpointMappingId,
        clusterUser = Canary + "-cluster-user", clusterPassword = Canary + "-cluster-password",
        sqlUser = Canary + "-sql-user", sqlPassword = Canary + "-sql-password",
        executable = false, scheduledJobsMustRemainBlocked = true, retryAllowed = false
    });
    private static HttpResponseMessage Reply(HttpRequestMessage request, string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { RequestMessage = request, Content = new StringContent(json, Encoding.UTF8, "application/json") };
        response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        return response;
    }
    private static string Change(string json, string property, string value)
    {
        var root = JsonNode.Parse(json)!.AsObject(); root[property] = JsonNode.Parse(value); return root.ToJsonString();
    }
    private static async Task Failure(Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<TargetCreationCredentialTransportException>(action);
        Assert.Equal("TargetCreationCredentialTransportUnavailable", ex.Message);
        Assert.Null(ex.InnerException);
        Assert.DoesNotContain(Canary, ex.ToString()); Assert.DoesNotContain(Token, ex.ToString());
        Assert.DoesNotContain("https://", ex.ToString());
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    [Fact]
    public async Task Resolve_posts_exact_five_pins_and_refreshes_bearer_once_per_explicit_call()
    {
        var input = Request(); var providerCalls = 0; var calls = 0;
        using var client = Client(async (request, ct) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://credentials.example/api/agents/target-creation-credentials/resolve", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("rotation-" + calls, request.Headers.Authorization.Parameter);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            using var json = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
            Assert.Equal(new[] { "permitId", "expectedPermitRevision", "attemptId", "credentialId", "endpointMappingId" }, json.RootElement.EnumerateObject().Select(p => p.Name));
            Assert.Equal(input.PermitId, json.RootElement.GetProperty("permitId").GetGuid());
            Assert.Equal(2, json.RootElement.GetProperty("expectedPermitRevision").GetInt64());
            Assert.Equal(input.AttemptId, json.RootElement.GetProperty("attemptId").GetGuid());
            Assert.Equal(input.CredentialId, json.RootElement.GetProperty("credentialId").GetGuid());
            Assert.Equal(input.EndpointMappingId, json.RootElement.GetProperty("endpointMappingId").GetGuid());
            return Reply(request, Json(input));
        }, () => "rotation-" + ++providerCalls);
        using var first = await client.ResolveAsync(input);
        using var second = await client.ResolveAsync(input);
        Assert.Equal(2, calls); Assert.Equal(2, providerCalls);
        var values = first.ReadSecrets();
        Assert.Equal(Canary + "-cluster-user", values.ClusterUser);
        Assert.Equal(Canary + "-cluster-password", values.ClusterPassword);
        Assert.Equal(Canary + "-sql-user", values.SqlUser);
        Assert.Equal(Canary + "-sql-password", values.SqlPassword);
        Assert.False(first.Executable); Assert.True(first.ScheduledJobsMustRemainBlocked); Assert.False(first.RetryAllowed);
    }

    [Fact]
    public async Task Default_off_is_before_provider_and_network_even_when_precancelled()
    {
        var calls = 0;
        using var client = new TargetCreationCredentialHttpClient(new Handler((_, _) => { calls++; throw new Exception(Canary); }),
            new() { BaseUri = new Uri("https://credentials.example/") }, () => { calls++; throw new Exception(Canary); });
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Failure(() => client.ResolveAsync(Request(), cancelled.Token)); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Precancel_is_before_provider_and_network_and_keeps_only_caller_token()
    {
        var calls = 0;
        using var client = Client((_, _) => { calls++; throw new Exception(Canary); }, () => { calls++; return Token; });
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ResolveAsync(Request(), cancelled.Token));
        Assert.Equal(cancelled.Token, ex.CancellationToken); Assert.Null(ex.InnerException); Assert.DoesNotContain(Canary, ex.ToString()); Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("http://credentials.example/")]
    [InlineData("https://credentials.example/path")]
    [InlineData("https://credentials.example/?secret-canary")]
    [InlineData("https://credentials.example/#secret-canary")]
    [InlineData("https://user:secret-canary@credentials.example/")]
    [InlineData("/relative")]
    public void Invalid_base_is_rejected_even_disabled_without_echo(string uri)
    {
        var ex = Assert.Throws<TargetCreationCredentialTransportException>(() => new TargetCreationCredentialHttpClient(
            new Handler((_, _) => throw new Exception()), new() { BaseUri = new Uri(uri, UriKind.RelativeOrAbsolute) }, () => Token));
        Assert.Equal("TargetCreationCredentialTransportUnavailable", ex.Message); Assert.Null(ex.InnerException); Assert.DoesNotContain(uri, ex.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public void Invalid_timeout_is_rejected_even_disabled(int timeout) =>
        Assert.Throws<TargetCreationCredentialTransportException>(() => new TargetCreationCredentialHttpClient(new Handler((_, _) => throw new Exception()), Options(false, timeout), () => Token));

    [Theory]
    [InlineData(201)] [InlineData(204)] [InlineData(301)] [InlineData(302)] [InlineData(307)] [InlineData(308)] [InlineData(401)] [InlineData(500)]
    public async Task Non200_is_not_retried_or_followed_and_error_body_is_never_read(int status)
    {
        var calls = 0; var content = new SpyContent(Encoding.UTF8.GetBytes(Canary), throwOnRead: true);
        using var client = Client(request => { calls++; var response = Reply(request, "", (HttpStatusCode)status); response.Content = content; response.Headers.Location = new Uri("https://untrusted.example/"); return response; });
        await Failure(() => client.ResolveAsync(Request())); Assert.Equal(1, calls); Assert.Equal(0, content.Reads); Assert.True(content.Disposed);
    }

    [Fact]
    public async Task Holder_has_no_ambient_plaintext_properties_and_denies_json_and_post_dispose_reads()
    {
        var input = Request(); using var client = Client(request => Reply(request, Json(input)));
        var holder = await client.ResolveAsync(input);
        Assert.Equal(new[] { "Executable", "RetryAllowed", "ScheduledJobsMustRemainBlocked" },
            typeof(TargetCreationDeliveredCredentials).GetProperties().Select(p => p.Name).OrderBy(n => n));
        Assert.DoesNotContain(Canary, holder.ToString()); Assert.DoesNotContain(Canary, holder.ReadSecrets().ToString());
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(holder));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TargetCreationDeliveredCredentials>("{}"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TargetCreationDeliveredCredentials>("null"));
        holder.Dispose(); holder.Dispose();
        var ex = Assert.Throws<ObjectDisposedException>(() => holder.ReadSecrets()); Assert.DoesNotContain(Canary, ex.ToString());
        Assert.False(holder.Executable); Assert.True(holder.ScheduledJobsMustRemainBlocked); Assert.False(holder.RetryAllowed);
    }

    [Theory]
    [InlineData("permitId")] [InlineData("expectedPermitRevision")] [InlineData("attemptId")]
    [InlineData("credentialId")] [InlineData("endpointMappingId")] [InlineData("clusterUser")]
    [InlineData("clusterPassword")] [InlineData("sqlUser")] [InlineData("sqlPassword")]
    [InlineData("executable")] [InlineData("scheduledJobsMustRemainBlocked")] [InlineData("retryAllowed")]
    public async Task Every_response_field_is_required_and_duplicate_or_wrong_case_is_rejected(string name)
    {
        var input = Request(); var valid = Json(input); var root = JsonNode.Parse(valid)!.AsObject(); root.Remove(name);
        foreach (var body in new[] { root.ToJsonString(), valid[..^1] + ",\"" + name + "\":" + JsonNode.Parse(valid)![name]!.ToJsonString() + "}",
            valid.Replace("\"" + name + "\":", "\"" + char.ToUpperInvariant(name[0]) + name[1..] + "\":", StringComparison.Ordinal) })
        {
            using var client = Client(request => Reply(request, body)); await Failure(() => client.ResolveAsync(input));
        }
    }

    [Theory]
    [InlineData("permitId")] [InlineData("attemptId")] [InlineData("credentialId")] [InlineData("endpointMappingId")]
    public async Task Every_guid_pin_is_correlated_strict_and_nonempty(string name)
    {
        var input = Request(); var valid = Json(input); var value = JsonNode.Parse(valid)![name]!.GetValue<string>();
        foreach (var wrong in new[] { Guid.NewGuid().ToString("D"), Guid.Empty.ToString("D"), " " + value, value.Replace("-", ""), value + " " })
        {
            using var client = Client(request => Reply(request, Change(valid, name, JsonSerializer.Serialize(wrong))));
            await Failure(() => client.ResolveAsync(input));
        }
    }

    [Theory]
    [InlineData("expectedPermitRevision", "1")] [InlineData("expectedPermitRevision", "3")]
    [InlineData("expectedPermitRevision", "2.0")] [InlineData("expectedPermitRevision", "\"2\"")]
    [InlineData("expectedPermitRevision", "null")] [InlineData("executable", "true")]
    [InlineData("scheduledJobsMustRemainBlocked", "false")] [InlineData("retryAllowed", "true")]
    [InlineData("executable", "null")] [InlineData("unknown", "123")]
    public async Task Revision_and_physical_safety_flags_are_not_coerced(string name, string value)
    {
        var input = Request(); using var client = Client(request => Reply(request, Change(Json(input), name, value)));
        await Failure(() => client.ResolveAsync(input));
    }

    [Theory]
    [InlineData("clusterUser")] [InlineData("clusterPassword")] [InlineData("sqlUser")] [InlineData("sqlPassword")]
    public async Task Each_secret_is_nonempty_bounded_control_free_and_valid_unicode(string name)
    {
        var input = Request();
        foreach (var value in new[] { "\"\"", "null", "123", "\"\\u0000\"", "\"\\n\"", "\"\\u007f\"", "\"\\ud800\"", "\"\\udc00\"", JsonSerializer.Serialize(new string('x', 4097)) })
        {
            using var client = Client(request => Reply(request, Change(Json(input), name, value)));
            await Failure(() => client.ResolveAsync(input));
        }
    }

    [Theory]
    [InlineData("ascii")] [InlineData("unicode")] [InlineData("emoji")]
    public async Task Secrets_accept_4096_characters_including_valid_nonascii_and_surrogate_pairs(string kind)
    {
        var value = kind == "ascii" ? new string('x', 4096) : kind == "unicode" ? new string('界', 4096) : string.Concat(Enumerable.Repeat("😀", 2048));
        var input = Request(); using var client = Client(request => Reply(request, Change(Json(input), "clusterPassword", JsonSerializer.Serialize(value))));
        using var result = await client.ResolveAsync(input); Assert.Equal(value, result.ReadSecrets().ClusterPassword);
    }

    [Theory]
    [InlineData("")] [InlineData("null")] [InlineData("[]")] [InlineData("{}")] [InlineData("not-json")]
    [InlineData("{\"secret-canary\":1}")]
    public async Task Malformed_json_is_sanitized(string body)
    {
        using var client = Client(request => Reply(request, body)); await Failure(() => client.ResolveAsync(Request()));
    }

    [Fact]
    public async Task Invalid_utf8_and_unpaired_raw_escape_are_rejected()
    {
        var input = Request();
        foreach (var bytes in new[] { new byte[] { 0x7b, 0x22, 0xff, 0x22, 0x3a, 0x31, 0x7d },
            Encoding.UTF8.GetBytes(Json(input).Replace(Canary + "-cluster-user", "\\ud800", StringComparison.Ordinal)) })
        {
            using var client = Client(request => { var response = Reply(request, ""); response.Content = new ByteArrayContent(bytes); response.Content.Headers.ContentType = new("application/json"); return response; });
            await Failure(() => client.ResolveAsync(input));
        }
    }

    [Theory]
    [InlineData("application/json")] [InlineData("application/json; charset=utf-8")]
    [InlineData("application/json; charset=\"UTF-8\"")]
    public async Task Json_utf8_or_no_charset_is_accepted_without_browser_nosniff_header(string mediaType)
    {
        var input = Request(); using var client = Client(request => { var response = Reply(request, Json(input)); response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType); return response; });
        using var holder = await client.ResolveAsync(input); Assert.NotNull(holder.ReadSecrets());
    }

    [Theory]
    [InlineData("text/plain")] [InlineData("application/problem+json")] [InlineData("application/json; charset=utf-16")]
    public async Task Non_json_or_non_utf8_is_rejected_before_body_read(string mediaType)
    {
        var content = new SpyContent([], true); content.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        using var client = Client(request => { var response = Reply(request, ""); response.Content = content; return response; });
        await Failure(() => client.ResolveAsync(Request())); Assert.Equal(0, content.Reads); Assert.True(content.Disposed);
    }

    [Theory]
    [InlineData("cache")] [InlineData("encoding")] [InlineData("destination")]
    public async Task Unsafe_response_metadata_is_rejected_before_body_read(string bad)
    {
        var content = new SpyContent([], true); content.Headers.ContentType = new("application/json");
        using var client = Client(request =>
        {
            var response = Reply(request, ""); response.Content = content;
            if (bad == "cache") response.Headers.CacheControl = null;
            if (bad == "encoding") response.Content.Headers.ContentEncoding.Add("gzip");
            if (bad == "destination") response.RequestMessage = new HttpRequestMessage(HttpMethod.Post, "https://untrusted.example/");
            return response;
        });
        await Failure(() => client.ResolveAsync(Request())); Assert.Equal(0, content.Reads); Assert.True(content.Disposed);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Body_over_128KiB_is_rejected_for_known_and_unknown_length(bool unknownLength)
    {
        var content = new SpyContent(Enumerable.Repeat((byte)' ', 128 * 1024 + 1).ToArray(), false, unknownLength);
        content.Headers.ContentType = new("application/json");
        using var client = Client(request => { var response = Reply(request, ""); response.Content = content; return response; });
        await Failure(() => client.ResolveAsync(Request())); Assert.True(content.Disposed);
        if (!unknownLength) Assert.Equal(0, content.Reads);
    }

    [Theory]
    [InlineData("")] [InlineData("Bearer token")] [InlineData("white space")]
    [InlineData("token\r\nInjected: value")] [InlineData("nonasciié")]
    public async Task Invalid_bearer_never_reaches_handler(string token)
    {
        var calls = 0; using var client = Client((_, _) => { calls++; throw new Exception(Canary); }, () => token);
        await Failure(() => client.ResolveAsync(Request())); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Provider_and_handler_failures_have_no_remote_diagnostics()
    {
        using var providerFailure = Client((_, _) => throw new Exception("unexpected"), () => throw new Exception(Canary + " https://private.example/"));
        await Failure(() => providerFailure.ResolveAsync(Request()));
        using var sendFailure = Client((_, _) => throw new HttpRequestException(Canary + " https://private.example/"));
        await Failure(() => sendFailure.ResolveAsync(Request()));
    }

    [Fact]
    public async Task Caller_cancel_after_headers_sanitizes_cancellation_and_disposes_content()
    {
        using var cancel = new CancellationTokenSource(); var body = new BlockingStream();
        var content = new StreamContent(body); content.Headers.ContentType = new("application/json");
        using var client = Client(request => { var response = Reply(request, ""); response.Content = content; return response; });
        var pending = client.ResolveAsync(Request(), cancel.Token);
        await body.Entered.Task; cancel.Cancel();
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancel.Token, ex.CancellationToken); Assert.Null(ex.InnerException);
        Assert.DoesNotContain(Canary, ex.ToString()); Assert.True(body.Disposed);
    }

    [Fact]
    public async Task Body_timeout_is_fixed_failure_and_disposes_content()
    {
        var body = new BlockingStream(); var content = new StreamContent(body); content.Headers.ContentType = new("application/json");
        using var client = Client((request, _) => { var response = Reply(request, ""); response.Content = content; return Task.FromResult(response); }, timeout: 1);
        await Failure(() => client.ResolveAsync(Request())); Assert.True(body.Disposed);
    }

    [Fact]
    public async Task Client_disposal_during_send_sanitizes_and_disposes_late_uncooperative_response()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        HttpRequestMessage? captured = null;
        using var client = Client((request, _) => { captured = request; entered.SetResult(); return finish.Task; }, timeout: 1);
        var pending = client.ResolveAsync(Request()); await entered.Task; client.Dispose();
        await Failure(() => pending);
        var content = new SpyContent([]) { OnDispose = () => disposed.TrySetResult() };
        var response = Reply(captured!, ""); response.Content = content; finish.SetResult(response);
        await disposed.Task; Assert.True(content.Disposed);
    }

    [Fact]
    public async Task Disposed_client_rejects_before_provider_and_network()
    {
        var calls = 0; var client = Client((_, _) => { calls++; throw new Exception(Canary); }, () => { calls++; return Token; });
        client.Dispose(); await Failure(() => client.ResolveAsync(Request())); Assert.Equal(0, calls);
    }

    private sealed class BlockingStream : Stream
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            var blocked = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => blocked.TrySetCanceled(cancellationToken));
            return await blocked.Task;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class SpyContent(byte[] bytes, bool throwOnRead = false, bool unknownLength = false) : HttpContent
    {
        public int Reads; public bool Disposed; public Action? OnDispose;
        protected override bool TryComputeLength(out long length) { length = bytes.Length; return !unknownLength; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        { Reads++; if (throwOnRead) throw new InvalidOperationException(Canary); return stream.WriteAsync(bytes).AsTask(); }
        protected override Task<Stream> CreateContentReadStreamAsync()
        { Reads++; if (throwOnRead) throw new InvalidOperationException(Canary); return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)); }
        protected override void Dispose(bool disposing) { Disposed = true; OnDispose?.Invoke(); base.Dispose(disposing); }
    }
}
