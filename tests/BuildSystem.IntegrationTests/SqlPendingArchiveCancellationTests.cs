using BuildSystem.Application;
using BuildSystem.Contracts;
using BuildSystem.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildSystem.IntegrationTests;

public sealed class SqlPendingArchiveCancellationTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string OrderKey = "cancel-order";
    private const long ArchiveBytes = 100;
    private const long ReservedBytes = 1_000;

    private sealed record Seed(Actor Owner, Actor Administrator, Actor OtherMember, Guid Project, Guid Root,
        Guid Pool, Guid Version, Guid Agent, OrderReceipt Order, Guid Reservation, string Snapshot, byte[] RowVersion)
    {
        public CreateEnvironmentRequest Request() => new(Project, "cancel", "archive", new ArchiveSource(Version), 7);
        public PendingArchiveCancellationRequest Cancellation() => new(Order.JobId);
    }

    private static async Task<Seed> PrepareAsync(SqlServerTestDatabase sql)
    {
        await sql.ApplyMigrationsAsync();
        var owner = new Actor(Guid.NewGuid(), false);
        var admin = new Actor(Guid.NewGuid(), true);
        var other = new Actor(Guid.NewGuid(), false);
        var project = Guid.NewGuid(); var root = Guid.NewGuid(); var pool = Guid.NewGuid();
        var archive = Guid.NewGuid(); var version = Guid.NewGuid(); var agent = Guid.NewGuid();
        await using var db = sql.NewContext();
        db.Users.AddRange(new BuildSystemUser { Id = owner.UserId, UserName = "owner", NormalizedUserName = "OWNER" },
            new BuildSystemUser { Id = admin.UserId, UserName = "admin", NormalizedUserName = "ADMIN" },
            new BuildSystemUser { Id = other.UserId, UserName = "other", NormalizedUserName = "OTHER" });
        db.Projects.Add(new Project { Id = project, Name = "project", IsolationPolicyId = "isolated", PreparationProfileVersion = "v1" });
        db.ProjectMemberships.AddRange(new ProjectMembership { UserId = owner.UserId, ProjectId = project },
            new ProjectMembership { UserId = other.UserId, ProjectId = project });
        db.ArchiveRoots.Add(new ArchiveRoot { Id = root, Enabled = true, AgentScopeId = agent,
            IsolationPolicyId = "isolated", MaxArchiveBytes = ArchiveBytes, MaxStagingBytes = ArchiveBytes });
        db.ArchiveRootProjects.Add(new ArchiveRootProject { RootId = root, ProjectId = project });
        db.DiskArchives.Add(new DiskArchive { Id = archive, RootId = root, RelativePath = "sample.dt" });
        db.DiskArchiveVersions.Add(new DiskArchiveVersion { Id = version, ArchiveId = archive,
            Sha256 = new string('A', 64), SizeBytes = ArchiveBytes, Format = "dt", State = "Ready", EstimatedDbBytes = ReservedBytes });
        db.EnvironmentPools.Add(new EnvironmentPool { Id = pool, Enabled = true, ClusterId = Guid.NewGuid(),
            SqlServerId = Guid.NewGuid(), AgentScopeId = agent, IsolationPolicyId = "isolated", CapacityBytes = ReservedBytes,
            MaxEnvironmentBytes = ReservedBytes, MaxStagingBytes = ArchiveBytes, MaxConcurrentProvisioning = 1,
            SupportsDt = true, SupportsBak = true, IsolationVerified = true, ScheduledJobsSuppressionVerified = true, ResourceLimitVerified = true });
        db.ProjectPools.Add(new ProjectPool { ProjectId = project, PoolId = pool, Enabled = true });
        db.EnvironmentQuotas.Add(new EnvironmentQuota { SubjectId = owner.UserId, MaxActiveEnvironments = 1,
            MaxConcurrentProvisioning = 1, MaxStorageBytes = ReservedBytes, MaxTtlDays = 30 });
        await db.SaveChangesAsync(Ct);
        var order = await new SqlEnvironmentOrders(db).CreateAsync(owner, OrderKey,
            new CreateEnvironmentRequest(project, "cancel", "archive", new ArchiveSource(version), 7), Ct);
        var environment = await db.TestEnvironments.SingleAsync(Ct);
        var job = await db.EnvironmentJobs.SingleAsync(Ct);
        Assert.Equal("ProvisionArchive", job.Kind);
        return new(owner, admin, other, project, root, pool, version, agent, order, environment.ReservationId,
            job.SnapshotJson, environment.RowVersion);
    }

    private static async Task<AgentCredential> EnrollAsync(SqlServerTestDatabase sql, Seed seed)
    {
        await using var db = sql.NewContext();
        var control = new SqlAgentControl(db);
        const string name = "cancel-race-worker";
        var ticket = await control.CreateEnrollmentAsync(seed.Administrator, new CreateEnrollment(seed.Agent, name, seed.Pool), Ct);
        await control.RegisterAsync(new RegisterAgent(seed.Agent, ticket.EnrollmentId, ticket.Token, name), Ct);
        return await control.ApproveAsync(seed.Administrator, seed.Agent, Ct);
    }

    private static async Task<StageLeaseGrant?> PollAsync(SqlServerTestDatabase sql, AgentCredential credential)
    {
        await using var db = sql.NewContext();
        return await new SqlArchiveStageControl(db, new ArchiveStageOptions { Enabled = true }).PollAsync(credential.Token, Ct);
    }

    [SqlServerFact]
    public async Task Default_disabled_leaves_pending_order_and_offline_reservation_untouched()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        await using var db = sql.NewContext();
        IPendingArchiveCancellations cancellation = new SqlPendingArchiveCancellations(db);
        var rejected = await Assert.ThrowsAsync<RequestRejectedException>(() =>
            cancellation.CancelAsync(seed.Owner, seed.Order.EnvironmentId, seed.Cancellation(), Ct));
        Assert.Equal((409, "PendingArchiveCancellationDisabled"), (rejected.StatusCode, rejected.Code));
        await AssertUncancelledAsync(db, seed, "Pending");
        Assert.Equal(0, await db.Agents.CountAsync(Ct));
        Assert.Equal(0, await db.AgentLeases.CountAsync(Ct));
        Assert.Equal(1, await db.AuditEvents.CountAsync(Ct));
    }

    [SqlServerFact]
    public async Task Offline_cancel_replays_immutable_receipt_and_reuses_quota_for_a_new_order()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        await using var db = sql.NewContext();
        var orders = new SqlEnvironmentOrders(db);
        var blocked = await Assert.ThrowsAsync<RequestRejectedException>(() => orders.CreateAsync(seed.Owner, "replacement", seed.Request(), Ct));
        Assert.Equal("QuotaExceeded", blocked.Code);
        IPendingArchiveCancellations cancellation = new SqlPendingArchiveCancellations(db, enabled: true);
        var first = await cancellation.CancelAsync(seed.Owner, seed.Order.EnvironmentId, seed.Cancellation(), Ct);
        byte[] terminalVersion;
        DateTimeOffset cancelledAt;
        await using (var check = sql.NewContext())
        {
            var receipt = await AssertCancelledAsync(check, seed, seed.Owner);
            terminalVersion = (await check.TestEnvironments.SingleAsync(Ct)).RowVersion;
            Assert.False(seed.RowVersion.SequenceEqual(terminalVersion));
            Assert.Equal(new PendingArchiveCancellationReceipt(receipt.EnvironmentId, receipt.RequestId, receipt.JobId,
                receipt.ReservationId, receipt.RootId, receipt.PoolId, receipt.ActorId, receipt.CancelledAt), first);
            cancelledAt = receipt.CancelledAt;
            Assert.Equal(0, await check.Agents.CountAsync(Ct));
        }
        // Bypass EF's immutable-entity guard: the SQL table itself must reject both writes.
        var update = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [PendingArchiveCancellations] SET [ActorId] = {seed.Administrator.UserId} WHERE [EnvironmentId] = {seed.Order.EnvironmentId}", Ct));
        Assert.Equal(51002, update.Number);
        var delete = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM [PendingArchiveCancellations] WHERE [EnvironmentId] = {seed.Order.EnvironmentId}", Ct));
        Assert.Equal(51002, delete.Number);

        await using var replayDb = sql.NewContext();
        var replay = await new SqlPendingArchiveCancellations(replayDb, enabled: true)
            .CancelAsync(seed.Owner, seed.Order.EnvironmentId, seed.Cancellation(), Ct);
        Assert.Equal(first, replay);
        Assert.Equal(cancelledAt, (await AssertCancelledAsync(replayDb, seed, seed.Owner)).CancelledAt);
        Assert.Equal(terminalVersion, (await replayDb.TestEnvironments.SingleAsync(Ct)).RowVersion);

        var replayOrders = new SqlEnvironmentOrders(replayDb);
        Assert.Equal(seed.Order, await replayOrders.CreateAsync(seed.Owner, OrderKey, seed.Request(), Ct));
        var replacement = await replayOrders.CreateAsync(seed.Owner, "replacement", seed.Request(), Ct);
        Assert.NotEqual(seed.Order.EnvironmentId, replacement.EnvironmentId);
        Assert.NotEqual(seed.Order.JobId, replacement.JobId);
        var environment = await replayDb.TestEnvironments.SingleAsync(x => x.Id == replacement.EnvironmentId, Ct);
        Assert.NotEqual(seed.Reservation, environment.ReservationId);
        Assert.Equal("Held", (await replayDb.EnvironmentReservations.SingleAsync(x => x.Id == environment.ReservationId, Ct)).State);
        Assert.Equal(1, await replayDb.PendingArchiveCancellations.CountAsync(Ct));
    }

    [SqlServerFact]
    public async Task First_stage_lease_and_cancel_can_never_both_win()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        var credential = await EnrollAsync(sql, seed);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<RequestRejectedException?> CancelAsync()
        {
            await start.Task;
            await using var db = sql.NewContext();
            try
            {
                await new SqlPendingArchiveCancellations(db, enabled: true)
                    .CancelAsync(seed.Owner, seed.Order.EnvironmentId, seed.Cancellation(), Ct);
                return null;
            }
            catch (RequestRejectedException rejection) { return rejection; }
        }
        async Task<StageLeaseGrant?> RacePollAsync()
        {
            await start.Task;
            return await PollAsync(sql, credential);
        }
        var cancel = CancelAsync();
        var poll = RacePollAsync();
        start.SetResult(true);
        await Task.WhenAll(cancel, poll);
        var grant = await poll;
        var rejected = await cancel;
        await using var check = sql.NewContext();
        if (rejected is null)
        {
            Assert.Null(grant);
            await AssertCancelledAsync(check, seed, seed.Owner);
            Assert.Null(await PollAsync(sql, credential));
        }
        else
        {
            AssertMismatch(rejected);
            var issued = Assert.IsType<StageLeaseGrant>(grant);
            Assert.Equal(seed.Order.JobId, issued.JobId);
            var lease = await check.AgentLeases.SingleAsync(Ct);
            Assert.Equal(seed.Order.JobId, lease.JobId);
            Assert.Equal(seed.Agent, lease.AgentId);
            Assert.Equal(issued.FencingToken, lease.FencingToken);
            Assert.Equal("Active", lease.State);
            await AssertUncancelledAsync(check, seed, "Leased");
        }
    }

    [SqlServerFact]
    public async Task Completed_lease_history_blocks_cancel_even_if_the_order_looks_pending_again()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        var credential = await EnrollAsync(sql, seed);
        var grant = Assert.IsType<StageLeaseGrant>(await PollAsync(sql, credential));
        Assert.Equal(seed.Order.JobId, grant.JobId);
        await using (var db = sql.NewContext())
        {
            // Simulate stale pending metadata, isolating the terminal lease row from the
            // separate issuance-audit guard. The real lease must still veto cancellation.
            (await db.EnvironmentJobs.SingleAsync(Ct)).State = "Pending";
            (await db.AgentLeases.SingleAsync(Ct)).State = "Completed";
            db.AuditEvents.Remove(await db.AuditEvents.SingleAsync(x => x.Action == "ArchiveStageLeased", Ct));
            await db.SaveChangesAsync(Ct);
        }
        await using var check = sql.NewContext();
        var rejected = await Assert.ThrowsAsync<RequestRejectedException>(() => new SqlPendingArchiveCancellations(check, enabled: true)
            .CancelAsync(seed.Owner, seed.Order.EnvironmentId, seed.Cancellation(), Ct));
        AssertMismatch(rejected);
        await AssertUncancelledAsync(check, seed, "Pending");
        var history = await check.AgentLeases.SingleAsync(Ct);
        Assert.Equal(seed.Order.JobId, history.JobId);
        Assert.Equal(grant.FencingToken, history.FencingToken);
        Assert.Equal(grant.ExpiresAt, history.ExpiresAt);
        Assert.Equal("Completed", history.State);
    }

    [SqlServerFact]
    public async Task Cancellation_checks_current_visibility_and_expected_job_before_writing_or_replaying()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        await using var db = sql.NewContext();
        IPendingArchiveCancellations cancellation = new SqlPendingArchiveCancellations(db, enabled: true);
        async Task<RequestRejectedException> RejectAsync(Actor actor, Guid environment, PendingArchiveCancellationRequest input) =>
            await Assert.ThrowsAsync<RequestRejectedException>(() => cancellation.CancelAsync(actor, environment, input, Ct));

        var wrongJob = new PendingArchiveCancellationRequest(Guid.NewGuid());
        var missing = await RejectAsync(seed.Owner, Guid.NewGuid(), seed.Cancellation());
        var foreign = await RejectAsync(seed.OtherMember, seed.Order.EnvironmentId, seed.Cancellation());
        var unverifiedAdmin = await RejectAsync(seed.Administrator, seed.Order.EnvironmentId, seed.Cancellation());
        Assert.Equal((404, "PendingArchiveCancellationNotFound"), (missing.StatusCode, missing.Code));
        Assert.Equal((missing.StatusCode, missing.Code), (foreign.StatusCode, foreign.Code));
        Assert.Equal((missing.StatusCode, missing.Code), (unverifiedAdmin.StatusCode, unverifiedAdmin.Code));
        AssertMismatch(await RejectAsync(seed.Owner, seed.Order.EnvironmentId, wrongJob));
        await AssertUncancelledAsync(db, seed, "Pending");
        Assert.Equal(1, await db.AuditEvents.CountAsync(Ct));

        db.ProjectMemberships.Remove(await db.ProjectMemberships.SingleAsync(x => x.UserId == seed.Owner.UserId, Ct));
        var role = await db.Roles.SingleOrDefaultAsync(x => x.NormalizedName == "ADMINISTRATOR", Ct);
        if (role is null)
        {
            role = new IdentityRole<Guid> { Id = Guid.NewGuid(), Name = "Administrator", NormalizedName = "ADMINISTRATOR" };
            db.Roles.Add(role);
        }
        db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = seed.Administrator.UserId, RoleId = role.Id });
        await db.SaveChangesAsync(Ct);
        Assert.Equal(404, (await RejectAsync(seed.Owner, seed.Order.EnvironmentId, seed.Cancellation())).StatusCode);
        var first = await cancellation.CancelAsync(seed.Administrator, seed.Order.EnvironmentId, seed.Cancellation(), Ct);
        Assert.Equal(404, (await RejectAsync(seed.Owner, seed.Order.EnvironmentId, seed.Cancellation())).StatusCode);
        AssertMismatch(await RejectAsync(seed.Administrator, seed.Order.EnvironmentId, wrongJob));
        Assert.Equal(first, await cancellation.CancelAsync(seed.Administrator, seed.Order.EnvironmentId, seed.Cancellation(), Ct));
        await AssertCancelledAsync(db, seed, seed.Administrator);
    }

    private static void AssertMismatch(RequestRejectedException rejection) =>
        Assert.Equal((409, "PendingArchiveCancellationMismatch"), (rejection.StatusCode, rejection.Code));

    private static async Task AssertUncancelledAsync(BuildSystemDbContext db, Seed seed, string jobState)
    {
        var job = await db.EnvironmentJobs.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((seed.Order.JobId, "ProvisionArchive", jobState, seed.Snapshot), (job.Id, job.Kind, job.State, job.SnapshotJson));
        Assert.Equal("Pending", (await db.EnvironmentRequests.AsNoTracking().SingleAsync(Ct)).State);
        Assert.Equal("Held", (await db.EnvironmentReservations.AsNoTracking().SingleAsync(Ct)).State);
        var environment = await db.TestEnvironments.AsNoTracking().SingleAsync(Ct);
        Assert.Equal("Requested", environment.State);
        Assert.True(environment.HasUnsettledProvisioningOperation);
        Assert.Null(environment.ExpiresAt);
        Assert.Equal(seed.RowVersion, environment.RowVersion);
        Assert.Equal(0, await db.PendingArchiveCancellations.CountAsync(Ct));
    }

    private static async Task<PendingArchiveCancellationRecord> AssertCancelledAsync(BuildSystemDbContext db, Seed seed, Actor actor)
    {
        var request = await db.EnvironmentRequests.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((seed.Order.RequestId, seed.Order.EnvironmentId, seed.Order.JobId, "Cancelled"),
            (request.Id, request.EnvironmentId, request.JobId, request.State));
        var job = await db.EnvironmentJobs.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((seed.Order.JobId, "ProvisionArchive", "Cancelled", seed.Snapshot), (job.Id, job.Kind, job.State, job.SnapshotJson));
        var environment = await db.TestEnvironments.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((seed.Order.EnvironmentId, seed.Reservation, "Cancelled"), (environment.Id, environment.ReservationId, environment.State));
        Assert.False(environment.HasUnsettledProvisioningOperation);
        Assert.Null(environment.ExpiresAt);
        var reservation = await db.EnvironmentReservations.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((seed.Reservation, "Released", ReservedBytes, ArchiveBytes),
            (reservation.Id, reservation.State, reservation.ReservedBytes, reservation.StagingBytes));

        var receipt = await db.PendingArchiveCancellations.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((seed.Order.EnvironmentId, seed.Order.RequestId, seed.Order.JobId, seed.Reservation, seed.Root, seed.Pool, actor.UserId),
            (receipt.EnvironmentId, receipt.RequestId, receipt.JobId, receipt.ReservationId, receipt.RootId, receipt.PoolId, receipt.ActorId));
        Assert.Equal(TimeSpan.Zero, receipt.CancelledAt.Offset);
        Assert.True(receipt.CancelledAt > DateTimeOffset.MinValue);
        var audit = await db.AuditEvents.AsNoTracking().SingleAsync(x => x.Action == "PendingArchiveCancelled", Ct);
        Assert.Equal((actor.UserId, seed.Order.EnvironmentId, receipt.CancelledAt), (audit.ActorId, audit.TargetId, audit.CreatedAt));
        Assert.Equal(0, await db.AgentLeases.CountAsync(Ct));
        Assert.Equal(0, await db.StagedArtifacts.CountAsync(Ct));
        Assert.Equal(0, await db.RestoreIntents.CountAsync(Ct));
        Assert.Equal(0, await db.StageRetirementIntents.CountAsync(Ct));
        Assert.Equal(0, await db.StageCleanupAuthorizations.CountAsync(Ct));
        Assert.Equal(0, await db.StageCleanupResults.CountAsync(Ct));
        Assert.Equal(0, await db.StagingAllocationReleases.CountAsync(Ct));
        return receipt;
    }
}
