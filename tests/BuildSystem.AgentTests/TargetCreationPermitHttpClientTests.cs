using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BuildSystem.Agent;
using Xunit;

namespace BuildSystem.AgentTests;

// Fake-handler-only transport tests. No workers, journal, database, listeners or target commands.
public sealed class TargetCreationPermitHttpClientTests
{
    private const string Canary = "secret-canary";
    private const string Token = "machine-token";
    private static readonly DateTimeOffset Issued = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static TargetCreationIntent Intent()
    {
        var reservation = Guid.NewGuid();
        return new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 17, reservation, Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), "bs_test_" + reservation.ToString("N"), "bs_test_" + reservation.ToString("N"));
    }
    private static TargetCreationPermitBinding Binding(TargetCreationIntent intent) =>
        new(Guid.NewGuid(), intent.OperationId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Issued, Issued.AddMinutes(5))
        { CredentialId = Guid.NewGuid(), EndpointMappingId = Guid.NewGuid() };
    private static TargetCreationPermitHttpClientOptions Options(bool enabled = true, int timeout = 30) =>
        new() { BaseUri = new Uri("https://permits.example/"), Enabled = enabled, TimeoutSeconds = timeout };
    private static TargetCreationPermitHttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        Func<string>? provider = null, bool enabled = true, int timeout = 30) =>
        new(new Handler(send), Options(enabled, timeout), provider ?? (() => Token));
    private static TargetCreationPermitHttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> send) =>
        Client((request, _) => Task.FromResult(send(request)));

    // Exact application-record wire shape (not an invented execution-grant DTO). Domain enums:
    // TargetCreationPermitState.StartRecorded = 1; TargetCreationOperationState.AttemptRecorded = 1.
    private static string Json(TargetCreationIntent i, TargetCreationPermitBinding b, bool newlyRecorded = true) => JsonSerializer.Serialize(new
    {
        permitId = b.PermitId, operationId = i.OperationId, attemptId = b.AttemptId,
        expectedOperationRevision = 1L, issuedAt = b.IssuedAt, startBefore = b.StartBefore,
        state = 1, revision = 2L, startedAt = b.IssuedAt.AddMinutes(1), startExpectedRevision = 1L,
        newlyRecorded,
        operation = new
        {
            operationId = i.OperationId, restoreIntentId = b.RestoreIntentId, environmentId = i.EnvironmentId,
            stageJobId = i.JobId, reservationId = i.ReservationId, artifactId = b.ArtifactId, agentId = b.AgentId,
            targetIncarnationId = i.TargetIncarnationId, canonicalClusterId = i.CanonicalClusterId,
            canonicalSqlServerId = i.CanonicalSqlServerId, infobaseName = i.InfobaseName, sqlDbName = i.SqlDatabaseName,
            state = 1, revision = 2L, attemptId = b.AttemptId, attemptExpectedRevision = 1L,
            quarantineReason = (int?)null, newlyRecorded = false, executable = false, scheduledJobsMustRemainBlocked = true
        },
        credentialId = b.CredentialId, endpointMappingId = b.EndpointMappingId,
        executable = false, scheduledJobsMustRemainBlocked = true
    });
    private static HttpResponseMessage Reply(HttpRequestMessage request, string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { RequestMessage = request, Content = new StringContent(json, Encoding.UTF8, "application/json") };
        response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        return response;
    }
    private static string Change(string json, string path, string value)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var nested = path.StartsWith("operation.", StringComparison.Ordinal);
        var target = nested ? root["operation"]!.AsObject() : root;
        target[nested ? path[10..] : path] = JsonNode.Parse(value);
        return root.ToJsonString();
    }
    private static async Task Failure(Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<TargetCreationPermitTransportException>(action);
        Assert.Equal("TargetCreationPermitTransportUnavailable", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Canary, error.ToString()); Assert.DoesNotContain(Token, error.ToString());
        Assert.DoesNotContain("https://", error.ToString());
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    [Fact]
    public void Options_are_default_off_with_bounded_default_timeout()
    {
        var options = new TargetCreationPermitHttpClientOptions { BaseUri = new Uri("https://permits.example/") };
        Assert.False(options.Enabled); Assert.Equal(30, options.TimeoutSeconds);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Exact_server_receipt_is_metadata_only_for_first_record_and_explicit_replay(bool newlyRecorded)
    {
        var i = Intent(); var b = Binding(i);
        using var client = Client(request => Reply(request, Json(i, b, newlyRecorded)));
        ITargetCreationPermitStartTransport transport = client;
        var receipt = await transport.RecordStartAsync(i, b);
        Assert.Equal(newlyRecorded, receipt.NewlyRecorded); Assert.Equal(2, receipt.Revision);
        Assert.Equal(b.IssuedAt.AddMinutes(1), receipt.StartedAt);
        Assert.False(receipt.Executable); Assert.False(receipt.FreshAuthority);
        Assert.True(receipt.ScheduledJobsMustRemainBlocked); Assert.False(receipt.RetryAllowed);
        Assert.DoesNotContain(b.PermitId.ToString(), receipt.ToString()); Assert.DoesNotContain(Token, receipt.ToString());
    }

    [Fact]
    public async Task Post_has_only_three_server_pins_and_gets_fresh_bearer_for_each_explicit_call()
    {
        var i = Intent(); var b = Binding(i); var sends = 0; var providers = 0;
        using var client = Client(async (request, ct) =>
        {
            sends++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://permits.example/api/agents/target-creation-permit-starts", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("rotation-" + sends, request.Headers.Authorization.Parameter);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
            Assert.Equal(new[] { "permitId", "expectedPermitRevision", "attemptId" }, body.RootElement.EnumerateObject().Select(p => p.Name));
            Assert.Equal(b.PermitId, body.RootElement.GetProperty("permitId").GetGuid());
            Assert.Equal(1, body.RootElement.GetProperty("expectedPermitRevision").GetInt64());
            Assert.Equal(b.AttemptId, body.RootElement.GetProperty("attemptId").GetGuid());
            return Reply(request, Json(i, b));
        }, () => "rotation-" + ++providers);
        await client.RecordStartAsync(i, b); await client.RecordStartAsync(i, b);
        Assert.Equal(2, sends); Assert.Equal(2, providers);
    }

    [Fact]
    public async Task Default_off_precedes_provider_network_and_precancellation()
    {
        var i = Intent(); var b = Binding(i); var calls = 0;
        using var client = new TargetCreationPermitHttpClient(new Handler((_, _) => { calls++; throw new Exception(Canary); }),
            new() { BaseUri = new Uri("https://permits.example/") }, () => { calls++; return Token; });
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Failure(() => client.RecordStartAsync(i, b, cancellation.Token)); Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("http://permits.example/")] [InlineData("https://permits.example/path")]
    [InlineData("https://permits.example/?secret-canary")] [InlineData("https://permits.example/#secret-canary")]
    [InlineData("https://user:secret-canary@permits.example/")] [InlineData("/relative")]
    public void Invalid_base_is_rejected_even_disabled_without_echo(string uri)
    {
        var error = Assert.Throws<TargetCreationPermitTransportException>(() => new TargetCreationPermitHttpClient(
            new Handler((_, _) => throw new Exception()), new() { BaseUri = new Uri(uri, UriKind.RelativeOrAbsolute) }, () => Token));
        Assert.Equal("TargetCreationPermitTransportUnavailable", error.Message); Assert.Null(error.InnerException);
        Assert.DoesNotContain(uri, error.ToString());
    }

    [Theory]
    [InlineData(0)] [InlineData(61)]
    public void Invalid_timeout_is_rejected_even_disabled(int timeout) =>
        Assert.Throws<TargetCreationPermitTransportException>(() => new TargetCreationPermitHttpClient(new Handler((_, _) => throw new Exception()), Options(false, timeout), () => Token));

    [Fact]
    public async Task Invalid_intent_is_gated_before_provider_and_send()
    {
        var i = Intent(); var b = Binding(i); var calls = 0;
        using var client = Client((_, _) => { calls++; throw new Exception(Canary); }, () => { calls++; return Token; });
        foreach (var invalid in new[] { i with { OperationId = Guid.Empty }, i with { EnvironmentId = Guid.Empty },
            i with { JobId = Guid.Empty }, i with { ReservationId = Guid.Empty }, i with { TargetIncarnationId = Guid.Empty },
            i with { CanonicalClusterId = Guid.Empty }, i with { CanonicalSqlServerId = Guid.Empty },
            i with { FencingToken = 0 }, i with { FencingToken = -1 }, i with { InfobaseName = Canary }, i with { SqlDatabaseName = Canary } })
            await Failure(() => client.RecordStartAsync(invalid, b));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Unbound_partial_empty_or_conflicting_binding_is_gated_before_provider_and_send()
    {
        var i = Intent(); var b = Binding(i); var calls = 0;
        using var client = Client((_, _) => { calls++; throw new Exception(Canary); }, () => { calls++; return Token; });
        foreach (var invalid in new[] { b with { CredentialId = null, EndpointMappingId = null }, b with { CredentialId = null },
            b with { EndpointMappingId = null }, b with { CredentialId = Guid.Empty }, b with { EndpointMappingId = Guid.Empty },
            b with { PermitId = Guid.Empty }, b with { AttemptId = Guid.Empty }, b with { OperationId = Guid.NewGuid() },
            b with { RestoreIntentId = Guid.Empty }, b with { ArtifactId = Guid.Empty }, b with { AgentId = Guid.Empty },
            b with { IssuedAt = b.IssuedAt.ToOffset(TimeSpan.FromHours(1)) }, b with { StartBefore = b.StartBefore.AddSeconds(1) } })
            await Failure(() => client.RecordStartAsync(i, invalid));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(201)] [InlineData(204)] [InlineData(301)] [InlineData(302)] [InlineData(307)] [InlineData(308)] [InlineData(401)] [InlineData(500)]
    public async Task Only_200_accepted_no_retry_or_redirect_and_error_body_never_read(int status)
    {
        var i = Intent(); var b = Binding(i); var calls = 0; var content = new SpyContent(Encoding.UTF8.GetBytes(Canary), true);
        using var client = Client(request => { calls++; var response = Reply(request, "", (HttpStatusCode)status); response.Content = content;
            response.Headers.Location = new Uri("https://untrusted.example/"); return response; });
        await Failure(() => client.RecordStartAsync(i, b));
        Assert.Equal(1, calls); Assert.Equal(0, content.Reads); Assert.True(content.Disposed);
    }

    [Theory]
    [InlineData("permitId")] [InlineData("operationId")] [InlineData("attemptId")] [InlineData("credentialId")] [InlineData("endpointMappingId")]
    [InlineData("operation.operationId")] [InlineData("operation.restoreIntentId")] [InlineData("operation.environmentId")]
    [InlineData("operation.stageJobId")] [InlineData("operation.reservationId")] [InlineData("operation.artifactId")]
    [InlineData("operation.agentId")] [InlineData("operation.targetIncarnationId")]
    [InlineData("operation.canonicalClusterId")] [InlineData("operation.canonicalSqlServerId")] [InlineData("operation.attemptId")]
    public async Task Every_identity_pin_is_correlated_nonempty_and_canonical(string path)
    {
        var i = Intent(); var b = Binding(i); var valid = Json(i, b);
        var node = JsonNode.Parse(valid)!; var value = (path.StartsWith("operation.", StringComparison.Ordinal) ? node["operation"]![path[10..]] : node[path])!.GetValue<string>();
        foreach (var wrong in new[] { Guid.NewGuid().ToString("D"), Guid.Empty.ToString("D"), value.Replace("-", ""), " " + value })
        {
            using var client = Client(request => Reply(request, Change(valid, path, JsonSerializer.Serialize(wrong))));
            await Failure(() => client.RecordStartAsync(i, b));
        }
    }

    [Theory]
    [InlineData("expectedOperationRevision", "2")] [InlineData("expectedOperationRevision", "1.0")]
    [InlineData("revision", "1")] [InlineData("revision", "3")] [InlineData("revision", "\"2\"")]
    [InlineData("startExpectedRevision", "2")] [InlineData("startExpectedRevision", "null")]
    [InlineData("state", "0")] [InlineData("state", "2")] [InlineData("state", "\"StartRecorded\"")]
    [InlineData("executable", "true")] [InlineData("scheduledJobsMustRemainBlocked", "false")]
    [InlineData("operation.state", "0")] [InlineData("operation.state", "2")]
    [InlineData("operation.revision", "1")] [InlineData("operation.revision", "3")]
    [InlineData("operation.attemptExpectedRevision", "2")] [InlineData("operation.quarantineReason", "0")]
    [InlineData("operation.newlyRecorded", "true")] [InlineData("operation.executable", "true")]
    [InlineData("operation.scheduledJobsMustRemainBlocked", "false")]
    [InlineData("operation.infobaseName", "\"secret-canary\"")] [InlineData("operation.sqlDbName", "\"secret-canary\"")]
    [InlineData("clusterPassword", "\"secret-canary\"")] [InlineData("operation.sqlPassword", "\"secret-canary\"")]
    public async Task Revisions_states_names_safety_and_secret_unknown_fields_are_strict(string path, string value)
    {
        var i = Intent(); var b = Binding(i);
        using var client = Client(request => Reply(request, Change(Json(i, b), path, value)));
        await Failure(() => client.RecordStartAsync(i, b));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Every_outer_and_nested_field_required_unique_and_case_sensitive(bool nested)
    {
        var i = Intent(); var b = Binding(i); var valid = Json(i, b);
        var properties = (nested ? JsonNode.Parse(valid)!["operation"]!.AsObject() : JsonNode.Parse(valid)!.AsObject()).ToArray();
        foreach (var property in properties)
        {
            var missing = JsonNode.Parse(valid)!.AsObject();
            (nested ? missing["operation"]!.AsObject() : missing).Remove(property.Key);
            var wrongCase = JsonNode.Parse(valid)!.AsObject(); var target = nested ? wrongCase["operation"]!.AsObject() : wrongCase;
            target.Remove(property.Key); target[char.ToUpperInvariant(property.Key[0]) + property.Key[1..]] = property.Value?.DeepClone();
            var duplicateRoot = JsonNode.Parse(valid)!.AsObject();
            var duplicated = (nested ? duplicateRoot["operation"]!.ToJsonString() : valid);
            duplicated = duplicated[..^1] + ",\"" + property.Key + "\":" + (property.Value?.ToJsonString() ?? "null") + "}";
            var duplicate = nested ? valid.Replace(duplicateRoot["operation"]!.ToJsonString(), duplicated, StringComparison.Ordinal) : duplicated;
            foreach (var body in new[] { missing.ToJsonString(), wrongCase.ToJsonString(), duplicate })
            {
                using var client = Client(request => Reply(request, body)); await Failure(() => client.RecordStartAsync(i, b));
            }
        }
    }

    [Theory]
    [InlineData("issuedAt")] [InlineData("startBefore")] [InlineData("startedAt")]
    public async Task Datetimes_are_utc_exact_and_start_is_within_bound_window(string field)
    {
        var i = Intent(); var b = Binding(i);
        foreach (var wrong in new[] { "null", "123", "\"not-a-date\"", JsonSerializer.Serialize(b.IssuedAt.ToOffset(TimeSpan.FromHours(1))),
            JsonSerializer.Serialize(b.IssuedAt.AddSeconds(-1)), JsonSerializer.Serialize(b.StartBefore.AddSeconds(1)) })
        {
            using var client = Client(request => Reply(request, Change(Json(i, b), field, wrong)));
            await Failure(() => client.RecordStartAsync(i, b));
        }
    }

    [Fact]
    public async Task Revoked_historical_200_is_rejected_even_with_previous_start_metadata()
    {
        var i = Intent(); var b = Binding(i);
        var revoked = Change(Change(Json(i, b, false), "state", "2"), "revision", "3");
        using var client = Client(request => Reply(request, revoked)); await Failure(() => client.RecordStartAsync(i, b));
    }

    [Theory]
    [InlineData("")] [InlineData("null")] [InlineData("[]")] [InlineData("{}")]
    [InlineData("not-json")] [InlineData("{\"secret-canary\":1}")]
    public async Task Malformed_json_is_sanitized(string body)
    {
        var i = Intent(); var b = Binding(i); using var client = Client(request => Reply(request, body));
        await Failure(() => client.RecordStartAsync(i, b));
    }

    [Fact]
    public async Task Invalid_utf8_or_unpaired_unicode_escape_is_rejected()
    {
        var i = Intent(); var b = Binding(i);
        foreach (var bytes in new[] { new byte[] { 0x7b, 0x22, 0xff, 0x22, 0x3a, 0x31, 0x7d },
            Encoding.UTF8.GetBytes(Json(i, b).Replace(i.InfobaseName, "\\ud800", StringComparison.Ordinal)) })
        {
            using var client = Client(request => { var response = Reply(request, ""); response.Content = new ByteArrayContent(bytes);
                response.Content.Headers.ContentType = new("application/json"); return response; });
            await Failure(() => client.RecordStartAsync(i, b));
        }
    }

    [Theory]
    [InlineData("application/json")] [InlineData("application/json; charset=utf-8")] [InlineData("application/json; charset=\"UTF-8\"")]
    public async Task Json_utf8_or_absent_charset_accepted(string type)
    {
        var i = Intent(); var b = Binding(i); using var client = Client(request => { var response = Reply(request, Json(i, b));
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(type); return response; });
        Assert.False((await client.RecordStartAsync(i, b)).Executable);
    }

    [Theory]
    [InlineData("text/plain")] [InlineData("application/problem+json")] [InlineData("application/json; charset=utf-16")]
    public async Task Unsafe_content_type_rejected_before_read(string type)
    {
        var i = Intent(); var b = Binding(i); var content = new SpyContent([], true); content.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
        using var client = Client(request => { var response = Reply(request, ""); response.Content = content; return response; });
        await Failure(() => client.RecordStartAsync(i, b)); Assert.Equal(0, content.Reads); Assert.True(content.Disposed);
    }

    [Theory]
    [InlineData("cache")] [InlineData("encoding")] [InlineData("destination")]
    public async Task Unsafe_response_metadata_rejected_before_read(string bad)
    {
        var i = Intent(); var b = Binding(i); var content = new SpyContent([], true); content.Headers.ContentType = new("application/json");
        using var client = Client(request =>
        {
            var response = Reply(request, ""); response.Content = content;
            if (bad == "cache") response.Headers.CacheControl = null;
            if (bad == "encoding") content.Headers.ContentEncoding.Add("gzip");
            if (bad == "destination") response.RequestMessage = new HttpRequestMessage(HttpMethod.Post, "https://untrusted.example/");
            return response;
        });
        await Failure(() => client.RecordStartAsync(i, b)); Assert.Equal(0, content.Reads); Assert.True(content.Disposed);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Actual_body_limit_is_32KiB_including_unknown_length(bool unknownLength)
    {
        var i = Intent(); var b = Binding(i);
        var content = new SpyContent(Enumerable.Repeat((byte)' ', 32 * 1024 + 1).ToArray(), false, unknownLength);
        content.Headers.ContentType = new("application/json");
        using var client = Client(request => { var response = Reply(request, ""); response.Content = content; return response; });
        await Failure(() => client.RecordStartAsync(i, b)); Assert.True(content.Disposed);
        if (!unknownLength) Assert.Equal(0, content.Reads);
    }

    [Theory]
    [InlineData("")] [InlineData("Bearer token")] [InlineData("white space")] [InlineData("token\r\nInjected: value")] [InlineData("nonasciié")]
    public async Task Invalid_bearer_never_reaches_handler(string token)
    {
        var i = Intent(); var b = Binding(i); var calls = 0;
        using var client = Client((_, _) => { calls++; throw new Exception(Canary); }, () => token);
        await Failure(() => client.RecordStartAsync(i, b)); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Provider_and_handler_failures_sanitized_without_retry()
    {
        var i = Intent(); var b = Binding(i); var calls = 0;
        using var provider = Client((_, _) => throw new Exception(), () => throw new Exception(Canary));
        await Failure(() => provider.RecordStartAsync(i, b));
        using var handler = Client((_, _) => { calls++; throw new HttpRequestException(Canary + " https://private.example/"); });
        await Failure(() => handler.RecordStartAsync(i, b)); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Precancel_precedes_provider_and_preserves_only_caller_token()
    {
        var i = Intent(); var b = Binding(i); var calls = 0; using var cancel = new CancellationTokenSource(); cancel.Cancel();
        using var client = Client((_, _) => { calls++; throw new Exception(Canary); }, () => { calls++; return Token; });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RecordStartAsync(i, b, cancel.Token));
        Assert.Equal(cancel.Token, error.CancellationToken); Assert.Null(error.InnerException); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Deadline_covers_body_read_and_disposes_blocked_stream()
    {
        var i = Intent(); var b = Binding(i); var body = new BlockingStream(); var content = new StreamContent(body);
        content.Headers.ContentType = new("application/json");
        using var client = Client((request, _) => { var response = Reply(request, ""); response.Content = content; return Task.FromResult(response); }, timeout: 1);
        await Failure(() => client.RecordStartAsync(i, b)); Assert.True(body.Disposed);
    }

    [Fact]
    public async Task Disposed_client_rejects_before_provider_and_send()
    {
        var i = Intent(); var b = Binding(i); var calls = 0;
        var client = Client((_, _) => { calls++; throw new Exception(Canary); }, () => { calls++; return Token; });
        client.Dispose(); client.Dispose(); await Failure(() => client.RecordStartAsync(i, b)); Assert.Equal(0, calls);
    }

    private sealed class SpyContent(byte[] bytes, bool throwOnRead = false, bool unknownLength = false) : HttpContent
    {
        public int Reads; public bool Disposed;
        protected override bool TryComputeLength(out long length) { length = bytes.Length; return !unknownLength; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        { Reads++; if (throwOnRead) throw new InvalidOperationException(Canary); return stream.WriteAsync(bytes).AsTask(); }
        protected override Task<Stream> CreateContentReadStreamAsync()
        { Reads++; if (throwOnRead) throw new InvalidOperationException(Canary); return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class BlockingStream : Stream
    {
        public bool Disposed;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
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
}
