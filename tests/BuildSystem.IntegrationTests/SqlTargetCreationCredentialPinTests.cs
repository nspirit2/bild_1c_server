using System.Data.Common;
using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Domain;
using BuildSystem.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Seed = BuildSystem.IntegrationTests.SqlTargetCreationOperationTests.Seed;

namespace BuildSystem.IntegrationTests;

// Disposable metadata SQL only. No target connection, worker, RAC or secret-bearing receipt.
public sealed class SqlTargetCreationCredentialPinTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly TargetCredentialInput Input = new("pin-cluster-user-canary", "pin-cluster-password-canary",
        "pin-sql-user-canary", "pin-sql-password-canary");
    private static TargetCreationPermitOptions Enabled => new()
        { Enabled = true, AgentStartEnabled = true, CredentialBindingEnabled = true };
    private static SqlTargetCreationPermits Permits(BuildSystemDbContext db, Seed seed) => new(db, Enabled, seed.Options);
    private static SqlTargetCredentials Credentials(BuildSystemDbContext db) => new(db,
        new TargetCredentialOptions { Enabled = true }, new DataProtectionTargetCredentialProtector(new EphemeralDataProtectionProvider()));
    private static async Task<TargetCreationOperationReceipt> Register(SqlServerTestDatabase sql, Seed seed)
    {
        await using var db = sql.NewContext();
        return await new SqlTargetCreationOperations(db, seed.Options).RegisterAsync(seed.Admin,
            seed.Order.EnvironmentId, seed.Restore.RestoreIntentId, Ct);
    }
    private static TargetCredentialBinding Binding(TargetCreationOperationReceipt operation) => new(Guid.NewGuid(),
        operation.CanonicalClusterId, operation.CanonicalSqlServerId, Guid.NewGuid());
    private static async Task Store(SqlServerTestDatabase sql, Seed seed, TargetCredentialBinding binding)
    {
        await using var db = sql.NewContext();
        await Credentials(db).StoreAsync(seed.Admin, binding, Input, Ct);
    }
    private static Task<TargetCreationPermitReceipt> Issue(SqlTargetCreationPermits service, Seed seed,
        TargetCreationOperationReceipt operation, Guid permit, Guid attempt, TargetCredentialBinding binding)
        => service.IssueWithCredentialAsync(seed.Admin, operation.OperationId, operation.Revision,
            permit, attempt, binding.CredentialId, binding.EndpointMappingId, Ct);
    private static async Task<RequestRejectedException> Rejected(Func<Task> action, int status = 409, string? code = null)
    {
        var error = await Assert.ThrowsAsync<RequestRejectedException>(action);
        Assert.Equal(status, error.StatusCode);
        if (code is not null) Assert.Equal(code, error.Code);
        NoSecrets(error.ToString());
        return error;
    }
    private static void NoSecrets(string text)
    {
        foreach (var secret in new[] { Input.ClusterUser, Input.ClusterPassword, Input.SqlUser, Input.SqlPassword })
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Ciphertext", text, StringComparison.OrdinalIgnoreCase);
    }
    private static void Pins(TargetCreationPermitReceipt receipt, TargetCredentialBinding binding)
    {
        Assert.Equal(binding.CredentialId, receipt.CredentialId);
        Assert.Equal(binding.EndpointMappingId, receipt.EndpointMappingId);
        Assert.False(receipt.Executable); Assert.True(receipt.ScheduledJobsMustRemainBlocked);
        NoSecrets(JsonSerializer.Serialize(receipt)); NoSecrets(receipt.ToString());
    }
    private sealed class NoConnection : DbConnectionInterceptor
    {
        public int Attempts { get; private set; }
        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result)
        {
            Attempts++;
            throw new InvalidOperationException("Disabled credential binding must not open SQL.");
        }
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw new InvalidOperationException("Disabled credential binding must not open SQL.");
        }
    }
    private static BuildSystemDbContext Offline(NoConnection guard) => new(new DbContextOptionsBuilder<BuildSystemDbContext>()
        .UseSqlServer("Server=unused;Database=unused;Integrated Security=true;Connect Timeout=1;TrustServerCertificate=true")
        .AddInterceptors(guard).Options);

    [Fact]
    public async Task Credential_binding_is_default_off_and_new_issue_rejects_without_opening_SQL()
    {
        Assert.False(new TargetCreationPermitOptions().CredentialBindingEnabled);
        var guard = new NoConnection(); await using var db = Offline(guard);
        ITargetCreationPermits service = new SqlTargetCreationPermits(db, new TargetCreationPermitOptions { Enabled = true },
            new TargetCreationOperationOptions { Enabled = true });
        await Rejected(() => service.IssueWithCredentialAsync(new Actor(Guid.NewGuid(), true), Guid.NewGuid(), 1,
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Ct));
        Assert.Equal(0, guard.Attempts); Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Every_disabled_gate_precedes_invalid_input_and_any_database_connection()
    {
        foreach (var gates in new[] { (Permit: false, Creation: true, Binding: true),
                     (Permit: true, Creation: false, Binding: true), (Permit: true, Creation: true, Binding: false) })
        {
            var guard = new NoConnection(); await using var db = Offline(guard);
            var service = new SqlTargetCreationPermits(db, new TargetCreationPermitOptions
                { Enabled = gates.Permit, CredentialBindingEnabled = gates.Binding },
                new TargetCreationOperationOptions { Enabled = gates.Creation });
            await Rejected(() => service.IssueWithCredentialAsync(new Actor(Guid.Empty, true), Guid.Empty, -1,
                Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Ct));
            Assert.Equal(0, guard.Attempts); Assert.Empty(db.ChangeTracker.Entries());
        }
    }

    [SqlServerFact]
    public async Task Bound_issue_persists_metadata_only_exact_replays_and_conflicts_with_rotation_mapping_and_legacy()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await SqlTargetCreationOperationTests.PrepareAsync(sql); var operation = await Register(sql, seed);
        var binding = Binding(operation); await Store(sql, seed, binding);
        var rotated = binding with { CredentialId = Guid.NewGuid() }; await Store(sql, seed, rotated);
        var permit = Guid.NewGuid(); var attempt = Guid.NewGuid();
        await using var db = sql.NewContext(); var service = Permits(db, seed);
        var audits = await db.AuditEvents.CountAsync();
        var issued = await Issue(service, seed, operation, permit, attempt, binding);
        Pins(issued, binding); Assert.True(issued.NewlyRecorded); Assert.Equal(1L, issued.Revision);
        var replay = await Issue(service, seed, operation, permit, attempt, binding);
        Pins(replay, binding); Assert.False(replay.NewlyRecorded);
        Assert.Equal(issued.IssuedAt, replay.IssuedAt); Assert.Equal(issued.StartBefore, replay.StartBefore);
        Assert.Equal(issued.Operation, replay.Operation);
        await Rejected(() => Issue(service, seed, operation, permit, attempt, rotated));
        await Rejected(() => Issue(service, seed, operation, permit, attempt,
            binding with { EndpointMappingId = Guid.NewGuid() }));
        await Rejected(() => service.IssueAsync(seed.Admin, operation.OperationId, operation.Revision, permit, attempt, Ct));
        var row = await db.TargetCreationPermits.AsNoTracking().SingleAsync();
        Assert.Equal(binding.CredentialId, row.CredentialId); Assert.Equal(binding.EndpointMappingId, row.EndpointMappingId);
        Assert.Equal(audits + 1, await db.AuditEvents.CountAsync());
        Assert.Equal(TargetCreationOperationState.Registered, (await db.TargetCreationOperations.AsNoTracking().SingleAsync()).State);
        NoSecrets(JsonSerializer.Serialize(await db.AuditEvents.AsNoTracking().ToListAsync()));
    }

    [SqlServerFact]
    public async Task New_issue_checks_current_admin_admission_exact_canonical_mapping_and_stored_state_before_replay()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await SqlTargetCreationOperationTests.PrepareAsync(sql); var operation = await Register(sql, seed);
        var binding = Binding(operation); await Store(sql, seed, binding);
        foreach (var bad in new[] { binding with { CredentialId = Guid.NewGuid(), CanonicalClusterId = Guid.NewGuid() },
                     binding with { CredentialId = Guid.NewGuid(), CanonicalSqlServerId = Guid.NewGuid() } })
        {
            await Store(sql, seed, bad); await using var invalidDb = sql.NewContext();
            await Rejected(() => Issue(Permits(invalidDb, seed), seed, operation, Guid.NewGuid(), Guid.NewGuid(), bad),
                code: "TargetCreationCredentialUnavailable");
        }
        await using var db = sql.NewContext(); var service = Permits(db, seed);
        await Rejected(() => Issue(service, seed, operation, Guid.NewGuid(), Guid.NewGuid(),
            binding with { EndpointMappingId = Guid.NewGuid() }), code: "TargetCreationCredentialUnavailable");
        await Rejected(() => Issue(service, seed, operation, Guid.NewGuid(), Guid.NewGuid(),
            binding with { CredentialId = Guid.NewGuid() }), code: "TargetCreationCredentialUnavailable");
        await Rejected(() => service.IssueWithCredentialAsync(new Actor(seed.Owner.UserId, true), operation.OperationId,
            operation.Revision, Guid.NewGuid(), Guid.NewGuid(), binding.CredentialId, binding.EndpointMappingId, Ct), 403);
        var revoked = binding with { CredentialId = Guid.NewGuid() }; await Store(sql, seed, revoked);
        await Credentials(db).RevokeAsync(seed.Admin, revoked.CredentialId, 1, Ct);
        await Rejected(() => Issue(service, seed, operation, Guid.NewGuid(), Guid.NewGuid(), revoked),
            code: "TargetCreationCredentialUnavailable");
        Assert.Empty(await db.TargetCreationPermits.AsNoTracking().ToListAsync());
        var permit = Guid.NewGuid(); var attempt = Guid.NewGuid();
        await Issue(service, seed, operation, permit, attempt, binding);
        await using (var change = sql.NewContext())
        {
            (await change.EnvironmentPools.SingleAsync(p => p.Id == seed.Pool)).Enabled = false;
            await change.SaveChangesAsync();
        }
        await Rejected(() => Issue(service, seed, operation, permit, attempt, binding));
        await using (var change = sql.NewContext())
        {
            (await change.EnvironmentPools.SingleAsync(p => p.Id == seed.Pool)).Enabled = true;
            change.UserRoles.RemoveRange(await change.UserRoles.Where(r => r.UserId == seed.Admin.UserId).ToListAsync());
            await change.SaveChangesAsync();
        }
        await Rejected(() => Issue(service, seed, operation, permit, attempt, binding), 403);
        Assert.Single(await db.TargetCreationPermits.AsNoTracking().ToListAsync());
    }

    [SqlServerFact]
    public async Task Both_start_paths_recheck_revocation_before_first_start_and_before_historical_start_replay()
    {
        foreach (var agent in new[] { false, true })
        foreach (var startFirst in new[] { false, true })
        {
            await using var sql = await SqlServerTestDatabase.CreateAsync();
            var seed = await SqlTargetCreationOperationTests.PrepareAsync(sql); var operation = await Register(sql, seed);
            var binding = Binding(operation); await Store(sql, seed, binding);
            await using var db = sql.NewContext(); var service = Permits(db, seed);
            var permit = Guid.NewGuid(); var attempt = Guid.NewGuid();
            await Issue(service, seed, operation, permit, attempt, binding);
            Task<TargetCreationPermitReceipt> Start() => agent
                ? service.RecordAgentStartAsync(seed.Credential.Token, permit, 1, attempt, Ct)
                : service.RecordStartAsync(seed.Admin, permit, 1, attempt, Ct);
            if (startFirst)
            {
                var started = await Start(); Pins(started, binding); Assert.True(started.NewlyRecorded);
                var replay = await Start(); Pins(replay, binding); Assert.False(replay.NewlyRecorded);
            }
            await Credentials(db).RevokeAsync(seed.Admin, binding.CredentialId, 1, Ct);
            var audits = await db.AuditEvents.CountAsync();
            await Rejected(() => Start(), code: "TargetCreationCredentialUnavailable");
            await Rejected(() => Issue(service, seed, operation, permit, attempt, binding),
                code: "TargetCreationCredentialUnavailable");
            var historical = await service.GetAsync(seed.Admin, permit, Ct); Pins(historical, binding);
            Assert.Equal(startFirst ? 2L : 1L, historical.Revision);
            Assert.Equal(audits, await db.AuditEvents.CountAsync());
            var revoked = await service.RevokeAsync(seed.Admin, permit, historical.Revision, Ct); Pins(revoked, binding);
            Assert.Equal(historical.StartedAt, revoked.StartedAt); Assert.Equal(TargetCreationPermitState.Revoked, revoked.State);
            Pins(await service.GetAsync(seed.Admin, permit, Ct), binding);
        }
    }

    [SqlServerFact]
    public async Task Legacy_issue_remains_unbound_and_cannot_be_replayed_as_a_credential_pinned_permit()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await SqlTargetCreationOperationTests.PrepareAsync(sql); var operation = await Register(sql, seed);
        var binding = Binding(operation); await Store(sql, seed, binding);
        await using var db = sql.NewContext(); var service = Permits(db, seed);
        var permit = Guid.NewGuid(); var attempt = Guid.NewGuid();
        var legacy = await service.IssueAsync(seed.Admin, operation.OperationId, operation.Revision, permit, attempt, Ct);
        Assert.Null(legacy.CredentialId); Assert.Null(legacy.EndpointMappingId); NoSecrets(JsonSerializer.Serialize(legacy));
        await Rejected(() => Issue(service, seed, operation, permit, attempt, binding));
        var replay = await service.IssueAsync(seed.Admin, operation.OperationId, operation.Revision, permit, attempt, Ct);
        Assert.False(replay.NewlyRecorded); Assert.Null(replay.CredentialId); Assert.Null(replay.EndpointMappingId);
        // Revocation of an unrelated credential cannot disable historical legacy metadata semantics.
        await Credentials(db).RevokeAsync(seed.Admin, binding.CredentialId, 1, Ct);
        var started = await service.RecordStartAsync(seed.Admin, permit, 1, attempt, Ct);
        Assert.Null(started.CredentialId); Assert.Null(started.EndpointMappingId); Assert.False(started.Executable);
    }

    [SqlServerFact]
    public async Task EF_guard_rejects_pin_changes_unbinding_and_permit_deletion()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await SqlTargetCreationOperationTests.PrepareAsync(sql); var operation = await Register(sql, seed);
        var binding = Binding(operation); await Store(sql, seed, binding);
        await using (var setup = sql.NewContext())
            await Issue(Permits(setup, seed), seed, operation, Guid.NewGuid(), Guid.NewGuid(), binding);
        foreach (var mutate in new Action<CreationPermitRecord>[] { r => r.CredentialId = Guid.NewGuid(),
                     r => r.EndpointMappingId = Guid.NewGuid(), r => { r.CredentialId = null; r.EndpointMappingId = null; } })
        {
            await using var db = sql.NewContext(); var row = await db.TargetCreationPermits.SingleAsync(); mutate(row);
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await using (var db = sql.NewContext())
        {
            db.TargetCreationPermits.Remove(await db.TargetCreationPermits.SingleAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await using var verify = sql.NewContext();
        var permit = (await verify.TargetCreationPermits.SingleAsync()).PermitId;
        Pins(await Permits(verify, seed).GetAsync(seed.Admin, permit, Ct), binding);
    }

    [SqlServerFact]
    public async Task Raw_SQL_enforces_nullable_nonempty_pin_pair_credential_FK_and_restricted_credential_deletion()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await SqlTargetCreationOperationTests.PrepareAsync(sql); var operation = await Register(sql, seed);
        var binding = Binding(operation); await Store(sql, seed, binding);
        var permit = Guid.NewGuid();
        await using var db = sql.NewContext();
        await Issue(Permits(db, seed), seed, operation, permit, Guid.NewGuid(), binding);
        // Fixed literals only. These bypass SaveChanges; SQL constraints, not the EF guard, must reject them.
        foreach (var assignment in new[] { "[CredentialId] = NULL", "[EndpointMappingId] = NULL",
                     "[CredentialId] = '00000000-0000-0000-0000-000000000000'",
                     "[EndpointMappingId] = '00000000-0000-0000-0000-000000000000'",
                     "[CredentialId] = '11111111-1111-1111-1111-111111111111'" })
        {
#pragma warning disable EF1003
            var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
                "UPDATE [TargetCreationPermits] SET " + assignment + " WHERE [PermitId] = {0}", permit));
#pragma warning restore EF1003
            Assert.Equal(547, error.Number);
        }
        var delete = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM [TargetCredentials] WHERE [CredentialId] = {binding.CredentialId}"));
        Assert.Equal(547, delete.Number);
        Pins(await Permits(db, seed).GetAsync(seed.Admin, permit, Ct), binding);
        Assert.Single(await db.TargetCreationPermits.AsNoTracking().ToListAsync());
    }
}
