using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Contracts;
using BuildSystem.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
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

    private static async Task<Seed> PrepareAsync(SqlServerTestDatabase sql, int poolSlots = 1)
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
            IsolationPolicyId = "isolated", MaxArchiveBytes = ArchiveBytes, MaxStagingBytes = poolSlots * ArchiveBytes });
        db.ArchiveRootProjects.Add(new ArchiveRootProject { RootId = root, ProjectId = project });
        db.DiskArchives.Add(new DiskArchive { Id = archive, RootId = root, RelativePath = "sample.dt" });
        db.DiskArchiveVersions.Add(new DiskArchiveVersion { Id = version, ArchiveId = archive,
            Sha256 = new string('A', 64), SizeBytes = ArchiveBytes, Format = "dt", State = "Ready", EstimatedDbBytes = ReservedBytes });
        db.EnvironmentPools.Add(new EnvironmentPool { Id = pool, Enabled = true, ClusterId = Guid.NewGuid(),
            SqlServerId = Guid.NewGuid(), AgentScopeId = agent, IsolationPolicyId = "isolated", CapacityBytes = poolSlots * ReservedBytes,
            MaxEnvironmentBytes = ReservedBytes, MaxStagingBytes = poolSlots * ArchiveBytes, MaxConcurrentProvisioning = poolSlots,
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
            terminalVersion = (await check.TestEnvironments.AsNoTracking().SingleAsync(Ct)).RowVersion;
            Assert.False(seed.RowVersion.SequenceEqual(terminalVersion));
            var statusReader = new SqlStageStatus(check);
            var status = Assert.IsType<StageStatusView>(await statusReader.GetAsync(seed.Owner, seed.Order.EnvironmentId, Ct));
            Assert.Equal((seed.Order.EnvironmentId, seed.Order.RequestId, seed.Order.JobId), (status.EnvironmentId, status.RequestId, status.JobId));
            Assert.Equal(("Cancelled", "Cancelled", "Cancelled", "Released", "CancelledBeforeFirstLease"),
                (status.EnvironmentState, status.RequestState, status.JobState, status.ReservationState, status.ReconciliationCode));
            Assert.False(status.Unsettled);
            Assert.Equal(ArchiveBytes, status.StagingBytes);
            Assert.Null(status.ArtifactId);
            Assert.Null(status.ArtifactState);
            Assert.Null(status.LeaseState);
            Assert.Null(status.LeaseExpiresAt);
            Assert.Null(status.RestoreIntent);
            Assert.Null(await statusReader.GetAsync(seed.OtherMember, seed.Order.EnvironmentId, Ct));
            Assert.Null(await new SqlCleanupStatus(check).GetAsync(seed.Owner, seed.Order.EnvironmentId, Ct));
            Assert.Equal(terminalVersion, (await check.TestEnvironments.AsNoTracking().SingleAsync(Ct)).RowVersion);
            var receipt = await AssertCancelledAsync(check, seed, seed.Owner);
            Assert.True(status.ObservedAt >= receipt.CancelledAt);
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
    public async Task Stage_status_rejects_cancelled_metadata_without_a_durable_receipt()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        await using var db = sql.NewContext();
        var environment = await db.TestEnvironments.SingleAsync(Ct);
        environment.State = "Cancelled";
        environment.HasUnsettledProvisioningOperation = false;
        (await db.EnvironmentRequests.SingleAsync(Ct)).State = "Cancelled";
        (await db.EnvironmentJobs.SingleAsync(Ct)).State = "Cancelled";
        (await db.EnvironmentReservations.SingleAsync(Ct)).State = "Released";
        await db.SaveChangesAsync(Ct);
        var rowVersion = environment.RowVersion.ToArray();

        var rejected = await Assert.ThrowsAsync<RequestRejectedException>(() =>
            new SqlStageStatus(db).GetAsync(seed.Owner, seed.Order.EnvironmentId, Ct));
        Assert.Equal((409, "StageStatusInconsistent"), (rejected.StatusCode, rejected.Code));
        Assert.Empty(await db.PendingArchiveCancellations.ToListAsync(Ct));
        Assert.Equal(rowVersion, (await db.TestEnvironments.AsNoTracking().SingleAsync(Ct)).RowVersion);

        await AssertReplacementBlockedAsync(db, seed);
        Assert.Equal(rowVersion, (await db.TestEnvironments.AsNoTracking().SingleAsync(Ct)).RowVersion);
    }

    [SqlServerFact]
    public async Task Cancellation_receipt_does_not_release_quota_when_state_or_reserved_bytes_drift()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        await using var db = sql.NewContext();
        await new SqlPendingArchiveCancellations(db, enabled: true)
            .CancelAsync(seed.Owner, seed.Order.EnvironmentId, seed.Cancellation(), Ct);

        var environment = await db.TestEnvironments.SingleAsync(Ct);
        environment.HasUnsettledProvisioningOperation = true;
        await db.SaveChangesAsync(Ct);
        await AssertReplacementBlockedAsync(db, seed);

        environment = await db.TestEnvironments.SingleAsync(Ct);
        environment.HasUnsettledProvisioningOperation = false;
        var reservation = await db.EnvironmentReservations.SingleAsync(Ct);
        reservation.ReservedBytes++;
        await db.SaveChangesAsync(Ct);
        await AssertReplacementBlockedAsync(db, seed);

        reservation = await db.EnvironmentReservations.SingleAsync(Ct);
        reservation.ReservedBytes = ReservedBytes;
        await db.SaveChangesAsync(Ct);
        var replacement = await new SqlEnvironmentOrders(db).CreateAsync(seed.Owner, "replacement", seed.Request(), Ct);
        Assert.NotEqual(seed.Order.EnvironmentId, replacement.EnvironmentId);
        Assert.Equal(1, await db.PendingArchiveCancellations.CountAsync(Ct));
    }

    [SqlServerFact]
    public async Task Unproven_release_does_not_free_shared_root_staging_for_another_owner_and_pool()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        await using var db = sql.NewContext();
        var otherProjectId = Guid.NewGuid();
        var otherPoolId = Guid.NewGuid();
        db.Projects.Add(new Project { Id = otherProjectId, Name = "other-project", IsolationPolicyId = "isolated", PreparationProfileVersion = "v1" });
        db.ProjectMemberships.Add(new ProjectMembership { UserId = seed.OtherMember.UserId, ProjectId = otherProjectId });
        db.ArchiveRootProjects.Add(new ArchiveRootProject { RootId = seed.Root, ProjectId = otherProjectId });
        db.EnvironmentPools.Add(new EnvironmentPool { Id = otherPoolId, Enabled = true, ClusterId = Guid.NewGuid(),
            SqlServerId = Guid.NewGuid(), AgentScopeId = seed.Agent, IsolationPolicyId = "isolated", CapacityBytes = ReservedBytes,
            MaxEnvironmentBytes = ReservedBytes, MaxStagingBytes = ArchiveBytes, MaxConcurrentProvisioning = 1,
            SupportsDt = true, IsolationVerified = true, ScheduledJobsSuppressionVerified = true, ResourceLimitVerified = true });
        db.ProjectPools.Add(new ProjectPool { ProjectId = otherProjectId, PoolId = otherPoolId, Enabled = true });
        db.EnvironmentQuotas.Add(new EnvironmentQuota { SubjectId = seed.OtherMember.UserId, MaxActiveEnvironments = 1,
            MaxConcurrentProvisioning = 1, MaxStorageBytes = ReservedBytes, MaxTtlDays = 30 });
        (await db.EnvironmentReservations.SingleAsync(Ct)).State = "Released";
        await db.SaveChangesAsync(Ct);

        var otherRequest = new CreateEnvironmentRequest(otherProjectId, "other", "archive", new ArchiveSource(seed.Version), 7);
        var orders = new SqlEnvironmentOrders(db);
        var blocked = await Assert.ThrowsAsync<RequestRejectedException>(() => orders.CreateAsync(seed.OtherMember, "other", otherRequest, Ct));
        Assert.Equal((409, "QuotaExceeded"), (blocked.StatusCode, blocked.Code));
        Assert.Equal(1, await db.EnvironmentReservations.CountAsync(Ct));

        (await db.EnvironmentReservations.SingleAsync(Ct)).State = "Held";
        await db.SaveChangesAsync(Ct);
        await new SqlPendingArchiveCancellations(db, enabled: true)
            .CancelAsync(seed.Owner, seed.Order.EnvironmentId, seed.Cancellation(), Ct);
        (await db.EnvironmentReservations.SingleAsync(Ct)).StagingBytes = 0;
        await db.SaveChangesAsync(Ct);
        blocked = await Assert.ThrowsAsync<RequestRejectedException>(() => orders.CreateAsync(seed.OtherMember, "other", otherRequest, Ct));
        Assert.Equal((409, "QuotaExceeded"), (blocked.StatusCode, blocked.Code));

        (await db.EnvironmentReservations.SingleAsync(Ct)).StagingBytes = ArchiveBytes;
        await db.SaveChangesAsync(Ct);
        var replacement = await orders.CreateAsync(seed.OtherMember, "other", otherRequest, Ct);
        Assert.Equal(otherPoolId, (await db.TestEnvironments.SingleAsync(x => x.Id == replacement.EnvironmentId, Ct)).PoolId);
    }

    private static async Task AssertReplacementBlockedAsync(BuildSystemDbContext db, Seed seed)
    {
        var rejected = await Assert.ThrowsAsync<RequestRejectedException>(() =>
            new SqlEnvironmentOrders(db).CreateAsync(seed.Owner, "replacement", seed.Request(), Ct));
        Assert.Equal((409, "QuotaExceeded"), (rejected.StatusCode, rejected.Code));
        Assert.Equal(1, await db.EnvironmentRequests.CountAsync(Ct));
        Assert.Equal(1, await db.EnvironmentReservations.CountAsync(Ct));
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
    public async Task Committed_cancel_prevents_first_lease_for_an_enrolled_agent()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        var credential = await EnrollAsync(sql, seed);
        await using (var db = sql.NewContext())
            await new SqlPendingArchiveCancellations(db, enabled: true)
                .CancelAsync(seed.Owner, seed.Order.EnvironmentId, seed.Cancellation(), Ct);

        Assert.Null(await PollAsync(sql, credential));
        await using var check = sql.NewContext();
        await AssertCancelledAsync(check, seed, seed.Owner);
    }

    [SqlServerFact]
    public async Task Committed_first_lease_prevents_cancel_and_keeps_the_grant_replayable()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        var credential = await EnrollAsync(sql, seed);
        var grant = Assert.IsType<StageLeaseGrant>(await PollAsync(sql, credential));
        Assert.Equal(seed.Order.JobId, grant.JobId);
        await using (var db = sql.NewContext())
            AssertMismatch(await Assert.ThrowsAsync<RequestRejectedException>(() => new SqlPendingArchiveCancellations(db, enabled: true)
                .CancelAsync(seed.Owner, seed.Order.EnvironmentId, seed.Cancellation(), Ct)));

        Assert.Equal(grant, await PollAsync(sql, credential));
        await using var check = sql.NewContext();
        await AssertUncancelledAsync(check, seed, "Leased");
        var lease = await check.AgentLeases.SingleAsync(Ct);
        Assert.Equal((seed.Order.JobId, seed.Agent, grant.FencingToken, "Active"),
            (lease.JobId, lease.AgentId, lease.FencingToken, lease.State));
        Assert.Equal(0, await check.AuditEvents.CountAsync(x => x.Action == "PendingArchiveCancelled", Ct));
    }

    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public int? WrittenEntries { get; private set; }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            WrittenEntries = result;
            throw new InvalidOperationException("Injected failure after SQL writes and before cancellation commit.");
        }
    }

    [SqlServerFact]
    public async Task Failure_after_save_rolls_back_cancellation_and_preserves_an_unrelated_order()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql, poolSlots: 2);
        Seed unrelated;
        await using (var db = sql.NewContext())
        {
            db.EnvironmentQuotas.Add(new EnvironmentQuota { SubjectId = seed.OtherMember.UserId, MaxActiveEnvironments = 1,
                MaxConcurrentProvisioning = 1, MaxStorageBytes = ReservedBytes, MaxTtlDays = 30 });
            await db.SaveChangesAsync(Ct);
            var order = await new SqlEnvironmentOrders(db).CreateAsync(seed.OtherMember, "unrelated", seed.Request(), Ct);
            var environment = await db.TestEnvironments.SingleAsync(e => e.Id == order.EnvironmentId, Ct);
            var job = await db.EnvironmentJobs.SingleAsync(j => j.Id == order.JobId, Ct);
            unrelated = seed with { Owner = seed.OtherMember, Order = order, Reservation = environment.ReservationId,
                Snapshot = job.SnapshotJson, RowVersion = environment.RowVersion };
        }

        var failure = new FailAfterSave();
        var options = new DbContextOptionsBuilder<BuildSystemDbContext>()
            .UseSqlServer(sql.ConnectionString, options => options.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), new[] { 51001 }))
            .AddInterceptors(failure).Options;
        await using (var db = new BuildSystemDbContext(options))
        {
            var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => new SqlPendingArchiveCancellations(db, enabled: true)
                .CancelAsync(seed.Owner, seed.Order.EnvironmentId, seed.Cancellation(), Ct));
            Assert.Equal("Injected failure after SQL writes and before cancellation commit.", rejected.Message);
            Assert.Equal<int?>(6, failure.WrittenEntries); // Four state changes, one receipt, one audit.
        }

        await using var check = sql.NewContext();
        await AssertUncancelledAsync(check, seed, "Pending");
        await AssertUncancelledAsync(check, unrelated, "Pending");
        Assert.Equal(2, await check.EnvironmentRequests.CountAsync(Ct));
        Assert.Equal(2, await check.EnvironmentJobs.CountAsync(Ct));
        Assert.Equal(2, await check.TestEnvironments.CountAsync(Ct));
        Assert.Equal(2, await check.EnvironmentReservations.CountAsync(Ct));
        Assert.Equal(2, await check.AuditEvents.CountAsync(Ct));
        Assert.Equal(2, await check.AuditEvents.CountAsync(x => x.Action == "EnvironmentOrderAccepted", Ct));
        Assert.Equal(0, await check.AgentLeases.CountAsync(Ct));
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

    [SqlServerFact]
    public async Task Owner_cookie_requires_csrf_and_current_membership_to_cancel_or_replay()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        using var factory = new CancellationCookieFactory(sql);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true
        });
        await LoginOwnerAsync(factory, client, seed.Owner);

        var route = $"/api/test-environments/{seed.Order.EnvironmentId:D}/pending-archive-cancellations";
        var before = await CancellationStateAsync(sql);
        using (var missing = await client.PostAsJsonAsync(route, seed.Cancellation(), Ct))
            await AssertErrorAsync(missing, HttpStatusCode.BadRequest, "InvalidCsrfToken");
        Assert.Equal(before, await CancellationStateAsync(sql));
        await using (var db = sql.NewContext())
            await AssertUncancelledAsync(db, seed, "Pending");

        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await CsrfAsync(client));
        using var first = await client.PostAsJsonAsync(route, seed.Cancellation(), Ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var body = await first.Content.ReadAsStringAsync(Ct);
        using var json = JsonDocument.Parse(body);
        var receipt = json.RootElement;
        await using (var db = sql.NewContext())
        {
            var persisted = await AssertCancelledAsync(db, seed, seed.Owner);
            Assert.Equal(persisted.EnvironmentId, receipt.GetProperty("environmentId").GetGuid());
            Assert.Equal(persisted.JobId, receipt.GetProperty("jobId").GetGuid());
            Assert.Equal("CancelledBeforeFirstLease", receipt.GetProperty("code").GetString());
            Assert.Equal(persisted.CancelledAt, receipt.GetProperty("cancelledAt").GetDateTimeOffset());
            Assert.Equal(2, await db.AuditEvents.CountAsync(Ct));
        }
        var cancelled = await CancellationStateAsync(sql);
        using var replay = await client.PostAsJsonAsync(route, seed.Cancellation(), Ct);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(body, await replay.Content.ReadAsStringAsync(Ct));
        Assert.Equal(cancelled, await CancellationStateAsync(sql));

        await using (var db = sql.NewContext())
        {
            db.ProjectMemberships.Remove(await db.ProjectMemberships.SingleAsync(
                m => m.UserId == seed.Owner.UserId && m.ProjectId == seed.Project, Ct));
            await db.SaveChangesAsync(Ct);
        }
        // Same authenticated cookie, valid token and request: the existing receipt must no longer be visible.
        using var revoked = await client.PostAsJsonAsync(route, seed.Cancellation(), Ct);
        await AssertErrorAsync(revoked, HttpStatusCode.NotFound, "PendingArchiveCancellationNotFound");
        Assert.Equal(cancelled, await CancellationStateAsync(sql));
    }

    private sealed class CancellationCookieFactory(SqlServerTestDatabase sql) : WebApplicationFactory<global::Program>
    {
        private const string KeysPrefix = "bs-cancel-cookie-keys-";
        private readonly DirectoryInfo keys = Directory.CreateTempSubdirectory(KeysPrefix);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Configure only; Identity, antiforgery, the endpoint and its SQL service remain real.
            builder.UseEnvironment("Testing");
            builder.UseContentRoot(AppContext.BaseDirectory);
            builder.UseSetting("ConnectionStrings:BuildSystem", sql.ConnectionString);
            builder.UseSetting("Security:DataProtectionDirectory", keys.FullName);
            builder.UseSetting("PendingArchiveCancellation:Enabled", "true");
        }

        protected override void Dispose(bool disposing)
        {
            try { base.Dispose(disposing); }
            finally
            {
                if (disposing)
                {
                    var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
                    if (keys.Parent?.FullName != temp || !keys.Name.StartsWith(KeysPrefix, StringComparison.Ordinal))
                        throw new InvalidOperationException("Unexpected fixture key cleanup path.");
                    if (Directory.Exists(keys.FullName)) keys.Delete(recursive: true);
                }
            }
        }
    }

    private static async Task LoginOwnerAsync(CancellationCookieFactory factory, HttpClient client, Actor owner)
    {
        var password = "Fixture-Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<BuildSystemUser>>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            if (!await roles.RoleExistsAsync("Developer"))
                Assert.True((await roles.CreateAsync(new IdentityRole<Guid>("Developer"))).Succeeded);
            var user = await users.FindByIdAsync(owner.UserId.ToString());
            Assert.NotNull(user);
            Assert.True((await users.SetEmailAsync(user, "owner@fixture.invalid")).Succeeded);
            Assert.True((await users.AddPasswordAsync(user, password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, "Developer")).Succeeded);
        }

        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await CsrfAsync(client));
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = "owner", password }, Ct);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var principal = await client.GetFromJsonAsync<JsonElement>("/api/auth/me", Ct);
        Assert.Equal(owner.UserId, principal.GetProperty("id").GetGuid());
        Assert.Contains("Developer", principal.GetProperty("roles").EnumerateArray().Select(role => role.GetString()));
    }

    private sealed record CsrfResponse(string Token);

    private static async Task<string> CsrfAsync(HttpClient client)
    {
        var response = await client.GetFromJsonAsync<CsrfResponse>("/api/auth/csrf", Ct);
        Assert.NotNull(response);
        return response.Token;
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(code, body.GetProperty("code").GetString());
    }

    private static async Task<string> CancellationStateAsync(SqlServerTestDatabase sql)
    {
        await using var db = sql.NewContext();
        return JsonSerializer.Serialize(new
        {
            Environment = await db.TestEnvironments.AsNoTracking().SingleAsync(Ct),
            Request = await db.EnvironmentRequests.AsNoTracking().SingleAsync(Ct),
            Job = await db.EnvironmentJobs.AsNoTracking().SingleAsync(Ct),
            Reservation = await db.EnvironmentReservations.AsNoTracking().SingleAsync(Ct),
            Receipts = await db.PendingArchiveCancellations.AsNoTracking().OrderBy(r => r.EnvironmentId).ToArrayAsync(Ct),
            Audits = await db.AuditEvents.AsNoTracking().OrderBy(a => a.Id).ToArrayAsync(Ct)
        });
    }

    private static void AssertMismatch(RequestRejectedException rejection) =>
        Assert.Equal((409, "PendingArchiveCancellationMismatch"), (rejection.StatusCode, rejection.Code));

    private static async Task AssertUncancelledAsync(BuildSystemDbContext db, Seed seed, string jobState)
    {
        var job = await db.EnvironmentJobs.AsNoTracking().SingleAsync(j => j.Id == seed.Order.JobId, Ct);
        Assert.Equal((seed.Order.JobId, "ProvisionArchive", jobState, seed.Snapshot), (job.Id, job.Kind, job.State, job.SnapshotJson));
        var request = await db.EnvironmentRequests.AsNoTracking().SingleAsync(q => q.Id == seed.Order.RequestId, Ct);
        Assert.Equal((seed.Owner.UserId, seed.Order.EnvironmentId, seed.Order.JobId, "Pending"),
            (request.OwnerId, request.EnvironmentId, request.JobId, request.State));
        var reservation = await db.EnvironmentReservations.AsNoTracking().SingleAsync(r => r.Id == seed.Reservation, Ct);
        Assert.Equal((seed.Owner.UserId, seed.Pool, "Held", ReservedBytes, ArchiveBytes),
            (reservation.OwnerId, reservation.PoolId, reservation.State, reservation.ReservedBytes, reservation.StagingBytes));
        var environment = await db.TestEnvironments.AsNoTracking().SingleAsync(e => e.Id == seed.Order.EnvironmentId, Ct);
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
