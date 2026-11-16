using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildSystem.Agent.Execution;

namespace BuildSystem.Agent;

public sealed class TargetRegistrationEvidenceHttpClientOptions
{
    public bool Enabled { get; init; }
    public Uri? BaseUri { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
    public override string ToString() => "TargetRegistrationEvidenceHttpClientOptions [REDACTED]";
}

/// <summary>No provider text, destination, credentials, response or inner exception.</summary>
public sealed class TargetRegistrationEvidenceTransportException : Exception
{
    public TargetRegistrationEvidenceTransportException() : base("TargetRegistrationEvidenceTransportUnavailable") { }
    public string Code => "TargetRegistrationEvidenceTransportUnavailable";
    public override string ToString() => Code;
}

/// <summary>One explicit historical report exchange; no retry, execution or authority inference.</summary>
public sealed class TargetRegistrationEvidenceHttpClient : ITargetRegistrationEvidenceTransport, IDisposable
{
    private const int MaximumResponseBytes = 2048;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly string[] ReceiptNames = ["claimId", "operationId", "status", "acceptedAt",
        "executable", "ownershipEstablished", "restoreAllowed", "retryAllowed", "freshAuthority",
        "requiresIndependentVerification", "scheduledJobsMustRemainBlocked"];
    private readonly HttpClient _http;
    private readonly Uri _destination;
    private readonly bool _enabled;
    private readonly TimeSpan _timeout;
    private readonly Func<string> _credentialProvider;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    public TargetRegistrationEvidenceHttpClient(TargetRegistrationEvidenceHttpClientOptions options,
        Func<string> credentialProvider) : this(CreateHandler(options, credentialProvider), options, credentialProvider) { }

    /// <summary>Ownership transfers only after valid option/reference snapshots. This trusted seam must
    /// honor cancellation, avoid redirects/retries, and never inject cookies, tracing or destinations.
    /// Cancellation is cooperative: arbitrary trusted handler/stream code cannot be forcibly preempted.</summary>
    public TargetRegistrationEvidenceHttpClient(HttpMessageHandler trustedHandler,
        TargetRegistrationEvidenceHttpClientOptions options, Func<string> credentialProvider, TimeProvider? clock = null)
    {
        ValidateOptions(options, credentialProvider);
        if (trustedHandler is null) Fail();
        _enabled = options.Enabled;
        _destination = _enabled ? new Uri(options.BaseUri!, "/api/agent/target-creation/registration-evidence")
            : new Uri("https://localhost/api/agent/target-creation/registration-evidence");
        _timeout = TimeSpan.FromSeconds(_enabled ? options.TimeoutSeconds : 30);
        _credentialProvider = credentialProvider;
        _clock = clock ?? TimeProvider.System;
        try { _http = new HttpClient(trustedHandler, true) { Timeout = Timeout.InfiniteTimeSpan }; }
        catch (Exception) { try { trustedHandler.Dispose(); } catch (Exception) { } Fail(); }
    }

    private static HttpMessageHandler CreateHandler(TargetRegistrationEvidenceHttpClientOptions options, Func<string> provider)
    {
        ValidateOptions(options, provider);
        try { return new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None, ActivityHeadersPropagator = null }; }
        catch (Exception) { throw new TargetRegistrationEvidenceTransportException(); }
    }

    private static void ValidateOptions(TargetRegistrationEvidenceHttpClientOptions options, Func<string> provider)
    {
        if (options is null || provider is null) Fail();
        if (options.Enabled && (options.TimeoutSeconds is < 1 or > 120 ||
            options.BaseUri is not { IsAbsoluteUri: true } uri || uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(uri.Host) || uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)) Fail();
    }

    public async Task<TargetRegistrationEvidenceDeliveryReceipt> ReportAsync(TargetRegistrationEvidenceReport report,
        CancellationToken cancellationToken = default)
    {
        if (!_enabled) Fail(); // Before provider, report access, cancellation or HTTP.
        byte[]? requestBytes = null, responseBytes = null;
        try
        {
            var started = _clock.GetTimestamp();
            var previous = started;
            void CheckClock()
            {
                cancellationToken.ThrowIfCancellationRequested();
                var now = _clock.GetTimestamp();
                var elapsed = _clock.GetElapsedTime(started, now);
                if (Volatile.Read(ref _disposed) != 0 || now < previous || elapsed < TimeSpan.Zero || elapsed >= _timeout) Fail();
                previous = now;
            }
            TargetRegistrationEvidenceDeliveryReceipt result;
            using (var timer = new CancellationTokenSource(_timeout, _clock))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token, _lifetime.Token))
            {
            var token = linked.Token;
            void Check() { token.ThrowIfCancellationRequested(); CheckClock(); }
            Check();
            ValidateReport(report);
            requestBytes = JsonSerializer.SerializeToUtf8Bytes(report, WebJson);
            if (requestBytes.Length > 8192) Fail();
            Check();
            var credential = _credentialProvider(); // Exactly one lookup; never cached.
            Check();
            if (string.IsNullOrEmpty(credential) || credential.Length > 256 ||
                credential.StartsWith("Bearer", StringComparison.OrdinalIgnoreCase) ||
                credential.Any(c => c < '!' || c > '~' || c is ',' or ';')) Fail();
            using (var request = new HttpRequestMessage(HttpMethod.Post, _destination))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Content = new ByteArrayContent(requestBytes);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                {
                    Check();
                    // Never open error or redirect bodies.
                    if (response.StatusCode != HttpStatusCode.OK || response.RequestMessage?.RequestUri != _destination ||
                        response.Content is null || response.Headers.CacheControl?.NoStore != true ||
                        !response.Headers.Pragma.Any(p => string.Equals(p.Name, "no-cache", StringComparison.OrdinalIgnoreCase) && p.Value is null) ||
                        response.Headers.Contains("Set-Cookie") || response.Content.Headers.ContentLength is > MaximumResponseBytes ||
                        response.Content.Headers.ContentEncoding.Count != 0) Fail();
                    var type = response.Content.Headers.ContentType;
                    if (type is null || !string.Equals(type.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
                        type.Parameters.Count > 1 || type.Parameters.Any(p => !string.Equals(p.Name, "charset", StringComparison.OrdinalIgnoreCase)) ||
                        (type.CharSet is not null && !string.Equals(type.CharSet, "utf-8", StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(type.CharSet, "\"utf-8\"", StringComparison.OrdinalIgnoreCase))) Fail();
                    responseBytes = new byte[MaximumResponseBytes + 1];
                    var count = 0;
                    using (var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                    {
                        Check();
                        while (count < responseBytes.Length)
                        {
                            var read = await stream.ReadAsync(responseBytes.AsMemory(count), token).ConfigureAwait(false);
                            Check();
                            if (read == 0) break;
                            count += read;
                        }
                        if (count > MaximumResponseBytes) Fail();
                    }
                    Check();
                    _ = StrictUtf8.GetCharCount(responseBytes, 0, count);
                    using var document = JsonDocument.Parse(responseBytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 2 });
                    result = ParseReceipt(document.RootElement, report.ClaimId);
                    Check();
                }
            }
            // All stream, response and request disposal is inside the exchange budget.
            CryptographicOperations.ZeroMemory(requestBytes);
            CryptographicOperations.ZeroMemory(responseBytes!);
            Check();
            }
            // Timer/link disposal can execute trusted clock callbacks; budget includes that cleanup too.
            CheckClock();
            return result;
        }
        catch (Exception) { throw new TargetRegistrationEvidenceTransportException(); }
        finally
        {
            if (requestBytes is not null) CryptographicOperations.ZeroMemory(requestBytes);
            if (responseBytes is not null) CryptographicOperations.ZeroMemory(responseBytes);
        }
    }

    private static void ValidateReport(TargetRegistrationEvidenceReport report)
    {
        if (report is null || new[] { report.ClaimId, report.ReportedInfobaseId, report.RacObservationId,
            report.SqlObservationId, report.RacEndpointMappingId, report.SqlEndpointMappingId }.Any(x => x == Guid.Empty) ||
            report.RacObservationId == report.SqlObservationId ||
            report.RegistrationProfileId != InfobaseRegistrationReadback.ObservedProfileId ||
            !Name(report.InfobaseName) || !Name(report.DatabaseName) ||
            report.SqlServer is not { Length: > 0 and <= 255 } || report.SqlServer[0] == '-' ||
            report.SqlServer.Any(c => c < '!' || c > '~') || report.SqlDatabaseId <= 0 ||
            report.SqlState != "ONLINE" || !SqlDate(report.SqlCreateDate)) Fail();
    }

    private static bool Name(string value) => value is { Length: > 0 and <= 128 } &&
        !char.IsWhiteSpace(value[0]) && !char.IsWhiteSpace(value[^1]) && !value.Any(char.IsControl);

    // Validate the local SQL DateTime representation without converting an offset through this host's zone.
    private static bool SqlDate(string text)
    {
        if (text is null || text.Length is not (27 or 28 or 33) ||
            !DateTime.TryParseExact(text[..27], "yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date) || date == default) return false;
        if (text.Length == 27) return true;
        if (text.Length == 28) return text[27] == 'Z';
        return DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var offsetDate) &&
            text == offsetDate.ToString("O", CultureInfo.InvariantCulture);
    }

    private static TargetRegistrationEvidenceDeliveryReceipt ParseReceipt(JsonElement root, Guid claimId)
    {
        if (root.ValueKind != JsonValueKind.Object) Fail();
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in root.EnumerateObject())
            if (!ReceiptNames.Contains(field.Name, StringComparer.Ordinal) || !fields.TryAdd(field.Name, field.Value)) Fail();
        if (fields.Count != ReceiptNames.Length) Fail();
        var claim = GuidValue(fields["claimId"]);
        var operation = GuidValue(fields["operationId"]); // Report contains no operation pin; delivery layer checks it.
        if (claim != claimId || fields["status"].ValueKind != JsonValueKind.String || fields["status"].GetString() != "Recorded") Fail();
        foreach (var name in ReceiptNames.Skip(4))
            if (fields[name].ValueKind != (name is "requiresIndependentVerification" or "scheduledJobsMustRemainBlocked"
                ? JsonValueKind.True : JsonValueKind.False)) Fail();
        var timestamp = fields["acceptedAt"];
        if (timestamp.ValueKind != JsonValueKind.String) Fail();
        var text = timestamp.GetString()!;
        DateTimeOffset accepted = default;
        var suffixLength = text.EndsWith('Z') ? 1 : 6;
        var dateLength = text.Length - suffixLength;
        if (text.Length > 40 || !(text.EndsWith("+00:00", StringComparison.Ordinal) || text.EndsWith('Z')) ||
            !(dateLength == 19 || (dateLength is >= 21 and <= 27 && text[19] == '.')) ||
            !timestamp.TryGetDateTimeOffset(out accepted) || accepted == default || accepted.Offset != TimeSpan.Zero) Fail();
        return new(claim, operation, "Recorded", accepted);
    }

    private static Guid GuidValue(JsonElement field)
    {
        if (field.ValueKind != JsonValueKind.String) Fail();
        var text = field.GetString();
        if (!Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty || text != id.ToString("D")) Fail();
        return id;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _lifetime.Cancel(); } catch (Exception) { }
        try { _http.Dispose(); } catch (Exception) { }
        try { _lifetime.Dispose(); } catch (Exception) { }
    }

    [DoesNotReturn]
    private static void Fail() => throw new TargetRegistrationEvidenceTransportException();
}
