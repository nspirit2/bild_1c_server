using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuildSystem.Agent;

public sealed class TargetCreationCredentialHttpClientOptions
{
    public required Uri BaseUri { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
    public bool Enabled { get; init; }
    public override string ToString() => "TargetCreationCredentialHttpClientOptions [REDACTED; NONEXECUTABLE]";
}

public sealed record TargetCreationCredentialRequest(Guid PermitId, long ExpectedPermitRevision,
    Guid AttemptId, Guid CredentialId, Guid EndpointMappingId)
{
    public override string ToString() => "TargetCreationCredentialRequest [REDACTED; NONEXECUTABLE]";
}

/// <summary>Explicit plaintext escape. Callers own its lifetime and must not log or serialize it.
/// Disposing the delivery holder cannot erase strings already returned to a caller.</summary>
public sealed record TargetCreationDeliveredSecretValues(string ClusterUser, string ClusterPassword,
    string SqlUser, string SqlPassword)
{
    public override string ToString() => "TargetCreationDeliveredSecretValues [REDACTED; NONEXECUTABLE]";
}

/// <summary>Caller-owned delivery, not execution authority. Disposal drops references; managed strings,
/// parser scratch and explicitly escaped values cannot be guaranteed to be erased.</summary>
[JsonConverter(typeof(TargetCreationDeliveredCredentialsJsonConverter))]
public sealed class TargetCreationDeliveredCredentials : IDisposable
{
    private string? _clusterUser;
    private string? _clusterPassword;
    private string? _sqlUser;
    private string? _sqlPassword;
    private int _disposed;

    internal TargetCreationDeliveredCredentials(string clusterUser, string clusterPassword, string sqlUser, string sqlPassword)
    {
        _clusterUser = clusterUser;
        _clusterPassword = clusterPassword;
        _sqlUser = sqlUser;
        _sqlPassword = sqlPassword;
    }

    public bool Executable => false;
    public bool ScheduledJobsMustRemainBlocked => true;
    /// <summary>Physical creation retries are never authorized by delivery or repeated resolution.</summary>
    public bool RetryAllowed => false;

    public TargetCreationDeliveredSecretValues ReadSecrets()
    {
        if (Volatile.Read(ref _disposed) != 0) throw Disposed();
        var clusterUser = _clusterUser;
        var clusterPassword = _clusterPassword;
        var sqlUser = _sqlUser;
        var sqlPassword = _sqlPassword;
        if (Volatile.Read(ref _disposed) != 0 || clusterUser is null || clusterPassword is null ||
            sqlUser is null || sqlPassword is null) throw Disposed();
        return new(clusterUser, clusterPassword, sqlUser, sqlPassword);
    }

    private static ObjectDisposedException Disposed() => new(nameof(TargetCreationDeliveredCredentials),
        "Target creation delivered credentials disposed.");
    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        Interlocked.Exchange(ref _clusterUser, null);
        Interlocked.Exchange(ref _clusterPassword, null);
        Interlocked.Exchange(ref _sqlUser, null);
        Interlocked.Exchange(ref _sqlPassword, null);
    }
    public override string ToString() => "TargetCreationDeliveredCredentials [REDACTED; NONEXECUTABLE]";
}

public sealed class TargetCreationDeliveredCredentialsJsonConverter : JsonConverter<TargetCreationDeliveredCredentials>
{
    public override bool HandleNull => true;
    public override TargetCreationDeliveredCredentials Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options) => throw new JsonException("Target creation credential holder serialization denied.");
    public override void Write(Utf8JsonWriter writer, TargetCreationDeliveredCredentials value,
        JsonSerializerOptions options) => throw new JsonException("Target creation credential holder serialization denied.");
}

public interface ITargetCreationCredentialTransport
{
    Task<TargetCreationDeliveredCredentials> ResolveAsync(TargetCreationCredentialRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Contains no remote status, body, URL, credential, provider exception or inner exception.</summary>
public sealed class TargetCreationCredentialTransportException : Exception
{
    public TargetCreationCredentialTransportException() : base("TargetCreationCredentialTransportUnavailable") { }
    public string Code => "TargetCreationCredentialTransportUnavailable";
    public override string ToString() => "TargetCreationCredentialTransportUnavailable";
}

/// <summary>Opt-in, bounded, one-exchange delivery transport. No caching, automatic retry, execution,
/// worker registration or authority decision: the server verifies the assigned token and all pins.</summary>
public sealed class TargetCreationCredentialHttpClient : ITargetCreationCredentialTransport, IDisposable
{
    private const int MaximumResponseBytes = 128 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] ResponseNames =
    [
        "permitId", "expectedPermitRevision", "attemptId", "credentialId", "endpointMappingId",
        "clusterUser", "clusterPassword", "sqlUser", "sqlPassword", "executable",
        "scheduledJobsMustRemainBlocked", "retryAllowed"
    ];
    private readonly HttpClient _http;
    private readonly Uri _destination;
    private readonly TimeSpan _timeout;
    private readonly bool _enabled;
    private readonly Func<string> _credentialProvider;
    private int _disposed;

    public TargetCreationCredentialHttpClient(TargetCreationCredentialHttpClientOptions options, Func<string> credentialProvider)
        : this(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ActivityHeadersPropagator = null
        }, options, credentialProvider) { }

    /// <summary>Owns the handler. This test seam is trusted: it must not redirect, change destinations,
    /// inject cookies/tracing, forward credentials, or retry. It is not a production authority seam.</summary>
    public TargetCreationCredentialHttpClient(HttpMessageHandler trustedHandler,
        TargetCreationCredentialHttpClientOptions options, Func<string> credentialProvider)
    {
        try
        {
            if (options is null || options.BaseUri is not { IsAbsoluteUri: true } uri ||
                uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host) ||
                uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                uri.UserInfo.Length != 0 || options.TimeoutSeconds is < 1 or > 60 ||
                trustedHandler is null || credentialProvider is null) Fail();
            _destination = new Uri(options!.BaseUri, "/api/agents/target-creation-credentials/resolve");
            _timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            _enabled = options.Enabled;
            _credentialProvider = credentialProvider!;
            _http = new HttpClient(trustedHandler!, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        }
        catch (Exception)
        {
            try { trustedHandler?.Dispose(); } catch (Exception) { }
            throw new TargetCreationCredentialTransportException();
        }
    }

    public async Task<TargetCreationDeliveredCredentials> ResolveAsync(TargetCreationCredentialRequest request,
        CancellationToken cancellationToken = default)
    {
        // Disabled is the first gate, even for a canceled or malformed call.
        if (!_enabled) throw new TargetCreationCredentialTransportException();
        TargetCreationDeliveredCredentials? result = null;
        byte[]? bytes = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clock = Stopwatch.StartNew();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_timeout);
            var token = deadline.Token;
            void Check()
            {
                token.ThrowIfCancellationRequested();
                if (clock.Elapsed >= _timeout || Volatile.Read(ref _disposed) != 0) Fail();
            }
            Check();
            if (request is null || request.PermitId == Guid.Empty || request.ExpectedPermitRevision != 2 ||
                request.AttemptId == Guid.Empty || request.CredentialId == Guid.Empty ||
                request.EndpointMappingId == Guid.Empty) Fail();
            // Exactly once per explicit call; never retained on this client.
            var credential = _credentialProvider();
            Check();
            if (string.IsNullOrEmpty(credential) || credential.Length > 256 ||
                credential.StartsWith("Bearer", StringComparison.OrdinalIgnoreCase) ||
                credential.Any(c => c < '!' || c > '~' || c is ',' or ';')) Fail();
            using (var message = new HttpRequestMessage(HttpMethod.Post, _destination))
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
                message.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    permitId = request!.PermitId,
                    expectedPermitRevision = request.ExpectedPermitRevision,
                    attemptId = request.AttemptId,
                    credentialId = request.CredentialId,
                    endpointMappingId = request.EndpointMappingId
                }));
                message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
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
                    // Error and redirect bodies are never opened or read.
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
                            var reading = stream.ReadAsync(bytes.AsMemory(count, Math.Min(4096, bytes.Length - count)), token).AsTask();
                            int read;
                            try { read = await reading.WaitAsync(token).ConfigureAwait(false); }
                            catch
                            {
                                // An uncooperative read may still write into this buffer after timeout.
                                var captured = bytes;
                                ObserveLateCompletion(reading, _ => CryptographicOperations.ZeroMemory(captured));
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
                    using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 4 });
                    result = Parse(document.RootElement, request);
                    Check();
                }
            }
            // Disposal failures and time spent parsing/disposal cannot escape a live holder.
            Check();
            return result!;
        }
        catch (Exception)
        {
            result?.Dispose();
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            throw new TargetCreationCredentialTransportException();
        }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static TargetCreationDeliveredCredentials Parse(JsonElement root, TargetCreationCredentialRequest request)
    {
        if (root.ValueKind != JsonValueKind.Object) Fail();
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!ResponseNames.Contains(property.Name, StringComparer.Ordinal) || !fields.TryAdd(property.Name, property.Value)) Fail();
        if (fields.Count != ResponseNames.Length) Fail();
        RequireGuid(fields["permitId"], request.PermitId);
        RequireGuid(fields["attemptId"], request.AttemptId);
        RequireGuid(fields["credentialId"], request.CredentialId);
        RequireGuid(fields["endpointMappingId"], request.EndpointMappingId);
        if (fields["expectedPermitRevision"].ValueKind != JsonValueKind.Number ||
            fields["expectedPermitRevision"].GetRawText() != "2" ||
            fields["executable"].ValueKind != JsonValueKind.False ||
            fields["scheduledJobsMustRemainBlocked"].ValueKind != JsonValueKind.True ||
            fields["retryAllowed"].ValueKind != JsonValueKind.False) Fail();
        // Validate the entire shape and all authority pins before materializing plaintext strings.
        foreach (var name in new[] { "clusterUser", "clusterPassword", "sqlUser", "sqlPassword" })
            if (fields[name].ValueKind != JsonValueKind.String) Fail();
        return new(ReadSecret(fields["clusterUser"]), ReadSecret(fields["clusterPassword"]),
            ReadSecret(fields["sqlUser"]), ReadSecret(fields["sqlPassword"]));
    }

    private static void RequireGuid(JsonElement element, Guid expected)
    {
        if (element.ValueKind != JsonValueKind.String) Fail();
        var text = element.GetString();
        if (text is null || text.Length != 36 || !Guid.TryParseExact(text, "D", out var value) ||
            value == Guid.Empty || value != expected ||
            !string.Equals(text, value.ToString("D"), StringComparison.OrdinalIgnoreCase)) Fail();
    }

    private static string ReadSecret(JsonElement element)
    {
        var value = element.GetString();
        if (string.IsNullOrEmpty(value) || value.Length > 4096 || value.Any(char.IsControl)) Fail();
        // Strict encoding rejects lone surrogates while allowing valid Unicode surrogate pairs.
        _ = StrictUtf8.GetByteCount(value);
        return value!;
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
    private static void Fail() => throw new TargetCreationCredentialTransportException();
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _http.Dispose();
    }
}
