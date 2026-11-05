using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Contracts;
using BuildSystem.Domain;
using BuildSystem.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildSystem.IntegrationTests;

// Opt-in disposable metadata SQL only. No target SQL, RAC, restore worker or external mutations.
public sealed class SqlTargetCreationOperationTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly string Hash = new('A', 64);
    private sealed record Seed(Actor Admin, Actor Owner, Guid Project, Guid Pool, OrderReceipt Order,
        AgentCredential Credential, StageLeaseGrant Grant, ReportStagedArtifact Report,
        RestoreIntentReceipt Restore, TargetCreationOperationOptions Options);

    // Local fixture: the restore-intent and observation preparation helpers are intentionally private.
    // All stage and restore pins originate from the standard control services, not fabricated ledger rows.
    private static async Task<Seed> PrepareAsync(SqlServerTestDatabase sql, bool stageOnly = false)
    {
        await sql.ApplyMigrationsAsync();
        var admin = new Actor(Guid.NewGuid(), true); var owner = new Actor(Guid.NewGuid(), false);
        var project = Guid.NewGuid(); var pool = Guid.NewGuid(); var root = Guid.NewGuid();
        var archive = Guid.NewGuid(); var version = Guid.NewGuid(); var agent = Guid.NewGuid();
        var cluster = Guid.NewGuid(); var server = Guid.NewGuid();
        await using var db = sql.NewContext();
        db.Users.AddRange(new BuildSystemUser { Id = admin.UserId, UserName = "admin-" + admin.UserId.ToString("N"),
                NormalizedUserName = "ADMIN-" + admin.UserId.ToString("N") },
            new BuildSystemUser { Id = owner.UserId, UserName = "owner-" + owner.UserId.ToString("N"),
                NormalizedUserName = "OWNER-" + owner.UserId.ToString("N") });
        var role = new IdentityRole<Guid> { Id = Guid.NewGuid(), Name = "Administrator", NormalizedName = "ADMINISTRATOR" };
        db.Roles.Add(role);
        db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = admin.UserId, RoleId = role.Id });
        db.Projects.Add(new Project { Id = project, Name = "creation-ledger", IsolationPolicyId = "isolated", PreparationProfileVersion = "v1" });
        db.ProjectMemberships.Add(new ProjectMembership { UserId = owner.UserId, ProjectId = project });
        db.ArchiveRoots.Add(new ArchiveRoot { Id = root, Enabled = true, AgentScopeId = agent,
            IsolationPolicyId = "isolated", MaxArchiveBytes = 10000, MaxStagingBytes = 10000 });
        db.ArchiveRootProjects.Add(new ArchiveRootProject { RootId = root, ProjectId = project });
        db.DiskArchives.Add(new DiskArchive { Id = archive, RootId = root, RelativePath = "creation-ledger.dt" });
        db.DiskArchiveVersions.Add(new DiskArchiveVersion { Id = version, ArchiveId = archive,
            Sha256 = Hash, SizeBytes = 10, Format = "dt", State = "Ready", EstimatedDbBytes = 100 });
        db.EnvironmentPools.Add(new EnvironmentPool { Id = pool, Enabled = true,
            ClusterId = cluster, SqlServerId = server, AgentScopeId = agent,
            IsolationPolicyId = "isolated", CapacityBytes = 100000, MaxEnvironmentBytes = 10000,
            MaxStagingBytes = 10000, MaxConcurrentProvisioning = 5, SupportsDt = true,
            IsolationVerified = !stageOnly, ScheduledJobsSuppressionVerified = !stageOnly, ResourceLimitVerified = !stageOnly });
        db.ProjectPools.Add(new ProjectPool { ProjectId = project, PoolId = pool, Enabled = true });
        db.EnvironmentQuotas.Add(new EnvironmentQuota { SubjectId = owner.UserId, MaxActiveEnvironments = 5,
            MaxConcurrentProvisioning = 5, MaxStorageBytes = 100000, MaxTtlDays = 30 });
        await db.SaveChangesAsync();
        var control = new SqlAgentControl(db);
        var ticket = await control.CreateEnrollmentAsync(admin, new(agent, "creation-worker", pool), Ct);
        await control.RegisterAsync(new(agent, ticket.EnrollmentId, ticket.Token, "creation-worker"), Ct);
        var credential = await control.ApproveAsync(admin, agent, Ct);
        var order = await new SqlEnvironmentOrders(db).CreateAsync(owner, "creation-order",
            new CreateEnvironmentRequest(project, "creation", "archive", new(version), 7)
                { Mode = stageOnly ? "archive-stage-only" : "archive" }, Ct);
        var grant = Assert.IsType<StageLeaseGrant>(await new SqlArchiveStageControl(db,
            new ArchiveStageOptions { Enabled = true }).PollAsync(credential.Token, Ct));
        var report = new ReportStagedArtifact(grant.JobId, grant.FencingToken, grant.Plan.ReservationId,
            version, Guid.NewGuid(), Hash, 10, "dt");
        await new SqlArtifactReceipts(db).ReportAsync(credential.Token, report, Ct);
        await new SqlStageCompletion(db, new ArchiveStageOptions { Enabled = true })
            .CompleteAsync(credential.Token, new(grant.JobId, grant.FencingToken, report.ArtifactId), Ct);
        var restore = await new SqlRestoreIntents(db, new RestoreIntentOptions { Enabled = true,
            CanonicalMappings = new() { [pool] = new(cluster, server) } })
            .RequestAsync(owner, order.EnvironmentId, new(order.JobId, report.ArtifactId), Ct);
        return new(admin, owner, project, pool, order, credential, grant, report, restore,
            new TargetCreationOperationOptions { Enabled = true,
                CanonicalMappings = new() { [pool] = new(cluster, server) } });
    }

    private static async Task<TargetCreationOperationReceipt> RegisterAsync(SqlServerTestDatabase sql, Seed seed,
        Actor? actor = null, TargetCreationOperationOptions? options = null, Guid? expected = null)
    {
        await using var db = sql.NewContext();
        return await new SqlTargetCreationOperations(db, options ?? seed.Options).RegisterAsync(actor ?? seed.Admin,
            seed.Order.EnvironmentId, expected ?? seed.Restore.RestoreIntentId, Ct);
    }
    private static async Task<TargetCreationOperationReceipt> BeginAsync(SqlServerTestDatabase sql, Seed seed,
        Guid operation, long revision, Guid attempt, Actor? actor = null)
    {
        await using var db = sql.NewContext();
        return await new SqlTargetCreationOperations(db, seed.Options)
            .BeginAttemptAsync(actor ?? seed.Admin, operation, revision, attempt, Ct);
    }
    private static async Task<TargetCreationOperationReceipt> QuarantineAsync(SqlServerTestDatabase sql, Seed seed,
        Guid operation, long revision, TargetCreationQuarantineReason reason = TargetCreationQuarantineReason.OutcomeUnknown,
        Actor? actor = null)
    {
        await using var db = sql.NewContext();
        return await new SqlTargetCreationOperations(db, seed.Options)
            .QuarantineAsync(actor ?? seed.Admin, operation, revision, reason, Ct);
    }
    private static async Task RejectedAsync(Func<Task<TargetCreationOperationReceipt>> action, int status = 409)
        => Assert.Equal(status, (await Assert.ThrowsAsync<RequestRejectedException>(action)).StatusCode);
    private static async Task<int> AuditsAsync(SqlServerTestDatabase sql)
    {
        await using var db = sql.NewContext(); return await db.AuditEvents.CountAsync();
    }

    // Include every scalar and rowversion: ledger writes must not alter leases, holds, stage or environment state.
    private static async Task<string> LifecycleAsync(SqlServerTestDatabase sql)
    {
        await using var db = sql.NewContext();
        await db.EnvironmentJobs.LoadAsync(); await db.AgentLeases.LoadAsync();
        await db.StagedArtifacts.LoadAsync(); await db.TestEnvironments.LoadAsync();
        await db.EnvironmentReservations.LoadAsync(); await db.EnvironmentQuotas.LoadAsync();
        await db.EnvironmentRequests.LoadAsync(); await db.StagingAllocationReleases.LoadAsync();
        await db.RestoreIntents.LoadAsync();
        return JsonSerializer.Serialize(db.ChangeTracker.Entries()
            .Select(e => e.Metadata.Name + ":" + JsonSerializer.Serialize(e.Properties.OrderBy(p => p.Metadata.Name)
                .ToDictionary(p => p.Metadata.Name, p => p.CurrentValue)))
            .OrderBy(s => s).ToArray());
    }
    private static string Pins(TargetCreationOperationReceipt receipt) => JsonSerializer.Serialize(new
    {
        receipt.OperationId, receipt.RestoreIntentId, receipt.EnvironmentId, receipt.StageJobId,
        receipt.ReservationId, receipt.ArtifactId, receipt.AgentId, receipt.TargetIncarnationId,
        receipt.CanonicalClusterId, receipt.CanonicalSqlServerId, receipt.InfobaseName, receipt.SqlDbName
    });
    private static void Nonexecuting(TargetCreationOperationReceipt receipt)
    {
        Assert.False(receipt.Executable); Assert.True(receipt.ScheduledJobsMustRemainBlocked);
    }

    [SqlServerFact]
    public async Task Registration_is_default_off_without_ledger_audit_or_lifecycle_changes()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var before = await LifecycleAsync(sql); var audits = await AuditsAsync(sql);
        await RejectedAsync(() => RegisterAsync(sql, seed, options: new()));
        await using var db = sql.NewContext();
        Assert.Empty(await db.TargetCreationOperations.ToListAsync());
        Assert.Equal(audits, await AuditsAsync(sql)); Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Observation_only_stage_cannot_be_promoted_to_creation_even_with_a_restore_intent()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql, stageOnly: true);
        var before = await LifecycleAsync(sql); var audits = await AuditsAsync(sql);
        await using (var db = sql.NewContext())
        {
            Assert.Equal("ObserveArchiveStage", (await db.EnvironmentJobs.SingleAsync()).Kind);
            Assert.Single(await db.RestoreIntents.ToListAsync());
        }
        await RejectedAsync(() => RegisterAsync(sql, seed));
        await using (var db = sql.NewContext()) Assert.Empty(await db.TargetCreationOperations.ToListAsync());
        Assert.Equal(audits, await AuditsAsync(sql)); Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Registration_replay_retains_one_pinned_nonexecuting_operation_and_one_audit()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var before = await LifecycleAsync(sql); var audits = await AuditsAsync(sql);
        var first = await RegisterAsync(sql, seed); var replay = await RegisterAsync(sql, seed);
        Assert.Equal(Pins(first), Pins(replay)); Assert.NotEqual(Guid.Empty, first.OperationId);
        Assert.NotEqual(first.RestoreIntentId, first.OperationId);
        Assert.Equal(seed.Restore.RestoreIntentId, first.RestoreIntentId);
        Assert.Equal(seed.Order.EnvironmentId, first.EnvironmentId); Assert.Equal(seed.Order.JobId, first.StageJobId);
        Assert.Equal(seed.Grant.Plan.ReservationId, first.ReservationId); Assert.Equal(seed.Report.ArtifactId, first.ArtifactId);
        Assert.Equal(seed.Credential.AgentId, first.AgentId); Assert.Equal(seed.Restore.TargetIncarnation, first.TargetIncarnationId);
        Assert.Equal(seed.Restore.CanonicalClusterId, first.CanonicalClusterId);
        Assert.Equal(seed.Restore.CanonicalSqlServerId, first.CanonicalSqlServerId);
        Assert.Equal(seed.Restore.InfobaseName, first.InfobaseName); Assert.Equal(seed.Restore.SqlDbName, first.SqlDbName);
        Assert.Equal(TargetCreationOperationState.Registered, first.State); Assert.Equal(1L, first.Revision);
        Assert.Equal(first.State, replay.State); Assert.Equal(first.Revision, replay.Revision);
        Assert.Null(first.AttemptId); Assert.Null(first.AttemptExpectedRevision); Assert.Null(first.QuarantineReason);
        Nonexecuting(first); Nonexecuting(replay);
        await using var db = sql.NewContext(); Assert.Single(await db.TargetCreationOperations.ToListAsync());
        Assert.Equal(audits + 1, await AuditsAsync(sql)); Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Begin_is_revision_CAS_and_exact_replay_after_quarantine_is_not_new_or_executable()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var before = await LifecycleAsync(sql); var registered = await RegisterAsync(sql, seed);
        var audits = await AuditsAsync(sql); var attempt = Guid.NewGuid();
        await RejectedAsync(() => BeginAsync(sql, seed, registered.OperationId, registered.Revision + 1, attempt));
        var begun = await BeginAsync(sql, seed, registered.OperationId, registered.Revision, attempt);
        Assert.True(begun.NewlyRecorded); Assert.Equal(TargetCreationOperationState.AttemptRecorded, begun.State);
        Assert.Equal(registered.Revision + 1, begun.Revision); Assert.Equal(attempt, begun.AttemptId);
        Assert.Equal((long?)registered.Revision, begun.AttemptExpectedRevision); Assert.Equal(Pins(registered), Pins(begun));
        var replay = await BeginAsync(sql, seed, registered.OperationId, registered.Revision, attempt);
        Assert.False(replay.NewlyRecorded); Assert.Equal(begun.Revision, replay.Revision); Nonexecuting(replay);
        await RejectedAsync(() => BeginAsync(sql, seed, registered.OperationId, registered.Revision, Guid.NewGuid()));
        await RejectedAsync(() => BeginAsync(sql, seed, registered.OperationId, begun.Revision, attempt));
        var quarantined = await QuarantineAsync(sql, seed, registered.OperationId, begun.Revision);
        Assert.Equal(TargetCreationOperationState.ReconciliationRequired, quarantined.State);
        Assert.Equal(begun.Revision + 1, quarantined.Revision);
        Assert.Equal(TargetCreationQuarantineReason.OutcomeUnknown, quarantined.QuarantineReason);
        replay = await BeginAsync(sql, seed, registered.OperationId, registered.Revision, attempt);
        Assert.False(replay.NewlyRecorded); Assert.Equal(quarantined.State, replay.State);
        Assert.Equal(quarantined.Revision, replay.Revision); Assert.Equal(quarantined.QuarantineReason, replay.QuarantineReason);
        Assert.Equal(attempt, replay.AttemptId); Assert.Equal(Pins(registered), Pins(replay)); Nonexecuting(replay);
        await RejectedAsync(() => BeginAsync(sql, seed, registered.OperationId, quarantined.Revision, Guid.NewGuid()));
        await RejectedAsync(() => QuarantineAsync(sql, seed, registered.OperationId, quarantined.Revision));
        Assert.Equal(audits + 2, await AuditsAsync(sql)); Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Two_contexts_competing_for_different_attempts_record_exactly_one_winner()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var registered = await RegisterAsync(sql, seed); var before = await LifecycleAsync(sql); var audits = await AuditsAsync(sql);
        await using var leftDb = sql.NewContext(); await using var rightDb = sql.NewContext();
        var left = new SqlTargetCreationOperations(leftDb, seed.Options);
        var right = new SqlTargetCreationOperations(rightDb, seed.Options);
        var leftAttempt = Guid.NewGuid(); var rightAttempt = Guid.NewGuid();
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<(TargetCreationOperationReceipt? Receipt, RequestRejectedException? Error)> CompeteAsync(
            SqlTargetCreationOperations service, Guid attempt)
        {
            await ready.Task;
            try { return (await service.BeginAttemptAsync(seed.Admin, registered.OperationId, registered.Revision, attempt, Ct), null); }
            catch (RequestRejectedException error) { return (null, error); }
        }
        var tasks = new[] { CompeteAsync(left, leftAttempt), CompeteAsync(right, rightAttempt) };
        ready.SetResult(true); var results = await Task.WhenAll(tasks);
        var winner = Assert.Single(results, r => r.Receipt is not null).Receipt!;
        Assert.True(winner.NewlyRecorded); Nonexecuting(winner);
        Assert.Equal(409, Assert.Single(results, r => r.Error is not null).Error!.StatusCode);
        Assert.Contains(winner.AttemptId!.Value, new[] { leftAttempt, rightAttempt });
        Assert.Equal(registered.Revision + 1, winner.Revision);
        var replay = await BeginAsync(sql, seed, registered.OperationId, registered.Revision, winner.AttemptId.Value);
        Assert.False(replay.NewlyRecorded); Assert.Equal(winner.Revision, replay.Revision);
        await using var db = sql.NewContext(); Assert.Single(await db.TargetCreationOperations.ToListAsync());
        Assert.Equal(audits + 1, await AuditsAsync(sql)); Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Owner_and_spoofed_admin_are_denied_and_current_database_role_is_rechecked()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var before = await LifecycleAsync(sql); var audits = await AuditsAsync(sql);
        await RejectedAsync(() => RegisterAsync(sql, seed, seed.Owner), 403);
        await RejectedAsync(() => RegisterAsync(sql, seed, new Actor(seed.Owner.UserId, true)), 403);
        var registered = await RegisterAsync(sql, seed); var attempt = Guid.NewGuid();
        await RejectedAsync(() => BeginAsync(sql, seed, registered.OperationId, registered.Revision, attempt, seed.Owner), 403);
        await RejectedAsync(() => QuarantineAsync(sql, seed, registered.OperationId, registered.Revision, actor: seed.Owner), 403);
        await using (var db = sql.NewContext())
            await db.UserRoles.Where(r => r.UserId == seed.Admin.UserId).ExecuteDeleteAsync();
        await RejectedAsync(() => RegisterAsync(sql, seed), 403);
        await RejectedAsync(() => BeginAsync(sql, seed, registered.OperationId, registered.Revision, attempt), 403);
        await RejectedAsync(() => QuarantineAsync(sql, seed, registered.OperationId, registered.Revision), 403);
        Assert.Equal(audits + 1, await AuditsAsync(sql)); Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Current_agent_binding_is_rechecked_before_begin_and_before_exact_attempt_replay()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var registered = await RegisterAsync(sql, seed); var attempt = Guid.NewGuid();
        await using (var db = sql.NewContext())
            await new SqlAgentControl(db).RevokeAsync(seed.Admin, seed.Credential.AgentId, Ct);
        var audits = await AuditsAsync(sql); var before = await LifecycleAsync(sql);
        await RejectedAsync(() => BeginAsync(sql, seed, registered.OperationId, registered.Revision, attempt));
        Assert.Equal(audits, await AuditsAsync(sql)); Assert.Equal(before, await LifecycleAsync(sql));

        // A second independently staged environment exercises denial of an otherwise exact recorded-attempt replay.
        await using var replaySql = await SqlServerTestDatabase.CreateAsync(); var replaySeed = await PrepareAsync(replaySql);
        var operation = await RegisterAsync(replaySql, replaySeed); var replayAttempt = Guid.NewGuid();
        await BeginAsync(replaySql, replaySeed, operation.OperationId, operation.Revision, replayAttempt);
        await using (var db = replaySql.NewContext())
            await new SqlAgentControl(db).RevokeAsync(replaySeed.Admin, replaySeed.Credential.AgentId, Ct);
        audits = await AuditsAsync(replaySql); before = await LifecycleAsync(replaySql);
        await RejectedAsync(() => BeginAsync(replaySql, replaySeed, operation.OperationId, operation.Revision, replayAttempt));
        Assert.Equal(audits, await AuditsAsync(replaySql)); Assert.Equal(before, await LifecycleAsync(replaySql));
    }

    [SqlServerFact]
    public async Task Quarantine_is_allowed_after_project_pool_disable_without_rebinding_or_releasing_holds()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var registered = await RegisterAsync(sql, seed); var attempt = Guid.NewGuid();
        var begun = await BeginAsync(sql, seed, registered.OperationId, registered.Revision, attempt);
        await using (var db = sql.NewContext())
            await db.ProjectPools.Where(p => p.ProjectId == seed.Project && p.PoolId == seed.Pool)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, false));
        var before = await LifecycleAsync(sql); var audits = await AuditsAsync(sql);
        await RejectedAsync(() => BeginAsync(sql, seed, registered.OperationId, registered.Revision, attempt));
        var quarantined = await QuarantineAsync(sql, seed, registered.OperationId, begun.Revision,
            TargetCreationQuarantineReason.Cancelled);
        Assert.Equal(TargetCreationOperationState.ReconciliationRequired, quarantined.State);
        Assert.Equal(TargetCreationQuarantineReason.Cancelled, quarantined.QuarantineReason);
        Assert.Equal(begun.Revision + 1, quarantined.Revision); Assert.Equal(Pins(registered), Pins(quarantined));
        Assert.Equal(begun.AttemptId, quarantined.AttemptId); Assert.Equal(begun.AttemptExpectedRevision, quarantined.AttemptExpectedRevision);
        Nonexecuting(quarantined);
        Assert.Equal(audits + 1, await AuditsAsync(sql)); Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Sql_constraint_rejects_quarantine_without_a_reason_instead_of_accepting_SQL_unknown()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var registered = await RegisterAsync(sql, seed); var before = await LifecycleAsync(sql); var audits = await AuditsAsync(sql);
        await using (var db = sql.NewContext())
        {
            var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [TargetCreationOperations] SET [State] = N'ReconciliationRequired', [Revision] = 2, [QuarantineReason] = NULL WHERE [OperationId] = {registered.OperationId}", Ct));
            Assert.Equal(547, error.Number);
        }
        var replay = await RegisterAsync(sql, seed);
        Assert.Equal(TargetCreationOperationState.Registered, replay.State); Assert.Equal(registered.Revision, replay.Revision);
        Assert.Null(replay.QuarantineReason); Assert.Equal(Pins(registered), Pins(replay));
        Assert.Equal(audits, await AuditsAsync(sql)); Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Revoked_owner_membership_blocks_registration_and_exact_replay_but_not_quarantine()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var registered = await RegisterAsync(sql, seed); var attempt = Guid.NewGuid();
        var begun = await BeginAsync(sql, seed, registered.OperationId, registered.Revision, attempt);
        await using (var db = sql.NewContext())
            await db.ProjectMemberships.Where(x => x.UserId == seed.Owner.UserId && x.ProjectId == seed.Project).ExecuteDeleteAsync();
        var audits = await AuditsAsync(sql);
        await RejectedAsync(() => RegisterAsync(sql, seed));
        await RejectedAsync(() => BeginAsync(sql, seed, registered.OperationId, registered.Revision, attempt));
        Assert.Equal(audits, await AuditsAsync(sql));
        var terminal = await QuarantineAsync(sql, seed, begun.OperationId, begun.Revision);
        Assert.Equal(TargetCreationOperationState.ReconciliationRequired, terminal.State);
        Assert.Equal(Pins(registered), Pins(terminal)); Nonexecuting(terminal);
    }

    [SqlServerFact]
    public async Task Quarantine_rejects_malformed_retained_names_before_mutation_or_audit()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var registered = await RegisterAsync(sql, seed);
        await using (var db = sql.NewContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [RestoreIntents] SET [SqlDbName] = N'invalid_retained_name' WHERE [RestoreIntentId] = {seed.Restore.RestoreIntentId}", Ct);
        var audits = await AuditsAsync(sql); var before = await LifecycleAsync(sql);
        await RejectedAsync(() => QuarantineAsync(sql, seed, registered.OperationId, registered.Revision));
        await using var verify = sql.NewContext(); var row = await verify.TargetCreationOperations.SingleAsync();
        Assert.Equal(TargetCreationOperationState.Registered, row.State); Assert.Equal(1L, row.Revision);
        Assert.Equal(audits, await AuditsAsync(sql)); Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Concurrent_registration_in_two_contexts_records_one_operation_and_one_audit()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var before = await LifecycleAsync(sql); var audits = await AuditsAsync(sql);
        await using var leftDb = sql.NewContext(); await using var rightDb = sql.NewContext();
        var left = new SqlTargetCreationOperations(leftDb, seed.Options);
        var right = new SqlTargetCreationOperations(rightDb, seed.Options);
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<TargetCreationOperationReceipt> CompeteAsync(SqlTargetCreationOperations service)
        {
            await ready.Task;
            return await service.RegisterAsync(seed.Admin, seed.Order.EnvironmentId, seed.Restore.RestoreIntentId, Ct);
        }
        var tasks = new[] { CompeteAsync(left), CompeteAsync(right) };
        ready.SetResult(true); var results = await Task.WhenAll(tasks);
        var recorded = Assert.Single(results, r => r.NewlyRecorded);
        var replay = Assert.Single(results, r => !r.NewlyRecorded);
        Assert.NotEqual(Guid.Empty, recorded.OperationId); Assert.Equal(Pins(recorded), Pins(replay));
        Assert.Equal(seed.Restore.RestoreIntentId, recorded.RestoreIntentId);
        foreach (var receipt in results)
        {
            Assert.Equal(TargetCreationOperationState.Registered, receipt.State); Assert.Equal(1L, receipt.Revision);
            Assert.Null(receipt.AttemptId); Assert.Null(receipt.AttemptExpectedRevision); Assert.Null(receipt.QuarantineReason);
            Nonexecuting(receipt);
        }
        await using var verify = sql.NewContext(); var row = Assert.Single(await verify.TargetCreationOperations.ToListAsync());
        Assert.Equal(recorded.OperationId, row.OperationId); Assert.Equal(seed.Restore.RestoreIntentId, row.RestoreIntentId);
        Assert.Equal(audits + 1, await AuditsAsync(sql)); Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Quarantine_before_begin_rejects_every_begin_including_a_repeated_unrecorded_attempt()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var registered = await RegisterAsync(sql, seed); var before = await LifecycleAsync(sql);
        var terminal = await QuarantineAsync(sql, seed, registered.OperationId, registered.Revision);
        Assert.Equal(TargetCreationOperationState.ReconciliationRequired, terminal.State);
        Assert.Equal(registered.Revision + 1, terminal.Revision);
        Assert.Null(terminal.AttemptId); Assert.Null(terminal.AttemptExpectedRevision); Nonexecuting(terminal);
        var audits = await AuditsAsync(sql); var attempt = Guid.NewGuid();
        await RejectedAsync(() => BeginAsync(sql, seed, terminal.OperationId, registered.Revision, attempt));
        await RejectedAsync(() => BeginAsync(sql, seed, terminal.OperationId, registered.Revision, attempt));
        await RejectedAsync(() => BeginAsync(sql, seed, terminal.OperationId, terminal.Revision, attempt));
        await RejectedAsync(() => BeginAsync(sql, seed, terminal.OperationId, terminal.Revision, Guid.NewGuid()));
        await using var verify = sql.NewContext(); var row = await verify.TargetCreationOperations.SingleAsync();
        Assert.Equal(terminal.State, row.State); Assert.Equal(terminal.Revision, row.Revision);
        Assert.Equal(terminal.QuarantineReason, row.QuarantineReason); Assert.Equal(terminal.RestoreIntentId, row.RestoreIntentId);
        Assert.Null(row.AttemptId); Assert.Null(row.AttemptExpectedRevision);
        Assert.False(row.Executable); Assert.True(row.ScheduledJobsMustRemainBlocked);
        Assert.Equal(audits, await AuditsAsync(sql)); Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task SaveChanges_rejects_attempt_reset_replacement_and_terminal_reopening()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var registered = await RegisterAsync(sql, seed); var attempt = Guid.NewGuid();
        var begun = await BeginAsync(sql, seed, registered.OperationId, registered.Revision, attempt);
        var before = await LifecycleAsync(sql); var audits = await AuditsAsync(sql);
        await using (var db = sql.NewContext())
        {
            var row = await db.TargetCreationOperations.SingleAsync();
            row.State = TargetCreationOperationState.Registered; row.Revision = 1;
            row.AttemptId = null; row.AttemptExpectedRevision = null;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Equal("Creation ledger transition is not permitted.", error.Message);
        }
        await using (var db = sql.NewContext())
        {
            var row = await db.TargetCreationOperations.SingleAsync();
            // Otherwise valid quarantine must not replace the already recorded attempt identity.
            row.State = TargetCreationOperationState.ReconciliationRequired; row.Revision = begun.Revision + 1;
            row.QuarantineReason = TargetCreationQuarantineReason.OutcomeUnknown; row.AttemptId = Guid.NewGuid();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Equal("Creation ledger transition is not permitted.", error.Message);
        }
        var replay = await BeginAsync(sql, seed, registered.OperationId, registered.Revision, attempt);
        Assert.False(replay.NewlyRecorded); Assert.Equal(begun.Revision, replay.Revision); Assert.Equal(attempt, replay.AttemptId);
        Assert.Equal(audits, await AuditsAsync(sql));
        var terminal = await QuarantineAsync(sql, seed, begun.OperationId, begun.Revision);
        audits = await AuditsAsync(sql);
        await using (var db = sql.NewContext())
        {
            var row = await db.TargetCreationOperations.SingleAsync();
            row.State = TargetCreationOperationState.AttemptRecorded; row.Revision = begun.Revision; row.QuarantineReason = null;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Equal("Creation ledger transition is not permitted.", error.Message);
        }
        await using var verify = sql.NewContext(); var retained = await verify.TargetCreationOperations.SingleAsync();
        Assert.Equal(terminal.State, retained.State); Assert.Equal(terminal.Revision, retained.Revision);
        Assert.Equal(attempt, retained.AttemptId); Assert.Equal(terminal.AttemptExpectedRevision, retained.AttemptExpectedRevision);
        Assert.Equal(terminal.QuarantineReason, retained.QuarantineReason);
        Assert.Equal(audits, await AuditsAsync(sql)); Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task SaveChanges_rejects_each_hard_safety_flag_change_even_during_a_valid_transition()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var registered = await RegisterAsync(sql, seed); var before = await LifecycleAsync(sql); var audits = await AuditsAsync(sql);
        foreach (var executable in new[] { true, false })
        {
            await using var db = sql.NewContext(); var row = await db.TargetCreationOperations.SingleAsync();
            row.State = TargetCreationOperationState.AttemptRecorded; row.Revision = registered.Revision + 1;
            row.AttemptId = Guid.NewGuid(); row.AttemptExpectedRevision = registered.Revision;
            if (executable) row.Executable = true;
            else row.ScheduledJobsMustRemainBlocked = false;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Equal("Creation operation anchors and safety flags are immutable.", error.Message);
        }
        var replay = await RegisterAsync(sql, seed);
        Assert.Equal(registered.State, replay.State); Assert.Equal(registered.Revision, replay.Revision);
        Assert.Null(replay.AttemptId); Assert.Null(replay.AttemptExpectedRevision); Nonexecuting(replay);
        Assert.Equal(audits, await AuditsAsync(sql)); Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task Sql_constraints_reject_hard_flag_changes_and_malformed_attempt_shapes()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var registered = await RegisterAsync(sql, seed); var before = await LifecycleAsync(sql); var audits = await AuditsAsync(sql);
        var attempt = Guid.NewGuid();
        var mutations = new (FormattableString Sql, string Constraint)[]
        {
            ($"UPDATE [TargetCreationOperations] SET [Executable] = 1 WHERE [OperationId] = {registered.OperationId}", "CK_TargetCreationOperations_NonExecutable"),
            ($"UPDATE [TargetCreationOperations] SET [ScheduledJobsMustRemainBlocked] = 0 WHERE [OperationId] = {registered.OperationId}", "CK_TargetCreationOperations_JobsBlocked"),
            ($"UPDATE [TargetCreationOperations] SET [State] = N'AttemptRecorded', [Revision] = 2, [AttemptId] = {Guid.Empty}, [AttemptExpectedRevision] = 1 WHERE [OperationId] = {registered.OperationId}", "CK_TargetCreationOperations_Ids"),
            ($"UPDATE [TargetCreationOperations] SET [AttemptId] = {attempt}, [AttemptExpectedRevision] = 1 WHERE [OperationId] = {registered.OperationId}", "CK_TargetCreationOperations_RevisionState"),
            ($"UPDATE [TargetCreationOperations] SET [State] = N'AttemptRecorded', [Revision] = 2, [AttemptId] = {attempt}, [AttemptExpectedRevision] = NULL WHERE [OperationId] = {registered.OperationId}", "CK_TargetCreationOperations_RevisionState")
        };
        foreach (var mutation in mutations)
        {
            await using var db = sql.NewContext();
            var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(mutation.Sql, Ct));
            Assert.Equal(547, error.Number); Assert.Contains(mutation.Constraint, error.Message);
        }
        var replay = await RegisterAsync(sql, seed);
        Assert.Equal(Pins(registered), Pins(replay)); Assert.Equal(registered.State, replay.State);
        Assert.Equal(registered.Revision, replay.Revision); Assert.Null(replay.AttemptId);
        Assert.Null(replay.AttemptExpectedRevision); Assert.Null(replay.QuarantineReason); Nonexecuting(replay);
        Assert.Equal(audits, await AuditsAsync(sql)); Assert.Equal(before, await LifecycleAsync(sql));
    }

    [SqlServerFact]
    public async Task SaveChanges_rejects_deletion_and_anchor_rewrites_of_the_persisted_operation()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var registered = await RegisterAsync(sql, seed); var before = await LifecycleAsync(sql); var audits = await AuditsAsync(sql);
        // Other pins live in the existing immutable restore intent; this row anchors them by FK.
        await using (var db = sql.NewContext())
        {
            var row = await db.TargetCreationOperations.SingleAsync();
            db.Entry(row).Property("RestoreIntentId").CurrentValue = Guid.NewGuid();
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await using (var db = sql.NewContext())
        {
            db.TargetCreationOperations.Remove(await db.TargetCreationOperations.SingleAsync());
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        var replay = await RegisterAsync(sql, seed); Assert.Equal(Pins(registered), Pins(replay));
        Assert.Equal(registered.Revision, replay.Revision); Assert.Equal(registered.State, replay.State);
        Assert.Equal(audits, await AuditsAsync(sql)); Assert.Equal(before, await LifecycleAsync(sql));
    }
}
