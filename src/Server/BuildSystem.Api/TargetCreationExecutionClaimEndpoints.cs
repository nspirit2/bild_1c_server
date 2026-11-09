using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Infrastructure;

/// <summary>Independent default-off HTTP gate for one-shot machine execution claims.</summary>
public sealed class TargetCreationExecutionClaimHttpOptions
{
    public bool Enabled { get; init; }
    public override string ToString() => "TargetCreationExecutionClaimHttpOptions [REDACTED]";
}

public static class TargetCreationExecutionClaimEndpoints
{
    public const string Path = "/api/agents/target-creation-execution-claims";
    private const int MaxBodyBytes = 4096;
    private const string Unavailable = "TargetCreationExecutionClaimUnavailable";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapTargetCreationExecutionClaimEndpoints(this WebApplication app) =>
        app.MapPost(Path, ClaimAsync).AllowAnonymous().WithMetadata(new AgentMachineEndpoint());

    private static async Task ClaimAsync(HttpContext context)
    {
        byte[]? payload = null;
        try
        {
            if (!context.Request.IsHttps) throw new RequestRejectedException(400, "TargetCreationExecutionClaimHttpsRequired");
            if (context.Request.Headers.Origin.Count != 0) throw new RequestRejectedException(403, "TargetCreationExecutionClaimForbidden");
            if (!context.RequestServices.GetRequiredService<TargetCreationExecutionClaimHttpOptions>().Enabled ||
                !context.RequestServices.GetRequiredService<TargetCreationExecutionClaimOptions>().Enabled)
                throw new RequestRejectedException(503, Unavailable);
            if (context.Request.QueryString.HasValue) throw Invalid();
            var authorization = context.Request.Headers.Authorization;
            if (authorization.Count != 1 || authorization[0] is not { } value ||
                !value.StartsWith("Bearer ", StringComparison.Ordinal) || value.Length is < 8 or > 263 ||
                value[7..].Any(c => c < 0x21 || c > 0x7e || c == ','))
                throw new RequestRejectedException(401, "AgentUnauthorized");
            var input = await ReadAsync(context.Request, context.RequestAborted);
            var receipt = await context.RequestServices.GetRequiredService<ITargetCreationExecutionClaims>()
                .ClaimAsync(value[7..], input.PermitId, 2, input.AttemptId, input.CredentialId,
                    input.EndpointMappingId, context.RequestAborted);
            ValidateReceipt(receipt, input);
            // Dedicated bounded shape, serialized BEFORE response start, inside the protected boundary.
            payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                receipt.ClaimId, receipt.PermitId, receipt.OperationId, receipt.AttemptId, receipt.AgentId,
                receipt.CredentialId, receipt.EndpointMappingId, receipt.IssuedAt, receipt.ExpiresAt,
                receipt.NewlyIssued, receipt.Executable, receipt.ScheduledJobsMustRemainBlocked, receipt.RetryAllowed
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
                (400, "InvalidTargetCreationExecutionClaim") => (400, "InvalidTargetCreationExecutionClaim"),
                (400, "TargetCreationExecutionClaimHttpsRequired") => (400, "TargetCreationExecutionClaimHttpsRequired"),
                (413, "TargetCreationExecutionClaimBodyTooLarge") => (413, "TargetCreationExecutionClaimBodyTooLarge"),
                (401, "AgentUnauthorized") => (401, "AgentUnauthorized"),
                (403, "TargetCreationExecutionClaimForbidden") => (403, "TargetCreationExecutionClaimForbidden"),
                (403, "TargetCreationPermitForbidden") => (403, "TargetCreationPermitForbidden"),
                (409, "TargetCreationExecutionClaimAlreadyIssued") => (409, "TargetCreationExecutionClaimAlreadyIssued"),
                (409, "TargetCreationExecutionClaimUnavailable") => (409, Unavailable),
                (409, "TargetCreationCredentialUnavailable") => (409, "TargetCreationCredentialUnavailable"),
                _ => (503, Unavailable)
            };
            await ErrorAsync(context, status, code);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); }
        catch (Exception) { await ErrorAsync(context, 503, Unavailable); }
        finally { if (payload is not null) CryptographicOperations.ZeroMemory(payload); }
    }

    private static void ValidateReceipt(TargetCreationExecutionClaimReceipt receipt, Input input)
    {
        // SQL owns the issuance/deadline clock; API host skew must not discard a committed one-shot grant.
        if (receipt is null || receipt.ClaimId == Guid.Empty || receipt.OperationId == Guid.Empty ||
            receipt.AgentId == Guid.Empty || receipt.PermitId != input.PermitId || receipt.AttemptId != input.AttemptId ||
            receipt.CredentialId != input.CredentialId || receipt.EndpointMappingId != input.EndpointMappingId ||
            !receipt.NewlyIssued || !receipt.Executable || !receipt.ScheduledJobsMustRemainBlocked || receipt.RetryAllowed ||
            receipt.IssuedAt.Offset != TimeSpan.Zero || receipt.ExpiresAt.Offset != TimeSpan.Zero ||
            receipt.ExpiresAt <= receipt.IssuedAt ||
            receipt.ExpiresAt - receipt.IssuedAt > TimeSpan.FromMinutes(5))
            throw new InvalidOperationException();
    }

    public static bool IsClaimPath(PathString path) => path.StartsWithSegments(Path);

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

    private static async Task<Input> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasJsonContentType() || request.Headers.ContentEncoding.Count != 0 ||
            !Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType) ||
            mediaType.Parameters.Count(p => string.Equals(p.Name.Value, "charset", StringComparison.OrdinalIgnoreCase)) > 1 ||
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
            { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            Guid permit = default, attempt = default, credential = default, mapping = default;
            foreach (var property in root.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw Invalid();
                switch (property.Name)
                {
                    case "permitId": permit = ReadGuid(property.Value); break;
                    case "attemptId": attempt = ReadGuid(property.Value); break;
                    case "credentialId": credential = ReadGuid(property.Value); break;
                    case "endpointMappingId": mapping = ReadGuid(property.Value); break;
                    case "expectedPermitRevision":
                        if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt64(out var revision) ||
                            revision != 2 || property.Value.GetRawText() != 2.ToString(CultureInfo.InvariantCulture)) throw Invalid();
                        break;
                    default: throw Invalid();
                }
            }
            if (seen.Count != 5) throw Invalid();
            return new(permit, attempt, credential, mapping);
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
            !string.Equals(text, id.ToString("D"), StringComparison.OrdinalIgnoreCase)) throw Invalid();
        return id;
    }
    private static RequestRejectedException Invalid() => new(400, "InvalidTargetCreationExecutionClaim");
    private static RequestRejectedException TooLarge() => new(413, "TargetCreationExecutionClaimBodyTooLarge");
    private sealed record Input(Guid PermitId, Guid AttemptId, Guid CredentialId, Guid EndpointMappingId)
    {
        public override string ToString() => "TargetCreationExecutionClaimInput [REDACTED]";
    }
}
