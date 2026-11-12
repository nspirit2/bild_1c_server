using System.Security.Claims;
using BuildSystem.Application;

internal static class EnvironmentOrderSummaryEndpoints
{
    public static void MapEnvironmentOrderSummaryEndpoints(this WebApplication app)
    {
        app.MapGet("/api/test-environments/{environmentId:guid}/order-summary", async (Guid environmentId,
            HttpContext context, IEnvironmentOrderSummary summary, CancellationToken ct) =>
        {
            if (environmentId == Guid.Empty)
                return Results.BadRequest(new { code = "InvalidEnvironmentOrderSummary" });
            if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) || userId == Guid.Empty)
                return Results.Json(new { code = "Forbidden" }, statusCode: StatusCodes.Status403Forbidden);
            var actor = new Actor(userId, context.User.IsInRole("Administrator"));
            var view = await summary.GetAsync(actor, environmentId, ct);
            return view is null ? Results.NotFound() : Results.Ok(view);
        }).RequireAuthorization();
    }
}
