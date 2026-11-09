using System.Data.Common;
using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace BuildSystem.IntegrationTests;

// Disposable metadata SQL only: these tests never contact a target SQL server, RAC or agent.
public sealed class SqlTargetCredentialTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly TargetCredentialOptions Enabled = new() { Enabled = true };
    private static TargetCredentialBinding Binding() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static TargetCredentialInput Input() => new("cluster-user-canary", "cluster-password-canary", "sql-user-canary", "sql-password-canary");
    private static DataProtectionTargetCredentialProtector Codec() => new(new EphemeralDataProtectionProvider());
    private static TargetCredentialScope Scope(TargetCredentialBinding binding) => new(binding.CredentialId,
        binding.CanonicalClusterId, binding.CanonicalSqlServerId, binding.EndpointMappingId);
    private static string[] Secrets(TargetCredentialInput input) => [input.ClusterUser, input.ClusterPassword, input.SqlUser, input.SqlPassword];
    private static void NoSecrets(string text, TargetCredentialInput input)
    {
        foreach (var secret in Secrets(input)) Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
    }
    private static async Task Rejected(Func<Task> action, int status)
        => Assert.Equal(status, (await Assert.ThrowsAsync<RequestRejectedException>(action)).StatusCode);

    private static async Task<Actor> SeedAsync(SqlServerTestDatabase sql)
    {
        await sql.ApplyMigrationsAsync();
        // The hint is deliberately false: only the current database membership authorizes this actor.
        var admin = new Actor(Guid.NewGuid(), false);
        await using var db = sql.NewContext();
        db.Users.Add(new BuildSystemUser { Id = admin.UserId, UserName = "credential-admin-" + admin.UserId.ToString("N"),
            NormalizedUserName = "CREDENTIAL-ADMIN-" + admin.UserId.ToString("N") });
        var role = await db.Roles.SingleOrDefaultAsync(r => r.NormalizedName == "ADMINISTRATOR")
            ?? new IdentityRole<Guid> { Id = Guid.NewGuid(), Name = "Administrator", NormalizedName = "ADMINISTRATOR" };
        if (db.Entry(role).State == EntityState.Detached) db.Roles.Add(role);
        db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = admin.UserId, RoleId = role.Id });
        await db.SaveChangesAsync();
        return admin;
    }

    private sealed class NoConnection : DbConnectionInterceptor
    {
        public int Attempts { get; private set; }
        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result)
        {
            Attempts++;
            throw new InvalidOperationException("A disabled credential service must not open SQL.");
        }
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw new InvalidOperationException("A disabled credential service must not open SQL.");
        }
    }

    private static BuildSystemDbContext OfflineContext(NoConnection guard) => new(
        new DbContextOptionsBuilder<BuildSystemDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=true;Connect Timeout=1;TrustServerCertificate=true")
            .AddInterceptors(guard).Options);

    [Fact]
    public async Task Default_off_store_get_and_revoke_reject_before_any_database_connection()
    {
        var guard = new NoConnection();
        await using var db = OfflineContext(guard);
        Assert.False(new TargetCredentialOptions().Enabled);
        var service = new SqlTargetCredentials(db, new TargetCredentialOptions(), Codec());
        var actor = new Actor(Guid.NewGuid(), true); var binding = Binding();
        await Rejected(() => service.StoreAsync(actor, binding, Input(), Ct), 503);
        await Rejected(() => service.GetAsync(actor, binding.CredentialId, Ct), 503);
        await Rejected(() => service.RevokeAsync(actor, binding.CredentialId, 1, Ct), 503);
        Assert.Equal(0, guard.Attempts);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Explicitly_disabled_service_fails_closed_even_for_invalid_ids_and_secret_input()
    {
        var guard = new NoConnection();
        await using var db = OfflineContext(guard);
        var service = new SqlTargetCredentials(db, new TargetCredentialOptions { Enabled = false }, Codec());
        var actor = new Actor(Guid.Empty, true);
        var invalid = new TargetCredentialInput("", "password\ncanary", "", "");
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => service.StoreAsync(actor,
            new(Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty), invalid, Ct));
        Assert.Equal(503, error.StatusCode);
        Assert.DoesNotContain("password", error.ToString(), StringComparison.OrdinalIgnoreCase);
        await Rejected(() => service.GetAsync(actor, Guid.Empty, Ct), 503);
        await Rejected(() => service.RevokeAsync(actor, Guid.Empty, -1, Ct), 503);
        Assert.Equal(0, guard.Attempts);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [SqlServerFact]
    public async Task Store_retains_only_ciphertext_and_metadata_and_codec_binds_every_identifier()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var actor = await SeedAsync(sql);
        await using var db = sql.NewContext(); var codec = Codec(); var binding = Binding(); var input = Input();
        var service = new SqlTargetCredentials(db, Enabled, codec);
        var metadata = await service.StoreAsync(actor, binding, input, Ct);
        Assert.Equal(binding, metadata.Binding); Assert.Equal(TargetCredentialState.Stored, metadata.State);
        Assert.Equal(1L, metadata.Revision); Assert.Null(metadata.RevokedAt); Assert.False(metadata.Executable);
        Assert.Equal(TimeSpan.Zero, metadata.CreatedAt.Offset);
        var row = await db.TargetCredentials.AsNoTracking().SingleAsync();
        Assert.False(string.IsNullOrWhiteSpace(row.Ciphertext)); NoSecrets(row.Ciphertext, input);
        Assert.Equal(new TargetCredentialSecrets(input.ClusterUser, input.ClusterPassword, input.SqlUser, input.SqlPassword),
            codec.Unprotect(Scope(binding), row.Ciphertext));
        foreach (var wrong in new[] { binding with { CredentialId = Guid.NewGuid() },
                     binding with { CanonicalClusterId = Guid.NewGuid() }, binding with { CanonicalSqlServerId = Guid.NewGuid() },
                     binding with { EndpointMappingId = Guid.NewGuid() } })
            Assert.Throws<TargetCredentialProtectionException>(() => codec.Unprotect(Scope(wrong), row.Ciphertext));
        NoSecrets(JsonSerializer.Serialize(metadata), input);
        NoSecrets(JsonSerializer.Serialize(await service.GetAsync(actor, binding.CredentialId, Ct)), input);
        NoSecrets(JsonSerializer.Serialize(await db.AuditEvents.AsNoTracking().ToListAsync()), input);
        Assert.Single(await db.AuditEvents.ToListAsync());

        // Real codec validation failures are uniformly redacted and must not persist a partial row/audit.
        var invalid = input with { SqlPassword = input.SqlPassword + "\n" };
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => service.StoreAsync(actor, Binding(), invalid, Ct));
        Assert.Equal(503, error.StatusCode); NoSecrets(error.ToString(), input);
        Assert.Single(await db.TargetCredentials.AsNoTracking().ToListAsync());
        Assert.Single(await db.AuditEvents.AsNoTracking().ToListAsync());
    }

    [SqlServerFact]
    public async Task Duplicate_id_conflicts_without_replacing_ciphertext_or_adding_audit()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var actor = await SeedAsync(sql);
        await using var db = sql.NewContext(); var binding = Binding(); var service = new SqlTargetCredentials(db, Enabled, Codec());
        var original = await service.StoreAsync(actor, binding, Input(), Ct);
        var ciphertext = (await db.TargetCredentials.AsNoTracking().SingleAsync()).Ciphertext;
        var audits = await db.AuditEvents.CountAsync();
        await Rejected(() => service.StoreAsync(actor, binding, Input(), Ct), 409);
        await Rejected(() => service.StoreAsync(actor, binding with { EndpointMappingId = Guid.NewGuid() },
            Input() with { SqlPassword = "replacement-canary" }, Ct), 409);
        var stored = await db.TargetCredentials.AsNoTracking().SingleAsync();
        Assert.Equal(ciphertext, stored.Ciphertext); Assert.Equal(1L, stored.Revision);
        Assert.Equal(original, await service.GetAsync(actor, binding.CredentialId, Ct));
        Assert.Equal(audits, await db.AuditEvents.CountAsync());
    }

    [SqlServerFact]
    public async Task Store_get_and_revoke_require_current_database_administrator_not_actor_hint()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var actor = await SeedAsync(sql);
        await using var db = sql.NewContext(); var binding = Binding(); var service = new SqlTargetCredentials(db, Enabled, Codec());
        await service.StoreAsync(actor, binding, Input(), Ct);
        Assert.False((await service.GetAsync(actor, binding.CredentialId, Ct)).Executable);
        var impostor = new Actor(Guid.NewGuid(), true);
        await Rejected(() => service.GetAsync(impostor, binding.CredentialId, Ct), 403);
        await Rejected(() => service.RevokeAsync(impostor, binding.CredentialId, 1, Ct), 403);
        await Rejected(() => service.StoreAsync(impostor, Binding(), Input(), Ct), 403);
        await using (var membership = sql.NewContext())
        {
            membership.UserRoles.RemoveRange(await membership.UserRoles.Where(r => r.UserId == actor.UserId).ToListAsync());
            await membership.SaveChangesAsync();
        }
        var staleHint = new Actor(actor.UserId, true);
        await Rejected(() => service.GetAsync(staleHint, binding.CredentialId, Ct), 403);
        await Rejected(() => service.RevokeAsync(staleHint, binding.CredentialId, 1, Ct), 403);
        await Rejected(() => service.StoreAsync(staleHint, Binding(), Input(), Ct), 403);
        Assert.Equal(TargetCredentialState.Stored, (await db.TargetCredentials.AsNoTracking().SingleAsync()).State);
        Assert.Single(await db.AuditEvents.AsNoTracking().ToListAsync());
    }

    [SqlServerFact]
    public async Task Revoke_requires_expected_revision_and_is_terminal_revision_two_with_ciphertext_retained()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var actor = await SeedAsync(sql);
        await using var db = sql.NewContext(); var binding = Binding(); var service = new SqlTargetCredentials(db, Enabled, Codec());
        var stored = await service.StoreAsync(actor, binding, Input(), Ct);
        var ciphertext = (await db.TargetCredentials.AsNoTracking().SingleAsync()).Ciphertext;
        await Rejected(() => service.RevokeAsync(actor, binding.CredentialId, 2, Ct), 409);
        var revoked = await service.RevokeAsync(actor, binding.CredentialId, 1, Ct);
        Assert.Equal(TargetCredentialState.Revoked, revoked.State); Assert.Equal(2L, revoked.Revision);
        Assert.Equal(stored.CreatedAt, revoked.CreatedAt); Assert.Equal(binding, revoked.Binding);
        Assert.NotNull(revoked.RevokedAt); Assert.Equal(TimeSpan.Zero, revoked.RevokedAt!.Value.Offset);
        Assert.True(revoked.RevokedAt >= revoked.CreatedAt); Assert.False(revoked.Executable);
        await Rejected(() => service.RevokeAsync(actor, binding.CredentialId, 1, Ct), 409);
        await Rejected(() => service.RevokeAsync(actor, binding.CredentialId, 2, Ct), 409);
        await Rejected(() => service.StoreAsync(actor, binding, Input(), Ct), 409);
        Assert.Equal(revoked, await service.GetAsync(actor, binding.CredentialId, Ct));
        var row = await db.TargetCredentials.AsNoTracking().SingleAsync();
        Assert.Equal(ciphertext, row.Ciphertext); Assert.Equal(2L, row.Revision);
        Assert.Equal(2, await db.AuditEvents.CountAsync());
    }

    [SqlServerFact]
    public async Task Ef_guard_rejects_ciphertext_binding_and_creation_mutations_and_deletion()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var actor = await SeedAsync(sql); var binding = Binding();
        await using (var db = sql.NewContext())
            await new SqlTargetCredentials(db, Enabled, Codec()).StoreAsync(actor, binding, Input(), Ct);
        foreach (var mutate in new Action<TargetCredentialRecord>[] { r => r.Ciphertext = "replacement",
                     r => r.CanonicalClusterId = Guid.NewGuid(), r => r.CanonicalSqlServerId = Guid.NewGuid(),
                     r => r.EndpointMappingId = Guid.NewGuid(), r => r.CreatedAt = r.CreatedAt.AddMinutes(1) })
        {
            await using var db = sql.NewContext(); var row = await db.TargetCredentials.SingleAsync(); mutate(row);
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await using (var db = sql.NewContext())
        {
            db.TargetCredentials.Remove(await db.TargetCredentials.SingleAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await using var verify = sql.NewContext();
        Assert.Single(await verify.TargetCredentials.AsNoTracking().ToListAsync());
        Assert.Single(await verify.AuditEvents.AsNoTracking().ToListAsync());
    }

    [SqlServerFact]
    public async Task Sql_constraints_reject_invalid_state_empty_identifiers_non_utc_and_null_revocation_bypassing_ef_guard()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var actor = await SeedAsync(sql); var binding = Binding();
        await using var db = sql.NewContext();
        await new SqlTargetCredentials(db, Enabled, Codec()).StoreAsync(actor, binding, Input(), Ct);
        // ExecuteSqlRaw bypasses SaveChanges and its immutability guard: SQL itself must reject these rows.
        foreach (var assignment in new[] { "[State] = N'Invalid'", "[CredentialId] = '00000000-0000-0000-0000-000000000000'",
                     "[CanonicalClusterId] = '00000000-0000-0000-0000-000000000000'",
                     "[CanonicalSqlServerId] = '00000000-0000-0000-0000-000000000000'",
                     "[EndpointMappingId] = '00000000-0000-0000-0000-000000000000'",
                     "[CreatedAt] = '2026-01-01T00:00:00+03:00'", "[State] = N'Revoked', [Revision] = 2, [RevokedAt] = NULL" })
        {
            // Assignment is from the fixed literal allowlist above, never from request/test parameters.
#pragma warning disable EF1003
            var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
                "UPDATE [TargetCredentials] SET " + assignment + " WHERE [CredentialId] = {0}", binding.CredentialId));
#pragma warning restore EF1003
            Assert.Equal(547, error.Number);
        }
        var row = await db.TargetCredentials.AsNoTracking().SingleAsync();
        Assert.Equal(TargetCredentialState.Stored, row.State); Assert.Equal(1L, row.Revision); Assert.Null(row.RevokedAt);
        Assert.Single(await db.AuditEvents.AsNoTracking().ToListAsync());
    }
}