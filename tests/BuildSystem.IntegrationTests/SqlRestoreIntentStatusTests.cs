using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Contracts;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildSystem.IntegrationTests;

// Read-only projections against disposable metadata SQL; never restores or probes target systems.
public sealed class SqlRestoreIntentStatusTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly string Hash = new('A', 64);
    private sealed record Seed(Actor Admin, Actor Owner, Guid Project, OrderReceipt Order,
        Guid ArtifactId, RestoreIntentOptions Options);

    private static async Task<Seed> PrepareAsync(SqlServerTestDatabase sql)
    {
        await sql.ApplyMigrationsAsync();
        var admin = new Actor(Guid.NewGuid(), true); var owner = new Actor(Guid.NewGuid(), false);
        var project = Guid.NewGuid(); var pool = Guid.NewGuid(); var root = Guid.NewGuid();
        var archive = Guid.NewGuid(); var version = Guid.NewGuid(); var agent = Guid.NewGuid();
        var cluster = Guid.NewGuid(); var server = Guid.NewGuid();
        await using var db = sql.NewContext();
        db.Users.AddRange(new BuildSystemUser { Id = admin.UserId, UserName = "admin", NormalizedUserName = "ADMIN" },
            new BuildSystemUser { Id = owner.UserId, UserName = "owner", NormalizedUserName = "OWNER" });
        db.Projects.Add(new Project { Id = project, Name = "restore-status", IsolationPolicyId = "isolated", PreparationProfileVersion = "v1" });
        db.ProjectMemberships.Add(new ProjectMembership { UserId = owner.UserId, ProjectId = project });
        db.ArchiveRoots.Add(new ArchiveRoot { Id = root, Enabled = true, AgentScopeId = agent,
            IsolationPolicyId = "isolated", MaxArchiveBytes = 10000, MaxStagingBytes = 10000 });
        db.ArchiveRootProjects.Add(new ArchiveRootProject { RootId = root, ProjectId = project });
        db.DiskArchives.Add(new DiskArchive { Id = archive, RootId = root, RelativePath = "private-restore-status.dt" });
        db.DiskArchiveVersions.Add(new DiskArchiveVersion { Id = version, ArchiveId = archive,
            Sha256 = Hash, SizeBytes = 10, Format = "dt", State = "Ready", EstimatedDbBytes = 100 });
        db.EnvironmentPools.Add(new EnvironmentPool { Id = pool, Enabled = true, ClusterId = cluster,
            SqlServerId = server, AgentScopeId = agent, IsolationPolicyId = "isolated", CapacityBytes = 100000,
            MaxEnvironmentBytes = 10000, MaxStagingBytes = 10000, MaxConcurrentProvisioning = 5,
            SupportsDt = true, SupportsBak = true, IsolationVerified = true,
            ScheduledJobsSuppressionVerified = true, ResourceLimitVerified = true });
        db.ProjectPools.Add(new ProjectPool { ProjectId = project, PoolId = pool, Enabled = true });
        db.EnvironmentQuotas.Add(new EnvironmentQuota { SubjectId = owner.UserId, MaxActiveEnvironments = 5,
            MaxConcurrentProvisioning = 5, MaxStorageBytes = 100000, MaxTtlDays = 30 });
        await db.SaveChangesAsync();
        var control = new SqlAgentControl(db);
        var ticket = await control.CreateEnrollmentAsync(admin, new(agent, "restore-status-worker", pool), Ct);
        await control.RegisterAsync(new(agent, ticket.EnrollmentId, ticket.Token, "restore-status-worker"), Ct);
        var credential = await control.ApproveAsync(admin, agent, Ct);
        var order = await new SqlEnvironmentOrders(db).CreateAsync(owner, "restore-status-order",
            new(project, "restore-status", "archive", new(version), 7), Ct);
        var grant = Assert.IsType<StageLeaseGrant>(await new SqlArchiveStageControl(db,
            new ArchiveStageOptions { Enabled = true }).PollAsync(credential.Token, Ct));
        var artifact = Guid.NewGuid();
        await new SqlArtifactReceipts(db).ReportAsync(credential.Token,
            new(grant.JobId, grant.FencingToken, grant.Plan.ReservationId, version, artifact, Hash, 10, "dt"), Ct);
        await new SqlStageCompletion(db, new ArchiveStageOptions { Enabled = true })
            .CompleteAsync(credential.Token, new(grant.JobId, grant.FencingToken, artifact), Ct);
        return new(admin, owner, project, order, artifact, new RestoreIntentOptions { Enabled = true,
            CanonicalMappings = new() { [pool] = new(cluster, server) } });
    }
    private static async Task<RestoreIntentReceipt> RequestAsync(SqlServerTestDatabase sql, Seed seed)
    {
        await using var db = sql.NewContext();
        return await new SqlRestoreIntents(db, seed.Options).RequestAsync(seed.Owner, seed.Order.EnvironmentId,
            new(seed.Order.JobId, seed.ArtifactId), Ct);
    }
    private static async Task<StageStatusView?> StatusAsync(SqlServerTestDatabase sql, Seed seed, Actor? actor = null)
    {
        await using var db = sql.NewContext();
        return await new SqlStageStatus(db).GetAsync(actor ?? seed.Owner, seed.Order.EnvironmentId, Ct);
    }
    private static async Task<CleanupStatusView?> CleanupAsync(SqlServerTestDatabase sql, Seed seed, Actor? actor = null)
    {
        await using var db = sql.NewContext();
        return await new SqlCleanupStatus(db).GetAsync(actor ?? seed.Owner, seed.Order.EnvironmentId, Ct);
    }
    private static async Task<string> DurableStateAsync(SqlServerTestDatabase sql)
    {
        await using var db = sql.NewContext();
        await db.EnvironmentJobs.LoadAsync(); await db.AgentLeases.LoadAsync(); await db.StagedArtifacts.LoadAsync();
        await db.TestEnvironments.LoadAsync(); await db.EnvironmentReservations.LoadAsync();
        await db.EnvironmentQuotas.LoadAsync(); await db.EnvironmentRequests.LoadAsync();
        await db.RestoreIntents.LoadAsync(); await db.AuditEvents.LoadAsync();
        await db.StageRetirementIntents.LoadAsync(); await db.StageCleanupAuthorizations.LoadAsync();
        await db.StageCleanupResults.LoadAsync(); await db.StagingAllocationReleases.LoadAsync();
        return JsonSerializer.Serialize(db.ChangeTracker.Entries().Select(e => e.Metadata.Name + ":" +
            JsonSerializer.Serialize(e.Properties.OrderBy(p => p.Metadata.Name)
                .ToDictionary(p => p.Metadata.Name, p => p.CurrentValue))).OrderBy(s => s).ToArray());
    }

    [SqlServerFact]
    public async Task Completed_stage_has_no_restore_projection_until_intent_then_owner_and_admin_observe_safe_hold_without_side_effects()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var before = await DurableStateAsync(sql);
        var absent = Assert.IsType<StageStatusView>(await StatusAsync(sql, seed));
        Assert.Null(absent.RestoreIntent); Assert.Equal("StageCompleted", absent.ReconciliationCode);
        var noHold = Assert.IsType<CleanupStatusView>(await CleanupAsync(sql, seed)); Assert.False(noHold.RestoreIntentHold);
        Assert.Equal(before, await DurableStateAsync(sql));
        var receipt = await RequestAsync(sql, seed); before = await DurableStateAsync(sql);
        foreach (var actor in new[] { seed.Owner, seed.Admin })
        {
            var status = Assert.IsType<StageStatusView>(await StatusAsync(sql, seed, actor));
            Assert.Equal("StageCompleted", status.ReconciliationCode); Assert.Equal("StageCompleted", status.JobState);
            Assert.Equal("Completed", status.LeaseState); Assert.Equal("Requested", status.EnvironmentState);
            Assert.Equal("Pending", status.RequestState); Assert.Equal("Held", status.ReservationState); Assert.True(status.Unsettled);
            Assert.NotNull(status.RestoreIntent);
            Assert.Equal(receipt.RestoreIntentId, status.RestoreIntent.RestoreIntentId);
            Assert.Equal(receipt.TargetIncarnation, status.RestoreIntent.TargetIncarnation);
            Assert.Equal(receipt.RequestedAt, status.RestoreIntent.RequestedAt);
            Assert.False(status.RestoreIntent.Executable); Assert.True(status.RestoreIntent.ArtifactRetained);
            Assert.Equal("AwaitingTrustedTarget", status.RestoreIntent.RestoreIntentCode);
            var cleanup = Assert.IsType<CleanupStatusView>(await CleanupAsync(sql, seed, actor));
            Assert.True(cleanup.RestoreIntentHold); Assert.Equal(noHold.CleanupState, cleanup.CleanupState);
            Assert.Equal(noHold.HeldStagingBytes, cleanup.HeldStagingBytes);
            using var wire = JsonDocument.Parse(JsonSerializer.Serialize(status.RestoreIntent,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            Assert.Equal(new[] { "artifactRetained", "executable", "requestedAt", "restoreIntentCode", "restoreIntentId", "targetIncarnation" },
                wire.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
        }
        Assert.Equal(before, await DurableStateAsync(sql));
    }

    [SqlServerFact]
    public async Task Foreign_owner_and_revoked_membership_cannot_observe_restore_hold_but_admin_still_can()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var receipt = await RequestAsync(sql, seed); var before = await DurableStateAsync(sql);
        var foreign = new Actor(Guid.NewGuid(), false);
        Assert.Null(await StatusAsync(sql, seed, foreign)); Assert.Null(await CleanupAsync(sql, seed, foreign));
        Assert.Equal(before, await DurableStateAsync(sql));
        await using (var db = sql.NewContext())
            await db.ProjectMemberships.Where(m => m.ProjectId == seed.Project && m.UserId == seed.Owner.UserId).ExecuteDeleteAsync();
        before = await DurableStateAsync(sql);
        Assert.Null(await StatusAsync(sql, seed)); Assert.Null(await CleanupAsync(sql, seed));
        Assert.True(Assert.IsType<CleanupStatusView>(await CleanupAsync(sql, seed, seed.Admin)).RestoreIntentHold);
        var admin = Assert.IsType<StageStatusView>(await StatusAsync(sql, seed, seed.Admin));
        Assert.NotNull(admin.RestoreIntent); Assert.Equal(receipt.RestoreIntentId, admin.RestoreIntent.RestoreIntentId);
        Assert.Equal(before, await DurableStateAsync(sql));
    }

    [SqlServerFact]
    public async Task Corrupt_restore_tuple_is_a_conflict_not_a_hidden_or_reconstructed_hold_and_reads_do_not_repair_it()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var receipt = await RequestAsync(sql, seed);
        // Bypass EF's immutability guard solely in disposable metadata to exercise fail-closed reads.
        // No FK is changed: corruption of the pinned hash cannot be masked by an FK violation.
        await using (var db = sql.NewContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [RestoreIntents] SET [Sha256] = {new string('B', 64)} WHERE [RestoreIntentId] = {receipt.RestoreIntentId}", Ct);
        var before = await DurableStateAsync(sql);
        foreach (var actor in new[] { seed.Owner, seed.Admin })
        {
            var error = await Assert.ThrowsAsync<RequestRejectedException>(() => StatusAsync(sql, seed, actor));
            Assert.Equal(409, error.StatusCode);
            var cleanupError = await Assert.ThrowsAsync<RequestRejectedException>(() => CleanupAsync(sql, seed, actor));
            Assert.Equal(409, cleanupError.StatusCode);
        }
        Assert.Equal(before, await DurableStateAsync(sql));
        // Restore the original hash, then corrupt accounting rather than the restore row.
        // The stage snapshot pins stagingBytes exactly; > artifact size alone is insufficient.
        await using (var db = sql.NewContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [RestoreIntents] SET [Sha256] = {Hash} WHERE [RestoreIntentId] = {receipt.RestoreIntentId}", Ct);
            await db.Database.ExecuteSqlRawAsync("UPDATE [EnvironmentReservations] SET [StagingBytes] = [StagingBytes] + 1", Ct);
        }
        before = await DurableStateAsync(sql);
        foreach (var actor in new[] { seed.Owner, seed.Admin })
        {
            var error = await Assert.ThrowsAsync<RequestRejectedException>(() => StatusAsync(sql, seed, actor));
            Assert.Equal(409, error.StatusCode);
            var cleanupError = await Assert.ThrowsAsync<RequestRejectedException>(() => CleanupAsync(sql, seed, actor));
            Assert.Equal(409, cleanupError.StatusCode);
        }
        Assert.Equal(before, await DurableStateAsync(sql));
    }
}
// end
