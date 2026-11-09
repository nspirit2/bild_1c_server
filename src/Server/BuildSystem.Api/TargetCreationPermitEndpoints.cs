using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Domain;
using BuildSystem.Infrastructure;

/// <summary>Independent default-off HTTP metadata adapter; never enables physical execution.</summary>
public sealed class TargetCreationPermitHttpOptions
{
    public bool Enabled { get; init; }
    public override string ToString() => "TargetCreationPermitHttpOptions [NONEXECUTABLE]";
}

internal static class TargetCreationPermitEndpoints
{
    internal const string AdminPrefix = "/api/admin/target-creation-permits";
    internal const string AgentPrefix = "/api/agents/target-creation-permit-starts";
    private const int MaxBodyBytes = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static bool IsPermitPath(PathString path) =>
        path.StartsWithSegments(AdminPrefix) || path.StartsWithSegments(AgentPrefix);

    public static void MapTargetCreationPermits(this WebApplication app)
    {
        app.MapPost(AdminPrefix + "/issue", IssueAsync)
            .RequireAuthorization(policy => policy.RequireRole("Administrator"));
        app.MapGet(AdminPrefix + "/{permitId:guid}", GetAsync)
            .RequireAuthorization(policy => policy.RequireRole("Administrator"));
        app.MapPost(AdminPrefix + "/{permitId:guid}/revoke", RevokeAsync)
            .RequireAuthorization(policy => policy.RequireRole("Administrator"));
        // Only this precise machine route bypasses cookie CSRF. Authentication is rechecked by SQL.
        app.MapPost(AgentPrefix, StartAsync).AllowAnonymous().WithMetadata(new AgentMachineEndpoint());
    }

    private static Task IssueAsync(HttpContext context) => BoundaryAsync(context, async () =>
    {
        RequireEnabled(context, binding: true);
        var input = await ReadAsync(context.Request, "issue", context.RequestAborted);
        // Trusted callers generate these identities. A new HTTP POST is NOT an exact replay;
        // an uncertain acknowledgement must not trigger automatic retry or adopt an existing permit.
        var permitId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var receipt = await context.RequestServices.GetRequiredService<ITargetCreationPermits>()
            .IssueWithCredentialAsync(CurrentActor(context), input.OperationId, input.Revision,
                permitId, attemptId, input.CredentialId, input.EndpointMappingId, context.RequestAborted);
        ValidateReceipt(receipt, permitId);
        if (receipt.OperationId != input.OperationId || receipt.AttemptId != attemptId ||
            receipt.ExpectedOperationRevision != input.Revision || receipt.CredentialId != input.CredentialId ||
            receipt.EndpointMappingId != input.EndpointMappingId || !receipt.NewlyRecorded ||
            receipt.State != TargetCreationPermitState.Prepared || receipt.Revision != 1)
            throw new InvalidOperationException();
        await WriteReceiptAsync(context, receipt, 201, AdminPrefix + "/" + permitId.ToString("D"));
    });

    private static Task GetAsync(HttpContext context) => BoundaryAsync(context, async () =>
    {
        RequireEnabled(context);
        var permitId = RoutePermitId(context);
        var receipt = await context.RequestServices.GetRequiredService<ITargetCreationPermits>()
            .GetAsync(CurrentActor(context), permitId, context.RequestAborted);
        ValidateReceipt(receipt, permitId);
        if (receipt.NewlyRecorded) throw new InvalidOperationException();
        await WriteReceiptAsync(context, receipt, 200);
    });

    private static Task RevokeAsync(HttpContext context) => BoundaryAsync(context, async () =>
    {
        RequireEnabled(context);
        var permitId = RoutePermitId(context);
        var input = await ReadAsync(context.Request, "revoke", context.RequestAborted);
        var receipt = await context.RequestServices.GetRequiredService<ITargetCreationPermits>()
            .RevokeAsync(CurrentActor(context), permitId, input.Revision, context.RequestAborted);
        ValidateReceipt(receipt, permitId);
        if (receipt.State != TargetCreationPermitState.Revoked || receipt.Revision != input.Revision + 1 ||
            !receipt.NewlyRecorded) throw new InvalidOperationException();
        await WriteReceiptAsync(context, receipt, 200);
    });

    private static Task StartAsync(HttpContext context) => BoundaryAsync(context, async () =>
    {
        if (context.Request.Headers.Origin.Count != 0)
            throw new RequestRejectedException(403, "TargetCreationPermitForbidden");
        RequireEnabled(context, binding: true, agentStart: true);
        var authorization = context.Request.Headers.Authorization;
        if (authorization.Count != 1 || authorization[0] is not { } value ||
            !value.StartsWith("Bearer ", StringComparison.Ordinal) || value.Length is < 8 or > 263 ||
            value[7..].Any(c => c < 0x21 || c > 0x7e || c == ','))
            throw new RequestRejectedException(401, "AgentUnauthorized");
        var input = await ReadAsync(context.Request, "start", context.RequestAborted);
        var receipt = await context.RequestServices.GetRequiredService<ITargetCreationPermitAgentStarts>()
            .RecordAgentStartAsync(value[7..], input.PermitId, input.Revision, input.AttemptId, context.RequestAborted);
        ValidateReceipt(receipt, input.PermitId);
        if (receipt.AttemptId != input.AttemptId || receipt.StartExpectedRevision != input.Revision ||
            receipt.StartedAt is null) throw new InvalidOperationException();
        await WriteReceiptAsync(context, receipt, 200);
    });

    private static void RequireEnabled(HttpContext context, bool binding = false, bool agentStart = false)
    {
        if (!context.Request.IsHttps) throw new RequestRejectedException(400, "TargetCreationPermitHttpsRequired");
        var services = context.RequestServices;
        var options = services.GetRequiredService<TargetCreationPermitOptions>();
        if (!services.GetRequiredService<TargetCreationPermitHttpOptions>().Enabled || !options.Enabled ||
            !services.GetRequiredService<TargetCreationOperationOptions>().Enabled ||
            binding && !options.CredentialBindingEnabled || agentStart && !options.AgentStartEnabled)
            throw new RequestRejectedException(503, "TargetCreationPermitUnavailable");
    }

    private static Actor CurrentActor(HttpContext context) => new(
        Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty,
        context.User.IsInRole("Administrator"));

    private static async Task BoundaryAsync(HttpContext context, Func<Task> action)
    {
        try { await action(); }
        catch (RequestRejectedException ex) { await RejectedAsync(context, ex); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); }
        catch (Exception) { await ErrorAsync(context, 503, "TargetCreationPermitUnavailable"); }
    }

    internal static Task RejectedAsync(HttpContext context, RequestRejectedException ex)
    {
        // Literal pairs only: neither arbitrary status codes nor arbitrary service strings escape.
        var (status, code) = (ex.StatusCode, ex.Code) switch
        {
            (400, "InvalidTargetCreationPermit") => (400, "InvalidTargetCreationPermit"),
            (400, "TargetCreationPermitHttpsRequired") => (400, "TargetCreationPermitHttpsRequired"),
            (413, "TargetCreationPermitBodyTooLarge") => (413, "TargetCreationPermitBodyTooLarge"),
            (401, "AgentUnauthorized") => (401, "AgentUnauthorized"),
            (403, "TargetCreationPermitForbidden") => (403, "TargetCreationPermitForbidden"),
            (403, "TargetCreationForbidden") => (403, "TargetCreationForbidden"),
            (404, "TargetCreationPermitNotFound") => (404, "TargetCreationPermitNotFound"),
            (404, "TargetCreationNotFound") => (404, "TargetCreationNotFound"),
            (409, "PermitConflict") => (409, "PermitConflict"),
            (409, "RevisionConflict") => (409, "RevisionConflict"),
            (409, "InvalidTransition") => (409, "InvalidTransition"),
            (409, "InvalidServerTime") => (409, "InvalidServerTime"),
            (409, "PermitExpired") => (409, "PermitExpired"),
            (409, "TargetCreationCredentialUnavailable") => (409, "TargetCreationCredentialUnavailable"),
            (409, "TargetCreationMismatch") => (409, "TargetCreationMismatch"),
            (409, "TargetCreationPermitMismatch") => (409, "TargetCreationPermitMismatch"),
            (409, "TargetCreationPermitDisabled") => (503, "TargetCreationPermitUnavailable"),
            _ => (503, "TargetCreationPermitUnavailable")
        };
        return ErrorAsync(context, status, code);
    }

    internal static async Task ErrorAsync(HttpContext context, int status, string code)
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

    internal static void SetHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers.XContentTypeOptions = "nosniff";
    }

    private static async Task WriteReceiptAsync(HttpContext context, TargetCreationPermitReceipt receipt,
        int status, string? location = null)
    {
        // Serialize and bound before starting the response; serializer/service activation stays in the boundary.
        var payload = JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions);
        if (payload.Length > 16 * 1024) throw new InvalidOperationException();
        SetHeaders(context.Response);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = payload.Length;
        if (location is not null) context.Response.Headers.Location = location;
        await context.Response.Body.WriteAsync(payload, context.RequestAborted);
    }

    private static void ValidateReceipt(TargetCreationPermitReceipt receipt, Guid permitId)
    {
        if (receipt is null || receipt.PermitId != permitId || receipt.OperationId == Guid.Empty ||
            receipt.AttemptId == Guid.Empty || receipt.ExpectedOperationRevision != 1 ||
            receipt.Executable || !receipt.ScheduledJobsMustRemainBlocked || !Enum.IsDefined(receipt.State) ||
            receipt.Revision is < 1 or > 3 || receipt.CredentialId == Guid.Empty || receipt.EndpointMappingId == Guid.Empty ||
            receipt.CredentialId.HasValue != receipt.EndpointMappingId.HasValue ||
            receipt.Operation is not { } operation || operation.OperationId != receipt.OperationId ||
            operation.Executable || !operation.ScheduledJobsMustRemainBlocked || operation.ReservationId == Guid.Empty ||
            operation.InfobaseName != "bs_test_" + operation.ReservationId.ToString("N") ||
            operation.SqlDbName != "bs_test_" + operation.ReservationId.ToString("N"))
            throw new InvalidOperationException();
    }

    private static Guid RoutePermitId(HttpContext context) => ReadGuidText(context.Request.RouteValues["permitId"]?.ToString());

    private static async Task<Input> ReadAsync(HttpRequest request, string kind, CancellationToken ct)
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
            _ = StrictUtf8.GetCharCount(buffer, 0, count);
            using var document = JsonDocument.Parse(buffer.AsMemory(0, count), new JsonDocumentOptions
            { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 4 });
            return Parse(document.RootElement, kind);
        }
        catch (BadHttpRequestException ex) { throw ex.StatusCode == 413 ? TooLarge() : Invalid(); }
        catch (JsonException) { throw Invalid(); }
        catch (DecoderFallbackException) { throw Invalid(); }
        catch (InvalidOperationException) { throw Invalid(); }
        catch (IOException) { throw Invalid(); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer); }
    }

    private static Input Parse(JsonElement root, string kind)
    {
        if (root.ValueKind != JsonValueKind.Object) throw Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Guid operation = default, permit = default, attempt = default, credential = default, mapping = default;
        long revision = 0;
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw Invalid();
            switch (property.Name)
            {
                case "operationId" when kind == "issue": operation = ReadGuid(property.Value); break;
                case "credentialId" when kind == "issue": credential = ReadGuid(property.Value); break;
                case "endpointMappingId" when kind == "issue": mapping = ReadGuid(property.Value); break;
                case "permitId" when kind == "start": permit = ReadGuid(property.Value); break;
                case "attemptId" when kind == "start": attempt = ReadGuid(property.Value); break;
                case "expectedOperationRevision" when kind == "issue":
                case "expectedPermitRevision" when kind is "start" or "revoke":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt64(out revision) ||
                        revision < 1 || revision > (kind == "revoke" ? 2 : 1) ||
                        property.Value.GetRawText() != revision.ToString(System.Globalization.CultureInfo.InvariantCulture)) throw Invalid();
                    break;
                default: throw Invalid();
            }
        }
        if (seen.Count != (kind == "issue" ? 4 : kind == "start" ? 3 : 1) || revision == 0) throw Invalid();
        return new(operation, permit, attempt, credential, mapping, revision);
    }

    private static Guid ReadGuid(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? ReadGuidText(value.GetString()) : throw Invalid();
    private static Guid ReadGuidText(string? text)
    {
        if (text is null || text.Length != 36 || !Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty ||
            !string.Equals(text, id.ToString("D"), StringComparison.OrdinalIgnoreCase)) throw Invalid();
        return id;
    }
    private static RequestRejectedException Invalid() => new(400, "InvalidTargetCreationPermit");
    private static RequestRejectedException TooLarge() => new(413, "TargetCreationPermitBodyTooLarge");
    private sealed record Input(Guid OperationId, Guid PermitId, Guid AttemptId, Guid CredentialId,
        Guid EndpointMappingId, long Revision)
    {
        public override string ToString() => "TargetCreationPermitHttpInput [NONEXECUTABLE]";
    }
}
