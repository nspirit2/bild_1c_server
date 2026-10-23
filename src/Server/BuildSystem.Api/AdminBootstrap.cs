using BuildSystem.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

internal static class AdminBootstrap
{
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            Console.Error.WriteLine("Usage: bootstrap-admin <email>; password is read from hidden terminal input or protected stdin, never arguments.");
            return 2;
        }
        // No ordinary EF/Identity logging here: a user-supplied password must not enter diagnostics.
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BuildSystemDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<BuildSystemUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        if (await users.GetUsersInRoleAsync("Administrator") is { Count: > 0 })
        {
            Console.Error.WriteLine("An administrator already exists. Bootstrap does not elevate existing accounts.");
            return 1;
        }
        if (await users.FindByEmailAsync(args[0]) is not null)
        {
            Console.Error.WriteLine("Account already exists; refusing to modify it.");
            return 1;
        }
        var password = ReadPassword();
        if (password is null) return 2;
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            // Serializable transaction and a transaction-owned lock prevent parallel bootstrap races.
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            await db.Database.ExecuteSqlRawAsync("DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource=N'BuildSystem:BootstrapAdmin', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=10000; IF @r < 0 THROW 51000, 'Bootstrap lock unavailable', 1;");
            if ((await users.GetUsersInRoleAsync("Administrator")).Count > 0)
            {
                Console.Error.WriteLine("An administrator was created concurrently; no changes made.");
                return 1;
            }
            foreach (var role in new[] { "Administrator", "Developer" })
                if (!await roles.RoleExistsAsync(role))
                {
                    var roleResult = await roles.CreateAsync(new IdentityRole<Guid>(role));
                    if (!roleResult.Succeeded) return Failed(roleResult);
                }
            var user = new BuildSystemUser { Id = Guid.NewGuid(), UserName = args[0], Email = args[0] };
            var created = await users.CreateAsync(user, password);
            if (!created.Succeeded) return Failed(created);
            var assigned = await users.AddToRoleAsync(user, "Administrator");
            if (!assigned.Succeeded) return Failed(assigned);
            db.AuditEvents.Add(new AuditEvent { Id = Guid.NewGuid(), ActorId = user.Id,
                Action = "AdministratorBootstrapped", TargetId = user.Id, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            Console.WriteLine("Administrator created. No password has been printed.");
            return 0;
        });
    }

    private static int Failed(IdentityResult result)
    {
        // Codes only, never the supplied credentials or exception details.
        Console.Error.WriteLine("Bootstrap rejected: " + string.Join(", ", result.Errors.Select(x => x.Code)));
        return 1;
    }

    private static string? ReadPassword()
    {
        if (Console.IsInputRedirected) return Console.ReadLine();
        Console.Error.Write("Password (hidden): ");
        var value = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0) value.Length--;
            }
            else if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        return value.ToString();
    }
}
// End.
