using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Infrastructure;

/// <summary>Independent default-off machine delivery of one pinned, nonexecuting credential set.</summary>
public sealed class TargetCreationCredentialDeliveryOptions
{
    public bool Enabled { get; init; }
    public override string ToString() => "TargetCreationCredentialDeliveryOptions [NONEXECUTABLE]";
}

internal static class TargetCreationCredentialDeliveryEndpoints
{
    internal const string Path = "/api/agents/target-creation-credentials/resolve";
    private const int MaxBodyBytes = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void MapTargetCreationCredentialDelivery(this WebApplication app)
    {
        // No body binder or output DTO: default binders and exception pages may echo secret data.
        // The marker applies to this route only, not to arbitrary Bearer headers or path prefixes.
        app.MapPost(Path, DeliverAsync).AllowAnonymous().WithMetadata(new AgentMachineEndpoint());
    }

    private static async Task DeliverAsync(HttpContext context)
    {
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers.XContentTypeOptions = "nosniff";
        byte[]? payload = null;
        try
        {
            if (!context.Request.IsHttps)
            {
                await ErrorAsync(context, 400, "TargetCreationCredentialHttpsRequired");
                return;
            }
            if (context.Request.Headers.Origin.Count != 0)
            {
                await ErrorAsync(context, 403, "TargetCreationCredentialDeliveryForbidden");
                return;
            }
            if (!context.RequestServices.GetRequiredService<TargetCreationCredentialDeliveryOptions>().Enabled)
            {
                await ErrorAsync(context, 503, "TargetCreationCredentialDeliveryUnavailable");
                return;
            }
            var authorization = context.Request.Headers.Authorization;
            if (authorization.Count != 1 || authorization[0] is not { } value ||
                !value.StartsWith("Bearer ", StringComparison.Ordinal) || value.Length is < 8 or > 263 ||
                value[7..].Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            {
                await ErrorAsync(context, 401, "AgentUnauthorized");
                return;
            }
            var input = await ReadAsync(context.Request, context.RequestAborted);
            using var resolved = await context.RequestServices.GetRequiredService<ITargetCreationCredentialResolver>()
                .ResolveAsync(value[7..], input.PermitId, input.ExpectedPermitRevision,
                    input.AttemptId, input.CredentialId, input.EndpointMappingId, context.RequestAborted);
            // Construct a dedicated response, never serialize the deny-all resolver wrapper. A repeated
            // request is a fresh SQL/auth/permit/credential check, not an execution or CREATE retry.
            var secrets = resolved.ReadSecrets();
            payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                permitId = input.PermitId,
                expectedPermitRevision = input.ExpectedPermitRevision,
                attemptId = input.AttemptId,
                credentialId = input.CredentialId,
                endpointMappingId = input.EndpointMappingId,
                clusterUser = secrets.ClusterUser,
                clusterPassword = secrets.ClusterPassword,
                sqlUser = secrets.SqlUser,
                sqlPassword = secrets.SqlPassword,
                executable = false,
                scheduledJobsMustRemainBlocked = true,
                retryAllowed = false
            });
            if (payload.Length > 128 * 1024)
            {
                await ErrorAsync(context, 503, "TargetCreationCredentialDeliveryUnavailable");
                return;
            }
            response.StatusCode = 200;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength = payload.Length;
            await response.Body.WriteAsync(payload, context.RequestAborted);
        }
        catch (RequestRejectedException ex)
        {
            // Only fixed resolver codes are public. No exception message, inner exception or injected
            // arbitrary service code may become an HTTP diagnostic.
            var (status, code) = (ex.StatusCode, ex.Code) switch
            {
                (401, "AgentUnauthorized") => (401, "AgentUnauthorized"),
                (403, "TargetCreationPermitForbidden") => (403, "TargetCreationPermitForbidden"),
                (409, "TargetCreationCredentialUnavailable") => (409, "TargetCreationCredentialUnavailable"),
                (409, "TargetCreationMismatch") => (409, "TargetCreationCredentialUnavailable"),
                (409, "TargetCreationPermitMismatch") => (409, "TargetCreationCredentialUnavailable"),
                (409, "TargetCreationCredentialResolutionDisabled") => (503, "TargetCreationCredentialDeliveryUnavailable"),
                (503, "TargetCreationCredentialResolutionUnavailable") => (503, "TargetCreationCredentialDeliveryUnavailable"),
                (400, "InvalidTargetCreationCredentialDelivery") => (400, "InvalidTargetCreationCredentialDelivery"),
                (413, "TargetCreationCredentialDeliveryBodyTooLarge") => (413, "TargetCreationCredentialDeliveryBodyTooLarge"),
                _ => (503, "TargetCreationCredentialDeliveryUnavailable")
            };
            await ErrorAsync(context, status, code);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client cancellation cannot be delivered; do not emit or log plaintext.
            context.Abort();
        }
        catch (Exception)
        {
            await ErrorAsync(context, 503, "TargetCreationCredentialDeliveryUnavailable");
        }
        finally
        {
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
        }
    }

    internal static async Task ErrorAsync(HttpContext context, int status, string code)
    {
        try
        {
            if (context.Response.HasStarted)
            {
                context.Abort();
                return;
            }
            context.Response.Clear();
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json; charset=utf-8";
            // Codes originate solely from the literal allowlist above or this class's fixed rejections.
            await context.Response.WriteAsync("{\"code\":\"" + code + "\"}", context.RequestAborted);
        }
        catch (Exception) { context.Abort(); }
    }

    private static async Task<DeliveryInput> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasJsonContentType() || request.Headers.ContentEncoding.Count != 0 ||
            !Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType) ||
            mediaType.Parameters.Count(parameter => string.Equals(parameter.Name.Value, "charset", StringComparison.OrdinalIgnoreCase)) > 1 ||
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
            _ = StrictUtf8.GetCharCount(buffer, 0, count);
            using var document = JsonDocument.Parse(buffer.AsMemory(0, count), new JsonDocumentOptions
            {
                AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 4
            });
            return Parse(document.RootElement);
        }
        catch (BadHttpRequestException ex)
        {
            if (ex.StatusCode == 413) throw TooLarge();
            throw Invalid();
        }
        catch (JsonException) { throw Invalid(); }
        catch (DecoderFallbackException) { throw Invalid(); }
        catch (InvalidOperationException) { throw Invalid(); }
        catch (IOException) { throw Invalid(); }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static DeliveryInput Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Guid permitId = default, attemptId = default, credentialId = default, endpointMappingId = default;
        long revision = 0;
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw Invalid();
            switch (property.Name)
            {
                case "permitId": permitId = ReadGuid(property.Value); break;
                case "expectedPermitRevision":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt64(out revision) ||
                        revision != 2 || property.Value.GetRawText() != "2") throw Invalid();
                    break;
                case "attemptId": attemptId = ReadGuid(property.Value); break;
                case "credentialId": credentialId = ReadGuid(property.Value); break;
                case "endpointMappingId": endpointMappingId = ReadGuid(property.Value); break;
                default: throw Invalid();
            }
        }
        if (seen.Count != 5 || permitId == Guid.Empty || attemptId == Guid.Empty ||
            credentialId == Guid.Empty || endpointMappingId == Guid.Empty) throw Invalid();
        return new(permitId, revision, attemptId, credentialId, endpointMappingId);
    }

    private static Guid ReadGuid(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) throw Invalid();
        var text = value.GetString();
        if (text is null || text.Length != 36 || !Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty ||
            !string.Equals(text, id.ToString("D"), StringComparison.OrdinalIgnoreCase)) throw Invalid();
        return id;
    }

    private static RequestRejectedException Invalid() => new(400, "InvalidTargetCreationCredentialDelivery");
    private static RequestRejectedException TooLarge() => new(413, "TargetCreationCredentialDeliveryBodyTooLarge");

    private sealed record DeliveryInput(Guid PermitId, long ExpectedPermitRevision, Guid AttemptId,
        Guid CredentialId, Guid EndpointMappingId)
    {
        public override string ToString() => "TargetCreationCredentialDeliveryInput [REDACTED; NONEXECUTABLE]";
    }
}
