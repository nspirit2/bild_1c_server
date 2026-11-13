using System.Data.Common;
using System.Globalization;
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

// Conditional disposable metadata SQL only: no target connection, executable claim issuance or HTTP wiring.
public sealed class TargetRegistrationEvidenceReceiptTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Profile = "rac-registration-8.3.22.2557-v1";
    private static TargetCreationPermitOptions Gates => new() { Enabled = true, AgentStartEnabled = true, CredentialBindingEnabled = true };
    private sealed record Fixture(Seed Seed, TargetCreationPermitReceipt Permit, TargetCredentialBinding Binding,
        ITargetCredentialProtector Protector, TargetCreationExecutionClaim Claim, ReportTargetRegistrationEvidence Report);

    private static async Task<Fixture> Prepare(SqlServerTestDatabase sql, bool expired = false,
        Func<TargetCreationExecutionClaim, TargetCreationExecutionClaim>? alterClaim = null, bool persistClaim = true)
    {
        var seed = await SqlTargetCreationOperationTests.PrepareAsync(sql);
        await using var db = sql.NewContext();
        var operation = await new SqlTargetCreationOperations(db, seed.Options).RegisterAsync(seed.Admin,
            seed.Order.EnvironmentId, seed.Restore.RestoreIntentId, Ct);
        var binding = new TargetCredentialBinding(Guid.NewGuid(), operation.CanonicalClusterId, operation.CanonicalSqlServerId, Guid.NewGuid());
        var protector = new DataProtectionTargetCredentialProtector(new EphemeralDataProtectionProvider());
        await new SqlTargetCredentials(db, new() { Enabled = true }, protector).StoreAsync(seed.Admin, binding,
            new("receipt-user-canary", "receipt-password-canary", "receipt-sql-user-canary", "receipt-sql-password-canary"), Ct);
        var permits = new SqlTargetCreationPermits(db, Gates, seed.Options);
        var permit = await permits.IssueWithCredentialAsync(seed.Admin, operation.OperationId, 1, Guid.NewGuid(), Guid.NewGuid(),
            binding.CredentialId, binding.EndpointMappingId, Ct);
        permit = await permits.RecordAgentStartAsync(seed.Credential.Token, permit.PermitId, 1, permit.AttemptId, Ct);
        if (expired)
        {
            // Historical fixture, not a grant renewal: preserve the original exact five-minute window.
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [TargetCreationPermits] SET [IssuedAt] = DATEADD(day, -1, [IssuedAt]), [StartBefore] = DATEADD(day, -1, [StartBefore]), [StartedAt] = DATEADD(day, -1, [StartedAt]) WHERE [PermitId] = {permit.PermitId}", Ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [TargetCredentials] SET [CreatedAt] = DATEADD(day, -1, [CreatedAt]) WHERE [CredentialId] = {binding.CredentialId}", Ct);
        }
        var persisted = await db.TargetCreationPermits.AsNoTracking().SingleAsync(x => x.PermitId == permit.PermitId);
        // Seed issuance HISTORY directly; this suite must never obtain an executable grant.
        var claim = TargetCreationExecutionClaim.Issue(Guid.NewGuid(), permit.PermitId, operation.OperationId,
            permit.AttemptId, seed.Credential.AgentId, binding.CredentialId, binding.EndpointMappingId,
            persisted.IssuedAt, persisted.StartBefore, persisted.StartedAt!.Value);
        claim = alterClaim?.Invoke(claim) ?? claim;
        if (persistClaim) { db.TargetCreationExecutionClaims.Add(claim); await db.SaveChangesAsync(Ct); }
        var intent = await db.RestoreIntents.AsNoTracking().SingleAsync(x => x.RestoreIntentId == seed.Restore.RestoreIntentId);
        var report = new ReportTargetRegistrationEvidence(claim.ClaimId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Profile, intent.InfobaseName, "operator-sql.example\\instance", intent.SqlDbName,
            37, new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Unspecified).ToString("O", CultureInfo.InvariantCulture), "ONLINE");
        return new(seed, permit, binding, protector, claim, report);
    }
    private static Task<TargetRegistrationEvidenceReceipt> Report(BuildSystemDbContext db, Fixture f,
        ReportTargetRegistrationEvidence? report = null, string? token = null) =>
        new SqlTargetRegistrationEvidenceReceipts(db, new() { Enabled = true }, f.Seed.Options)
            .ReportAsync(token ?? f.Seed.Credential.Token, report ?? f.Report, Ct);
    private static async Task Reject(Func<Task> action, int status = 409, string code = "TargetRegistrationEvidenceReceiptMismatch")
    {
        var error = await Assert.ThrowsAsync<RequestRejectedException>(action);
        Assert.Equal(status, error.StatusCode); Assert.Equal(code, error.Code); Assert.Null(error.InnerException);
        Assert.DoesNotContain("canary", error.ToString(), StringComparison.Ordinal);
    }
    private static void Inert(TargetRegistrationEvidenceReceipt receipt)
    {
        Assert.Equal("Recorded", receipt.Status); Assert.False(receipt.Executable); Assert.False(receipt.OwnershipEstablished);
        Assert.False(receipt.RestoreAllowed); Assert.False(receipt.RetryAllowed); Assert.False(receipt.FreshAuthority);
        Assert.True(receipt.RequiresIndependentVerification); Assert.True(receipt.ScheduledJobsMustRemainBlocked);
    }
    private static Task<DateTimeOffset> Now(BuildSystemDbContext db) => db.Database.SqlQuery<DateTimeOffset>(
        $"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync();
    private static async Task<string> Snapshot(SqlServerTestDatabase sql)
    {
        await using var db = sql.NewContext();
        await db.TargetCreationPermits.LoadAsync(); await db.TargetCreationOperations.LoadAsync(); await db.TargetCredentials.LoadAsync();
        await db.TargetCreationExecutionClaims.LoadAsync(); await db.RestoreIntents.LoadAsync(); await db.EnvironmentJobs.LoadAsync();
        await db.AgentLeases.LoadAsync(); await db.StagedArtifacts.LoadAsync(); await db.TestEnvironments.LoadAsync();
        await db.EnvironmentReservations.LoadAsync(); await db.EnvironmentQuotas.LoadAsync(); await db.EnvironmentRequests.LoadAsync();
        await db.StagingAllocationReleases.LoadAsync();
        return JsonSerializer.Serialize(db.ChangeTracker.Entries().Select(e => e.Metadata.Name + ":" + JsonSerializer.Serialize(
            e.Properties.OrderBy(p => p.Metadata.Name).ToDictionary(p => p.Metadata.Name, p => p.CurrentValue))).OrderBy(x => x));
    }

    [Fact]
    public async Task Disabled_and_invalid_shapes_fail_before_any_SQL_even_with_an_invalid_connection()
    {
        await using var db = new BuildSystemDbContext(new DbContextOptionsBuilder<BuildSystemDbContext>()
            .UseSqlServer("this is not a connection string").Options);
        var report = new ReportTargetRegistrationEvidence(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Profile, "target", "literal-server", "database", 5,
            new DateTime(2025, 1, 1).ToString("O", CultureInfo.InvariantCulture), "ONLINE");
        Assert.False(new TargetRegistrationEvidenceReceiptOptions().Enabled);
        await Reject(() => new SqlTargetRegistrationEvidenceReceipts(db, new(), new()).ReportAsync("bad", report, Ct),
            409, "TargetRegistrationEvidenceReceiptDisabled");
        var enabled = new SqlTargetRegistrationEvidenceReceipts(db, new() { Enabled = true }, new());
        foreach (var invalid in InvalidShapes(report))
            await Reject(() => enabled.ReportAsync("syntactically-present-token", invalid, Ct), 400, "InvalidTargetRegistrationEvidenceReceipt");
        // A provider/lifetime failure must never leak ObjectDisposedException through this boundary.
        await db.DisposeAsync();
        await Reject(() => enabled.ReportAsync("syntactically-present-token", report, Ct),
            503, "TargetRegistrationEvidenceReceiptUnavailable");
    }
    private static IEnumerable<ReportTargetRegistrationEvidence> InvalidShapes(ReportTargetRegistrationEvidence r)
    {
        yield return r with { ClaimId = Guid.Empty }; yield return r with { ReportedInfobaseId = Guid.Empty };
        yield return r with { RacObservationId = Guid.Empty }; yield return r with { SqlObservationId = Guid.Empty };
        yield return r with { SqlObservationId = r.RacObservationId };
        yield return r with { RacEndpointMappingId = Guid.Empty }; yield return r with { SqlEndpointMappingId = Guid.Empty };
        yield return r with { RegistrationProfileId = "unknown" }; yield return r with { InfobaseName = "\n" };
        yield return r with { SqlServer = "-option" }; yield return r with { SqlServer = "server with space" };
        yield return r with { DatabaseName = new string('d', 129) }; yield return r with { SqlDatabaseId = 0 };
        yield return r with { SqlCreateDate = "2025-01-01" };
        yield return r with { SqlCreateDate = "2025-01-02T03:04:05.0000000+14:01" };
        yield return r with { SqlState = "OFFLINE" };
    }

    [SqlServerFact]
    public async Task Expired_claim_is_historical_only_with_DB_UTC_acceptance_exact_pins_and_stable_replay()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql, expired: true);
        var before = await Snapshot(sql); await using var db = sql.NewContext();
        var lower = await Now(db); Assert.True(f.Claim.ExpiresAt < lower);
        var receipt = await Report(db, f); var upper = await Now(db); Inert(receipt);
        Assert.Equal(f.Claim.ClaimId, receipt.ClaimId); Assert.Equal(f.Claim.OperationId, receipt.OperationId);
        Assert.InRange(receipt.AcceptedAt, lower, upper); Assert.Equal(TimeSpan.Zero, receipt.AcceptedAt.Offset);
        Assert.Equal(receipt, await Report(db, f)); Assert.Equal(before, await Snapshot(sql));
        var row = await db.TargetRegistrationEvidenceReceipts.AsNoTracking().SingleAsync();
        Assert.Equal(receipt.AcceptedAt, row.AcceptedAt); Assert.Equal(f.Claim.PermitId, row.PermitId);
        Assert.Equal(f.Claim.AttemptId, row.AttemptId); Assert.Equal(f.Claim.AgentId, row.AgentId);
        Assert.Equal(f.Claim.CredentialId, row.CredentialId); Assert.Equal(f.Binding.EndpointMappingId, row.CreationEndpointMappingId);
        Assert.Equal(f.Seed.Restore.RestoreIntentId, row.RestoreIntentId);
        var intent = await db.RestoreIntents.AsNoTracking().SingleAsync(x => x.RestoreIntentId == row.RestoreIntentId);
        Assert.Equal(intent.FencingToken, row.FencingToken); Assert.Equal(intent.TargetIncarnation, row.TargetIncarnationId);
        Assert.Equal(intent.ReservationId, row.ReservationId); Assert.Equal(intent.CanonicalClusterId, row.CanonicalClusterId);
        Assert.Equal(intent.CanonicalSqlServerId, row.CanonicalSqlServerId);
        Assert.NotEqual(row.CreationEndpointMappingId, row.RacEndpointMappingId);
        Assert.NotEqual(row.CreationEndpointMappingId, row.SqlEndpointMappingId);
        Assert.Equal(f.Report.SqlServer, row.SqlServer); Assert.Equal(f.Report.SqlCreateDate, row.SqlCreateDate);
        Assert.Equal(DateTimeKind.Unspecified, DateTime.ParseExact(row.SqlCreateDate, "O", CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).Kind);
        Assert.DoesNotContain("ObservedAt", JsonSerializer.Serialize(row), StringComparison.Ordinal);
        Assert.DoesNotContain("canary", JsonSerializer.Serialize(receipt), StringComparison.Ordinal);
        Assert.Equal(1, await db.AuditEvents.CountAsync(x => x.Action == "TargetRegistrationEvidenceRecorded" && x.TargetId == row.ClaimId));
        foreach (var date in new[] { "2025-01-02T03:04:05.0000000Z", "2025-01-02T03:04:05.0000000+03:00" })
        {
            var dated = await Prepare(sql);
            var payload = dated.Report with { SqlCreateDate = date };
            var spy = new CredentialCommands(); await using var observed = Intercepted(sql, spy);
            var datedReceipt = await Report(observed, dated, payload); Inert(datedReceipt);
            Assert.Equal(datedReceipt, await Report(observed, dated, payload));
            var stored = await observed.TargetRegistrationEvidenceReceipts.AsNoTracking().SingleAsync(x => x.ClaimId == dated.Claim.ClaimId);
            Assert.Equal(date, stored.SqlCreateDate); // Including foreign local offset: no zone conversion.
            Assert.NotEmpty(spy.Commands);
            Assert.All(spy.Commands, command => Assert.DoesNotContain("Ciphertext", command, StringComparison.OrdinalIgnoreCase));
        }
    }

    [SqlServerFact]
    public async Task Every_report_field_is_exact_replay_identity_and_shape_failures_are_fixed_400()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql);
        await using var db = sql.NewContext(); var accepted = await Report(db, f); var before = await Snapshot(sql);
        var r = f.Report;
        var changes = new[] {
            r with { ClaimId = Guid.NewGuid() }, r with { ReportedInfobaseId = Guid.NewGuid() },
            r with { RacObservationId = Guid.NewGuid() }, r with { SqlObservationId = Guid.NewGuid() },
            r with { RacEndpointMappingId = Guid.NewGuid() }, r with { SqlEndpointMappingId = Guid.NewGuid() },
            r with { InfobaseName = r.InfobaseName + "x" }, r with { SqlServer = r.SqlServer.ToUpperInvariant() },
            r with { DatabaseName = r.DatabaseName + "x" }, r with { SqlDatabaseId = r.SqlDatabaseId + 1 },
            r with { SqlCreateDate = new DateTime(2025, 2, 3, 4, 5, 7, DateTimeKind.Unspecified).ToString("O", CultureInfo.InvariantCulture) }
        };
        foreach (var change in changes) await Reject(() => Report(db, f, change));
        foreach (var change in InvalidShapes(r))
            await Reject(() => Report(db, f, change), 400, "InvalidTargetRegistrationEvidenceReceipt");
        Assert.Equal(accepted, await Report(db, f)); Assert.Equal(1, await db.TargetRegistrationEvidenceReceipts.CountAsync());
        Assert.Equal(before, await Snapshot(sql));
    }

    [SqlServerFact]
    public async Task Current_agent_credential_and_admission_gates_apply_before_replay()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql);
        var other = await Prepare(sql); await using var db = sql.NewContext(); var first = await Report(db, f);
        foreach (var token in new[] { "", "invalid-token", new string('x', 257), other.Seed.Credential.Token })
            await Reject(() => Report(db, f, token: token), 401, "AgentUnauthorized");
        var rotated = await new SqlAgentControl(db).RotateAsync(f.Seed.Admin, f.Seed.Credential.AgentId, Ct);
        await Reject(() => Report(db, f), 401, "AgentUnauthorized");
        Assert.Equal(first, await Report(db, f, token: rotated.Token));
        await new SqlTargetCredentials(db, new() { Enabled = true }, f.Protector).RevokeAsync(f.Seed.Admin, f.Binding.CredentialId, 1, Ct);
        await Reject(() => Report(db, f, token: rotated.Token));
        await new SqlAgentControl(db).RevokeAsync(f.Seed.Admin, f.Seed.Credential.AgentId, Ct);
        await Reject(() => Report(db, f, token: rotated.Token), 401, "AgentUnauthorized");
        var otherReceipt = await Report(db, other);
        await db.ProjectMemberships.Where(x => x.ProjectId == other.Seed.Project && x.UserId == other.Seed.Owner.UserId).ExecuteDeleteAsync();
        var before = await Snapshot(sql);
        await Reject(() => Report(db, other), code: "TargetCreationMismatch");
        Assert.Equal(before, await Snapshot(sql));
        Assert.Equal(first.AcceptedAt, (await db.TargetRegistrationEvidenceReceipts.AsNoTracking().SingleAsync(x => x.ClaimId == first.ClaimId)).AcceptedAt);
        Assert.Equal(otherReceipt.AcceptedAt, (await db.TargetRegistrationEvidenceReceipts.AsNoTracking().SingleAsync(x => x.ClaimId == otherReceipt.ClaimId)).AcceptedAt);
    }

    private static TargetCreationExecutionClaim CopyClaim(TargetCreationExecutionClaim c, Guid? agent = null,
        Guid? permit = null, Guid? operation = null, Guid? attempt = null, Guid? credential = null, Guid? mapping = null) =>
        TargetCreationExecutionClaim.Issue(c.ClaimId, permit ?? c.PermitId, operation ?? c.OperationId,
            attempt ?? c.AttemptId, agent ?? c.AgentId, credential ?? c.CredentialId, mapping ?? c.EndpointMappingId,
            c.ExpiresAt.AddMinutes(-5), c.ExpiresAt, c.IssuedAt);

    [SqlServerFact]
    public async Task Historical_chain_rejects_wrong_permit_operation_attempt_agent_credential_mapping_without_mutation()
    {
        foreach (var pin in new[] { "permit", "operation", "attempt", "agent", "credential", "mapping" })
        {
            await using var sql = await SqlServerTestDatabase.CreateAsync();
            // Retain genuine FK targets, without a second claim occupying its unique pins.
            var retained = await Prepare(sql, persistClaim: false);
            var f = await Prepare(sql, alterClaim: c => pin switch {
                "permit" => CopyClaim(c, permit: retained.Permit.PermitId),
                "operation" => CopyClaim(c, operation: retained.Permit.OperationId),
                "attempt" => CopyClaim(c, attempt: Guid.NewGuid()),
                "agent" => CopyClaim(c, agent: retained.Seed.Credential.AgentId),
                "credential" => CopyClaim(c, credential: retained.Binding.CredentialId),
                _ => CopyClaim(c, mapping: Guid.NewGuid())
            });
            var before = await Snapshot(sql); await using var db = sql.NewContext();
            // A crossed operation resolves to another agent's intent: authorization must win
            // over structural mismatch, just as for a crossed claim.AgentId.
            var unauthorized = pin is "agent" or "operation";
            await Reject(() => Report(db, f), unauthorized ? 401 : 409,
                unauthorized ? "AgentUnauthorized" : "TargetRegistrationEvidenceReceiptMismatch");
            Assert.Empty(await db.TargetRegistrationEvidenceReceipts.ToListAsync()); Assert.Equal(before, await Snapshot(sql));
        }
    }

    [SqlServerFact]
    public async Task Observation_identity_cannot_cross_roles_or_claims_and_storage_is_immutable_in_EF_and_SQL()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql); var other = await Prepare(sql);
        await using (var db = sql.NewContext())
        {
            await Report(db, f);
            foreach (var collision in new[] {
                other.Report with { RacObservationId = f.Report.RacObservationId },
                other.Report with { RacObservationId = f.Report.SqlObservationId },
                other.Report with { SqlObservationId = f.Report.RacObservationId },
                other.Report with { SqlObservationId = f.Report.SqlObservationId } })
                await Reject(() => Report(db, other, collision));
            Assert.Equal(1, await db.TargetRegistrationEvidenceReceipts.CountAsync());
        }
        foreach (var delete in new[] { false, true })
        {
            await using var db = sql.NewContext(); var row = await db.TargetRegistrationEvidenceReceipts.SingleAsync();
            if (delete) db.Remove(row); else db.Entry(row).Property(x => x.SqlDatabaseId).CurrentValue = 999;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
        }
        foreach (var delete in new[] { false, true })
        {
            await using var db = sql.NewContext();
            await Assert.ThrowsAsync<SqlException>(async () => {
                if (delete) await db.Database.ExecuteSqlRawAsync("DELETE FROM [dbo].[TargetRegistrationEvidenceRecords]", Ct);
                else await db.Database.ExecuteSqlRawAsync("UPDATE [dbo].[TargetRegistrationEvidenceRecords] SET [SqlDatabaseId] = 999", Ct);
            });
        }
        await using var check = sql.NewContext();
        Assert.Equal(f.Report.SqlDatabaseId, (await check.TargetRegistrationEvidenceReceipts.SingleAsync()).SqlDatabaseId);
    }

    private sealed class CredentialCommands : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("TargetCredentials", StringComparison.Ordinal)) Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
    private sealed class LostAcknowledgement : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default) => throw new IOException("Simulated lost commit ACK.");
    }
    private static BuildSystemDbContext Intercepted(SqlServerTestDatabase sql, IInterceptor interceptor) => new(
        new DbContextOptionsBuilder<BuildSystemDbContext>().UseSqlServer(sql.ConnectionString,
            o => o.EnableRetryOnFailure(3, TimeSpan.Zero, new[] { 51001 })).AddInterceptors(interceptor).Options);

    [SqlServerFact]
    public async Task Concurrent_exact_and_conflicting_reports_and_lost_commit_ACK_keep_one_stable_acceptance()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var f = await Prepare(sql);
        await using (var faulty = Intercepted(sql, new LostAcknowledgement()))
            await Reject(() => Report(faulty, f), 503, "TargetRegistrationEvidenceReceiptUnavailable");
        await using var check = sql.NewContext(); var persisted = await check.TargetRegistrationEvidenceReceipts.AsNoTracking().SingleAsync();
        await using var left = sql.NewContext(); await using var right = sql.NewContext();
        var replays = await Task.WhenAll(Report(left, f), Report(right, f));
        Assert.All(replays, r => { Inert(r); Assert.Equal(persisted.AcceptedAt, r.AcceptedAt); });
        var other = await Prepare(sql);
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<TargetRegistrationEvidenceReceipt?> Run(BuildSystemDbContext db, ReportTargetRegistrationEvidence report)
        {
            await ready.Task;
            try { return await Report(db, other, report); }
            catch (RequestRejectedException e) { Assert.Equal(409, e.StatusCode); Assert.Equal("TargetRegistrationEvidenceReceiptMismatch", e.Code); return null; }
        }
        var a = Run(left, other.Report); var b = Run(right, other.Report with { SqlDatabaseId = other.Report.SqlDatabaseId + 1 });
        ready.SetResult(true); var results = await Task.WhenAll(a, b);
        Assert.Single(results, x => x is not null); Assert.Single(results, x => x is null);
        var identical = await Prepare(sql);
        var same = await Task.WhenAll(Report(left, identical), Report(right, identical));
        Assert.Equal(same[0], same[1]); Assert.All(same, Inert);
        Assert.Equal(3, await check.TargetRegistrationEvidenceReceipts.CountAsync());
        Assert.Equal(3, await check.AuditEvents.CountAsync(x => x.Action == "TargetRegistrationEvidenceRecorded"));
    }
}
