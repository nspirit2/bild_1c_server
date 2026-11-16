using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Infrastructure;

/// <summary>Separate, default-off HTTP ingestion gate; never grants execution authority.</summary>
public sealed class TargetRegistrationEvidenceEndpointOptions
{
    public bool Enabled { get; init; }
    public override string ToString() => "TargetRegistrationEvidenceEndpointOptions [REDACTED]";
}

public static class TargetRegistrationEvidenceEndpoints
{
    public const string Path = "/api/agent/target-creation/registration-evidence";
    public const int MaxBodyBytes = 8192;
    private const string Unavailable = "TargetRegistrationEvidenceReceiptUnavailable";
    private const string Disabled = "TargetRegistrationEvidenceReceiptDisabled";
    private const string InvalidCode = "InvalidTargetRegistrationEvidenceReceipt";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapTargetRegistrationEvidenceEndpoints(this WebApplication app) =>
        app.MapPost(Path, ReportAsync).AllowAnonymous().WithMetadata(new AgentMachineEndpoint());

    public static bool IsEvidencePath(PathString path) => path.StartsWithSegments(Path);

    public static void SetHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers.XContentTypeOptions = "nosniff";
    }

    public static async Task ErrorAsync(HttpContext context, int status, string code)
    {
        try
        {
            if (context.Response.HasStarted) { context.Abort(); return; }
            context.Response.Clear();
            SetHeaders(context.Response);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync("{\"code\":\"" + code + "\"}", context.RequestAborted);
        }
        catch (Exception) { context.Abort(); }
    }

    private static async Task ReportAsync(HttpContext context)
    {
        byte[]? payload = null;
        try
        {
            if (!context.Request.IsHttps) throw new RequestRejectedException(400, "TargetRegistrationEvidenceReceiptHttpsRequired");
            if (context.Request.Headers.Origin.Count != 0)
                throw new RequestRejectedException(403, "TargetRegistrationEvidenceReceiptForbidden");
            if (!context.RequestServices.GetRequiredService<TargetRegistrationEvidenceEndpointOptions>().Enabled ||
                !context.RequestServices.GetRequiredService<TargetRegistrationEvidenceReceiptOptions>().Enabled)
                throw new RequestRejectedException(409, Disabled);
            if (context.Request.QueryString.HasValue) throw Invalid();
            if (context.Request.Headers.Cookie.Count != 0)
                throw new RequestRejectedException(401, "AgentUnauthorized");
            var authorization = context.Request.Headers.Authorization;
            if (authorization.Count != 1 || authorization[0] is not { } value ||
                !value.StartsWith("Bearer ", StringComparison.Ordinal) || value.Length is < 8 or > 263 ||
                value[7..].Any(c => c < 0x21 || c > 0x7e || c == ','))
                throw new RequestRejectedException(401, "AgentUnauthorized");
            var report = await ReadAsync(context.Request, context.RequestAborted);
            var receipt = await context.RequestServices.GetRequiredService<ITargetRegistrationEvidenceReceipts>()
                .ReportAsync(value[7..], report, context.RequestAborted);
            if (receipt is null || receipt.ClaimId != report.ClaimId || receipt.OperationId == Guid.Empty ||
                receipt.Status != "Recorded" || receipt.AcceptedAt == default || receipt.AcceptedAt.Offset != TimeSpan.Zero ||
                receipt.Executable || receipt.OwnershipEstablished || receipt.RestoreAllowed || receipt.RetryAllowed ||
                receipt.FreshAuthority || !receipt.RequiresIndependentVerification || !receipt.ScheduledJobsMustRemainBlocked)
                throw new InvalidOperationException();
            payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                receipt.ClaimId, receipt.OperationId, receipt.Status, receipt.AcceptedAt,
                receipt.Executable, receipt.OwnershipEstablished, receipt.RestoreAllowed, receipt.RetryAllowed,
                receipt.FreshAuthority, receipt.RequiresIndependentVerification, receipt.ScheduledJobsMustRemainBlocked
            }, JsonOptions);
            if (payload.Length > 2048) throw new InvalidOperationException();
            SetHeaders(context.Response);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength = payload.Length;
            await context.Response.Body.WriteAsync(payload, context.RequestAborted);
        }
        catch (RequestRejectedException ex)
        {
            var (status, code) = (ex.StatusCode, ex.Code) switch
            {
                (400, InvalidCode) => (400, InvalidCode),
                (400, "TargetRegistrationEvidenceReceiptHttpsRequired") => (400, "TargetRegistrationEvidenceReceiptHttpsRequired"),
                (413, "TargetRegistrationEvidenceReceiptBodyTooLarge") => (413, "TargetRegistrationEvidenceReceiptBodyTooLarge"),
                (401, "AgentUnauthorized") => (401, "AgentUnauthorized"),
                (403, "TargetRegistrationEvidenceReceiptForbidden") => (403, "TargetRegistrationEvidenceReceiptForbidden"),
                (409, Disabled) => (409, Disabled),
                (409, "TargetRegistrationEvidenceReceiptMismatch") => (409, "TargetRegistrationEvidenceReceiptMismatch"),
                _ => (503, Unavailable)
            };
            await ErrorAsync(context, status, code);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); }
        catch (Exception) { await ErrorAsync(context, 503, Unavailable); }
        finally { if (payload is not null) CryptographicOperations.ZeroMemory(payload); }
    }

    private static async Task<ReportTargetRegistrationEvidence> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.Headers.ContentEncoding.Count != 0 ||
            !Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType) ||
            !string.Equals(mediaType.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Parameters.Count > 1 ||
            mediaType.Parameters.Any(p => !string.Equals(p.Name.Value, "charset", StringComparison.OrdinalIgnoreCase)) ||
            mediaType.Charset.HasValue && !string.Equals(mediaType.Charset.Value?.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase))
            throw Invalid();
        if (request.ContentLength > MaxBodyBytes) throw TooLarge();
        var buffer = new byte[MaxBodyBytes + 1];
        try
        {
            var count = 0;
            while (count < buffer.Length)
            {
                var read = await request.Body.ReadAsync(buffer.AsMemory(count, buffer.Length - count), ct);
                if (read == 0) break;
                count += read;
            }
            if (count > MaxBodyBytes) throw TooLarge();
            if (count == 0) throw Invalid();
            _ = StrictUtf8.GetCharCount(buffer, 0, count);
            using var document = JsonDocument.Parse(buffer.AsMemory(0, count), new JsonDocumentOptions
            { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 2 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            Guid claim = default, infobaseId = default, racObservation = default, sqlObservation = default,
                racMapping = default, sqlMapping = default;
            string? profile = null, name = null, server = null, database = null, date = null, state = null;
            var databaseId = 0;
            foreach (var property in root.EnumerateObject())
            {
                if (!seen.Add(property.Name) || seen.Count > 13) throw Invalid();
                switch (property.Name)
                {
                    case "claimId": claim = ReadGuid(property.Value); break;
                    case "reportedInfobaseId": infobaseId = ReadGuid(property.Value); break;
                    case "racObservationId": racObservation = ReadGuid(property.Value); break;
                    case "sqlObservationId": sqlObservation = ReadGuid(property.Value); break;
                    case "racEndpointMappingId": racMapping = ReadGuid(property.Value); break;
                    case "sqlEndpointMappingId": sqlMapping = ReadGuid(property.Value); break;
                    case "registrationProfileId": profile = ReadString(property.Value, 64); break;
                    case "infobaseName": name = ReadString(property.Value, 128); break;
                    case "sqlServer": server = ReadString(property.Value, 255); break;
                    case "databaseName": database = ReadString(property.Value, 128); break;
                    case "sqlDatabaseId":
                        if (property.Value.ValueKind != JsonValueKind.Number ||
                            !property.Value.TryGetInt32(out databaseId) || databaseId <= 0 ||
                            property.Value.GetRawText() != databaseId.ToString(CultureInfo.InvariantCulture)) throw Invalid();
                        break;
                    case "sqlCreateDate": date = ReadString(property.Value, 33); break;
                    case "sqlState": state = ReadString(property.Value, 16); break;
                    default: throw Invalid();
                }
            }
            if (seen.Count != 13 || racObservation == sqlObservation ||
                profile != "rac-registration-8.3.22.2557-v1" || state != "ONLINE" ||
                !ValidName(name) || !ValidName(database) ||
                server is not { Length: >= 1 and <= 255 } || server[0] == '-' ||
                !server.All(c => c is >= '!' and <= '~') || !ValidCanonicalSqlDate(date)) throw Invalid();
            return new(claim, infobaseId, racObservation, sqlObservation, racMapping, sqlMapping,
                profile, name!, server, database!, databaseId, date!, state);
        }
        catch (BadHttpRequestException ex) { throw ex.StatusCode == 413 ? TooLarge() : Invalid(); }
        catch (JsonException) { throw Invalid(); }
        catch (DecoderFallbackException) { throw Invalid(); }
        catch (InvalidOperationException) { throw Invalid(); }
        catch (IOException) { throw Invalid(); }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static Guid ReadGuid(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) throw Invalid();
        var text = value.GetString();
        if (text is null || text.Length != 36 || !Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty ||
            !string.Equals(text, id.ToString("D"), StringComparison.Ordinal)) throw Invalid();
        return id;
    }

    private static string ReadString(JsonElement value, int maxLength)
    {
        if (value.ValueKind != JsonValueKind.String) throw Invalid();
        var text = value.GetString();
        if (text is null || text.Length is < 1 or > 255 || text.Length > maxLength || text.Any(char.IsControl)) throw Invalid();
        return text;
    }

    private static bool ValidName(string? text) => text is { Length: >= 1 and <= 128 } &&
        !string.IsNullOrWhiteSpace(text) && !char.IsWhiteSpace(text[0]) &&
        !char.IsWhiteSpace(text[^1]) && !text.Any(char.IsControl);

    private static bool ValidCanonicalSqlDate(string? text)
    {
        if (text is null || text.Length is not (27 or 28 or 33)) return false;
        const string coreFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff";
        var core = text[..27];
        if (!DateTime.TryParseExact(core, coreFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
            date == default || date.ToString(coreFormat, CultureInfo.InvariantCulture) != core) return false;
        if (text.Length == 27) return true;
        if (text.Length == 28) return text[27] == 'Z';
        return DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var offsetDate) &&
            offsetDate.ToString("O", CultureInfo.InvariantCulture) == text;
    }

    private static RequestRejectedException Invalid() => new(400, InvalidCode);
    private static RequestRejectedException TooLarge() => new(413, "TargetRegistrationEvidenceReceiptBodyTooLarge");
}
