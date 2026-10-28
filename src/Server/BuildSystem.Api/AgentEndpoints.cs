using System.Security.Claims;
using System.Text.Encodings.Web;
using BuildSystem.Application;
using BuildSystem.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

internal sealed class AgentTokenAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, IAgentControl agents)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.IsHttps) return AuthenticateResult.Fail("HTTPS required");
        var values = Request.Headers.Authorization;
        if (values.Count != 1 || values[0] is not { } value ||
            !value.StartsWith("Bearer ", StringComparison.Ordinal) || value.Length > 256)
            return AuthenticateResult.NoResult();
        var id = await agents.AuthenticateAsync(value[7..], Context.RequestAborted);
        if (id is null) return AuthenticateResult.Fail("Invalid agent credential");
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("agent_id", id.Value.ToString("D"))], Scheme.Name));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}

// A CSRF exemption is endpoint metadata, never a broad path prefix or arbitrary Bearer header.
internal sealed class AgentMachineEndpoint;

internal static class AgentEndpoints
{
    public static void MapAgentEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/admin/agents").RequireAuthorization(p => p.RequireRole("Administrator"));
        admin.MapPost("/enrollments", (CreateEnrollment input, ClaimsPrincipal user, IAgentControl service, CancellationToken ct) =>
            service.CreateEnrollmentAsync(Actor(user), input, ct));
        admin.MapPost("/{id:guid}/approve", (Guid id, ClaimsPrincipal user, IAgentControl service, CancellationToken ct) =>
            service.ApproveAsync(Actor(user), id, ct));
        admin.MapPost("/{id:guid}/rotate", (Guid id, ClaimsPrincipal user, IAgentControl service, CancellationToken ct) =>
            service.RotateAsync(Actor(user), id, ct));
        admin.MapPost("/{id:guid}/revoke", async (Guid id, ClaimsPrincipal user, IAgentControl service, CancellationToken ct) =>
        { await service.RevokeAsync(Actor(user), id, ct); return Results.NoContent(); });
        admin.MapGet("/", (ClaimsPrincipal user, IAgentControl service, CancellationToken ct) => service.ListAsync(Actor(user), ct));

        app.MapPost("/api/agent/register", async (HttpContext context, RegisterAgent input, IAgentControl service, CancellationToken ct) =>
        {
            if (!context.Request.IsHttps) return Results.BadRequest(new { code = "HttpsRequired" });
            return Results.Accepted(value: await service.RegisterAsync(input, ct));
        }).AllowAnonymous().WithMetadata(new AgentMachineEndpoint()).RequireRateLimiting("AgentEnrollment");

        var machine = app.MapGroup("/api/agent").RequireAuthorization("AgentOnly")
            .WithMetadata(new AgentMachineEndpoint());
        machine.MapPost("/capabilities", async (AgentCapabilities input, ClaimsPrincipal user, IAgentControl service, CancellationToken ct) =>
        { await service.SetCapabilitiesAsync(Id(user), input, ct); return Results.NoContent(); });
        machine.MapPost("/poll", async (ClaimsPrincipal user, IAgentControl service, CancellationToken ct) =>
        {
            var grant = await service.PollAsync(Id(user), ct);
            return grant is null ? Results.NoContent() : Results.Ok(grant);
        });
        machine.MapPost("/heartbeat", (LeaseHeartbeat input, ClaimsPrincipal user, IAgentControl service, CancellationToken ct) =>
            service.HeartbeatAsync(Id(user), input, ct));
        machine.MapPost("/events", (ReportAgentEvent input, ClaimsPrincipal user, IAgentControl service, CancellationToken ct) =>
            service.ReportAsync(Id(user), input, ct));
        machine.MapPost("/stage/poll", async (HttpContext context, IArchiveStageControl service, CancellationToken ct) =>
        {
            var grant = await service.PollAsync(context.Request.Headers.Authorization.ToString()[7..], ct);
            return grant is null ? Results.NoContent() : Results.Ok(grant);
        });
        machine.MapPost("/stage/retirement-plan", async (HttpContext context, IStageRetirementPlans service, CancellationToken ct) =>
        {
            var plan = await service.PollAsync(context.Request.Headers.Authorization.ToString()[7..], ct);
            return plan is null ? Results.NoContent() : Results.Ok(plan);
        });
        machine.MapPost("/stage/heartbeat", (HttpContext context, LeaseHeartbeat input, IArchiveStageControl service, CancellationToken ct) =>
            service.HeartbeatAsync(context.Request.Headers.Authorization.ToString()[7..], input, ct));
        machine.MapPost("/stage/complete", (HttpContext context, CompleteStage input, IStageCompletion service, CancellationToken ct) =>
            service.CompleteAsync(context.Request.Headers.Authorization.ToString()[7..], input, ct));
        machine.MapPost("/artifacts/staged", async (HttpContext context, ReportStagedArtifact input,
            IArtifactReceipts service, CancellationToken ct) =>
        {
            var credential = context.Request.Headers.Authorization.ToString()[7..];
            return Results.Ok(await service.ReportAsync(credential, input, ct));
        });
        machine.MapPost("/archives/inventory", async (HttpContext context, ArchiveInventoryReport input,
            IArchiveCatalog service, CancellationToken ct) =>
        {
            var credential = context.Request.Headers.Authorization.ToString()[7..];
            return Results.Ok(await service.ReportAsync(credential, input, ct));
        });
    }

    private static Guid Id(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue("agent_id")!);
    private static Actor Actor(ClaimsPrincipal user) => new(Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), user.IsInRole("Administrator"));
}
// end
