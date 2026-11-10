using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuildSystem.Agent;

public sealed class TargetCreationExecutionClaimHttpClientOptions
{
    public required Uri BaseUri { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
    public bool Enabled { get; init; }
    public override string ToString() => "TargetCreationExecutionClaimHttpClientOptions [REDACTED]";
}

/// <summary>A single server-issued claim delivery, not a reusable execution capability. ServerIssued
/// records server issuance at IssuedAt; it does not assert that the claim remains physically fresh.
/// Only a coordinator can decide whether and when to invoke the one physical creation attempt.</summary>
[JsonConverter(typeof(TargetCreationExecutionClaimGrantJsonConverter))]
public sealed class TargetCreationExecutionClaimGrant
{
    internal TargetCreationExecutionClaimGrant(Guid claimId, TargetCreationIntent targetIntent,
        TargetCreationPermitBinding scopeBinding, DateTimeOffset issuedAt, DateTimeOffset expiresAt)
    {
        ClaimId = claimId;
        TargetIntent = targetIntent;
        ScopeBinding = scopeBinding;
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt;
    }

    public Guid ClaimId { get; }
    public TargetCreationIntent TargetIntent { get; }
    public TargetCreationPermitBinding ScopeBinding { get; }
    public DateTimeOffset IssuedAt { get; }
    public DateTimeOffset ExpiresAt { get; }
    public bool ServerIssued => true;
    public bool NewlyIssued => true;
    public bool Executable => true;
    public bool ScheduledJobsMustRemainBlocked => true;
    public bool RetryAllowed => false;
    public override string ToString() => "TargetCreationExecutionClaimGrant [REDACTED]";
}

/// <summary>Reject even null values; claims must never be serialized into journals or replayed.</summary>
public sealed class TargetCreationExecutionClaimGrantJsonConverter : JsonConverter<TargetCreationExecutionClaimGrant>
{
    public override bool HandleNull => true;
    public override TargetCreationExecutionClaimGrant Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options) => throw new JsonException("Target creation claim serialization denied.");
    public override void Write(Utf8JsonWriter writer, TargetCreationExecutionClaimGrant value,
        JsonSerializerOptions options) => throw new JsonException("Target creation claim serialization denied.");
}

public interface ITargetCreationExecutionClaimTransport
{
    Task<TargetCreationExecutionClaimGrant> ClaimAsync(TargetCreationIntent expectedIntent,
        TargetCreationPermitBinding expectedBinding, CancellationToken cancellationToken = default);
}

/// <summary>Contains no HTTP status, URL, token, response, provider exception or inner exception.</summary>
public sealed class TargetCreationExecutionClaimHttpException : Exception
{
    public TargetCreationExecutionClaimHttpException() : this("TargetCreationExecutionClaimUnavailable") { }
    internal TargetCreationExecutionClaimHttpException(string code) : base(code) => Code = code;
    public string Code { get; }
    public override string ToString() => Code;
}

/// <summary>Opt-in, one HTTP exchange per explicit call. No automatic retry, claim cache,
/// local persistence, clock-based authority inference, physical execution or job release.
/// A second explicit call is a NEW server request, not a local replay or grant renewal.</summary>
public sealed class TargetCreationExecutionClaimHttpClient : ITargetCreationExecutionClaimTransport, IDisposable
{
    private const int MaximumResponseBytes = 32 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] ResponseNames =
    [
        "claimId", "permitId", "operationId", "attemptId", "agentId", "credentialId",
        "endpointMappingId", "issuedAt", "expiresAt", "newlyIssued", "executable",
        "scheduledJobsMustRemainBlocked", "retryAllowed"
    ];
    private readonly HttpClient _http;
    private readonly Uri _destination;
    private readonly TimeSpan _timeout;
    private readonly bool _enabled;
    private readonly Func<string> _credentialProvider;
    private int _disposed;

    public TargetCreationExecutionClaimHttpClient(TargetCreationExecutionClaimHttpClientOptions options,
        Func<string> credentialProvider)
        : this(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ActivityHeadersPropagator = null
        }, options, credentialProvider) { }

    /// <summary>Owns the handler. This test seam is trusted: it must not redirect, change destinations,
    /// inject cookies/tracing, forward credentials or retry. It is not a production authority seam.</summary>
    public TargetCreationExecutionClaimHttpClient(HttpMessageHandler trustedHandler,
        TargetCreationExecutionClaimHttpClientOptions options, Func<string> credentialProvider)
    {
        try
        {
            if (options is null || options.BaseUri is not { IsAbsoluteUri: true } uri ||
                uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host) ||
                uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                uri.UserInfo.Length != 0 || options.TimeoutSeconds is < 1 or > 60 ||
                trustedHandler is null || credentialProvider is null) InvalidOptions();
            _destination = new Uri(options!.BaseUri, "/api/agents/target-creation-execution-claims");
            _timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            _enabled = options.Enabled;
            _credentialProvider = credentialProvider!;
            _http = new HttpClient(trustedHandler!, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        }
        catch (Exception)
        {
            try { trustedHandler?.Dispose(); } catch (Exception) { }
            throw new TargetCreationExecutionClaimHttpException("InvalidTargetCreationExecutionClaimHttpOptions");
        }
    }

    public async Task<TargetCreationExecutionClaimGrant> ClaimAsync(TargetCreationIntent expectedIntent,
        TargetCreationPermitBinding expectedBinding, CancellationToken cancellationToken = default)
    {
        // The disabled gate precedes even malformed parameters and an already-canceled token.
        if (!_enabled) throw new TargetCreationExecutionClaimHttpException();
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
            // Validate only caller-supplied shape; this does NOT read or assert the persisted journal.
            if (expectedBinding is null) Fail();
            _ = TargetCreationLocalPreflight.ValidateInFlight(
                new(expectedIntent, TargetCreationState.CreationInFlight, 2, expectedBinding.AttemptId, 1, null),
                expectedBinding);
            Check();
            // One fresh bearer lookup per explicit call. Neither credentials nor claims are cached.
            var credential = _credentialProvider();
            Check();
            if (string.IsNullOrEmpty(credential) || credential.Length > 256 ||
                credential.StartsWith("Bearer", StringComparison.OrdinalIgnoreCase) ||
                credential.Any(c => c < '!' || c > '~' || c is ',' or ';')) Fail();
            TargetCreationExecutionClaimGrant result;
            using (var message = new HttpRequestMessage(HttpMethod.Post, _destination))
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
                message.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    permitId = expectedBinding.PermitId,
                    expectedPermitRevision = 2,
                    attemptId = expectedBinding.AttemptId,
                    credentialId = expectedBinding.CredentialId!.Value,
                    endpointMappingId = expectedBinding.EndpointMappingId!.Value
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
                    // Never open error or redirect bodies, regardless of their status or declared length.
                    if (response.StatusCode != HttpStatusCode.OK ||
                        response.RequestMessage?.RequestUri != _destination || response.Content is null ||
                        response.Headers.CacheControl?.NoStore != true ||
                        response.Content.Headers.ContentLength is > MaximumResponseBytes ||
                        response.Content.Headers.ContentEncoding.Count != 0) Fail();
                    var contentType = response.Content.Headers.ContentType;
                    if (contentType is null ||
                        !string.Equals(contentType.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
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
                                var captured = bytes;
                                // Uncooperative reads may touch the captured buffer after timeout.
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
                    using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 6 });
                    result = Parse(document.RootElement, expectedIntent, expectedBinding);
                    Check();
                }
            }
            // Include response, stream and request disposal in the same exchange deadline.
            Check();
            return result;
        }
        catch (Exception)
        {
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            throw new TargetCreationExecutionClaimHttpException();
        }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static TargetCreationExecutionClaimGrant Parse(JsonElement root, TargetCreationIntent intent,
        TargetCreationPermitBinding binding)
    {
        var fields = Fields(root, ResponseNames);
        var claim = ReadGuid(fields["claimId"]);
        RequireGuid(fields["permitId"], binding.PermitId);
        RequireGuid(fields["operationId"], intent.OperationId);
        RequireGuid(fields["attemptId"], binding.AttemptId);
        RequireGuid(fields["agentId"], binding.AgentId);
        RequireGuid(fields["credentialId"], binding.CredentialId!.Value);
        RequireGuid(fields["endpointMappingId"], binding.EndpointMappingId!.Value);
        var issuedAt = Timestamp(fields["issuedAt"]);
        var expiresAt = Timestamp(fields["expiresAt"]);
        // The SQL server owns freshness. Never infer it from DateTimeOffset.UtcNow on this agent.
        if (issuedAt < binding.IssuedAt || issuedAt >= expiresAt || expiresAt != binding.StartBefore ||
            fields["newlyIssued"].ValueKind != JsonValueKind.True ||
            fields["executable"].ValueKind != JsonValueKind.True ||
            fields["scheduledJobsMustRemainBlocked"].ValueKind != JsonValueKind.True ||
            fields["retryAllowed"].ValueKind != JsonValueKind.False) Fail();
        return new(claim, intent, binding, issuedAt, expiresAt);
    }

    private static Dictionary<string, JsonElement> Fields(JsonElement root, string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) Fail();
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !fields.TryAdd(property.Name, property.Value)) Fail();
        if (fields.Count != names.Length) Fail();
        return fields;
    }

    private static Guid ReadGuid(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String) Fail();
        var text = element.GetString();
        if (text is null || text.Length != 36) Fail();
        if (!Guid.TryParseExact(text, "D", out var value) || value == Guid.Empty ||
            !string.Equals(text, value.ToString("D"), StringComparison.OrdinalIgnoreCase)) Fail();
        return value;
    }

    private static void RequireGuid(JsonElement element, Guid expected)
    {
        if (ReadGuid(element) != expected) Fail();
    }

    private static DateTimeOffset Timestamp(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String) Fail();
        var text = element.GetString();
        if (text is null) Fail();
        if (!element.TryGetDateTimeOffset(out var value) || value.Offset != TimeSpan.Zero ||
            !(text.EndsWith('Z') || text.EndsWith("+00:00", StringComparison.Ordinal))) Fail();
        return value;
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
    private static void InvalidOptions() => throw new TargetCreationExecutionClaimHttpException("InvalidTargetCreationExecutionClaimHttpOptions");
    [DoesNotReturn]
    private static void Fail() => throw new TargetCreationExecutionClaimHttpException();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _http.Dispose();
    }
}
