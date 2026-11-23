using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BuildSystem.Application;
using BuildSystem.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;

internal static class PendingArchiveCancellationEndpoints
{
    private const string EnabledKey = "PendingArchiveCancellation:Enabled";
    private const int MaxBodyBytes = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static IServiceCollection AddPendingArchiveCancellationEndpoints(this IServiceCollection services)
    {
        services.TryAddSingleton<Options>(provider =>
        {
            // Resolve configuration through the final host provider, including test overrides.
            var value = provider.GetRequiredService<IConfiguration>()[EnabledKey];
            if (value is null) return new Options(false);
            if (!bool.TryParse(value, out var enabled))
                throw new InvalidOperationException($"{EnabledKey} must be a boolean.");
            return new Options(enabled);
        });
        services.TryAddScoped<IPendingArchiveCancellations>(provider => new SqlPendingArchiveCancellations(
            provider.GetRequiredService<BuildSystemDbContext>(), provider.GetRequiredService<Options>().Enabled));
        return services;
    }

    public static void MapPendingArchiveCancellationEndpoints(this WebApplication app)
    {
        // Validate the opt-in before accepting traffic, even when disabled.
        _ = app.Services.GetRequiredService<Options>();
        // This cookie-authenticated API POST uses the shared antiforgery and no-store middleware.
        app.MapPost("/api/test-environments/{id:guid}/pending-archive-cancellations", CancelAsync)
            .RequireAuthorization(policy => policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme)
                .RequireAuthenticatedUser());
    }

    private static async Task<IResult> CancelAsync(Guid id, HttpContext context, CancellationToken ct)
    {
        if (!context.Request.IsHttps) throw Invalid();
        if (!context.RequestServices.GetRequiredService<Options>().Enabled)
            throw new RequestRejectedException(409, "PendingArchiveCancellationDisabled");
        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) || userId == Guid.Empty)
            throw new RequestRejectedException(404, "PendingArchiveCancellationNotFound");
        if (id == Guid.Empty) throw Invalid();

        var expectedJobId = await ReadExpectedJobIdAsync(context.Request, ct);
        var actor = new Actor(userId, context.User.IsInRole("Administrator"));
        var receipt = await context.RequestServices.GetRequiredService<IPendingArchiveCancellations>()
            .CancelAsync(actor, id, new PendingArchiveCancellationRequest(expectedJobId), ct);
        if (receipt is null || receipt.EnvironmentId != id || receipt.JobId != expectedJobId ||
            receipt.CancelledAt == default || receipt.CancelledAt.Offset != TimeSpan.Zero)
            throw new RequestRejectedException(503, "PendingArchiveCancellationUnconfirmed");
        return Results.Ok(new CancellationResponse(receipt.EnvironmentId, receipt.JobId,
            "CancelledBeforeFirstLease", receipt.CancelledAt));
    }

    private static async Task<Guid> ReadExpectedJobIdAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > MaxBodyBytes) throw TooLarge();
        if (!request.HasJsonContentType() || request.Headers.ContentEncoding.Count != 0 ||
            !MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType) ||
            mediaType.Parameters.Count(p => string.Equals(p.Name.Value, "charset", StringComparison.OrdinalIgnoreCase)) > 1 ||
            mediaType.Charset.HasValue && !string.Equals(mediaType.Charset.Value?.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase))
            throw Invalid();

        // The extra byte detects oversized streaming bodies without reading the rest.
        var buffer = new byte[MaxBodyBytes + 1];
        try
        {
            var count = 0;
            while (count < buffer.Length)
            {
                var read = await request.Body.ReadAsync(buffer.AsMemory(count), ct);
                if (read == 0) break;
                count += read;
            }
            if (count > MaxBodyBytes) throw TooLarge();

            _ = StrictUtf8.GetCharCount(buffer, 0, count);
            using var document = JsonDocument.Parse(buffer.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 2 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
            var properties = document.RootElement.EnumerateObject();
            if (!properties.MoveNext()) throw Invalid();
            var property = properties.Current;
            if (!property.NameEquals("expectedJobId") || property.Value.ValueKind != JsonValueKind.String ||
                !property.Value.TryGetGuid(out var jobId) || jobId == Guid.Empty || properties.MoveNext())
                throw Invalid();
            return jobId;
        }
        catch (BadHttpRequestException ex) { throw ex.StatusCode == 413 ? TooLarge() : Invalid(); }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or InvalidOperationException or IOException)
        {
            throw Invalid();
        }
    }

    private static RequestRejectedException Invalid() => new(400, "InvalidPendingArchiveCancellation");
    private static RequestRejectedException TooLarge() => new(413, "PendingArchiveCancellationBodyTooLarge");

    private sealed record Options(bool Enabled);

    private sealed record CancellationResponse(
        [property: JsonPropertyName("environmentId")] Guid EnvironmentId,
        [property: JsonPropertyName("jobId")] Guid JobId,
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("cancelledAt")] DateTimeOffset CancelledAt);
}
