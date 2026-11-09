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

// Opt-in disposable metadata SQL only; no target connection, worker or physical CREATE.
public sealed class TargetCreationExecutionClaimTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static TargetCreationPermitOptions Gates => new() { Enabled = true, AgentStartEnabled = true, CredentialBindingEnabled = true };
    private sealed record Fixture(Seed Seed, TargetCreationPermitReceipt Permit, TargetCredentialBinding Binding,
        ITargetCredentialProtector Protector);
    private static async Task<Fixture> Prepare(SqlServerTestDatabase sql, bool start = true)
    {
        var seed = await SqlTargetCreationOperationTests.PrepareAsync(sql);
        await using var db = sql.NewContext();
        var operation = await new SqlTargetCreationOperations(db, seed.Options).RegisterAsync(seed.Admin,
            seed.Order.EnvironmentId, seed.Restore.RestoreIntentId, Ct);
        var binding = new TargetCredentialBinding(Guid.NewGuid(), operation.CanonicalClusterId, operation.CanonicalSqlServerId, Guid.NewGuid());
        var protector = new DataProtectionTargetCredentialProtector(new EphemeralDataProtectionProvider());
        await new SqlTargetCredentials(db, new() { Enabled = true }, protector).StoreAsync(seed.Admin, binding,
            new("claim-user-canary", "claim-password-canary", "claim-sql-user-canary", "claim-sql-password-canary"), Ct);
        var permits = new SqlTargetCreationPermits(db, Gates, seed.Options);
        var permit = await permits.IssueWithCredentialAsync(seed.Admin, operation.OperationId, 1, Guid.NewGuid(), Guid.NewGuid(),
            binding.CredentialId, binding.EndpointMappingId, Ct);
        if (start) permit = await permits.RecordAgentStartAsync(seed.Credential.Token, permit.PermitId, 1, permit.AttemptId, Ct);
        return new(seed, permit, binding, protector);
    }
    private static Task<TargetCreationExecutionClaimReceipt> Claim(BuildSystemDbContext db, Fixture f, string? token = null,
        Guid? permit = null, Guid? attempt = null, Guid? credential = null, Guid? mapping = null, long revision = 2)
        => new SqlTargetCreationExecutionClaims(db, new() { Enabled = true }, Gates, f.Seed.Options, new() { Enabled = true })
            .ClaimAsync(token ?? f.Seed.Credential.Token, permit ?? f.Permit.PermitId, revision, attempt ?? f.Permit.AttemptId,
                credential ?? f.Binding.CredentialId, mapping ?? f.Binding.EndpointMappingId, Ct);
    private static async Task Reject(Func<Task> action, int status = 409, string code = "TargetCreationExecutionClaimUnavailable")
    {
        var error = await Assert.ThrowsAsync<RequestRejectedException>(action);
        Assert.Equal(status, error.StatusCode); Assert.Equal(code, error.Code); Assert.Null(error.InnerException);
        Assert.DoesNotContain("canary", error.ToString(), StringComparison.Ordinal);
    }
    private static Task<DateTimeOffset> Now(BuildSystemDbContext db) => db.Database.SqlQuery<DateTimeOffset>(
        $"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync();
    private static async Task<string> Snapshot(SqlServerTestDatabase sql)
    {
        await using var db = sql.NewContext();
        await db.TargetCreationPermits.LoadAsync(); await db.TargetCreationOperations.LoadAsync(); await db.TargetCredentials.LoadAsync();
        await db.AuditEvents.LoadAsync(); await db.RestoreIntents.LoadAsync(); await db.EnvironmentJobs.LoadAsync();
        await db.AgentLeases.LoadAsync(); await db.StagedArtifacts.LoadAsync(); await db.TestEnvironments.LoadAsync();
        await db.EnvironmentReservations.LoadAsync(); await db.EnvironmentQuotas.LoadAsync(); await db.EnvironmentRequests.LoadAsync();
        await db.StagingAllocationReleases.LoadAsync();
        return JsonSerializer.Serialize(db.ChangeTracker.Entries().Select(e => e.Metadata.Name + ":" + JsonSerializer.Serialize(
            e.Properties.OrderBy(p => p.Metadata.Name).ToDictionary(p => p.Metadata.Name, p => p.CurrentValue))).OrderBy(x => x));
    }
    [SqlServerFact]
    public async Task Fresh_current_assigned_agent_gets_exact_one_shot_grant_and_only_historical_evidence_is_persisted()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql); var before = await Snapshot(sql);
        await using var db = sql.NewContext(); var lower = await Now(db); var grant = await Claim(db, f); var upper = await Now(db);
        Assert.NotEqual(Guid.Empty, grant.ClaimId); Assert.Equal(f.Permit.PermitId, grant.PermitId);
        Assert.Equal(f.Permit.OperationId, grant.OperationId); Assert.Equal(f.Permit.AttemptId, grant.AttemptId);
        Assert.Equal(f.Seed.Credential.AgentId, grant.AgentId); Assert.Equal(f.Binding.CredentialId, grant.CredentialId);
        Assert.Equal(f.Binding.EndpointMappingId, grant.EndpointMappingId); Assert.InRange(grant.IssuedAt, lower, upper);
        Assert.Equal(TimeSpan.Zero, grant.IssuedAt.Offset); Assert.Equal(f.Permit.StartBefore, grant.ExpiresAt);
        Assert.True(grant.Executable); Assert.True(grant.NewlyIssued); Assert.True(grant.ScheduledJobsMustRemainBlocked); Assert.False(grant.RetryAllowed);
        var row = await db.TargetCreationExecutionClaims.AsNoTracking().SingleAsync();
        Assert.Equal(grant.ClaimId, row.ClaimId); Assert.Equal(grant.IssuedAt, row.IssuedAt); Assert.Equal(grant.ExpiresAt, row.ExpiresAt);
        Assert.Equal(grant.PermitId, row.PermitId); Assert.Equal(grant.OperationId, row.OperationId); Assert.Equal(grant.AttemptId, row.AttemptId);
        Assert.Equal(grant.AgentId, row.AgentId); Assert.Equal(grant.CredentialId, row.CredentialId); Assert.Equal(grant.EndpointMappingId, row.EndpointMappingId);
        Assert.DoesNotContain("Executable", JsonSerializer.Serialize(row), StringComparison.Ordinal);
        Assert.False((await db.TargetCreationPermits.AsNoTracking().SingleAsync()).Executable);
        Assert.Equal(before, await Snapshot(sql));
    }
    [SqlServerFact]
    public async Task Repeat_is_conflict_not_a_second_executable_receipt()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql); await using var db = sql.NewContext();
        var first = await Claim(db, f); await Reject(() => Claim(db, f), code: "TargetCreationExecutionClaimAlreadyIssued");
        Assert.Equal(first.ClaimId, (await db.TargetCreationExecutionClaims.AsNoTracking().SingleAsync()).ClaimId);
    }
    [SqlServerFact]
    public async Task Concurrent_separate_contexts_have_exactly_one_grant_and_one_conflict()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql);
        await using var left = sql.NewContext(); await using var right = sql.NewContext();
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Run(BuildSystemDbContext db)
        {
            await ready.Task;
            try { Assert.True((await Claim(db, f)).Executable); return true; }
            catch (RequestRejectedException e) { Assert.Equal(409, e.StatusCode); Assert.Equal("TargetCreationExecutionClaimAlreadyIssued", e.Code); return false; }
        }
        var a = Run(left); var b = Run(right); ready.SetResult(true); var results = await Task.WhenAll(a, b);
        Assert.Single(results, x => x); Assert.Single(results, x => !x);
        await using var check = sql.NewContext(); Assert.Equal(1, await check.TargetCreationExecutionClaims.CountAsync());
    }
    [SqlServerFact]
    public async Task Permit_revocation_before_claim_blocks_issuance()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql); await using var db = sql.NewContext();
        await new SqlTargetCreationPermits(db, Gates, f.Seed.Options).RevokeAsync(f.Seed.Admin, f.Permit.PermitId, 2, Ct);
        await Reject(() => Claim(db, f)); Assert.Empty(await db.TargetCreationExecutionClaims.ToListAsync());
    }
    [SqlServerFact]
    public async Task Permit_revocation_after_claim_never_erases_evidence_or_replays_authority()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql); await using var db = sql.NewContext();
        var grant = await Claim(db, f);
        await new SqlTargetCreationPermits(db, Gates, f.Seed.Options).RevokeAsync(f.Seed.Admin, f.Permit.PermitId, 2, Ct);
        await Reject(() => Claim(db, f)); Assert.Equal(grant.ClaimId, (await db.TargetCreationExecutionClaims.AsNoTracking().SingleAsync()).ClaimId);
    }
    [SqlServerFact]
    public async Task Revoked_pinned_credential_blocks_claim()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql); await using var db = sql.NewContext();
        await new SqlTargetCredentials(db, new() { Enabled = true }, f.Protector).RevokeAsync(f.Seed.Admin, f.Binding.CredentialId, 1, Ct);
        await Reject(() => Claim(db, f), code: "TargetCreationCredentialUnavailable"); Assert.Empty(await db.TargetCreationExecutionClaims.ToListAsync());
    }
    [SqlServerFact]
    public async Task Every_request_pin_and_exact_revision_are_required_without_mutation()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql); var before = await Snapshot(sql);
        await using var db = sql.NewContext();
        await Reject(() => Claim(db, f, permit: Guid.NewGuid())); await Reject(() => Claim(db, f, attempt: Guid.NewGuid()));
        await Reject(() => Claim(db, f, credential: Guid.NewGuid())); await Reject(() => Claim(db, f, mapping: Guid.NewGuid()));
        foreach (var revision in new[] { 0L, 1L, 3L, long.MaxValue })
            await Reject(() => Claim(db, f, revision: revision), 400, "InvalidTargetCreationExecutionClaim");
        Assert.Empty(await db.TargetCreationExecutionClaims.ToListAsync()); Assert.Equal(before, await Snapshot(sql));
    }
    [SqlServerFact]
    public async Task Original_window_expiry_and_future_window_reject_even_when_all_three_times_shift_consistently()
    {
        foreach (var days in new[] { -1, 1 })
        {
            await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql); await using var db = sql.NewContext();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [TargetCreationPermits] SET [IssuedAt] = DATEADD(day, {days}, [IssuedAt]), [StartBefore] = DATEADD(day, {days}, [StartBefore]), [StartedAt] = DATEADD(day, {days}, [StartedAt]) WHERE [PermitId] = {f.Permit.PermitId}", Ct);
            var row = await db.TargetCreationPermits.AsNoTracking().SingleAsync();
            Assert.Equal(row.IssuedAt.AddMinutes(5), row.StartBefore);
            Assert.Equal(f.Permit.StartedAt!.Value.AddDays(days), row.StartedAt);
            await Reject(() => Claim(db, f)); Assert.Empty(await db.TargetCreationExecutionClaims.ToListAsync());
        }
    }
    [SqlServerFact]
    public async Task Prepared_and_quarantined_operations_do_not_grant()
    {
        foreach (var started in new[] { false, true })
        {
            await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql, started); await using var db = sql.NewContext();
            if (started) await new SqlTargetCreationOperations(db, f.Seed.Options).QuarantineAsync(f.Seed.Admin,
                f.Permit.OperationId, 2, TargetCreationQuarantineReason.OutcomeUnknown, Ct);
            await Reject(() => Claim(db, f)); Assert.Empty(await db.TargetCreationExecutionClaims.ToListAsync());
        }
    }
    [SqlServerFact]
    public async Task Invalid_rotated_disabled_and_foreign_agents_cannot_probe_or_claim()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql);
        var other = await SqlTargetCreationOperationTests.PrepareAsync(sql); await using var db = sql.NewContext();
        foreach (var token in new[] { "", "invalid-token", new string('x', 257) })
            await Reject(() => Claim(db, f, token, permit: Guid.NewGuid()), 401, "AgentUnauthorized");
        await Reject(() => Claim(db, f, other.Credential.Token, credential: Guid.NewGuid()), 403, "TargetCreationPermitForbidden");
        var rotated = await new SqlAgentControl(db).RotateAsync(f.Seed.Admin, f.Seed.Credential.AgentId, Ct);
        await Reject(() => Claim(db, f), 401, "AgentUnauthorized");
        await new SqlAgentControl(db).RevokeAsync(f.Seed.Admin, f.Seed.Credential.AgentId, Ct);
        await Reject(() => Claim(db, f, rotated.Token), 401, "AgentUnauthorized"); Assert.Empty(await db.TargetCreationExecutionClaims.ToListAsync());
    }
    [SqlServerFact]
    public async Task Current_admission_withdrawal_blocks_claim_without_changing_metadata()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql); await using var db = sql.NewContext();
        await db.ProjectMemberships.Where(x => x.ProjectId == f.Seed.Project && x.UserId == f.Seed.Owner.UserId).ExecuteDeleteAsync();
        var before = await Snapshot(sql); await Reject(() => Claim(db, f), code: "TargetCreationMismatch");
        Assert.Equal(before, await Snapshot(sql)); Assert.Empty(await db.TargetCreationExecutionClaims.ToListAsync());
    }
    private sealed class MetadataCommands : DbCommandInterceptor
    {
        public List<string> Credentials { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("TargetCredentials", StringComparison.Ordinal)) Credentials.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
    private static BuildSystemDbContext Intercepted(SqlServerTestDatabase sql, IInterceptor interceptor) => new(
        new DbContextOptionsBuilder<BuildSystemDbContext>().UseSqlServer(sql.ConnectionString,
            o => o.EnableRetryOnFailure(3, TimeSpan.Zero, new[] { 51001 })).AddInterceptors(interceptor).Options);
    [SqlServerFact]
    public async Task Claim_reads_only_pinned_credential_metadata_never_ciphertext_or_newer_credentials()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql);
        await using (var setup = sql.NewContext())
            await new SqlTargetCredentials(setup, new() { Enabled = true }, f.Protector).StoreAsync(f.Seed.Admin,
                f.Binding with { CredentialId = Guid.NewGuid() }, new("new-user", "new-password", "new-sql-user", "new-sql-password"), Ct);
        var spy = new MetadataCommands(); await using var db = Intercepted(sql, spy); var grant = await Claim(db, f);
        Assert.Equal(f.Binding.CredentialId, grant.CredentialId); var command = Assert.Single(spy.Credentials);
        Assert.Contains("SELECT", command, StringComparison.OrdinalIgnoreCase); Assert.Contains("CredentialId", command, StringComparison.Ordinal);
        Assert.DoesNotContain("Ciphertext", command, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("canary", JsonSerializer.Serialize(grant), StringComparison.Ordinal);
    }
    [SqlServerFact]
    public async Task Historical_claim_is_append_only_for_modified_and_deleted_EF_entries()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql);
        await using (var issue = sql.NewContext()) await Claim(issue, f);
        foreach (var delete in new[] { false, true })
        {
            await using var db = sql.NewContext(); var row = await db.TargetCreationExecutionClaims.SingleAsync();
            if (delete) db.Remove(row); else db.Entry(row).Property(x => x.EndpointMappingId).CurrentValue = Guid.NewGuid();
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
        }
        await using var check = sql.NewContext(); Assert.Equal(f.Binding.EndpointMappingId, (await check.TargetCreationExecutionClaims.SingleAsync()).EndpointMappingId);
    }
    [SqlServerFact]
    public async Task Sql_unique_indexes_independently_reject_duplicate_permit_operation_and_attempt()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql); var other = await Prepare(sql);
        await using (var issue = sql.NewContext()) await Claim(issue, f);
        foreach (var key in new[] { "permit", "operation", "attempt" })
        {
            await using var db = new BuildSystemDbContext(new DbContextOptionsBuilder<BuildSystemDbContext>().UseSqlServer(sql.ConnectionString).Options);
            await using var tx = await db.Database.BeginTransactionAsync();
            var row = TargetCreationExecutionClaim.Issue(Guid.NewGuid(), key == "permit" ? f.Permit.PermitId : other.Permit.PermitId,
                key == "operation" ? f.Permit.OperationId : other.Permit.OperationId, key == "attempt" ? f.Permit.AttemptId : other.Permit.AttemptId,
                other.Seed.Credential.AgentId, other.Binding.CredentialId, other.Binding.EndpointMappingId,
                other.Permit.IssuedAt, other.Permit.StartBefore, other.Permit.StartedAt!.Value);
            var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [TargetCreationExecutionClaims] ([ClaimId], [PermitId], [OperationId], [AttemptId], [AgentId], [CredentialId], [EndpointMappingId], [IssuedAt], [ExpiresAt]) VALUES ({row.ClaimId}, {row.PermitId}, {row.OperationId}, {row.AttemptId}, {row.AgentId}, {row.CredentialId}, {row.EndpointMappingId}, {row.IssuedAt}, {row.ExpiresAt})", Ct));
            Assert.Equal(2601, error.Number); await tx.RollbackAsync();
        }
        await using var check = sql.NewContext(); Assert.Equal(1, await check.TargetCreationExecutionClaims.CountAsync());
    }
    private sealed class LostAcknowledgement(bool retryable) : DbTransactionInterceptor
    {
        public int Commits { get; private set; }
        public int Starts { get; private set; }
        public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData,
            DbTransaction result, CancellationToken cancellationToken = default) { Starts++; return ValueTask.FromResult(result); }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Commits++;
            // The actual SQL CommitAsync already returned; this simulates ACK loss, not a physical fault.
            if (retryable) throw new TimeoutException("Simulated post-commit ACK loss.");
            throw new IOException("Simulated post-commit ACK loss.");
        }
    }
    [SqlServerFact]
    public async Task Lost_commit_ACK_returns_neutral_503_retains_evidence_and_never_reissues_even_under_provider_retry()
    {
        foreach (var retryable in new[] { false, true })
        {
            await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql);
            var fault = new LostAcknowledgement(retryable);
            await using (var db = Intercepted(sql, fault)) await Reject(() => Claim(db, f), 503);
            Assert.Equal(1, fault.Commits); Assert.Equal(1, fault.Starts);
            await using var check = sql.NewContext(); Assert.Equal(1, await check.TargetCreationExecutionClaims.CountAsync());
            await Reject(() => Claim(check, f), code: "TargetCreationExecutionClaimAlreadyIssued");
            Assert.Equal(1, await check.TargetCreationExecutionClaims.CountAsync());
        }
    }
}
