using System.Reflection;

internal static class TargetCredentialsUi
{
    public static void MapTargetCredentialsUi(this WebApplication app)
    {
        Map("/admin/target-credentials", "index.html", "text/html; charset=utf-8");
        Map("/admin/target-credentials/app.js", "app.js", "text/javascript; charset=utf-8");
        Map("/admin/target-credentials/styles.css", "styles.css", "text/css; charset=utf-8");

        void Map(string route, string file, string contentType)
        {
            app.MapGet(route, (HttpContext context) =>
            {
                context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'; object-src 'none'";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers["X-Frame-Options"] = "DENY";
                context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=(), browsing-topics=()";
                context.Response.Headers.CacheControl = "no-store";
                var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BuildSystem.Api.wwwroot.target-credentials." + file)
                    ?? throw new InvalidOperationException("Embedded target credentials UI is missing.");
                return Results.Stream(stream, contentType);
            }).RequireAuthorization(policy => policy.RequireRole("Administrator"));
        }
    }
}
