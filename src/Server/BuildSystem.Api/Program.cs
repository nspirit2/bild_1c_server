using System.Security.Claims;
using System.Text.Json.Serialization;
using BuildSystem.Application;
using BuildSystem.Contracts;
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
// A dedicated read instance leaves the concrete ledger and mutation port disabled by default.
builder.Services.AddSingleton(services => new TargetCredentialOptions
{
    Enabled = services.GetRequiredService<IConfiguration>().GetValue<bool>("TargetCredentials:Enabled")
});
builder.Services.AddScoped<ITargetCredentials, SqlTargetCredentials>();
builder.Services.AddScoped<ITargetCreationPermitAgentReads, SqlTargetCreationPermits>();
// Independent, default-off metadata, resolution and machine-delivery gates. Resolve configuration
// through the final host provider; diagnostics opt-in never enables credential delivery.
builder.Services.AddSingleton(services => new TargetCreationOperationOptions
{
    Enabled = services.GetRequiredService<IConfiguration>().GetValue<bool>("TargetCreationOperation:Enabled"),
    CanonicalMappings = services.GetRequiredService<RestoreIntentOptions>().CanonicalMappings
});
builder.Services.AddSingleton(services => new TargetCreationPermitOptions
{
    Enabled = services.GetRequiredService<IConfiguration>().GetValue<bool>("TargetCreationPermit:Enabled"),
    AgentStartEnabled = services.GetRequiredService<IConfiguration>().GetValue<bool>("TargetCreationPermit:AgentStartEnabled"),
    AgentReadEnabled = services.GetRequiredService<IConfiguration>().GetValue<bool>("TargetCreationPermit:AgentReadEnabled"),
    CredentialBindingEnabled = services.GetRequiredService<IConfiguration>().GetValue<bool>("TargetCreationPermit:CredentialBindingEnabled")
});
builder.Services.AddSingleton(services => new TargetCreationPermitHttpOptions
{
    Enabled = services.GetRequiredService<IConfiguration>().GetValue<bool>("TargetCreationPermitHttp:Enabled")
});
builder.Services.AddSingleton(services => new TargetCreationCredentialResolutionOptions
{
    Enabled = services.GetRequiredService<IConfiguration>().GetValue<bool>("TargetCreationCredentialResolution:Enabled")
});
builder.Services.AddSingleton(services => new TargetCreationCredentialDeliveryOptions
{
    Enabled = services.GetRequiredService<IConfiguration>().GetValue<bool>("TargetCreationCredentialDelivery:Enabled")
});
builder.Services.AddScoped<ITargetCreationCredentialResolver, SqlTargetCreationCredentialResolver>();
builder.Services.AddSingleton<ITargetCredentialProtector, DataProtectionTargetCredentialProtector>();
builder.Services.AddSingleton(new TargetCreationDiagnosticsOptions
{
    Enabled = builder.Configuration.GetValue<bool>("TargetCreationDiagnostics:Enabled")
});
builder.Services.AddScoped<ITargetCreationOperationReads>(services => new SqlTargetCreationOperations(
    services.GetRequiredService<BuildSystemDbContext>(), new TargetCreationOperationOptions
    {
        Enabled = services.GetRequiredService<TargetCreationDiagnosticsOptions>().Enabled
    }));
builder.Services.AddSingleton(new ArchiveStageOptions { Enabled = builder.Configuration.GetValue<bool>("ArchiveStage:Enabled") });
builder.Services.AddSingleton(new StageRetirementOptions { Enabled = builder.Configuration.GetValue<bool>("StageRetirement:Enabled") });
// Canonical identity assertions are operator-owned configuration, never HTTP input.
// Bind eagerly and strictly: malformed dictionary keys/values must fail startup, not disappear.
builder.Services.AddSingleton(services =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var mappings = configuration.GetSection("RestoreIntent:CanonicalMappings")
        .Get<Dictionary<string, RestoreIntentCanonicalMapping>>(options => options.ErrorOnUnknownConfiguration = true) ?? [];
    if (mappings.Count > 1024 || mappings.Values.Any(mapping => mapping.CanonicalClusterId == Guid.Empty || mapping.CanonicalSqlServerId == Guid.Empty))
        throw new InvalidOperationException("RestoreIntent canonical mappings require nonempty canonical GUIDs and at most 1024 entries.");
    return new RestoreIntentOptions
    {
        Enabled = configuration.GetValue<bool>("RestoreIntent:Enabled"),
        // The binder does not support Guid dictionary keys; bind strings, then parse every key explicitly.
        CanonicalMappings = mappings.ToDictionary(entry => Guid.TryParse(entry.Key, out var poolId) && poolId != Guid.Empty
                ? poolId : throw new InvalidOperationException("RestoreIntent canonical mapping keys must be nonempty pool GUIDs."),
                entry => entry.Value)
    };
});
// Endpoint identities are operator configuration; validate the final configuration even when disabled.
builder.Services.AddSingleton(services =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var section = configuration.GetSection("TargetObservation:EndpointMappings");
    var entries = section.GetChildren().ToArray();
    if (section.Value is not null || entries.Length > 1024 || entries.Any(entry =>
            entry.Value is not null || !entry.GetChildren().Any()))
        throw new InvalidOperationException("TargetObservation endpoint mappings require nonempty objects and at most 1024 entries.");
    var mappings = section.Get<Dictionary<string, TargetObservationEndpointMapping>>(
        options => options.ErrorOnUnknownConfiguration = true) ?? [];
    if (mappings.Values.Any(mapping => mapping is null || mapping.RacEndpointMappingId == Guid.Empty ||
            mapping.SqlEndpointMappingId == Guid.Empty))
        throw new InvalidOperationException("TargetObservation endpoint mappings require nonempty endpoint GUIDs.");
    return new TargetObservationOptions
    {
        Enabled = configuration.GetValue<bool>("TargetObservation:Enabled"),
        // Parsing after binding also makes differently formatted duplicate GUIDs fail startup.
        EndpointMappings = mappings.ToDictionary(entry => Guid.TryParse(entry.Key, out var poolId) && poolId != Guid.Empty
                ? poolId : throw new InvalidOperationException("TargetObservation endpoint mapping keys must be nonempty pool GUIDs."),
            entry => entry.Value)
    };
});
builder.Services.AddSingleton(new StageCleanupOptions { Enabled = builder.Configuration.GetValue<bool>("StageCleanup:Enabled") });
builder.Services.AddSingleton(new StageCleanupResultOptions { Enabled = builder.Configuration.GetValue<bool>("StageCleanupResult:Enabled") });
builder.Services.AddSingleton(new StagingAllocationReleaseOptions { Enabled = builder.Configuration.GetValue<bool>("StagingAllocationRelease:Enabled") });
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
// Resolve before accepting traffic (also when disabled), after all host configuration providers apply.
_ = app.Services.GetRequiredService<RestoreIntentOptions>();
_ = app.Services.GetRequiredService<TargetObservationOptions>();
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
// Permit metadata has a prefix-wide boundary before redirect/authentication/CSRF/routing.
// Even unmatched methods and framework failures are noncacheable and never expose exception text.
app.Use(async (context, next) =>
{
    if (!TargetCreationPermitEndpoints.IsPermitPath(context.Request.Path))
    {
        await next(context);
        return;
    }
    TargetCreationPermitEndpoints.SetHeaders(context.Response);
    context.Response.OnStarting(() =>
    {
        TargetCreationPermitEndpoints.SetHeaders(context.Response);
        return Task.CompletedTask;
    });
    if (!context.Request.IsHttps)
    {
        await TargetCreationPermitEndpoints.ErrorAsync(context, 400, "TargetCreationPermitHttpsRequired");
        return;
    }
    if (HttpMethods.IsGet(context.Request.Method) &&
        context.Request.Path.StartsWithSegments(TargetCreationPermitEndpoints.AgentReadPrefix) &&
        (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding")))
    {
        await TargetCreationPermitEndpoints.ErrorAsync(context, 400, "InvalidTargetCreationPermit");
        return;
    }
    if (context.Request.ContentLength > 4096)
    {
        await TargetCreationPermitEndpoints.ErrorAsync(context, 413, "TargetCreationPermitBodyTooLarge");
        return;
    }
    try { await next(context); }
    catch (RequestRejectedException ex) { await TargetCreationPermitEndpoints.RejectedAsync(context, ex); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); }
    catch (Exception) { await TargetCreationPermitEndpoints.ErrorAsync(context, 503, "TargetCreationPermitUnavailable"); }
});
// Never redirect an HTTP machine credential request: redirects can forward Bearer credentials.
// Apply even to unmatched methods under this narrow prefix, before body reads, authorization and HSTS.
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api/agents/target-creation-credentials"))
    {
        await next(context);
        return;
    }
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.Pragma = "no-cache";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.OnStarting(() =>
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return Task.CompletedTask;
    });
    if (!context.Request.IsHttps)
    {
        await TargetCreationCredentialDeliveryEndpoints.ErrorAsync(context, 400, "TargetCreationCredentialHttpsRequired");
        return;
    }
    try { await next(context); }
    catch (Exception)
    {
        await TargetCreationCredentialDeliveryEndpoints.ErrorAsync(context, 503, "TargetCreationCredentialDeliveryUnavailable");
    }
});
// Run before redirects, body limits and authorization so failures cannot cache sensitive diagnostics.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/admin/target-credentials") ||
        context.Request.Path.StartsWithSegments("/api/admin/target-creation-operations") ||
        context.Request.Path.StartsWithSegments("/admin/creation-diagnostics") ||
        context.Request.Path.StartsWithSegments("/admin/target-credentials"))
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });
    }
    await next(context);
});
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    if (context.Request.ContentLength > 16 * 1024)
    {
        if (context.Request.Path.StartsWithSegments("/api/agents/target-creation-credentials"))
            await TargetCreationCredentialDeliveryEndpoints.ErrorAsync(context, 413, "TargetCreationCredentialDeliveryBodyTooLarge");
        else context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
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
        if (TargetCreationPermitEndpoints.IsPermitPath(context.Request.Path))
            await TargetCreationPermitEndpoints.RejectedAsync(context, ex);
        else if (context.Request.Path.StartsWithSegments("/api/agents/target-creation-credentials"))
            await TargetCreationCredentialDeliveryEndpoints.ErrorAsync(context, 503, "TargetCreationCredentialDeliveryUnavailable");
        else
        {
            context.Response.StatusCode = ex.StatusCode;
            await context.Response.WriteAsJsonAsync(new { code = ex.Code });
        }
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
app.MapTargetCreationCredentialDelivery();
app.MapTargetCreationPermits();
app.MapTargetCredentials();
app.MapTargetCreationDiagnostics();
app.MapTargetCreationDiagnosticsUi();
app.MapTargetCredentialsUi();
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
app.MapGet("/api/test-environments/{id:guid}/cleanup-status", async (Guid id, ClaimsPrincipal user,
    ICleanupStatus status, CancellationToken ct) =>
{
    var value = await status.GetAsync(CurrentActor(user), id, ct);
    return value is null ? Results.NotFound() : Results.Ok(value);
}).RequireAuthorization();
app.MapPost("/api/test-environments/{id:guid}/stage-retirement-intents", async (Guid id,
    StageRetirementRequest request, ClaimsPrincipal user, IStageRetirement retirement, CancellationToken ct) =>
    Results.Ok(await retirement.RequestAsync(CurrentActor(user), id, request, ct)))
    .RequireAuthorization();
app.MapPost("/api/test-environments/{id:guid}/restore-intents", async (Guid id,
    RequestRestoreIntent request, ClaimsPrincipal user, IRestoreIntents intents, CancellationToken ct) =>
    Results.Ok(await intents.RequestAsync(CurrentActor(user), id, request, ct)))
    .RequireAuthorization();
app.MapPost("/api/test-environments/{id:guid}/target-observations", async (Guid id,
    RequestTargetObservation input, ClaimsPrincipal user, ITargetObservations observations, CancellationToken ct) =>
    Results.Ok(await observations.RequestAsync(CurrentActor(user), id, input.ExpectedRestoreIntentId, ct)))
    .RequireAuthorization();
app.MapGet("/api/agents/target-observations/{id:guid}", async (Guid id, HttpContext context,
    ITargetObservationDelivery delivery, CancellationToken ct) =>
    Results.Ok(await delivery.ReadAsync(context.Request.Headers.Authorization.ToString()[7..], id, ct)))
    .RequireAuthorization("AgentOnly").WithMetadata(new AgentMachineEndpoint());
app.MapPost("/api/agents/target-observation-receipts", async (HttpContext context,
    ReportTargetObservation input, ITargetObservationReceipts receipts, CancellationToken ct) =>
    Results.Ok(await receipts.ReportAsync(context.Request.Headers.Authorization.ToString()[7..], input, ct)))
    .RequireAuthorization("AgentOnly").WithMetadata(new AgentMachineEndpoint());
app.MapPost("/api/admin/stage-cleanup-results/{resultId:guid}/release-staging", async (Guid resultId,
    ReleaseStagingAllocation input, ClaimsPrincipal user, IStagingAllocationRelease release, CancellationToken ct) =>
    Results.Ok(await release.ReleaseAsync(CurrentActor(user), resultId, input, ct)))
    .RequireAuthorization(policy => policy.RequireRole("Administrator"));
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
