using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BuildSystem.Agent;
using Xunit;

namespace BuildSystem.AgentTests;

// Fake handlers only: observing API identity never grants execution or fresh authority.
public sealed class AgentReadinessIdentityHttpClientTests
{
    private static readonly Guid Agent = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const string Canary = "secret-canary";
    private static AgentReadinessIdentityHttpClientOptions Options(bool enabled = true, int timeout = 30) =>
        new() { BaseUri = new Uri("https://identity.example/"), Enabled = enabled, TimeoutSeconds = timeout };
    private static string Json(Guid? id = null) => "{\"agentId\":\"" + (id ?? Agent).ToString("D") + "\",\"executable\":false,\"freshAuthority\":false,\"readinessComplete\":false}";
    private static AgentReadinessIdentityHttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        Func<string>? provider = null, bool enabled = true, int timeout = 30) =>
        new(new Handler(send), Options(enabled, timeout), provider ?? (() => Canary));
    private static AgentReadinessIdentityHttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> send) => Client((r, _) => Task.FromResult(send(r)));
    private static HttpResponseMessage Reply(HttpRequestMessage r, string? json = null, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { RequestMessage = r, Content = new StringContent(json ?? Json(), Encoding.UTF8, "application/json") };
        response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true }; return response;
    }
    private static async Task Failure(Func<Task> action)
    {
        var e = await Assert.ThrowsAsync<AgentReadinessIdentityException>(action);
        Assert.Equal("AgentReadinessIdentityUnavailable", e.Message); Assert.Equal("AgentReadinessIdentityUnavailable", e.Code); Assert.Null(e.InnerException);
        Assert.DoesNotContain(Canary, e.ToString()); Assert.DoesNotContain(Agent.ToString(), e.ToString()); Assert.DoesNotContain("https://", e.ToString());
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => send(r, ct); }
    private sealed class SpyContent(byte[] bytes, bool throwOnRead = false) : HttpContent
    {
        public int Reads; public bool Disposed;
        public TaskCompletionSource<bool> Disposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        { Interlocked.Increment(ref Reads); if (throwOnRead) throw new InvalidOperationException(Canary); return stream.WriteAsync(bytes).AsTask(); }
        protected override void Dispose(bool disposing) { Disposed = true; Disposal.TrySetResult(true); base.Dispose(disposing); }
    }

    [Fact]
    public void Options_default_disabled()
    { var options = new AgentReadinessIdentityHttpClientOptions { BaseUri = new Uri("https://identity.example/") }; Assert.False(options.Enabled); }

    [Fact]
    public async Task Good_get_is_exact_and_two_explicit_calls_fetch_fresh_tokens_without_retry_or_inherited_headers()
    {
        var sends = 0; var tokens = 0;
        using var client = Client((r, _) =>
        {
            sends++; Assert.Equal(HttpMethod.Get, r.Method); Assert.Equal("https://identity.example/api/agent/readiness-identity", r.RequestUri!.AbsoluteUri);
            Assert.Null(r.Content); Assert.Equal("Bearer", r.Headers.Authorization!.Scheme); Assert.Equal("rotation-" + sends, r.Headers.Authorization.Parameter);
            Assert.False(r.Headers.Contains("Cookie")); Assert.False(r.Headers.Contains("X-Agent-Id"));
            Assert.DoesNotContain("rotation-", r.RequestUri.AbsoluteUri); return Task.FromResult(Reply(r));
        }, () => "rotation-" + ++tokens);
        await client.ProbeAsync(Agent); await client.ProbeAsync(Agent); Assert.Equal(2, tokens); Assert.Equal(2, sends);
    }

    [Fact]
    public async Task Report_is_immutable_non_authoritative_and_redacted_in_diagnostics_and_json()
    {
        using var client = Client(r => Reply(r)); var report = await client.ProbeAsync(Agent);
        Assert.True(report.ApiIdentityObserved); Assert.False(report.Executable); Assert.False(report.FreshAuthority);
        Assert.False(report.ReadyForExecution); Assert.True(report.OperationalVerificationRequired);
        Assert.True(typeof(AgentReadinessIdentityReport).IsSealed);
        Assert.All(typeof(AgentReadinessIdentityReport).GetProperties(), p => Assert.False(p.SetMethod?.IsPublic == true));
        Assert.DoesNotContain(Agent.ToString(), report.ToString()); Assert.DoesNotContain(Canary, report.ToString());
        var json = JsonSerializer.Serialize(report); Assert.DoesNotContain("agentId", json, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain(Agent.ToString(), json);
    }

    [Fact]
    public async Task Disabled_precedes_cancellation_and_empty_expected_identity_without_io()
    {
        var calls = 0; using var ct = new CancellationTokenSource(); ct.Cancel();
        using var client = Client((_, _) => { calls++; throw new Exception(Canary); }, () => { calls++; throw new Exception(Canary); }, enabled: false);
        await Failure(() => client.ProbeAsync(Guid.Empty, ct.Token)); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Enabled_precancel_is_cancellation_without_provider_or_send()
    {
        var calls = 0; using var ct = new CancellationTokenSource(); ct.Cancel();
        using var client = Client((_, _) => { calls++; throw new Exception(); }, () => { calls++; return Canary; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ProbeAsync(Agent, ct.Token)); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Empty_expected_identity_is_fixed_failure_without_io()
    {
        var calls = 0; using var client = Client((_, _) => { calls++; throw new Exception(); }, () => { calls++; return Canary; });
        await Failure(() => client.ProbeAsync(Guid.Empty)); Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("http://identity.example/")] [InlineData("https://identity.example/path/")] [InlineData("https://identity.example/?secret-canary")]
    [InlineData("https://identity.example/#secret-canary")] [InlineData("https://user:secret-canary@identity.example/")] [InlineData("/relative")]
    public void Malformed_base_options_fail_closed_even_disabled(string address)
    {
        var e = Assert.Throws<AgentReadinessIdentityException>(() => new AgentReadinessIdentityHttpClient(new Handler((_, _) => throw new Exception()),
            new() { BaseUri = new Uri(address, UriKind.RelativeOrAbsolute) }, () => Canary));
        Assert.Equal("AgentReadinessIdentityUnavailable", e.Message); Assert.Null(e.InnerException); Assert.DoesNotContain(address, e.ToString());
    }

    [Fact]
    public void Missing_base_is_fixed_failure_without_provider_or_http()
    {
        var calls = 0; var e = Assert.Throws<AgentReadinessIdentityException>(() => new AgentReadinessIdentityHttpClient(
            new Handler((_, _) => { calls++; throw new Exception(); }), new() { BaseUri = null! }, () => { calls++; return Canary; }));
        Assert.Equal("AgentReadinessIdentityUnavailable", e.Message); Assert.Equal(0, calls);
    }

    [Theory] [InlineData(0)] [InlineData(61)]
    public void Timeout_options_fail_closed(int timeout)
    {
        var e = Assert.Throws<AgentReadinessIdentityException>(() => new AgentReadinessIdentityHttpClient(Options(false, timeout), () => Canary));
        Assert.Equal("AgentReadinessIdentityUnavailable", e.Message); Assert.Null(e.InnerException);
    }

    [Theory] [InlineData("")] [InlineData("white space")] [InlineData("token\r\nInjected: header")] [InlineData("nonasciié")] [InlineData("control\u007f")]
    public async Task Malformed_token_never_reaches_handler(string token)
    {
        var sends = 0; using var client = Client((_, _) => { sends++; throw new Exception(); }, () => token);
        await Failure(() => client.ProbeAsync(Agent)); Assert.Equal(0, sends);
    }

    [Fact]
    public async Task Provider_exception_is_sanitized_without_http()
    {
        var sends = 0; using var client = Client((_, _) => { sends++; throw new Exception(); }, () => throw new Exception(Canary));
        await Failure(() => client.ProbeAsync(Agent)); Assert.Equal(0, sends);
    }

    [Theory] [InlineData(201)] [InlineData(204)] [InlineData(301)] [InlineData(302)] [InlineData(307)] [InlineData(308)] [InlineData(401)] [InlineData(500)]
    public async Task Non200_never_reads_error_or_redirect_body_or_retries(int status)
    {
        var sends = 0; var content = new SpyContent([], true);
        using var client = Client(r => { sends++; var response = Reply(r, status: (HttpStatusCode)status); response.Content = content; response.Headers.Location = new Uri("https://evil.example/"); return response; });
        await Failure(() => client.ProbeAsync(Agent)); Assert.Equal(1, sends); Assert.Equal(0, content.Reads); Assert.True(content.Disposed);
    }

    [Theory] [InlineData("cache")] [InlineData("type")] [InlineData("missing-type")] [InlineData("charset")] [InlineData("encoding")] [InlineData("destination")]
    public async Task Unsafe_metadata_is_rejected_before_body_read(string defect)
    {
        var content = new SpyContent([], true); content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        using var client = Client(r =>
        {
            var response = Reply(r); response.Content = content;
            if (defect == "cache") response.Headers.CacheControl = null;
            if (defect == "type") content.Headers.ContentType = new("text/plain");
            if (defect == "missing-type") content.Headers.ContentType = null;
            if (defect == "charset") content.Headers.ContentType!.CharSet = "utf-16";
            if (defect == "encoding") content.Headers.ContentEncoding.Add("gzip");
            if (defect == "destination") response.RequestMessage = new(HttpMethod.Get, "https://evil.example/"); return response;
        });
        await Failure(() => client.ProbeAsync(Agent)); Assert.Equal(0, content.Reads); Assert.True(content.Disposed);
    }

    [Theory]
    [InlineData("duplicate")] [InlineData("unknown")] [InlineData("missing")] [InlineData("case")]
    [InlineData("trailing")] [InlineData("depth")] [InlineData("mismatch")] [InlineData("empty")] [InlineData("N")]
    [InlineData("executable")] [InlineData("freshAuthority")] [InlineData("readinessComplete")]
    public async Task Exact_four_field_schema_identity_and_false_flags_are_required(string defect)
    {
        var json = Json(); json = defect switch
        {
            "duplicate" => json[..^1] + ",\"executable\":false}", "unknown" => json[..^1] + ",\"unknown\":false}",
            "missing" => json.Replace(",\"freshAuthority\":false", ""), "case" => json.Replace("agentId", "AgentId"),
            "trailing" => json + " {}", "depth" => new string('[', 100) + json + new string(']', 100),
            "mismatch" => Json(Guid.NewGuid()), "empty" => Json(Guid.Empty), "N" => json.Replace(Agent.ToString("D"), Agent.ToString("N")),
            _ => json.Replace("\"" + defect + "\":false", "\"" + defect + "\":true")
        };
        using var client = Client(r => Reply(r, json)); await Failure(() => client.ProbeAsync(Agent));
    }

    [Fact]
    public async Task Invalid_utf8_is_rejected()
    {
        var bytes = Encoding.UTF8.GetBytes(Json()); bytes[12] = 0xff;
        using var client = Client(r => { var response = Reply(r); response.Content = new ByteArrayContent(bytes); response.Content.Headers.ContentType = new("application/json"); return response; });
        await Failure(() => client.ProbeAsync(Agent));
    }

    [Theory] [InlineData(4096)] [InlineData(4097)]
    public async Task Actual_unknown_length_body_has_exact_4096_byte_bound(int length)
    {
        var json = Json(); var content = new SpyContent(Encoding.UTF8.GetBytes(json + new string(' ', length - Encoding.UTF8.GetByteCount(json))));
        content.Headers.ContentType = new("application/json");
        using var client = Client(r => { var response = Reply(r); response.Content = content; return response; });
        if (length == 4096) Assert.True((await client.ProbeAsync(Agent)).ApiIdentityObserved); else await Failure(() => client.ProbeAsync(Agent));
        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task Cancellation_returns_promptly_when_handler_ignores_token_and_disposes_late_response()
    {
        var entered = new TaskCompletionSource<HttpRequestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ct = new CancellationTokenSource(); using var client = Client((r, _) => { entered.TrySetResult(r); return late.Task; });
        var probe = client.ProbeAsync(Agent, ct.Token); var request = await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); ct.Cancel();
        var content = new SpyContent([]);
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.WaitAsync(TimeSpan.FromSeconds(3))); }
        finally { var response = Reply(request); response.Content = content; late.TrySetResult(response); }
        await content.Disposal.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(0, content.Reads);
    }

    [Fact]
    public async Task One_second_timeout_is_fixed_failure_and_late_response_is_disposed()
    {
        var entered = new TaskCompletionSource<HttpRequestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously); var sends = 0;
        using var client = Client((r, _) => { sends++; entered.TrySetResult(r); return late.Task; }, timeout: 1);
        var probe = client.ProbeAsync(Agent); var request = await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); var content = new SpyContent([]);
        try { await Failure(() => probe.WaitAsync(TimeSpan.FromSeconds(3))); }
        finally { var response = Reply(request); response.Content = content; late.TrySetResult(response); }
        await content.Disposal.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(1, sends); Assert.Equal(0, content.Reads);
    }
}
