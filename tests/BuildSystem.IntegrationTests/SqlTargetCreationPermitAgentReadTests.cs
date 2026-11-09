using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Domain;
using BuildSystem.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Seed = BuildSystem.IntegrationTests.SqlTargetCreationOperationTests.Seed;

namespace BuildSystem.IntegrationTests;

// Disposable metadata SQL only: reads must never start an attempt or deliver target secrets.
public sealed class SqlTargetCreationPermitAgentReadTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static TargetCreationPermitOptions Gates => new() { Enabled = true, AgentReadEnabled = true, CredentialBindingEnabled = true, AgentStartEnabled = true };
    private sealed record Fixture(Seed Seed, TargetCreationPermitReceipt Permit, ITargetCredentialProtector Protector);
    private static async Task<Fixture> Prepare(SqlServerTestDatabase sql)
    {
        var seed = await SqlTargetCreationOperationTests.PrepareAsync(sql);
        await using var db = sql.NewContext();
        var operation = await new SqlTargetCreationOperations(db, seed.Options).RegisterAsync(seed.Admin, seed.Order.EnvironmentId, seed.Restore.RestoreIntentId, Ct);
        var protector = new DataProtectionTargetCredentialProtector(new EphemeralDataProtectionProvider());
        var binding = new TargetCredentialBinding(Guid.NewGuid(), operation.CanonicalClusterId, operation.CanonicalSqlServerId, Guid.NewGuid());
        await new SqlTargetCredentials(db, new() { Enabled = true }, protector).StoreAsync(seed.Admin, binding,
            new("read-cluster-user-canary", "read-cluster-password-canary", "read-sql-user-canary", "read-sql-password-canary"), Ct);
        var permit = await new SqlTargetCreationPermits(db, Gates, seed.Options).IssueWithCredentialAsync(seed.Admin,
            operation.OperationId, 1, Guid.NewGuid(), Guid.NewGuid(), binding.CredentialId, binding.EndpointMappingId, Ct);
        return new(seed, permit, protector);
    }
    private static async Task<TargetCreationPermitReceipt> Read(SqlServerTestDatabase sql, Fixture f, string? token = null,
        Guid? id = null, TargetCreationPermitOptions? gates = null, TargetCreationOperationOptions? creation = null)
    {
        await using var db = sql.NewContext();
        ITargetCreationPermitAgentReads reads = new SqlTargetCreationPermits(db, gates ?? Gates, creation ?? f.Seed.Options);
        var receipt = await reads.GetForAgentAsync(token ?? f.Seed.Credential.Token, id ?? f.Permit.PermitId, Ct);
        Assert.False(db.ChangeTracker.HasChanges());
        return receipt;
    }
    private static async Task Reject(Func<Task<TargetCreationPermitReceipt>> action, int status, string? code = null)
    {
        var error = await Assert.ThrowsAsync<RequestRejectedException>(action);
        Assert.Equal(status, error.StatusCode); if (code is not null) Assert.Equal(code, error.Code);
    }
    private static async Task<string> Snapshot(SqlServerTestDatabase sql)
    {
        await using var db = sql.NewContext();
        await db.TargetCreationPermits.LoadAsync(); await db.TargetCreationOperations.LoadAsync(); await db.TargetCredentials.LoadAsync();
        await db.AuditEvents.LoadAsync(); await db.RestoreIntents.LoadAsync(); await db.EnvironmentJobs.LoadAsync();
        await db.AgentLeases.LoadAsync(); await db.StagedArtifacts.LoadAsync(); await db.TestEnvironments.LoadAsync();
        await db.EnvironmentReservations.LoadAsync(); await db.Agents.LoadAsync(); await db.ProjectMemberships.LoadAsync();
        return JsonSerializer.Serialize(db.ChangeTracker.Entries().Select(e => e.Metadata.Name + ":" +
            JsonSerializer.Serialize(e.Properties.OrderBy(p => p.Metadata.Name).ToDictionary(p => p.Metadata.Name, p => p.CurrentValue))).OrderBy(x => x));
    }
    [SqlServerFact]
    public async Task Exact_bound_prepared_permit_repeats_without_consumption_renewal_or_any_metadata_write()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql); var before = await Snapshot(sql);
        for (var i = 0; i < 2; i++)
        {
            var receipt = await Read(sql, f);
            Assert.Equal(f.Permit with { NewlyRecorded = false }, receipt);
            Assert.Equal(TargetCreationPermitState.Prepared, receipt.State); Assert.Equal(1, receipt.Revision);
            Assert.Null(receipt.StartedAt); Assert.Null(receipt.StartExpectedRevision); Assert.False(receipt.NewlyRecorded);
            Assert.Equal(TargetCreationOperationState.Registered, receipt.Operation.State); Assert.Equal(1, receipt.Operation.Revision);
            Assert.Null(receipt.Operation.AttemptId); Assert.Null(receipt.Operation.QuarantineReason);
            Assert.False(receipt.Executable); Assert.True(receipt.ScheduledJobsMustRemainBlocked);
            Assert.False(receipt.Operation.Executable); Assert.True(receipt.Operation.ScheduledJobsMustRemainBlocked);
            Assert.Equal(before, await Snapshot(sql));
        }
        await Reject(() => Read(sql, f, id: Guid.NewGuid()), 404, "TargetCreationPermitNotFound");
        Assert.Equal(before, await Snapshot(sql));
    }
    [SqlServerFact]
    public async Task Current_token_approval_and_assigned_agent_identity_are_required_without_mutation()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql); var other = await Prepare(sql);
        var before = await Snapshot(sql);
        await Reject(() => Read(sql, f, token: "wrong-token"), 401, "AgentUnauthorized");
        await Reject(() => Read(sql, f, token: "wrong-token", id: Guid.NewGuid()), 401, "AgentUnauthorized");
        await Reject(() => Read(sql, f, token: other.Seed.Credential.Token), 403, "TargetCreationPermitForbidden");
        Assert.Equal(before, await Snapshot(sql));
        await using (var db = sql.NewContext())
            await db.Agents.Where(x => x.Id == f.Permit.Operation.AgentId).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Revoked"));
        before = await Snapshot(sql);
        await Reject(() => Read(sql, f), 401, "AgentUnauthorized");
        Assert.Equal(before, await Snapshot(sql));
    }
    [SqlServerFact]
    public async Task Every_service_gate_is_default_off_and_precedes_read_or_mutation()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql); var before = await Snapshot(sql);
        foreach (var gates in new[] { new TargetCreationPermitOptions(),
            new TargetCreationPermitOptions { AgentReadEnabled = true, CredentialBindingEnabled = true },
            new TargetCreationPermitOptions { Enabled = true, CredentialBindingEnabled = true },
            new TargetCreationPermitOptions { Enabled = true, AgentReadEnabled = true } })
            await Reject(() => Read(sql, f, gates: gates), 503, "TargetCreationPermitAgentReadDisabled");
        await Reject(() => Read(sql, f, creation: new()), 503, "TargetCreationPermitAgentReadDisabled");
        Assert.Equal(before, await Snapshot(sql));
    }
    [SqlServerFact]
    public async Task Sql_deadline_started_revoked_and_quarantined_permits_are_not_ready_and_never_mutated()
    {
        foreach (var state in new[] { "expired", "started", "revoked", "quarantined" })
        {
            await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql);
            await using (var db = sql.NewContext())
            {
                var permits = new SqlTargetCreationPermits(db, Gates, f.Seed.Options);
                if (state == "expired")
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TargetCreationPermits SET IssuedAt = DATEADD(minute, -10, IssuedAt), StartBefore = DATEADD(minute, -10, StartBefore) WHERE PermitId = {f.Permit.PermitId}");
                else if (state == "started")
                    await permits.RecordAgentStartAsync(f.Seed.Credential.Token, f.Permit.PermitId, 1, f.Permit.AttemptId, Ct);
                else if (state == "revoked") await permits.RevokeAsync(f.Seed.Admin, f.Permit.PermitId, 1, Ct);
                else await new SqlTargetCreationOperations(db, f.Seed.Options).QuarantineAsync(f.Seed.Admin, f.Permit.OperationId, 1, TargetCreationQuarantineReason.OutcomeUnknown, Ct);
            }
            var before = await Snapshot(sql);
            await Reject(() => Read(sql, f), 409, "TargetCreationPermitNotReady");
            Assert.Equal(before, await Snapshot(sql));
        }
    }
    [SqlServerFact]
    public async Task Current_credential_mapping_and_admission_withdrawals_block_even_repeated_reads()
    {
        foreach (var withdrawal in new[] { "credential", "mapping", "membership" })
        {
            await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql); await Read(sql, f);
            if (withdrawal == "credential")
            {
                await using var db = sql.NewContext();
                await new SqlTargetCredentials(db, new() { Enabled = true }, f.Protector).RevokeAsync(f.Seed.Admin, f.Permit.CredentialId!.Value, 1, Ct);
            }
            else if (withdrawal == "membership")
            {
                await using var db = sql.NewContext();
                await db.ProjectMemberships.Where(x => x.UserId == f.Seed.Owner.UserId && x.ProjectId == f.Seed.Project).ExecuteDeleteAsync();
            }
            var before = await Snapshot(sql);
            await Reject(() => Read(sql, f, creation: withdrawal == "mapping" ? new TargetCreationOperationOptions { Enabled = true } : null), 409);
            Assert.Equal(before, await Snapshot(sql));
        }
    }
}
