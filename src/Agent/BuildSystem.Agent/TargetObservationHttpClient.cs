using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using BuildSystem.Contracts;

namespace BuildSystem.Agent;

public interface ITargetObservationTransport
{
    Task<TargetObservationAssignment> ReadAsync(Guid observationRequestId, CancellationToken cancellationToken);
    Task<TargetObservationReceipt> ReportAsync(ReportTargetObservation report, CancellationToken cancellationToken);
}

public sealed class TargetObservationHttpClientOptions
{
    public required Uri BaseUri { get; init; }
    public required Guid AgentId { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
}

/// <summary>Deliberately contains no remote body, URL, credential or inner exception.</summary>
public sealed class TargetObservationTransportException : Exception
{
    public TargetObservationTransportException() : base("Target observation transport failed.") { }
}

/// <summary>Bounded observation-only transport. No worker registration, ambient cookies or redirects.</summary>
public sealed class TargetObservationHttpClient : ITargetObservationTransport, IDisposable
{
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };
    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly Guid _agentId;
    private readonly TimeSpan _timeout;
    private readonly Func<string> _credentialProvider;
    private int _disposed;

    public TargetObservationHttpClient(TargetObservationHttpClientOptions options, Func<string> credentialProvider)
        : this(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None
        }, options, credentialProvider) { }

    /// <summary>The handler is owned and must be trusted: it must not redirect, inject cookies,
    /// change destinations or forward credentials. Intended for isolated transport tests.</summary>
    public TargetObservationHttpClient(HttpMessageHandler trustedHandler,
        TargetObservationHttpClientOptions options, Func<string> credentialProvider)
    {
        if (options is null || options.BaseUri is not { IsAbsoluteUri: true } uri ||
            uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host) ||
            uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.UserInfo.Length != 0 || options.AgentId == Guid.Empty ||
            options.TimeoutSeconds is < 1 or > 60 || trustedHandler is null || credentialProvider is null)
            throw new ArgumentException("Invalid target observation transport configuration.");
        _baseUri = new Uri(uri.AbsoluteUri);
        _agentId = options.AgentId;
        _timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        _credentialProvider = credentialProvider;
        _http = new HttpClient(trustedHandler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public Task<TargetObservationAssignment> ReadAsync(Guid observationRequestId, CancellationToken cancellationToken) =>
        ExchangeAsync(HttpMethod.Get, "api/agents/target-observations/" + observationRequestId.ToString("D"),
            null, observationRequestId, cancellationToken, root =>
            {
                RequireSafety(root);
                var assignment = root.Deserialize<TargetObservationAssignment>(JsonOptions);
                ValidateAssignment(assignment);
                if (assignment!.ObservationRequestId != observationRequestId) Fail();
                RequireComputedName(root, "infobaseName", assignment.InfobaseName);
                RequireComputedName(root, "sqlDatabaseName", assignment.SqlDatabaseName);
                return assignment;
            });

    public Task<TargetObservationReceipt> ReportAsync(ReportTargetObservation report, CancellationToken cancellationToken) =>
        ExchangeAsync(HttpMethod.Post, "api/agents/target-observation-receipts", report,
            report?.Assignment?.ObservationRequestId ?? Guid.Empty, cancellationToken, root =>
            {
                RequireSafety(root);
                var receipt = root.Deserialize<TargetObservationReceipt>(JsonOptions);
                if (receipt is null || receipt.ReceiptId != report!.ReceiptId ||
                    receipt.ObservationRequestId != report.Assignment.ObservationRequestId ||
                    !string.Equals(receipt.State, "Recorded", StringComparison.Ordinal)) Fail();
                return receipt!;
            });

    private async Task<T> ExchangeAsync<T>(HttpMethod method, string route, ReportTargetObservation? report,
        Guid requestId, CancellationToken callerToken, Func<JsonElement, T> parse)
    {
        var clock = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        deadline.CancelAfter(_timeout);
        var token = deadline.Token;
        void Check()
        {
            token.ThrowIfCancellationRequested();
            if (clock.Elapsed >= _timeout || Volatile.Read(ref _disposed) != 0) Fail();
        }
        try
        {
            Check();
            if (requestId == Guid.Empty) Fail();
            if (method == HttpMethod.Post) ValidateReport(report);
            var credential = _credentialProvider();
            Check();
            if (string.IsNullOrEmpty(credential) || credential.Length > 256 ||
                credential.StartsWith("Bearer", StringComparison.OrdinalIgnoreCase) ||
                credential.Any(c => c < '!' || c > '~')) Fail();
            var destination = new Uri(_baseUri, route);
            T result;
            using (var request = new HttpRequestMessage(method, destination))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
                if (report is not null)
                    request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions));
                if (request.Content is not null)
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                var send = _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                HttpResponseMessage response;
                try { response = await send.WaitAsync(token).ConfigureAwait(false); }
                catch
                {
                    // A trusted test handler may ignore cancellation; do not retain its eventual response.
                    ObserveLateCompletion(send, response => response.Dispose());
                    throw;
                }
                using (response)
                {
                    Check();
                    if (response.StatusCode != HttpStatusCode.OK ||
                        response.RequestMessage?.RequestUri != destination ||
                        response.Content.Headers.ContentLength is > MaximumResponseBytes) Fail();
                    var bytes = new byte[MaximumResponseBytes + 1];
                    var count = 0;
                    await using (var stream = await OpenStreamAsync(response.Content, token).ConfigureAwait(false))
                    {
                        Check();
                        while (count < bytes.Length)
                        {
                            var read = await stream.ReadAsync(bytes.AsMemory(count, Math.Min(4096, bytes.Length - count)), token)
                                .AsTask().WaitAsync(token).ConfigureAwait(false);
                            Check();
                            if (read == 0) break;
                            count += read;
                        }
                        if (count > MaximumResponseBytes) Fail();
                    }
                    Check();
                    using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 32 });
                    RejectDuplicates(document.RootElement);
                    result = parse(document.RootElement);
                    Check();
                }
            }
            // Includes synchronous disposal and JSON processing in the monotonic bound.
            Check();
            return result;
        }
        catch (Exception)
        {
            if (callerToken.IsCancellationRequested)
                throw new OperationCanceledException("Target observation request canceled.", callerToken);
            throw new TargetObservationTransportException();
        }
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

    private static void ObserveLateCompletion<T>(Task<T> task, Action<T> dispose)
    {
        _ = task.ContinueWith(completed =>
        {
            // Cleanup must neither leak resources nor publish faults containing remote data.
            try
            {
                if (completed.IsCompletedSuccessfully) dispose(completed.Result);
                else if (completed.IsFaulted) _ = completed.Exception;
            }
            catch (Exception) { }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void ValidateAssignment(TargetObservationAssignment? assignment)
    {
        if (assignment is null || assignment.AgentId != _agentId || assignment.FencingToken <= 0 ||
            new[] { assignment.ObservationRequestId, assignment.RestoreIntentId, assignment.AgentId,
                assignment.OperationId, assignment.EnvironmentId, assignment.StageJobId, assignment.ReservationId,
                assignment.TargetIncarnationId, assignment.CanonicalClusterId, assignment.CanonicalSqlServerId,
                assignment.RacEndpointMappingId, assignment.SqlEndpointMappingId }.Any(id => id == Guid.Empty)) Fail();
    }

    private void ValidateReport(ReportTargetObservation? report)
    {
        if (report is null) Fail();
        ValidateAssignment(report!.Assignment);
        if (report.ReceiptId == Guid.Empty || report.RacObservationId == Guid.Empty ||
            report.SqlObservationId == Guid.Empty || report.RacObservationId == report.SqlObservationId ||
            !Enum.IsDefined(report.Outcome) || report.ObservedAt == default) Fail();
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name)) Fail();
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicates(child);
    }

    private static void RequireSafety(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            Property(root, "executable") is not { ValueKind: JsonValueKind.False } ||
            Property(root, "scheduledJobsMustRemainBlocked") is not { ValueKind: JsonValueKind.True }) Fail();
    }

    private static void RequireComputedName(JsonElement root, string name, string expected)
    {
        var value = Property(root, name);
        if (value is not null && (value.Value.ValueKind != JsonValueKind.String || value.Value.GetString() != expected)) Fail();
    }

    private static JsonElement? Property(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }

    private static void Fail() => throw new TargetObservationTransportException();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _http.Dispose();
    }
}
