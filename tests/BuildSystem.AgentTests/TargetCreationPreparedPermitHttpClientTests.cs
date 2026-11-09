using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BuildSystem.Agent;
using Xunit;

namespace BuildSystem.AgentTests;

// Fake handlers only: no network, SQL, journal, worker or target execution.
public sealed class TargetCreationPreparedPermitHttpClientTests
{
    private const string Canary = "secret-canary";
    private const string Token = "machine-token";
    private static readonly DateTimeOffset Issued = new(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static TargetCreationIntent Intent()
    {
        var reservation = Guid.NewGuid();
        return new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 17, reservation, Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), "bs_test_" + reservation.ToString("N"), "bs_test_" + reservation.ToString("N"));
    }
    private static TargetCreationPermitBinding Binding(TargetCreationIntent i) =>
        new(Guid.NewGuid(), i.OperationId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Issued, Issued.AddMinutes(5))
        { CredentialId = Guid.NewGuid(), EndpointMappingId = Guid.NewGuid() };
    private static TargetCreationPermitHttpClientOptions Options(bool enabled = true, int timeout = 30) =>
        new() { BaseUri = new Uri("https://permits.example/"), Enabled = enabled, TimeoutSeconds = timeout };
    private static TargetCreationPermitHttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> send, Func<string>? provider = null) =>
        new(new Handler(send), Options(), provider ?? (() => Token));
    private static string Json(TargetCreationIntent i, TargetCreationPermitBinding b) => JsonSerializer.Serialize(new
    {
        permitId = b.PermitId, operationId = i.OperationId, attemptId = b.AttemptId,
        expectedOperationRevision = 1L, issuedAt = b.IssuedAt, startBefore = b.StartBefore,
        state = 0, revision = 1L, startedAt = (DateTimeOffset?)null, startExpectedRevision = (long?)null, newlyRecorded = false,
        operation = new
        {
            operationId = i.OperationId, restoreIntentId = b.RestoreIntentId, environmentId = i.EnvironmentId,
            stageJobId = i.JobId, reservationId = i.ReservationId, artifactId = b.ArtifactId, agentId = b.AgentId,
            targetIncarnationId = i.TargetIncarnationId, canonicalClusterId = i.CanonicalClusterId,
            canonicalSqlServerId = i.CanonicalSqlServerId, infobaseName = i.InfobaseName, sqlDbName = i.SqlDatabaseName,
            state = 0, revision = 1L, attemptId = (Guid?)null, attemptExpectedRevision = (long?)null,
            quarantineReason = (int?)null, newlyRecorded = false, executable = false, scheduledJobsMustRemainBlocked = true
        },
        credentialId = b.CredentialId, endpointMappingId = b.EndpointMappingId,
        executable = false, scheduledJobsMustRemainBlocked = true
    });
    private static HttpResponseMessage Reply(HttpRequestMessage request, string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { RequestMessage = request, Content = new StringContent(json, Encoding.UTF8, "application/json") };
        response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true }; return response;
    }
    private static string Change(string json, string path, string value)
    {
        var root = JsonNode.Parse(json)!.AsObject(); var nested = path.StartsWith("operation.", StringComparison.Ordinal);
        (nested ? root["operation"]!.AsObject() : root)[nested ? path[10..] : path] = JsonNode.Parse(value);
        return root.ToJsonString();
    }
    private static async Task Failure(Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<TargetCreationPermitTransportException>(action);
        Assert.Equal("TargetCreationPermitTransportUnavailable", error.Message); Assert.Null(error.InnerException);
        Assert.DoesNotContain(Canary, error.ToString()); Assert.DoesNotContain(Token, error.ToString()); Assert.DoesNotContain("https://", error.ToString());
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
    private sealed class UnreadContent : HttpContent
    {
        public int Reads { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) { Reads++; throw new Exception(Canary); }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    [Fact]
    public async Task Exact_prepared_record_accepts_fixed_past_window_without_granting_authority()
    {
        var i = Intent(); var b = Binding(i); var json = Json(i, b);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(16, document.RootElement.EnumerateObject().Count());
        Assert.Equal(20, document.RootElement.GetProperty("operation").EnumerateObject().Count());
        using var client = Client(request => Reply(request, json));
        ITargetCreationPreparedPermitTransport transport = client;
        var receipt = await transport.GetPreparedAsync(i, b);
        Assert.Equal(i, receipt.TargetIntent); Assert.Equal(b, receipt.ScopeBinding);
        Assert.False(receipt.Executable); Assert.False(receipt.FreshAuthority); Assert.False(receipt.RetryAllowed);
        Assert.True(receipt.ScheduledJobsMustRemainBlocked); Assert.DoesNotContain(b.PermitId.ToString(), receipt.ToString());
    }

    [Fact]
    public async Task Get_has_fixed_uri_no_query_or_body_and_one_fresh_bearer_per_call()
    {
        var i = Intent(); var b = Binding(i); var sends = 0; var providers = 0;
        using var client = Client(request =>
        {
            sends++; Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal($"https://permits.example/api/agents/target-creation-permits/{b.PermitId:D}", request.RequestUri!.AbsoluteUri);
            Assert.Equal("", request.RequestUri.Query); Assert.Null(request.Content);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme); Assert.Equal("rotation-" + sends, request.Headers.Authorization.Parameter);
            Assert.False(request.Headers.Contains("Cookie")); Assert.False(request.Headers.Contains("traceparent"));
            Assert.Equal(new[] { "Authorization" }, request.Headers.Select(h => h.Key));
            return Reply(request, Json(i, b));
        }, () => "rotation-" + ++providers);
        await client.GetPreparedAsync(i, b); await client.GetPreparedAsync(i, b);
        Assert.Equal(2, sends); Assert.Equal(2, providers);
    }

    [Fact]
    public async Task Default_off_precedes_provider_network_and_cancellation()
    {
        var calls = 0; var i = Intent(); var b = Binding(i);
        var options = new TargetCreationPermitHttpClientOptions { BaseUri = new Uri("https://permits.example/") };
        Assert.False(options.Enabled); Assert.Equal(30, options.TimeoutSeconds);
        using var client = new TargetCreationPermitHttpClient(new Handler(_ => { calls++; throw new Exception(Canary); }), options,
            () => { calls++; return Token; });
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Failure(() => client.GetPreparedAsync(i, b, cancellation.Token)); Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("permitId")] [InlineData("operationId")] [InlineData("attemptId")] [InlineData("credentialId")] [InlineData("endpointMappingId")]
    [InlineData("operation.operationId")] [InlineData("operation.restoreIntentId")] [InlineData("operation.environmentId")]
    [InlineData("operation.stageJobId")] [InlineData("operation.reservationId")] [InlineData("operation.artifactId")]
    [InlineData("operation.agentId")] [InlineData("operation.targetIncarnationId")]
    [InlineData("operation.canonicalClusterId")] [InlineData("operation.canonicalSqlServerId")]
    public async Task Every_identity_pin_is_exact_nonempty_and_canonical(string path)
    {
        var i = Intent(); var b = Binding(i);
        foreach (var wrong in new[] { Guid.NewGuid().ToString("D"), Guid.Empty.ToString("D"), b.PermitId.ToString("N"), " " + b.PermitId })
        {
            using var client = Client(request => Reply(request, Change(Json(i, b), path, JsonSerializer.Serialize(wrong))));
            await Failure(() => client.GetPreparedAsync(i, b));
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Ready_revision_null_start_attempt_and_nonexecutability_are_mandatory(bool nested)
    {
        var i = Intent(); var b = Binding(i); var prefix = nested ? "operation." : "";
        var changes = new (string Field, string Value)[] { ("state", "1"), ("state", "2"), ("revision", "2"), ("revision", "0"),
            ("revision", "1.0"), ("newlyRecorded", "true"), ("executable", "true"), ("scheduledJobsMustRemainBlocked", "false") };
        changes = changes.Concat(nested ? new[] { ("attemptId", JsonSerializer.Serialize(b.AttemptId)), ("attemptExpectedRevision", "1"),
            ("quarantineReason", "0"), ("infobaseName", "\"secret-canary\""), ("sqlDbName", "\"secret-canary\"") }
            : new[] { ("startedAt", JsonSerializer.Serialize(b.IssuedAt)), ("startExpectedRevision", "1"), ("expectedOperationRevision", "2") }).ToArray();
        foreach (var (field, value) in changes)
        {
            using var client = Client(request => Reply(request, Change(Json(i, b), prefix + field, value)));
            await Failure(() => client.GetPreparedAsync(i, b));
        }
    }

    [Theory]
    [InlineData("issuedAt")] [InlineData("startBefore")]
    public async Task Date_window_matches_binding_exactly_and_is_utc(string field)
    {
        var i = Intent(); var b = Binding(i); var expected = field == "issuedAt" ? b.IssuedAt : b.StartBefore;
        foreach (var value in new[] { "null", "123", "\"not-a-date\"", JsonSerializer.Serialize(expected.AddSeconds(1)),
            JsonSerializer.Serialize(expected.ToOffset(TimeSpan.FromHours(1))) })
        {
            using var client = Client(request => Reply(request, Change(Json(i, b), field, value)));
            await Failure(() => client.GetPreparedAsync(i, b));
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Every_field_is_required_unique_case_sensitive_and_unknown_secrets_are_rejected(bool nested)
    {
        var i = Intent(); var b = Binding(i); var valid = Json(i, b); var root = JsonNode.Parse(valid)!.AsObject();
        var original = nested ? root["operation"]!.AsObject() : root;
        foreach (var property in original.ToArray())
        {
            var changed = JsonNode.Parse(valid)!.AsObject(); var target = nested ? changed["operation"]!.AsObject() : changed;
            target.Remove(property.Key); var missing = changed.ToJsonString();
            target[char.ToUpperInvariant(property.Key[0]) + property.Key[1..]] = property.Value?.DeepClone();
            var duplicateObject = original.ToJsonString()[..^1] + ",\"" + property.Key + "\":" + (property.Value?.ToJsonString() ?? "null") + "}";
            var duplicate = nested ? valid.Replace(original.ToJsonString(), duplicateObject, StringComparison.Ordinal) : duplicateObject;
            foreach (var body in new[] { missing, changed.ToJsonString(), duplicate })
            {
                using var client = Client(request => Reply(request, body)); await Failure(() => client.GetPreparedAsync(i, b));
            }
        }
        using var unknown = Client(request => Reply(request, Change(valid, nested ? "operation.sqlPassword" : "clusterPassword", "\"secret-canary\"")));
        await Failure(() => unknown.GetPreparedAsync(i, b));
    }

    [Fact]
    public async Task Invalid_local_intents_and_bindings_do_not_call_provider_or_handler()
    {
        var i = Intent(); var b = Binding(i); var calls = 0;
        using var client = Client(_ => { calls++; throw new Exception(Canary); }, () => { calls++; return Token; });
        foreach (var bad in new[] { i with { OperationId = Guid.Empty }, i with { FencingToken = 0 }, i with { InfobaseName = Canary },
            i with { CanonicalClusterId = Guid.Empty }, i with { SqlDatabaseName = Canary } }) await Failure(() => client.GetPreparedAsync(bad, b));
        foreach (var bad in new[] { b with { CredentialId = null }, b with { EndpointMappingId = null }, b with { CredentialId = Guid.Empty },
            b with { EndpointMappingId = Guid.Empty }, b with { PermitId = Guid.Empty }, b with { AttemptId = Guid.Empty },
            b with { OperationId = Guid.NewGuid() }, b with { IssuedAt = Issued.ToOffset(TimeSpan.FromHours(1)) },
            b with { StartBefore = Issued.AddMinutes(5).AddSeconds(1) } }) await Failure(() => client.GetPreparedAsync(i, bad));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(201)] [InlineData(204)] [InlineData(302)] [InlineData(307)] [InlineData(401)] [InlineData(500)]
    public async Task Non_200_never_reads_body_or_follows_redirect_or_retries(int status)
    {
        var i = Intent(); var b = Binding(i); var calls = 0; var content = new UnreadContent();
        using var client = Client(request => { calls++; var response = Reply(request, "", (HttpStatusCode)status); response.Content = content;
            response.Headers.Location = new Uri("https://untrusted.example/"); return response; });
        await Failure(() => client.GetPreparedAsync(i, b)); Assert.Equal(1, calls); Assert.Equal(0, content.Reads);
    }

    [Theory]
    [InlineData(0)] [InlineData(61)]
    public void Timeout_outside_one_to_sixty_is_rejected_even_disabled(int timeout) =>
        Assert.Throws<TargetCreationPermitTransportException>(() => new TargetCreationPermitHttpClient(new Handler(_ => throw new Exception()), Options(false, timeout), () => Token));

    [Fact]
    public async Task Precancellation_never_calls_provider_or_handler()
    {
        var i = Intent(); var b = Binding(i); var calls = 0; using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var client = Client(_ => { calls++; throw new Exception(Canary); }, () => { calls++; return Token; });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetPreparedAsync(i, b, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken); Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("")] [InlineData("bad\r\ntoken")] [InlineData("throw")]
    public async Task Invalid_or_throwing_provider_never_sends_and_is_redacted(string credential)
    {
        var i = Intent(); var b = Binding(i); var sends = 0;
        using var client = Client(_ => { sends++; throw new Exception(Canary); }, () => credential == "throw" ? throw new Exception(Canary) : credential);
        await Failure(() => client.GetPreparedAsync(i, b)); Assert.Equal(0, sends);
    }
}