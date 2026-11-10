using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BuildSystem.Agent;

public sealed class AgentReadinessIdentityHttpClientOptions
{
    public required Uri BaseUri { get; init; }
    public bool Enabled { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
    public override string ToString() => "AgentReadinessIdentityHttpClientOptions [NONEXECUTABLE]";
}

/// <summary>API identity observation only; never operational readiness or fresh authority.</summary>
public sealed class AgentReadinessIdentityReport
{
    internal AgentReadinessIdentityReport() { }
    public bool ApiIdentityObserved => true;
    public bool Executable => false;
    public bool FreshAuthority => false;
    public bool ReadyForExecution => false;
    public bool OperationalVerificationRequired => true;
    public override string ToString() => "AgentReadinessIdentityReport [NONEXECUTABLE]";
}

public sealed class AgentReadinessIdentityException : Exception
{
    public AgentReadinessIdentityException() : base("AgentReadinessIdentityUnavailable") { }
    public string Code => "AgentReadinessIdentityUnavailable";
    public override string ToString() => "AgentReadinessIdentityUnavailable";
}

/// <summary>Explicit, default-off, read-only identity observation. No retries or execution.</summary>
public sealed class AgentReadinessIdentityHttpClient : IDisposable
{
    private const int MaximumResponseBytes = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly HttpClient _http;
    private readonly Uri _destination;
    private readonly TimeSpan _timeout;
    private readonly bool _enabled;
    private readonly Func<string> _credentialProvider;
    private int _disposed;

    // Default certificate validation and UseProxy remain intact: deployment proxies must be trusted.
    public AgentReadinessIdentityHttpClient(AgentReadinessIdentityHttpClientOptions options, Func<string> credentialProvider)
        : this(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ActivityHeadersPropagator = null
        }, options, credentialProvider) { }

    /// <summary>Owns the trusted handler, which must not redirect, retry, change destinations,
    /// inject cookies/tracing, or forward credentials. This is not a production authority seam.</summary>
    public AgentReadinessIdentityHttpClient(HttpMessageHandler trustedHandler,
        AgentReadinessIdentityHttpClientOptions options, Func<string> credentialProvider)
    {
        try
        {
            if (options is null || options.BaseUri is not { IsAbsoluteUri: true } uri ||
                uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host) ||
                uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                uri.UserInfo.Length != 0 || options.TimeoutSeconds is < 1 or > 60 ||
                trustedHandler is null || credentialProvider is null) Fail();
            _destination = new Uri(options!.BaseUri, "/api/agent/readiness-identity");
            _timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            _enabled = options.Enabled;
            _credentialProvider = credentialProvider!;
            _http = new HttpClient(trustedHandler!, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        }
        catch (Exception)
        {
            try { trustedHandler?.Dispose(); } catch (Exception) { }
            throw new AgentReadinessIdentityException();
        }
    }

    public async Task<AgentReadinessIdentityReport> ProbeAsync(Guid expectedAgentId,
        CancellationToken cancellationToken = default)
    {
        // Disabled wins even over cancellation or malformed input; no credential access.
        if (!_enabled) throw new AgentReadinessIdentityException();
        byte[]? bytes = null;
        try
        {
            var clock = Stopwatch.StartNew();
            var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = deadline.Token;
            void Check()
            {
                token.ThrowIfCancellationRequested();
                if (clock.Elapsed >= _timeout || Volatile.Read(ref _disposed) != 0) Fail();
            }
            using (deadline)
            {
                deadline.CancelAfter(_timeout);
                Check();
                if (expectedAgentId == Guid.Empty) Fail();
                // Exactly once. A synchronous provider cannot be interrupted; check elapsed time afterward.
                var credential = _credentialProvider();
                Check();
                if (string.IsNullOrEmpty(credential) || credential.Length > 256 ||
                    credential.StartsWith("Bearer", StringComparison.OrdinalIgnoreCase) ||
                    credential.Any(c => c < '!' || c > '~' || c is ',' or ';')) Fail();
                try
                {
                    using var message = new HttpRequestMessage(HttpMethod.Get, _destination);
                    message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
                    var sending = _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
                    HttpResponseMessage response;
                    try { response = await sending.WaitAsync(token).ConfigureAwait(false); }
                    catch
                    {
                        ObserveLateCompletion(sending, late => late.Dispose());
                        throw;
                    }
                    using (response)
                    {
                        Check();
                        // Error/redirect bodies are never opened or read.
                        if (response.StatusCode != HttpStatusCode.OK ||
                            response.RequestMessage?.RequestUri != _destination || response.Content is null ||
                            response.Headers.CacheControl?.NoStore != true ||
                            response.Content.Headers.ContentLength is > MaximumResponseBytes ||
                            response.Content.Headers.ContentEncoding.Count != 0) Fail();
                        var contentType = response.Content.Headers.ContentType;
                        if (contentType is null || !string.Equals(contentType.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
                            contentType.Parameters.Any(p => !string.Equals(p.Name, "charset", StringComparison.OrdinalIgnoreCase)) ||
                            contentType.Parameters.Count > 1 ||
                            (contentType.CharSet is not null &&
                             !string.Equals(contentType.CharSet, "utf-8", StringComparison.OrdinalIgnoreCase) &&
                             !string.Equals(contentType.CharSet, "\"utf-8\"", StringComparison.OrdinalIgnoreCase))) Fail();
                        bytes = new byte[MaximumResponseBytes + 1];
                        var count = 0;
                        using (var stream = await OpenStreamAsync(response.Content, token).ConfigureAwait(false))
                        {
                            Check();
                            while (count < bytes.Length)
                            {
                                var reading = stream.ReadAsync(bytes.AsMemory(count, bytes.Length - count), token).AsTask();
                                int read;
                                try { read = await reading.WaitAsync(token).ConfigureAwait(false); }
                                catch
                                {
                                    // An uncooperative read can write after timeout: zero again on completion.
                                    var captured = bytes;
                                    ObserveLateCompletion(reading, _ => CryptographicOperations.ZeroMemory(captured));
                                    _ = reading.ContinueWith(_ => CryptographicOperations.ZeroMemory(captured), CancellationToken.None,
                                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                                    throw;
                                }
                                Check();
                                if (read == 0) break;
                                count += read;
                            }
                            if (count > MaximumResponseBytes) Fail();
                        }
                        Check();
                        _ = StrictUtf8.GetCharCount(bytes, 0, count);
                        using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 3 });
                        Validate(document.RootElement, expectedAgentId);
                        Check();
                    }
                }
                finally
                {
                    if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
                }
            }
            // Include parsing, buffer clearing and synchronous disposal in the elapsed-time check.
            Check();
            return new AgentReadinessIdentityReport();
        }
        catch (Exception)
        {
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            throw new AgentReadinessIdentityException();
        }
    }

    private static void Validate(JsonElement root, Guid expected)
    {
        if (root.ValueKind != JsonValueKind.Object) Fail();
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in root.EnumerateObject())
            if (p.Name is not ("agentId" or "executable" or "freshAuthority" or "readinessComplete") ||
                !fields.TryAdd(p.Name, p.Value)) Fail();
        if (fields.Count != 4 || fields["executable"].ValueKind != JsonValueKind.False ||
            fields["freshAuthority"].ValueKind != JsonValueKind.False ||
            fields["readinessComplete"].ValueKind != JsonValueKind.False ||
            fields["agentId"].ValueKind != JsonValueKind.String) Fail();
        var text = fields["agentId"].GetString();
        if (text is null || text.Length != 36 || !Guid.TryParseExact(text, "D", out var id) ||
            id == Guid.Empty || id != expected ||
            !string.Equals(text, id.ToString("D"), StringComparison.OrdinalIgnoreCase)) Fail();
    }

    private static async Task<Stream> OpenStreamAsync(HttpContent content, CancellationToken token)
    {
        var opening = content.ReadAsStreamAsync(token);
        try { return await opening.WaitAsync(token).ConfigureAwait(false); }
        catch
        {
            ObserveLateCompletion(opening, stream => stream.Dispose());
            throw;
        }
    }

    private static void ObserveLateCompletion<T>(Task<T> task, Action<T> cleanup)
    {
        _ = task.ContinueWith(completed =>
        {
            try
            {
                if (completed.IsCompletedSuccessfully) cleanup(completed.Result);
                else if (completed.IsFaulted) _ = completed.Exception;
            }
            catch (Exception) { }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    [DoesNotReturn]
    private static void Fail() => throw new AgentReadinessIdentityException();
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _http.Dispose(); }
        catch (Exception) { throw new AgentReadinessIdentityException(); }
    }
}
