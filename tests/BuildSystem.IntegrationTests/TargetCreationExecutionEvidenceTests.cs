using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Domain;
using BuildSystem.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Seed = BuildSystem.IntegrationTests.SqlTargetCreationOperationTests.Seed;

namespace BuildSystem.IntegrationTests;

// Opt-in disposable metadata SQL only. No target SQL, 1C, worker or execution-claim issuance.
public sealed class TargetCreationExecutionEvidenceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static TargetCreationPermitOptions Gates => new() { Enabled = true, AgentStartEnabled = true, CredentialBindingEnabled = true };
    private sealed record Fixture(Seed Seed, TargetCreationExecutionClaim Claim,
        ITargetCredentialProtector Protector);

    private static async Task<Fixture> PrepareClaim(SqlServerTestDatabase sql, bool expired = false)
    {
        var seed = await SqlTargetCreationOperationTests.PrepareAsync(sql);
        await using var db = sql.NewContext();
        var operation = await new SqlTargetCreationOperations(db, seed.Options).RegisterAsync(seed.Admin,
            seed.Order.EnvironmentId, seed.Restore.RestoreIntentId, Ct);
        var binding = new TargetCredentialBinding(Guid.NewGuid(), operation.CanonicalClusterId,
            operation.CanonicalSqlServerId, Guid.NewGuid());
        var protector = new DataProtectionTargetCredentialProtector(new EphemeralDataProtectionProvider());
        await new SqlTargetCredentials(db, new() { Enabled = true }, protector).StoreAsync(seed.Admin, binding,
            new("evidence-user-canary", "evidence-password-canary", "evidence-sql-user-canary", "evidence-sql-password-canary"), Ct);
        var permits = new SqlTargetCreationPermits(db, Gates, seed.Options);
        var permit = await permits.IssueWithCredentialAsync(seed.Admin, operation.OperationId, 1,
            Guid.NewGuid(), Guid.NewGuid(), binding.CredentialId, binding.EndpointMappingId, Ct);
        permit = await permits.RecordAgentStartAsync(seed.Credential.Token, permit.PermitId, 1, permit.AttemptId, Ct);
        var shift = expired ? TimeSpan.FromDays(-1) : TimeSpan.Zero;
        if (expired)
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [TargetCreationPermits] SET [IssuedAt] = DATEADD(day, -1, [IssuedAt]), [StartBefore] = DATEADD(day, -1, [StartBefore]), [StartedAt] = DATEADD(day, -1, [StartedAt]) WHERE [PermitId] = {permit.PermitId}", Ct);
        // Synthetic historical evidence in our disposable database, never a fresh executable grant.
        var claim = TargetCreationExecutionClaim.Issue(Guid.NewGuid(), permit.PermitId, permit.OperationId,
            permit.AttemptId, seed.Credential.AgentId, binding.CredentialId, binding.EndpointMappingId,
            permit.IssuedAt + shift, permit.StartBefore + shift, permit.StartedAt!.Value + shift);
        db.TargetCreationExecutionClaims.Add(claim);
        await db.SaveChangesAsync(Ct);
        return new(seed, claim, protector);
    }

    private static async Task<TargetCreationExecutionEvidenceView> Read(SqlServerTestDatabase sql, Seed seed,
        Guid operationId, Actor? actor = null)
    {
        await using var db = sql.NewContext();
        ITargetCreationExecutionEvidenceReads reads = new SqlTargetCreationExecutionEvidenceReads(db, seed.Options);
        var view = await reads.GetAsync(actor ?? seed.Admin, operationId, Ct);
        Assert.False(db.ChangeTracker.HasChanges());
        return view;
    }

    private static void Inert(TargetCreationExecutionEvidenceView view)
    {
        Assert.False(view.Executable); Assert.False(view.FreshAuthority); Assert.False(view.RetryAllowed);
        Assert.True(view.ScheduledJobsMustRemainBlocked); Assert.True(view.ReconciliationRequired);
        Assert.False(view.PhysicalOutcomeKnown);
        var json = JsonSerializer.Serialize(view);
        Assert.DoesNotContain("canary", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Succeeded", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Ciphertext", json, StringComparison.OrdinalIgnoreCase);
    }

    private static void Historical(TargetCreationExecutionClaim expected, TargetCreationExecutionEvidenceView view)
    {
        Inert(view); Assert.Equal(expected.OperationId, view.OperationId); Assert.True(view.ClaimRecorded);
        var claim = Assert.IsType<TargetCreationExecutionHistoricalClaim>(view.HistoricalClaim);
        Assert.Equal(expected.ClaimId, claim.ClaimId); Assert.Equal(expected.PermitId, claim.PermitId);
        Assert.Equal(expected.AttemptId, claim.AttemptId); Assert.Equal(expected.AgentId, claim.AgentId);
        Assert.Equal(expected.CredentialId, claim.CredentialId); Assert.Equal(expected.EndpointMappingId, claim.EndpointMappingId);
        Assert.Equal(expected.IssuedAt, claim.IssuedAt); Assert.Equal(expected.ExpiresAt, claim.ExpiresAt);
    }

    private static async Task<string> Snapshot(SqlServerTestDatabase sql)
    {
        await using var db = sql.NewContext();
        await db.TargetCreationExecutionClaims.LoadAsync(); await db.TargetCreationPermits.LoadAsync();
        await db.TargetCreationOperations.LoadAsync(); await db.TargetCredentials.LoadAsync();
        await db.AuditEvents.LoadAsync(); await db.RestoreIntents.LoadAsync(); await db.EnvironmentJobs.LoadAsync();
        await db.AgentLeases.LoadAsync(); await db.StagedArtifacts.LoadAsync(); await db.TestEnvironments.LoadAsync();
        await db.EnvironmentReservations.LoadAsync(); await db.EnvironmentQuotas.LoadAsync(); await db.EnvironmentRequests.LoadAsync();
        await db.StagingAllocationReleases.LoadAsync(); await db.ProjectMemberships.LoadAsync();
        await db.ProjectPools.LoadAsync(); await db.EnvironmentPools.LoadAsync(); await db.UserRoles.LoadAsync();
        return JsonSerializer.Serialize(db.ChangeTracker.Entries().Select(e => e.Metadata.Name + ":" + JsonSerializer.Serialize(
            e.Properties.OrderBy(p => p.Metadata.Name).ToDictionary(p => p.Metadata.Name, p => p.CurrentValue))).OrderBy(x => x));
    }

    [SqlServerFact]
    public async Task Current_administrator_reads_claim_with_original_pins_and_times_without_authority_or_mutation()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await PrepareClaim(sql);
        var before = await Snapshot(sql);
        Historical(f.Claim, await Read(sql, f.Seed, f.Claim.OperationId));
        Historical(f.Claim, await Read(sql, f.Seed, f.Claim.OperationId));
        Assert.Equal(before, await Snapshot(sql));
    }

    [SqlServerFact]
    public async Task Expired_claim_retains_original_times_and_pins_without_new_window_or_success_assertion()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await PrepareClaim(sql, expired: true);
        Assert.True(f.Claim.ExpiresAt < DateTimeOffset.UtcNow);
        var before = await Snapshot(sql);
        Historical(f.Claim, await Read(sql, f.Seed, f.Claim.OperationId));
        Assert.Equal(before, await Snapshot(sql));
    }

    [SqlServerFact]
    public async Task Registered_operation_without_claim_is_null_and_outcome_unknown_not_retry_authority()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await SqlTargetCreationOperationTests.PrepareAsync(sql);
        Guid operationId;
        await using (var db = sql.NewContext())
            operationId = (await new SqlTargetCreationOperations(db, seed.Options).RegisterAsync(seed.Admin,
                seed.Order.EnvironmentId, seed.Restore.RestoreIntentId, Ct)).OperationId;
        var before = await Snapshot(sql); var view = await Read(sql, seed, operationId);
        Assert.Equal(operationId, view.OperationId); Assert.False(view.ClaimRecorded); Assert.Null(view.HistoricalClaim);
        Inert(view); Assert.Equal(before, await Snapshot(sql));
    }

    [SqlServerFact]
    public async Task Agent_credential_and_admission_revocation_after_claim_do_not_erase_historical_metadata()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await PrepareClaim(sql);
        await using (var setup = sql.NewContext())
        {
            await new SqlAgentControl(setup).RevokeAsync(f.Seed.Admin, f.Seed.Credential.AgentId, Ct);
            await new SqlTargetCredentials(setup, new() { Enabled = true }, f.Protector)
                .RevokeAsync(f.Seed.Admin, f.Claim.CredentialId, 1, Ct);
            await setup.ProjectMemberships.Where(x => x.ProjectId == f.Seed.Project && x.UserId == f.Seed.Owner.UserId)
                .ExecuteDeleteAsync(Ct);
        }
        var before = await Snapshot(sql);
        Historical(f.Claim, await Read(sql, f.Seed, f.Claim.OperationId));
        Assert.Equal(before, await Snapshot(sql));
    }

    [SqlServerFact]
    public async Task Stale_administrator_claim_is_forbidden_by_current_database_role_before_operation_existence()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await PrepareClaim(sql);
        await using (var setup = sql.NewContext())
            await setup.UserRoles.Where(x => x.UserId == f.Seed.Admin.UserId).ExecuteDeleteAsync(Ct);
        var before = await Snapshot(sql);
        foreach (var operation in new[] { f.Claim.OperationId, Guid.NewGuid() })
        {
            var error = await Assert.ThrowsAsync<RequestRejectedException>(() => Read(sql, f.Seed, operation));
            Assert.Equal(403, error.StatusCode); Assert.Null(error.InnerException);
        }
        Assert.Equal(before, await Snapshot(sql));
    }

    [SqlServerFact]
    public async Task Current_administrator_missing_operation_gets_not_found_without_mutation()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await SqlTargetCreationOperationTests.PrepareAsync(sql); var before = await Snapshot(sql);
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => Read(sql, seed, Guid.NewGuid()));
        Assert.Equal(404, error.StatusCode); Assert.Equal(before, await Snapshot(sql));
    }

    [Fact]
    public async Task Disabled_feature_rejects_before_any_database_provider_is_required()
    {
        await using var db = new BuildSystemDbContext(new DbContextOptionsBuilder<BuildSystemDbContext>().Options);
        var reads = new SqlTargetCreationExecutionEvidenceReads(db, new() { Enabled = false });
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() =>
            reads.GetAsync(new Actor(Guid.NewGuid(), true), Guid.NewGuid(), Ct));
        Assert.Equal(409, error.StatusCode); Assert.Equal("TargetCreationDisabled", error.Code);
    }
}
