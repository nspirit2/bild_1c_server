using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BuildSystem.Agent;

public sealed class TargetCreationPermitHttpClientOptions
{
    public required Uri BaseUri { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
    public bool Enabled { get; init; }
    public override string ToString() => "TargetCreationPermitHttpClientOptions [NONEXECUTABLE]";
}

/// <summary>Historical correlated metadata, never fresh authority or proof of local persistence.</summary>
public sealed record TargetCreationPermitStartReceipt(TargetCreationIntent TargetIntent,
    TargetCreationPermitBinding ScopeBinding, bool NewlyRecorded, DateTimeOffset StartedAt)
{
    public Guid PermitId => ScopeBinding.PermitId;
    public Guid OperationId => ScopeBinding.OperationId;
    public Guid AttemptId => ScopeBinding.AttemptId;
    public long Revision => 2;
    public bool Executable => false;
    public bool FreshAuthority => false;
    public bool ScheduledJobsMustRemainBlocked => true;
    public bool RetryAllowed => false;
    public override string ToString() => "TargetCreationPermitStartReceipt [NONEXECUTABLE]";
}

public interface ITargetCreationPermitStartTransport
{
    Task<TargetCreationPermitStartReceipt> RecordStartAsync(TargetCreationIntent expectedIntent,
        TargetCreationPermitBinding expectedBinding, CancellationToken cancellationToken = default);
}

public sealed class TargetCreationPermitTransportException : Exception
{
    public TargetCreationPermitTransportException() : base("TargetCreationPermitTransportUnavailable") { }
    public string Code => "TargetCreationPermitTransportUnavailable";
    public override string ToString() => "TargetCreationPermitTransportUnavailable";
}

/// <summary>Isolated one-exchange metadata transport. No retries, journal writes, worker or execution.</summary>
public sealed class TargetCreationPermitHttpClient : ITargetCreationPermitStartTransport, IDisposable
{
    private const int MaximumResponseBytes = 32 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly HttpClient _http;
    private readonly Uri _destination;
    private readonly TimeSpan _timeout;
    private readonly bool _enabled;
    private readonly Func<string> _credentialProvider;
    private int _disposed;

    public TargetCreationPermitHttpClient(TargetCreationPermitHttpClientOptions options, Func<string> credentialProvider)
        : this(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ActivityHeadersPropagator = null
        }, options, credentialProvider) { }

    /// <summary>Owns the handler. This test seam is trusted: it must not redirect, change destinations,
    /// inject cookies/tracing, forward credentials, or retry. It is not a production authority seam.</summary>
    public TargetCreationPermitHttpClient(HttpMessageHandler trustedHandler,
        TargetCreationPermitHttpClientOptions options, Func<string> credentialProvider)
    {
        try
        {
            if (options is null || options.BaseUri is not { IsAbsoluteUri: true } uri ||
                uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host) ||
                uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                uri.UserInfo.Length != 0 || options.TimeoutSeconds is < 1 or > 60 ||
                trustedHandler is null || credentialProvider is null) Fail();
            _destination = new Uri(options!.BaseUri, "/api/agents/target-creation-permit-starts");
            _timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            _enabled = options.Enabled;
            _credentialProvider = credentialProvider!;
            _http = new HttpClient(trustedHandler!, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        }
        catch (Exception)
        {
            try { trustedHandler?.Dispose(); } catch (Exception) { }
            throw new TargetCreationPermitTransportException();
        }
    }

    public async Task<TargetCreationPermitStartReceipt> RecordStartAsync(TargetCreationIntent expectedIntent, TargetCreationPermitBinding expectedBinding,
        CancellationToken cancellationToken = default)
    {
        // Disabled is the first gate, even for a canceled or malformed call.
        if (!_enabled) throw new TargetCreationPermitTransportException();
        TargetCreationPermitStartReceipt? result = null;
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
            // Synthetic shape validates input only, not that any journal is initial or persisted.
            _ = TargetCreationLocalPreflight.ValidateInitial(new(expectedIntent, TargetCreationState.IntentRecorded, 1, null, null, null), expectedBinding);
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
                    permitId = expectedBinding.PermitId,
                    expectedPermitRevision = 1,
                    attemptId = expectedBinding.AttemptId
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
                    using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 6 });
                    result = Parse(document.RootElement, expectedIntent, expectedBinding);
                    Check();
                }
            }
            // Disposal failures and time spent parsing/disposal cannot escape a live holder.
            Check();
            return result!;
        }
        catch (Exception)
        {
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            throw new TargetCreationPermitTransportException();
        }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static TargetCreationPermitStartReceipt Parse(JsonElement root, TargetCreationIntent i, TargetCreationPermitBinding b)
    {
        var p = Fields(root, ["permitId", "operationId", "attemptId", "expectedOperationRevision", "issuedAt", "startBefore", "state", "revision", "startedAt", "startExpectedRevision", "newlyRecorded", "operation", "credentialId", "endpointMappingId", "executable", "scheduledJobsMustRemainBlocked"]);
        RequireGuid(p["permitId"], b.PermitId); RequireGuid(p["operationId"], i.OperationId);
        RequireGuid(p["attemptId"], b.AttemptId); RequireGuid(p["credentialId"], b.CredentialId!.Value);
        RequireGuid(p["endpointMappingId"], b.EndpointMappingId!.Value);
        Number(p["expectedOperationRevision"], "1"); Number(p["startExpectedRevision"], "1");
        Number(p["state"], "1"); Number(p["revision"], "2"); Safety(p);
        if (Timestamp(p["issuedAt"]) != b.IssuedAt || Timestamp(p["startBefore"]) != b.StartBefore) Fail();
        var started = Timestamp(p["startedAt"]);
        if (started < b.IssuedAt || started >= b.StartBefore) Fail();
        if (p["newlyRecorded"].ValueKind is not (JsonValueKind.True or JsonValueKind.False)) Fail();
        var o = Fields(p["operation"], ["operationId", "restoreIntentId", "environmentId", "stageJobId", "reservationId", "artifactId", "agentId", "targetIncarnationId", "canonicalClusterId", "canonicalSqlServerId", "infobaseName", "sqlDbName", "state", "revision", "attemptId", "attemptExpectedRevision", "quarantineReason", "newlyRecorded", "executable", "scheduledJobsMustRemainBlocked"]);
        foreach (var pair in new (string Name, Guid Id)[] {
            ("operationId", i.OperationId), ("restoreIntentId", b.RestoreIntentId), ("environmentId", i.EnvironmentId),
            ("stageJobId", i.JobId), ("reservationId", i.ReservationId), ("artifactId", b.ArtifactId), ("agentId", b.AgentId),
            ("targetIncarnationId", i.TargetIncarnationId), ("canonicalClusterId", i.CanonicalClusterId),
            ("canonicalSqlServerId", i.CanonicalSqlServerId), ("attemptId", b.AttemptId) }) RequireGuid(o[pair.Name], pair.Id);
        Number(o["state"], "1"); Number(o["revision"], "2"); Number(o["attemptExpectedRevision"], "1"); Safety(o);
        if (o["quarantineReason"].ValueKind != JsonValueKind.Null || o["newlyRecorded"].ValueKind != JsonValueKind.False ||
            o["infobaseName"].ValueKind != JsonValueKind.String || o["infobaseName"].GetString() != i.InfobaseName ||
            o["sqlDbName"].ValueKind != JsonValueKind.String || o["sqlDbName"].GetString() != i.SqlDatabaseName) Fail();
        return new(i, b, p["newlyRecorded"].GetBoolean(), started);
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
    private static void Number(JsonElement e, string value)
    {
        if (e.ValueKind != JsonValueKind.Number || e.GetRawText() != value) Fail();
    }
    private static void Safety(Dictionary<string, JsonElement> fields)
    {
        if (fields["executable"].ValueKind != JsonValueKind.False ||
            fields["scheduledJobsMustRemainBlocked"].ValueKind != JsonValueKind.True) Fail();
    }
    private static DateTimeOffset Timestamp(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.String) Fail();
        var text = e.GetString();
        if (text is null) Fail();
        if (!e.TryGetDateTimeOffset(out var value) || value.Offset != TimeSpan.Zero ||
            !(text.EndsWith('Z') || text.EndsWith("+00:00", StringComparison.Ordinal))) Fail();
        return value;
    }
    private static void RequireGuid(JsonElement element, Guid expected)
    {
        if (element.ValueKind != JsonValueKind.String) Fail();
        var text = element.GetString();
        if (text is null || text.Length != 36 || !Guid.TryParseExact(text, "D", out var value) ||
            value == Guid.Empty || value != expected ||
            !string.Equals(text, value.ToString("D"), StringComparison.OrdinalIgnoreCase)) Fail();
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
    private static void Fail() => throw new TargetCreationPermitTransportException();
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _http.Dispose();
    }
}
