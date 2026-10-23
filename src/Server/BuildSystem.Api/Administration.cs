using System.Security.Claims;
using BuildSystem.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

internal static class Administration
{
    public static void MapAdministration(this WebApplication app)
    {
        app.MapGet("/api/projects", async (ClaimsPrincipal principal, BuildSystemDbContext db, CancellationToken ct) =>
        {
            var actorId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var admin = principal.IsInRole("Administrator");
            return Results.Ok(await db.Projects.AsNoTracking().Where(p => p.Enabled &&
                (admin || db.ProjectMemberships.Any(m => m.ProjectId == p.Id && m.UserId == actorId)))
                .OrderBy(p => p.Id).Select(p => new { p.Id, p.Name }).Take(100).ToListAsync(ct));
        }).RequireAuthorization();

        app.MapPost("/api/admin/users", async (CreateUser input, UserManager<BuildSystemUser> users,
            RoleManager<IdentityRole<Guid>> roles, BuildSystemDbContext db, ClaimsPrincipal actor, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Email) || input.Email.Length > 256 ||
                input.Password is null || input.Password.Length > 1024) return Results.BadRequest(new { code = "InvalidUser" });
            return await db.Database.CreateExecutionStrategy().ExecuteAsync<IResult>(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                if (await users.FindByEmailAsync(input.Email) is not null) return Results.Conflict(new { code = "UserExists" });
                if (!await roles.RoleExistsAsync("Developer"))
                    return Results.Conflict(new { code = "BootstrapRequired" });
                var user = new BuildSystemUser { Id = Guid.NewGuid(), Email = input.Email, UserName = input.Email };
                var created = await users.CreateAsync(user, input.Password);
                if (!created.Succeeded) return Results.BadRequest(new { code = "InvalidUser", errors = created.Errors.Select(x => x.Code) });
                var assigned = await users.AddToRoleAsync(user, "Developer");
                if (!assigned.Succeeded) return Results.BadRequest(new { code = "RoleAssignmentFailed" });
                db.AuditEvents.Add(new AuditEvent
                {
                    Id = Guid.NewGuid(),
                    ActorId = Guid.Parse(actor.FindFirstValue(ClaimTypes.NameIdentifier)!),
                    Action = "UserCreated",
                    TargetId = user.Id,
                    CreatedAt = DateTimeOffset.UtcNow
                });
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Results.Created($"/api/admin/users/{user.Id}", new { user.Id });
            });
        }).RequireAuthorization(policy => policy.RequireRole("Administrator"));

        app.MapPost("/api/admin/project-memberships", async (SetMembership input, BuildSystemDbContext db,
            ClaimsPrincipal actor, CancellationToken ct) =>
        {
            if (!await db.Users.AnyAsync(x => x.Id == input.UserId, ct) ||
                !await db.Projects.AnyAsync(x => x.Id == input.ProjectId, ct)) return Results.NotFound();
            var row = await db.ProjectMemberships.FindAsync([input.UserId, input.ProjectId], ct);
            if (row is null) db.ProjectMemberships.Add(new ProjectMembership
            { UserId = input.UserId, ProjectId = input.ProjectId, Role = "Developer" });
            db.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.NewGuid(),
                ActorId = Guid.Parse(actor.FindFirstValue(ClaimTypes.NameIdentifier)!),
                Action = "ProjectMembershipGranted",
                TargetId = input.ProjectId,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization(policy => policy.RequireRole("Administrator"));
    }

    internal sealed record CreateUser(string Email, string Password);
    internal sealed record SetMembership(Guid UserId, Guid ProjectId);
}
// end
