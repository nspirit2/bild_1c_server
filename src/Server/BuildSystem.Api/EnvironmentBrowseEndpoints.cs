using System.Globalization;
using System.Security.Claims;
using BuildSystem.Application;

internal static class EnvironmentBrowseEndpoints
{
    public static void MapEnvironmentBrowseEndpoints(this WebApplication app)
    {
        // Static browse metadata is not an execution grant; the port rechecks actor visibility.
        app.MapGet("/api/test-environments/browse", async (HttpContext context,
            IEnvironmentBrowse browse, CancellationToken ct) =>
        {
            var query = context.Request.Query;
            if (query.Any(pair => pair.Key is not ("limit" or "cursor") || pair.Value.Count != 1))
                return Results.BadRequest(new { code = "InvalidEnvironmentBrowseQuery" });
            var limit = 25;
            if (query.TryGetValue("limit", out var limits))
            {
                var text = limits[0]!;
                if (text.Length == 0 || text.Any(c => c is < '0' or > '9') ||
                    !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out limit) ||
                    limit is < 1 or > 100)
                    return Results.BadRequest(new { code = "InvalidEnvironmentBrowseQuery" });
            }
            string? cursor = null;
            if (query.TryGetValue("cursor", out var cursors))
            {
                cursor = cursors[0]!;
                if (cursor.Length is < 1 or > 512 || cursor.Any(c =>
                    c is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_')))
                    return Results.BadRequest(new { code = "InvalidEnvironmentBrowseQuery" });
            }
            if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) || userId == Guid.Empty)
                return Results.Json(new { code = "Forbidden" }, statusCode: StatusCodes.Status403Forbidden);
            var actor = new Actor(userId, context.User.IsInRole("Administrator"));
            // Cursor integrity, scope and actor binding belong to the application port, not HTTP decoding.
            return Results.Ok(await browse.ListAsync(actor, new EnvironmentBrowseQuery(limit, cursor), ct));
        }).RequireAuthorization();
    }
}
