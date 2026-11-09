using System.Data.Common;
using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Domain;
using BuildSystem.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Seed = BuildSystem.IntegrationTests.SqlTargetCreationOperationTests.Seed;

namespace BuildSystem.IntegrationTests;

// Internal resolution only: disposable metadata SQL, no HTTP delivery or target execution.
public sealed class SqlTargetCreationCredentialResolutionTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly TargetCredentialInput Input = new("resolve-cluster-user-canary", "resolve-cluster-password-canary",
        "resolve-sql-user-canary", "resolve-sql-password-canary");
    private static TargetCreationPermitOptions Gates => new() { Enabled = true, AgentStartEnabled = true, CredentialBindingEnabled = true };
    private sealed record Fixture(Seed Seed, TargetCreationOperationReceipt Operation, TargetCredentialBinding Binding,
        Guid Permit, Guid Attempt, ITargetCredentialProtector Protector);
    private static ITargetCredentialProtector Protector() => new DataProtectionTargetCredentialProtector(new EphemeralDataProtectionProvider());
    private static async Task<Fixture> Prepare(SqlServerTestDatabase sql, bool start = true)
    {
        var seed = await SqlTargetCreationOperationTests.PrepareAsync(sql);
        await using var db = sql.NewContext();
        var operation = await new SqlTargetCreationOperations(db, seed.Options).RegisterAsync(seed.Admin,
            seed.Order.EnvironmentId, seed.Restore.RestoreIntentId, Ct);
        var binding = new TargetCredentialBinding(Guid.NewGuid(), operation.CanonicalClusterId, operation.CanonicalSqlServerId, Guid.NewGuid());
        var protector = Protector();
        await new SqlTargetCredentials(db, new() { Enabled = true }, protector).StoreAsync(seed.Admin, binding, Input, Ct);
        var permit = Guid.NewGuid(); var attempt = Guid.NewGuid();
        var service = new SqlTargetCreationPermits(db, Gates, seed.Options);
        await service.IssueWithCredentialAsync(seed.Admin, operation.OperationId, 1, permit, attempt,
            binding.CredentialId, binding.EndpointMappingId, Ct);
        if (start)
        {
            var receipt = await service.RecordAgentStartAsync(seed.Credential.Token, permit, 1, attempt, Ct);
            Assert.Equal(2L, receipt.Revision); Assert.Equal(TargetCreationOperationState.AttemptRecorded, receipt.Operation.State);
        }
        return new(seed, operation, binding, permit, attempt, protector);
    }
    private static SqlTargetCreationCredentialResolver Resolver(BuildSystemDbContext db, Fixture f, ITargetCredentialProtector? protector = null)
        => new(db, Gates, f.Seed.Options, protector ?? f.Protector, new() { Enabled = true }, new() { Enabled = true });
    private static Task<TargetCreationResolvedCredentials> Resolve(BuildSystemDbContext db, Fixture f, string? token = null,
        Guid? permit = null, Guid? attempt = null, Guid? credential = null, Guid? mapping = null, ITargetCredentialProtector? protector = null)
        => Resolver(db, f, protector).ResolveAsync(token ?? f.Seed.Credential.Token, permit ?? f.Permit, 2,
            attempt ?? f.Attempt, credential ?? f.Binding.CredentialId, mapping ?? f.Binding.EndpointMappingId, Ct);
    private static void Safe(string text)
    {
        foreach (var value in new[] { Input.ClusterUser, Input.ClusterPassword, Input.SqlUser, Input.SqlPassword })
            Assert.DoesNotContain(value, text, StringComparison.Ordinal);
    }
    private static async Task Reject(Func<Task> action, int status = 409, string code = "TargetCreationCredentialUnavailable")
    {
        var error = await Assert.ThrowsAsync<RequestRejectedException>(action);
        Assert.Equal(status, error.StatusCode); Assert.Equal(code, error.Code); Assert.Null(error.InnerException); Safe(error.ToString());
    }
    private sealed class Spy(ITargetCredentialProtector inner, bool fail = false) : ITargetCredentialProtector
    {
        public int Calls { get; private set; }
        public string Protect(TargetCredentialScope scope, TargetCredentialSecrets secrets) => inner.Protect(scope, secrets);
        public TargetCredentialSecrets Unprotect(TargetCredentialScope scope, string ciphertext)
        {
            Calls++;
            if (fail) throw new InvalidOperationException(Input.SqlPassword);
            return inner.Unprotect(scope, ciphertext);
        }
    }
    private sealed class NoConnection : DbConnectionInterceptor
    {
        public int Calls { get; private set; }
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException(Input.SqlPassword); }
        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        { Calls++; throw new InvalidOperationException(Input.SqlPassword); }
    }
    private static BuildSystemDbContext Offline(NoConnection guard) => new(new DbContextOptionsBuilder<BuildSystemDbContext>()
        .UseSqlServer("Server=unused;Database=unused;Integrated Security=true;Connect Timeout=1;TrustServerCertificate=true")
        .AddInterceptors(guard).Options);
    private static SqlTargetCreationCredentialResolver OfflineResolver(BuildSystemDbContext db, int disabled = -1) => new(db,
        new() { Enabled = disabled != 0, AgentStartEnabled = disabled != 1, CredentialBindingEnabled = disabled != 2 },
        new() { Enabled = disabled != 3 }, Protector(), new() { Enabled = disabled != 4 }, new() { Enabled = disabled != 5 });

    [Fact]
    public async Task Default_off_and_every_disabled_gate_reject_before_input_or_SQL()
    {
        Assert.False(new TargetCreationCredentialResolutionOptions().Enabled);
        for (var gate = 0; gate < 6; gate++)
        {
            var guard = new NoConnection(); await using var db = Offline(guard);
            await Reject(() => OfflineResolver(db, gate).ResolveAsync("", Guid.Empty, -1, Guid.Empty, Guid.Empty, Guid.Empty, Ct),
                code: "TargetCreationCredentialResolutionDisabled");
            Assert.Equal(0, guard.Calls); Assert.Empty(db.ChangeTracker.Entries());
        }
    }

    [Fact]
    public async Task Invalid_identifier_and_revision_pairs_reject_before_SQL()
    {
        var id = Guid.NewGuid();
        foreach (var pins in new[] { (Guid.Empty, 2L, id, id, id), (id, 2L, Guid.Empty, id, id),
                     (id, 2L, id, Guid.Empty, id), (id, 2L, id, id, Guid.Empty), (id, 0L, id, id, id),
                     (id, 1L, id, id, id), (id, 3L, id, id, id), (id, long.MaxValue, id, id, id) })
        {
            var guard = new NoConnection(); await using var db = Offline(guard);
            await Reject(() => OfflineResolver(db).ResolveAsync("token", pins.Item1, pins.Item2, pins.Item3, pins.Item4, pins.Item5, Ct),
                400, "InvalidTargetCreationCredentialResolution");
            Assert.Equal(0, guard.Calls); Assert.Empty(db.ChangeTracker.Entries());
        }
    }

    [Fact]
    public async Task Unexpected_database_failure_is_neutral_and_has_no_inner_exception()
    {
        var guard = new NoConnection(); await using var db = Offline(guard); var id = Guid.NewGuid();
        await Reject(() => OfflineResolver(db).ResolveAsync("valid-shaped-token", id, 2, id, id, id, Ct),
            503, "TargetCreationCredentialResolutionUnavailable");
        Assert.True(guard.Calls > 0); Assert.Empty(db.ChangeTracker.Entries());
    }

    [SqlServerFact]
    public async Task Success_decrypts_exact_values_repeats_fresh_and_never_serializes_or_executes()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql);
        await using var db = sql.NewContext(); var spy = new Spy(f.Protector);
        using var first = await Resolve(db, f, protector: spy); using var second = await Resolve(db, f, protector: spy);
        Assert.NotSame(first, second); Assert.Equal(2, spy.Calls);
        Assert.Equal(new TargetCredentialSecrets(Input.ClusterUser, Input.ClusterPassword, Input.SqlUser, Input.SqlPassword), first.ReadSecrets());
        Assert.Equal(first.ReadSecrets(), second.ReadSecrets()); Assert.False(first.Executable); Assert.False(first.RetryAllowed);
        Safe(first.ToString()); Safe(first.ReadSecrets().ToString());
        var json = Assert.Throws<JsonException>(() => JsonSerializer.Serialize(first)); Safe(json.ToString()); Assert.Null(json.InnerException);
        Assert.StartsWith("Target credentials unavailable.", json.Message, StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize<object>(first));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TargetCreationResolvedCredentials>("{}"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TargetCreationResolvedCredentials>("null"));
        first.Dispose(); first.Dispose(); Assert.Throws<ObjectDisposedException>(() => first.ReadSecrets());
        Safe(first.ToString()); Assert.Throws<JsonException>(() => JsonSerializer.Serialize(first));
    }

    [SqlServerFact]
    public async Task Authentication_rotation_and_assignment_are_checked_before_credential_lookup()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql);
        var other = await SqlTargetCreationOperationTests.PrepareAsync(sql);
        await using var db = sql.NewContext(); var spy = new Spy(f.Protector);
        foreach (var token in new[] { "", "invalid-token", new string('x', 257) })
            await Reject(() => Resolve(db, f, token, permit: Guid.NewGuid(), protector: spy), 401, "AgentUnauthorized");
        await Reject(() => Resolve(db, f, other.Credential.Token, credential: Guid.NewGuid(), protector: spy), 403, "TargetCreationPermitForbidden");
        var rotated = await new SqlAgentControl(db).RotateAsync(f.Seed.Admin, f.Seed.Credential.AgentId, Ct);
        await Reject(() => Resolve(db, f, protector: spy), 401, "AgentUnauthorized");
        using (await Resolve(db, f, rotated.Token)) { }
        await new SqlAgentControl(db).RevokeAsync(f.Seed.Admin, f.Seed.Credential.AgentId, Ct);
        await Reject(() => Resolve(db, f, rotated.Token, protector: spy), 401, "AgentUnauthorized");
        Assert.Equal(0, spy.Calls);
    }

    [SqlServerFact]
    public async Task Wrong_pins_current_admission_withdrawal_and_revocation_block_replay()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql);
        await using var db = sql.NewContext(); var spy = new Spy(f.Protector);
        await Reject(() => Resolve(db, f, permit: Guid.NewGuid(), protector: spy));
        await Reject(() => Resolve(db, f, attempt: Guid.NewGuid(), protector: spy));
        await Reject(() => Resolve(db, f, credential: Guid.NewGuid(), protector: spy));
        await Reject(() => Resolve(db, f, mapping: Guid.NewGuid(), protector: spy));
        using (await Resolve(db, f)) { }
        await db.ProjectMemberships.Where(m => m.UserId == f.Seed.Owner.UserId && m.ProjectId == f.Seed.Project).ExecuteDeleteAsync();
        await Reject(() => Resolve(db, f, protector: spy), code: "TargetCreationMismatch");
        db.ProjectMemberships.Add(new ProjectMembership { UserId = f.Seed.Owner.UserId, ProjectId = f.Seed.Project }); await db.SaveChangesAsync();
        await db.EnvironmentPools.Where(p => p.Id == f.Seed.Pool).ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, false));
        await Reject(() => Resolve(db, f, protector: spy), code: "TargetCreationMismatch");
        await db.EnvironmentPools.Where(p => p.Id == f.Seed.Pool).ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, true));
        await new SqlTargetCredentials(db, new() { Enabled = true }, f.Protector).RevokeAsync(f.Seed.Admin, f.Binding.CredentialId, 1, Ct);
        await Reject(() => Resolve(db, f, protector: spy)); await Reject(() => Resolve(db, f, protector: spy)); Assert.Equal(0, spy.Calls);
    }

    [SqlServerFact]
    public async Task Prepared_revoked_expired_future_and_reconciliation_states_reject_before_decryption()
    {
        foreach (var state in new[] { "prepared", "revoked", "expired", "future", "reconciliation" })
        {
            await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql, state != "prepared");
            await using var db = sql.NewContext(); var spy = new Spy(f.Protector);
            if (state == "revoked") await new SqlTargetCreationPermits(db, Gates, f.Seed.Options).RevokeAsync(f.Seed.Admin, f.Permit, 2, Ct);
            if (state == "reconciliation") await new SqlTargetCreationOperations(db, f.Seed.Options).QuarantineAsync(
                f.Seed.Admin, f.Operation.OperationId, 2, TargetCreationQuarantineReason.OutcomeUnknown, Ct);
            if (state == "expired") await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [TargetCreationPermits] SET [IssuedAt] = DATEADD(day, -1, [IssuedAt]), [StartBefore] = DATEADD(day, -1, [StartBefore]), [StartedAt] = DATEADD(day, -1, [StartedAt]) WHERE [PermitId] = {f.Permit}", Ct);
            if (state == "future") await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [TargetCreationPermits] SET [IssuedAt] = DATEADD(day, 1, [IssuedAt]), [StartBefore] = DATEADD(day, 1, [StartBefore]), [StartedAt] = DATEADD(day, 1, [StartedAt]) WHERE [PermitId] = {f.Permit}", Ct);
            await Reject(() => Resolve(db, f, protector: spy)); Assert.Equal(0, spy.Calls);
        }
    }

    [SqlServerFact]
    public async Task Corrupted_wrong_scope_envelopes_and_unexpected_protector_errors_are_sanitized()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql);
        await using var db = sql.NewContext(); var throwing = new Spy(f.Protector, true);
        await Reject(() => Resolve(db, f, protector: throwing)); Assert.Equal(1, throwing.Calls);
        var wrong = f.Protector.Protect(new(Guid.NewGuid(), f.Binding.CanonicalClusterId, f.Binding.CanonicalSqlServerId,
            f.Binding.EndpointMappingId), new(Input.ClusterUser, Input.ClusterPassword, Input.SqlUser, Input.SqlPassword));
        foreach (var ciphertext in new[] { "corrupt-envelope", wrong })
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [TargetCredentials] SET [Ciphertext] = {ciphertext} WHERE [CredentialId] = {f.Binding.CredentialId}", Ct);
            await Reject(() => Resolve(db, f));
        }
    }

    private static async Task<string> Snapshot(SqlServerTestDatabase sql)
    {
        await using var db = sql.NewContext();
        await db.TargetCreationPermits.LoadAsync(); await db.TargetCreationOperations.LoadAsync(); await db.TargetCredentials.LoadAsync();
        await db.AuditEvents.LoadAsync(); await db.RestoreIntents.LoadAsync(); await db.EnvironmentJobs.LoadAsync();
        await db.AgentLeases.LoadAsync(); await db.StagedArtifacts.LoadAsync(); await db.TestEnvironments.LoadAsync();
        await db.EnvironmentReservations.LoadAsync(); await db.EnvironmentQuotas.LoadAsync(); await db.EnvironmentRequests.LoadAsync();
        await db.StagingAllocationReleases.LoadAsync();
        return JsonSerializer.Serialize(db.ChangeTracker.Entries().Select(e => e.Metadata.Name + ":" + JsonSerializer.Serialize(
            e.Properties.OrderBy(p => p.Metadata.Name).ToDictionary(p => p.Metadata.Name, p => p.CurrentValue))).OrderBy(s => s));
    }
    [SqlServerFact]
    public async Task Resolution_never_selects_newer_credentials_or_mutates_metadata_lifecycle_or_audit()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql);
        await using var db = sql.NewContext(); var newer = f.Binding with { CredentialId = Guid.NewGuid() };
        await new SqlTargetCredentials(db, new() { Enabled = true }, f.Protector).StoreAsync(f.Seed.Admin, newer,
            new("new-user", "new-password", "new-sql-user", "new-sql-password"), Ct);
        var before = await Snapshot(sql);
        using (var result = await Resolve(db, f)) Assert.Equal(Input.SqlPassword, result.ReadSecrets().SqlPassword);
        await Reject(() => Resolve(db, f, credential: newer.CredentialId));
        using (await Resolve(db, f)) { }
        Assert.Equal(before, await Snapshot(sql)); Safe(JsonSerializer.Serialize(await db.AuditEvents.AsNoTracking().ToListAsync()));
    }
}
