using System.Security.Claims;
using System.Text.Json.Serialization;
using BuildSystem.Application;
using BuildSystem.Infrastructure;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024);
var connection = builder.Configuration.GetConnectionString("BuildSystem")
    ?? throw new InvalidOperationException("Configure ConnectionStrings__BuildSystem using protected configuration.");
var keysDirectory = builder.Configuration["Security:DataProtectionDirectory"]
    ?? throw new InvalidOperationException("Configure Security__DataProtectionDirectory with a service-only ACL.");
var protection = builder.Services.AddDataProtection().SetApplicationName("BuildSystem.Api")
    .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));
if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
else if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
    throw new InvalidOperationException("Production key protection is configured for Windows only.");

builder.Services.AddBuildSystemInfrastructure(connection);
builder.Services.AddSingleton(new ArchiveStageOptions { Enabled = builder.Configuration.GetValue<bool>("ArchiveStage:Enabled") });
builder.Services.AddSingleton(new StageRetirementOptions { Enabled = builder.Configuration.GetValue<bool>("StageRetirement:Enabled") });
builder.Services.AddSingleton(new StageCleanupOptions { Enabled = builder.Configuration.GetValue<bool>("StageCleanup:Enabled") });
builder.Services.AddSingleton(new StageCleanupResultOptions { Enabled = builder.Configuration.GetValue<bool>("StageCleanupResult:Enabled") });
builder.Services.AddIdentity<BuildSystemUser, IdentityRole<Guid>>(options =>
{
    options.Password.RequiredLength = 12;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.User.RequireUniqueEmail = true;
}).AddEntityFrameworkStores<BuildSystemDbContext>().AddDefaultTokenProviders();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "__Host-BuildSystem";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = false;
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthentication().AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, AgentTokenAuthentication>("AgentToken", _ => { });
builder.Services.AddAuthorization(options => options.AddPolicy("AgentOnly", policy =>
    policy.AddAuthenticationSchemes("AgentToken").RequireAuthenticatedUser()));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("AgentEnrollment", context => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
        { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "__Host-BuildSystem-Csrf";
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
});
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);

var app = builder.Build();
if (args.Length > 0 && args[0] == "bootstrap-admin")
{
    Environment.ExitCode = await AdminBootstrap.RunAsync(app.Services, args.Skip(1).ToArray());
    return;
}
if (args.Length == 1 && args[0] == "migrate")
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<BuildSystemDbContext>().Database.MigrateAsync();
    return;
}
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    if (context.Request.ContentLength > 16 * 1024)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        return;
    }
    await next(context);
});
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    try { await next(context); }
    catch (RequestRejectedException ex)
    {
        context.Response.StatusCode = ex.StatusCode;
        await context.Response.WriteAsJsonAsync(new { code = ex.Code });
    }
    catch (AntiforgeryValidationException)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new { code = "InvalidCsrfToken" });
    }
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
// JSON endpoints still require CSRF validation: cookie authentication is ambient authority.
app.Use(async (context, next) =>
{
    if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path.StartsWithSegments("/api") &&
        context.GetEndpoint()?.Metadata.GetMetadata<AgentMachineEndpoint>() is null)
        await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
    await next(context);
});

app.MapGet("/api/auth/csrf", (HttpContext context, IAntiforgery antiforgery) =>
{
    var tokens = antiforgery.GetAndStoreTokens(context);
    return Results.Ok(new { token = tokens.RequestToken });
}).AllowAnonymous();
app.MapPost("/api/auth/login", async (LoginInput input, SignInManager<BuildSystemUser> signIn) =>
{
    if (string.IsNullOrWhiteSpace(input.UserName) || string.IsNullOrEmpty(input.Password) ||
        input.UserName.Length > 256 || input.Password.Length > 1024) return Results.BadRequest();
    var result = await signIn.PasswordSignInAsync(input.UserName, input.Password,
        isPersistent: false, lockoutOnFailure: true);
    return result.Succeeded ? Results.NoContent() : Results.Unauthorized();
}).AllowAnonymous();
app.MapPost("/api/auth/logout", async (SignInManager<BuildSystemUser> signIn) =>
{
    await signIn.SignOutAsync();
    return Results.NoContent();
}).RequireAuthorization();
app.MapAdministration();
app.MapAgentEndpoints();
app.MapGet("/api/auth/me", (ClaimsPrincipal user) => Results.Ok(new
{
    id = user.FindFirstValue(ClaimTypes.NameIdentifier), name = user.Identity!.Name,
    roles = user.FindAll(ClaimTypes.Role).Select(x => x.Value).ToArray()
})).RequireAuthorization();

app.MapPost("/api/environment-requests", async (HttpContext context, CreateEnvironmentRequest request,
    IEnvironmentOrders orders, CancellationToken ct) =>
{
    var key = context.Request.Headers["Idempotency-Key"];
    if (key.Count != 1 || string.IsNullOrWhiteSpace(key[0]))
        return Results.BadRequest(new { code = "IdempotencyKeyRequired" });
    var receipt = await orders.CreateAsync(CurrentActor(context.User), key[0]!, request, ct);
    return Results.Accepted($"/api/environment-requests/{receipt.RequestId}", receipt);
}).RequireAuthorization();
app.MapGet("/api/environment-requests/{id:guid}", async (Guid id, ClaimsPrincipal user,
    IEnvironmentOrders orders, CancellationToken ct) =>
{
    var value = await orders.GetRequestAsync(CurrentActor(user), id, ct);
    return value is null ? Results.NotFound() : Results.Ok(value);
}).RequireAuthorization();
app.MapGet("/api/test-environments", async (ClaimsPrincipal user, IEnvironmentOrders orders,
    CancellationToken ct) => Results.Ok(await orders.ListEnvironmentsAsync(CurrentActor(user), ct)))
    .RequireAuthorization();
app.MapGet("/api/test-environments/{id:guid}", async (Guid id, ClaimsPrincipal user,
    IEnvironmentOrders orders, CancellationToken ct) =>
{
    var value = await orders.GetEnvironmentAsync(CurrentActor(user), id, ct);
    return value is null ? Results.NotFound() : Results.Ok(value);
}).RequireAuthorization();
app.MapGet("/api/test-environments/{id:guid}/stage-status", async (Guid id, ClaimsPrincipal user,
    IStageStatus status, CancellationToken ct) =>
{
    var value = await status.GetAsync(CurrentActor(user), id, ct);
    return value is null ? Results.NotFound() : Results.Ok(value);
}).RequireAuthorization();
app.MapPost("/api/test-environments/{id:guid}/stage-retirement-intents", async (Guid id,
    StageRetirementRequest request, ClaimsPrincipal user, IStageRetirement retirement, CancellationToken ct) =>
    Results.Ok(await retirement.RequestAsync(CurrentActor(user), id, request, ct)))
    .RequireAuthorization();
app.MapGet("/api/stage-operations", async (int? limit, string? cursor, string? state,
    ClaimsPrincipal user, IStageOperations operations, CancellationToken ct) =>
    Results.Ok(await operations.ListAsync(CurrentActor(user), new StageOperationsQuery(limit ?? 25, cursor, state), ct)))
    .RequireAuthorization();
app.MapGet("/api/projects/{projectId:guid}/archive-versions", async (Guid projectId, int? limit,
    string? cursor, ClaimsPrincipal user, IArchiveCatalogBrowse catalog, CancellationToken ct) =>
    Results.Ok(await catalog.ListAsync(CurrentActor(user), projectId, new ArchiveCatalogQuery(limit ?? 25, cursor), ct)))
    .RequireAuthorization();
app.MapGet("/api/projects/browse", async (int? limit, string? cursor, ClaimsPrincipal user,
    IProjectBrowse projects, CancellationToken ct) =>
    Results.Ok(await projects.ListAsync(CurrentActor(user), new ProjectBrowseQuery(limit ?? 25, cursor), ct)))
    .RequireAuthorization();
app.MapStageOperatorUi();
app.Run();

static Actor CurrentActor(ClaimsPrincipal user) => new(
    Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), user.IsInRole("Administrator"));

public sealed record LoginInput(string UserName, string Password);
public partial class Program;

// End of API entry point.
