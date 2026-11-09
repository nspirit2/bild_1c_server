using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildSystem.Application;

/// <summary>Administrator-only import and metadata. No secret retrieval or execution authority.</summary>
internal static class TargetCredentialsEndpoints
{
    private const int MaxBodyBytes = 16 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void MapTargetCredentials(this WebApplication app)
    {
        app.MapPost("/api/admin/target-credentials", async (HttpContext context,
            TargetCredentialOptions options, ITargetCredentials credentials, CancellationToken ct) =>
        {
            Enabled(options);
            var actor = CurrentActor(context.User);
            // Never bind a secret DTO through the framework: development binding errors can echo input.
            var input = await ReadBodyAsync(context.Request, ParseImport, ct);
            var metadata = await SafeAsync(() => credentials.StoreAsync(actor,
                new TargetCredentialBinding(input.CredentialId, input.CanonicalClusterId,
                    input.CanonicalSqlServerId, input.EndpointMappingId),
                new TargetCredentialInput(input.ClusterUser, input.ClusterPassword, input.SqlUser, input.SqlPassword), ct));
            return Results.Created($"/api/admin/target-credentials/{metadata.Binding.CredentialId:D}", metadata);
        }).RequireAuthorization(policy => policy.RequireRole("Administrator"));

        app.MapGet("/api/admin/target-credentials/{credentialId:guid}", async (Guid credentialId,
            HttpContext context, TargetCredentialOptions options, ITargetCredentials credentials, CancellationToken ct) =>
        {
            Enabled(options);
            return Results.Ok(await SafeAsync(() => credentials.GetAsync(CurrentActor(context.User), credentialId, ct)));
        }).RequireAuthorization(policy => policy.RequireRole("Administrator"));

        app.MapPost("/api/admin/target-credentials/{credentialId:guid}/revoke", async (Guid credentialId,
            HttpContext context, TargetCredentialOptions options, ITargetCredentials credentials, CancellationToken ct) =>
        {
            Enabled(options);
            var actor = CurrentActor(context.User);
            var revision = await ReadBodyAsync(context.Request, ParseRevision, ct);
            return Results.Ok(await SafeAsync(() => credentials.RevokeAsync(actor, credentialId, revision, ct)));
        }).RequireAuthorization(policy => policy.RequireRole("Administrator"));
    }

    private static async Task<T> SafeAsync<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (OperationCanceledException) { throw; }
        catch (RequestRejectedException) { throw; }
        catch (Exception) { throw new RequestRejectedException(503, "TargetCredentialsUnavailable"); }
    }

    private static void Enabled(TargetCredentialOptions options)
    {
        if (!options.Enabled) throw new RequestRejectedException(503, "TargetCredentialsUnavailable");
    }

    private static Actor CurrentActor(ClaimsPrincipal principal)
    {
        if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) || id == Guid.Empty)
            throw new RequestRejectedException(403, "Forbidden");
        // The application port additionally rechecks the current database administrator role.
        return new Actor(id, principal.IsInRole("Administrator"));
    }

    private static async Task<T> ReadBodyAsync<T>(HttpRequest request, Func<JsonElement, T> parse, CancellationToken ct)
    {
        if (!request.HasJsonContentType()) throw Invalid();
        if (request.ContentLength > MaxBodyBytes) throw TooLarge();
        // One extra byte detects oversized chunked bodies without trusting Content-Length.
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
            return parse(document.RootElement);
        }
        catch (BadHttpRequestException ex)
        {
            if (ex.StatusCode == StatusCodes.Status413PayloadTooLarge) throw TooLarge();
            throw Invalid();
        }
        catch (JsonException) { throw Invalid(); }
        catch (DecoderFallbackException) { throw Invalid(); }
        catch (EncoderFallbackException) { throw Invalid(); }
        catch (InvalidOperationException) { throw Invalid(); }
        catch (IOException) { throw Invalid(); }
        catch (OperationCanceledException) { throw; }
        catch (RequestRejectedException) { throw; }
        catch (Exception) { throw new RequestRejectedException(503, "TargetCredentialsUnavailable"); }
        finally
        {
            // Only our owned byte buffer can be cleared. Parsed strings and JsonDocument's
            // internal storage are managed by the runtime; disposal is not a strong erasure guarantee.
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static TargetCredentialImportRequest ParseImport(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Guid credentialId = default, clusterId = default, sqlServerId = default, endpointId = default;
        string? clusterUser = null, clusterPassword = null, sqlUser = null, sqlPassword = null;
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw Invalid();
            switch (property.Name)
            {
                case "credentialId": credentialId = ReadGuid(property.Value); break;
                case "canonicalClusterId": clusterId = ReadGuid(property.Value); break;
                case "canonicalSqlServerId": sqlServerId = ReadGuid(property.Value); break;
                case "endpointMappingId": endpointId = ReadGuid(property.Value); break;
                case "clusterUser": clusterUser = ReadSecret(property.Value); break;
                case "clusterPassword": clusterPassword = ReadSecret(property.Value); break;
                case "sqlUser": sqlUser = ReadSecret(property.Value); break;
                case "sqlPassword": sqlPassword = ReadSecret(property.Value); break;
                default: throw Invalid();
            }
        }
        if (seen.Count != 8 || credentialId == Guid.Empty || clusterId == Guid.Empty ||
            sqlServerId == Guid.Empty || endpointId == Guid.Empty || clusterUser is null ||
            clusterPassword is null || sqlUser is null || sqlPassword is null) throw Invalid();
        return new(credentialId, clusterId, sqlServerId, endpointId, clusterUser, clusterPassword, sqlUser, sqlPassword);
    }

    private static Guid ReadGuid(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) throw Invalid();
        var text = value.GetString();
        if (text is null || text.Length != 36 || !Guid.TryParseExact(text, "D", out var id) ||
            id == Guid.Empty || !string.Equals(text, id.ToString("D"), StringComparison.OrdinalIgnoreCase)) throw Invalid();
        return id;
    }

    private static string ReadSecret(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) throw Invalid();
        var text = value.GetString();
        if (text is null || text.Length is < 1 or > 4096 || text.Any(char.IsControl)) throw Invalid();
        _ = StrictUtf8.GetByteCount(text);
        return text;
    }

    private static long ParseRevision(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw Invalid();
        long revision = 0;
        var seen = false;
        foreach (var property in root.EnumerateObject())
        {
            if (seen || property.Name != "expectedRevision" || property.Value.ValueKind != JsonValueKind.Number ||
                !property.Value.TryGetInt64(out revision) || revision <= 0) throw Invalid();
            seen = true;
        }
        if (!seen) throw Invalid();
        return revision;
    }

    private static RequestRejectedException Invalid() => new(400, "InvalidTargetCredential");
    private static RequestRejectedException TooLarge() => new(413, "TargetCredentialBodyTooLarge");

    private sealed record TargetCredentialImportRequest(Guid CredentialId, Guid CanonicalClusterId,
        Guid CanonicalSqlServerId, Guid EndpointMappingId, string ClusterUser, string ClusterPassword,
        string SqlUser, string SqlPassword)
    {
        public override string ToString() => "TargetCredentialImportRequest [redacted]";
    }
}
