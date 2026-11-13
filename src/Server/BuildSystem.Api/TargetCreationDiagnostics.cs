using System.Security.Claims;
using BuildSystem.Application;

/// <summary>Independent opt-in for reading ledger metadata; does not enable ledger mutations.</summary>
public sealed class TargetCreationDiagnosticsOptions
{
    public bool Enabled { get; init; }
}

internal static class TargetCreationDiagnostics
{
    public static void MapTargetCreationDiagnostics(this WebApplication app)
    {
        if (app.Services.GetRequiredService<TargetCreationDiagnosticsOptions>().Enabled)
        {
            app.MapGet("/api/admin/target-creation-operations/{operationId}/execution-evidence", async (string operationId,
                ClaimsPrincipal principal, ITargetCreationExecutionEvidenceReads reads, HttpResponse response, CancellationToken ct) =>
            {
                response.Headers.CacheControl = "no-store";
                response.Headers.Pragma = "no-cache";
                if (!Guid.TryParseExact(operationId, "D", out var id) || id == Guid.Empty ||
                    !string.Equals(operationId, id.ToString("D"), StringComparison.Ordinal))
                    return Results.BadRequest(new { code = "InvalidTargetCreation" });
                if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) || userId == Guid.Empty)
                    return Results.Json(new { code = "Forbidden" }, statusCode: StatusCodes.Status403Forbidden);
                // The read service rechecks the current database administrator role; this is never a fresh grant.
                var view = await reads.GetAsync(new Actor(userId, principal.IsInRole("Administrator")), id, ct);
                return Results.Ok(view);
            }).RequireAuthorization(policy => policy.RequireRole("Administrator"));

            // Historical observations are diagnostic only, never ownership or execution authority.
            app.MapGet("/api/admin/target-creation-operations/{operationId}/registration-evidence", async (string operationId,
                ClaimsPrincipal principal, ITargetRegistrationEvidenceReads reads, HttpResponse response, CancellationToken ct) =>
            {
                response.Headers.CacheControl = "no-store";
                response.Headers.Pragma = "no-cache";
                if (!Guid.TryParseExact(operationId, "D", out var id) || id == Guid.Empty ||
                    !string.Equals(operationId, id.ToString("D"), StringComparison.Ordinal))
                    return Results.BadRequest(new { code = "InvalidTargetCreation" });
                if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) || userId == Guid.Empty)
                    return Results.Json(new { code = "Forbidden" }, statusCode: StatusCodes.Status403Forbidden);
                // The service rechecks the current database administrator role, not just cookie claims.
                var view = await reads.GetAsync(new Actor(userId, principal.IsInRole("Administrator")), id, ct);
                return Results.Ok(view);
            }).RequireAuthorization(policy => policy.RequireRole("Administrator"));
        }

        // Bind the route as text: the guid constraint would turn malformed IDs into 404 instead of 400.
        app.MapGet("/api/admin/target-creation-operations/{operationId}", async (string operationId,
            ClaimsPrincipal principal, ITargetCreationOperationReads reads, CancellationToken ct) =>
        {
            if (!Guid.TryParseExact(operationId, "D", out var id) || id == Guid.Empty ||
                !string.Equals(operationId, id.ToString("D"), StringComparison.Ordinal))
                return Results.BadRequest(new { code = "InvalidTargetCreation" });
            if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) || userId == Guid.Empty)
                return Results.Json(new { code = "Forbidden" }, statusCode: StatusCodes.Status403Forbidden);
            var actor = new Actor(userId, principal.IsInRole("Administrator"));
            // The read port also checks the *current database role*, not just the cookie claim.
            var receipt = await reads.GetAsync(actor, id, ct);
            return Results.Ok(new
            {
                receipt.OperationId, receipt.RestoreIntentId, receipt.EnvironmentId,
                receipt.StageJobId, receipt.ReservationId, receipt.ArtifactId, receipt.AgentId,
                receipt.TargetIncarnationId, receipt.CanonicalClusterId, receipt.CanonicalSqlServerId,
                receipt.InfobaseName, receipt.SqlDbName,
                State = receipt.State.ToString(), receipt.Revision, receipt.AttemptId,
                receipt.AttemptExpectedRevision,
                QuarantineReason = receipt.QuarantineReason?.ToString(),
                receipt.NewlyRecorded, receipt.Executable, receipt.ScheduledJobsMustRemainBlocked
            });
        }).RequireAuthorization(policy => policy.RequireRole("Administrator"));

        app.MapGet("/api/admin/target-creation-operations/{operationId}/audit", async (string operationId,
            ClaimsPrincipal principal, ITargetCreationOperationReads reads, CancellationToken ct) =>
        {
            if (!Guid.TryParseExact(operationId, "D", out var id) || id == Guid.Empty ||
                !string.Equals(operationId, id.ToString("D"), StringComparison.Ordinal))
                return Results.BadRequest(new { code = "InvalidTargetCreation" });
            if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) || userId == Guid.Empty)
                return Results.Json(new { code = "Forbidden" }, statusCode: StatusCodes.Status403Forbidden);
            var actor = new Actor(userId, principal.IsInRole("Administrator"));
            // The read service rechecks the current database administrator role.
            var audit = await reads.GetAuditAsync(actor, id, ct);
            return Results.Ok(new
            {
                audit.OperationId,
                Events = audit.Events.Select(entry => new
                {
                    entry.Id, entry.ActorId, entry.Action, entry.CreatedAt
                }),
                audit.Truncated, audit.Executable, audit.ScheduledJobsMustRemainBlocked
            });
        }).RequireAuthorization(policy => policy.RequireRole("Administrator"));
    }
}
