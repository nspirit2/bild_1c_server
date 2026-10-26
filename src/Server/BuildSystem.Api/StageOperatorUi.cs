using System.Reflection;

internal static class StageOperatorUi
{
    public static void MapStageOperatorUi(this WebApplication app)
    {
        Map("/stage", "index.html", "text/html; charset=utf-8");
        Map("/stage/app.js", "app.js", "text/javascript; charset=utf-8");
        Map("/stage/styles.css", "styles.css", "text/css; charset=utf-8");

        void Map(string route, string file, string contentType)
        {
            app.MapGet(route, (HttpContext context) =>
            {
                context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'; object-src 'none'";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers["X-Frame-Options"] = "DENY";
                context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
                var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BuildSystem.Api.wwwroot.stage." + file)
                    ?? throw new InvalidOperationException("Embedded operator UI is missing.");
                return Results.Stream(stream, contentType);
            }).AllowAnonymous(); // Shell only; all data endpoints independently enforce human authorization.
        }
    }
}
// end
