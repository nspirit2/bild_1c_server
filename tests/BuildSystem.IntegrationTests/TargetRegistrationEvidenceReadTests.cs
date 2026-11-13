using System.Data.Common;
using System.Globalization;
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

// Opt-in disposable metadata SQL only. Historical reads never obtain executable grants or contact targets.
public sealed class TargetRegistrationEvidenceReadTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private sealed record Fixture(Seed Seed, TargetCredentialBinding Binding, ITargetCredentialProtector Protector,
        TargetCreationExecutionClaim Claim, ReportTargetRegistrationEvidence Report);

    private static async Task<Fixture> Prepare(SqlServerTestDatabase sql, bool expired = false, bool record = true)
    {
        var seed = await SqlTargetCreationOperationTests.PrepareAsync(sql);
        await using var db = sql.NewContext();
        var operation = await new SqlTargetCreationOperations(db, seed.Options).RegisterAsync(seed.Admin,
            seed.Order.EnvironmentId, seed.Restore.RestoreIntentId, Ct);
        var binding = new TargetCredentialBinding(Guid.NewGuid(), operation.CanonicalClusterId, operation.CanonicalSqlServerId, Guid.NewGuid());
        var protector = new DataProtectionTargetCredentialProtector(new EphemeralDataProtectionProvider());
        await new SqlTargetCredentials(db, new() { Enabled = true }, protector).StoreAsync(seed.Admin, binding,
            new("history-user-canary", "history-password-canary", "history-sql-user-canary", "history-sql-password-canary"), Ct);
        var permits = new SqlTargetCreationPermits(db,
            new() { Enabled = true, AgentStartEnabled = true, CredentialBindingEnabled = true }, seed.Options);
        var permit = await permits.IssueWithCredentialAsync(seed.Admin, operation.OperationId, 1, Guid.NewGuid(), Guid.NewGuid(),
            binding.CredentialId, binding.EndpointMappingId, Ct);
        permit = await permits.RecordAgentStartAsync(seed.Credential.Token, permit.PermitId, 1, permit.AttemptId, Ct);
        if (expired)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [TargetCreationPermits] SET [IssuedAt] = DATEADD(day, -1, [IssuedAt]), [StartBefore] = DATEADD(day, -1, [StartBefore]), [StartedAt] = DATEADD(day, -1, [StartedAt]) WHERE [PermitId] = {permit.PermitId}", Ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [TargetCredentials] SET [CreatedAt] = DATEADD(day, -1, [CreatedAt]) WHERE [CredentialId] = {binding.CredentialId}", Ct);
        }
        var persisted = await db.TargetCreationPermits.AsNoTracking().SingleAsync(x => x.PermitId == permit.PermitId);
        // Seed immutable issuance history, never call an executable claim issuer.
        var claim = TargetCreationExecutionClaim.Issue(Guid.NewGuid(), permit.PermitId, operation.OperationId,
            permit.AttemptId, seed.Credential.AgentId, binding.CredentialId, binding.EndpointMappingId,
            persisted.IssuedAt, persisted.StartBefore, persisted.StartedAt!.Value);
        db.TargetCreationExecutionClaims.Add(claim); await db.SaveChangesAsync(Ct);
        var intent = await db.RestoreIntents.AsNoTracking().SingleAsync(x => x.RestoreIntentId == seed.Restore.RestoreIntentId);
        var report = new ReportTargetRegistrationEvidence(claim.ClaimId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), "rac-registration-8.3.22.2557-v1", intent.InfobaseName,
            "history-sql.example\\instance", intent.SqlDbName, 37,
            new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Unspecified).ToString("O", CultureInfo.InvariantCulture), "ONLINE");
        if (record) await new SqlTargetRegistrationEvidenceReceipts(db, new() { Enabled = true }, seed.Options)
            .ReportAsync(seed.Credential.Token, report, Ct);
        return new(seed, binding, protector, claim, report);
    }

    private static Task<TargetRegistrationEvidenceView> Get(BuildSystemDbContext db, Fixture f, Actor? actor = null,
        Guid? operation = null) => new SqlTargetRegistrationEvidenceReads(db, f.Seed.Options)
        .GetAsync(actor ?? f.Seed.Admin, operation ?? f.Claim.OperationId, Ct);

    private static async Task Reject(Func<Task> action, int status, string code)
    {
        var error = await Assert.ThrowsAsync<RequestRejectedException>(action);
        Assert.Equal(status, error.StatusCode); Assert.Equal(code, error.Code); Assert.Null(error.InnerException);
        Assert.DoesNotContain("canary", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ObjectDisposedException", error.ToString(), StringComparison.Ordinal);
    }

    private static void Inert(TargetRegistrationEvidenceView view)
    {
        Assert.False(view.Executable); Assert.False(view.FreshAuthority); Assert.False(view.OwnershipEstablished);
        Assert.False(view.RestoreAllowed); Assert.False(view.RetryAllowed); Assert.True(view.RequiresIndependentVerification);
        Assert.True(view.ScheduledJobsMustRemainBlocked); Assert.False(view.ReportedEndpointMappingsAreAttested);
        if (view.HistoricalEvidence is not { } h) return;
        Assert.False(h.Executable); Assert.False(h.FreshAuthority); Assert.False(h.OwnershipEstablished);
        Assert.False(h.RestoreAllowed); Assert.False(h.RetryAllowed); Assert.True(h.RequiresIndependentVerification);
        Assert.True(h.ScheduledJobsMustRemainBlocked); Assert.False(h.ReportedEndpointMappingsAreAttested);
    }

    private static async Task<string> Snapshot(SqlServerTestDatabase sql)
    {
        await using var db = sql.NewContext();
        await db.TargetRegistrationEvidenceReceipts.LoadAsync(); await db.AuditEvents.LoadAsync();
        await db.TargetCreationPermits.LoadAsync(); await db.TargetCreationOperations.LoadAsync(); await db.TargetCredentials.LoadAsync();
        await db.TargetCreationExecutionClaims.LoadAsync(); await db.RestoreIntents.LoadAsync(); await db.EnvironmentJobs.LoadAsync();
        await db.AgentLeases.LoadAsync(); await db.StagedArtifacts.LoadAsync(); await db.TestEnvironments.LoadAsync();
        await db.EnvironmentReservations.LoadAsync(); await db.EnvironmentQuotas.LoadAsync(); await db.EnvironmentRequests.LoadAsync();
        await db.StagingAllocationReleases.LoadAsync(); await db.Agents.LoadAsync();
        await db.EnvironmentPools.LoadAsync(); await db.ProjectPools.LoadAsync(); await db.ProjectMemberships.LoadAsync();
        await db.Users.LoadAsync(); await db.Roles.LoadAsync(); await db.UserRoles.LoadAsync();
        return JsonSerializer.Serialize(db.ChangeTracker.Entries().Select(e => e.Metadata.Name + ":" + JsonSerializer.Serialize(
            e.Properties.OrderBy(p => p.Metadata.Name).ToDictionary(p => p.Metadata.Name, p => p.CurrentValue))).OrderBy(x => x));
    }

    [Fact]
    public async Task Default_off_invalid_id_and_disposed_context_fail_closed_without_provider_details()
    {
        await using var db = new BuildSystemDbContext(new DbContextOptionsBuilder<BuildSystemDbContext>()
            .UseSqlServer("this is not a connection string").Options);
        Assert.False(new TargetCreationOperationOptions().Enabled);
        var actor = new Actor(Guid.NewGuid(), true);
        await Reject(() => new SqlTargetRegistrationEvidenceReads(db, new()).GetAsync(actor, Guid.NewGuid(), Ct),
            409, "TargetCreationDisabled");
        var enabled = new SqlTargetRegistrationEvidenceReads(db, new() { Enabled = true });
        await Reject(() => enabled.GetAsync(actor, Guid.Empty, Ct), 400, "InvalidTargetCreation");
        await db.DisposeAsync();
        await Reject(() => enabled.GetAsync(actor, Guid.NewGuid(), Ct), 503, "TargetRegistrationEvidenceHistoryUnavailable");
    }

    [SqlServerFact]
    public async Task Known_operation_without_evidence_is_an_inert_absent_view_and_unknown_is_404()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql, record: false);
        var before = await Snapshot(sql); await using var db = sql.NewContext();
        var view = await Get(db, f); Inert(view);
        Assert.Equal(f.Claim.OperationId, view.OperationId); Assert.False(view.EvidenceRecorded); Assert.Null(view.HistoricalEvidence);
        await Reject(() => Get(db, f, operation: Guid.NewGuid()), 404, "TargetCreationNotFound");
        Assert.Equal(before, await Snapshot(sql));
    }

    [SqlServerFact]
    public async Task Every_stored_field_is_returned_exactly_without_credentials_authority_or_mutation()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql);
        var before = await Snapshot(sql); await using var db = sql.NewContext();
        var view = await Get(db, f); Inert(view); Assert.True(view.EvidenceRecorded);
        Assert.Equal(f.Claim.OperationId, view.OperationId);
        var history = Assert.IsType<TargetRegistrationEvidenceHistory>(view.HistoricalEvidence);
        var row = await db.TargetRegistrationEvidenceReceipts.AsNoTracking().SingleAsync();
        // All persisted fields, not just the reported subset or selected linkage identifiers.
        var fields = typeof(TargetRegistrationEvidenceRecord).GetProperties();
        Assert.Equal(26, fields.Length);
        foreach (var field in fields)
        {
            var returned = typeof(TargetRegistrationEvidenceHistory).GetProperty(field.Name);
            Assert.NotNull(returned); Assert.Equal(field.GetValue(row), returned.GetValue(history));
        }
        Assert.DoesNotContain("canary", JsonSerializer.Serialize(view), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("TargetRegistrationEvidenceView [NONEXECUTABLE; independent verification required; details redacted]", view.ToString());
        Assert.Equal("TargetRegistrationEvidenceHistory [NONEXECUTABLE; historical evidence; details redacted]", history.ToString());
        Assert.Equal(view, await Get(db, f)); Assert.Equal(before, await Snapshot(sql));
    }

    [SqlServerFact]
    public async Task Current_database_administrator_is_required_before_operation_existence_is_disclosed()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql);
        await using var db = sql.NewContext(); var before = await Snapshot(sql);
        var forged = new Actor(f.Seed.Owner.UserId, true);
        foreach (var operation in new[] { f.Claim.OperationId, Guid.NewGuid() })
            await Reject(() => Get(db, f, forged, operation), 403, "TargetCreationForbidden");
        Assert.Equal(before, await Snapshot(sql));
        await db.UserRoles.Where(x => x.UserId == f.Seed.Admin.UserId).ExecuteDeleteAsync(Ct);
        before = await Snapshot(sql);
        foreach (var operation in new[] { f.Claim.OperationId, Guid.NewGuid() })
            await Reject(() => Get(db, f, f.Seed.Admin, operation), 403, "TargetCreationForbidden");
        Assert.Equal(before, await Snapshot(sql));
    }

    [SqlServerFact]
    public async Task Expired_history_survives_agent_credential_and_admission_revocation()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql, expired: true);
        await using var db = sql.NewContext(); var accepted = await Get(db, f);
        Assert.True(f.Claim.ExpiresAt < DateTimeOffset.UtcNow);
        await new SqlTargetCredentials(db, new() { Enabled = true }, f.Protector)
            .RevokeAsync(f.Seed.Admin, f.Binding.CredentialId, 1, Ct);
        await new SqlAgentControl(db).RevokeAsync(f.Seed.Admin, f.Seed.Credential.AgentId, Ct);
        await new SqlTargetCreationPermits(db,
            new() { Enabled = true, AgentStartEnabled = true, CredentialBindingEnabled = true }, f.Seed.Options)
            .RevokeAsync(f.Seed.Admin, f.Claim.PermitId, 2, Ct);
        await db.AgentLeases.Where(x => x.JobId == f.Seed.Order.JobId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddDays(-2)), Ct);
        await db.EnvironmentPools.Where(x => x.Id == f.Seed.Pool)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.AgentScopeId, Guid.NewGuid()), Ct);
        await db.ProjectMemberships.Where(x => x.ProjectId == f.Seed.Project && x.UserId == f.Seed.Owner.UserId).ExecuteDeleteAsync(Ct);
        await db.ProjectPools.Where(x => x.ProjectId == f.Seed.Project).ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false), Ct);
        var before = await Snapshot(sql); var view = await Get(db, f);
        Assert.Equal(accepted, view); Inert(view); Assert.Equal(before, await Snapshot(sql));
    }

    private sealed class Commands : DbCommandInterceptor
    {
        public List<string> Seen { get; } = new();
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Seen.Add(command.CommandText); return ValueTask.FromResult(result);
        }
    }

    [SqlServerFact]
    public async Task Getter_never_queries_target_credentials_or_ciphertext()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql);
        var before = await Snapshot(sql); var spy = new Commands();
        await using var db = new BuildSystemDbContext(new DbContextOptionsBuilder<BuildSystemDbContext>()
            .UseSqlServer(sql.ConnectionString).AddInterceptors(spy).Options);
        Inert(await Get(db, f)); Assert.NotEmpty(spy.Seen);
        Assert.All(spy.Seen, command =>
        {
            Assert.DoesNotContain("TargetCredentials", command, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Ciphertext", command, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("EncryptedEnvelope", command, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(before, await Snapshot(sql));
    }

    [SqlServerFact]
    public async Task Corrupt_stored_canonical_date_is_rejected_without_mutation()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql);
        await using var db = sql.NewContext();
        // Test-only corruption: production append-only protection must be restored before invoking the reader.
        await db.Database.ExecuteSqlRawAsync("DISABLE TRIGGER [TR_TargetRegistrationEvidenceRecords_AppendOnly] ON [TargetRegistrationEvidenceRecords]", Ct);
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [TargetRegistrationEvidenceRecords] SET [SqlCreateDate] = N'not-a-date' WHERE [ClaimId] = {f.Claim.ClaimId}", Ct);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("ENABLE TRIGGER [TR_TargetRegistrationEvidenceRecords_AppendOnly] ON [TargetRegistrationEvidenceRecords]", Ct);
        }
        var before = await Snapshot(sql);
        await Reject(() => Get(db, f), 409, "TargetCreationMismatch");
        Assert.Equal(before, await Snapshot(sql));
    }
}
