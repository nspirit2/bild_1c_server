using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Contracts;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildSystem.IntegrationTests;

public sealed class SqlStageStatusTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly string Hash = new('A', 64);
    private sealed record Seed(Actor Admin, Actor Owner, Guid Project, Guid Root, OrderReceipt Order, AgentCredential Credential);

    private static async Task<Seed> PrepareAsync(SqlServerTestDatabase sql)
    {
        await sql.ApplyMigrationsAsync();
        var admin = new Actor(Guid.NewGuid(), true); var owner = new Actor(Guid.NewGuid(), false);
        var project = Guid.NewGuid(); var pool = Guid.NewGuid(); var root = Guid.NewGuid();
        var archive = Guid.NewGuid(); var version = Guid.NewGuid(); var agent = Guid.NewGuid();
        await using var db = sql.NewContext();
        db.Users.AddRange(new BuildSystemUser { Id = admin.UserId, UserName = "admin", NormalizedUserName = "ADMIN" },
            new BuildSystemUser { Id = owner.UserId, UserName = "owner", NormalizedUserName = "OWNER" });
        db.Projects.Add(new Project { Id = project, Name = "project", IsolationPolicyId = "isolated", PreparationProfileVersion = "v1" });
        db.ProjectMemberships.Add(new ProjectMembership { UserId = owner.UserId, ProjectId = project });
        db.ArchiveRoots.Add(new ArchiveRoot { Id = root, Enabled = true, AgentScopeId = agent,
            IsolationPolicyId = "isolated", MaxArchiveBytes = 10000, MaxStagingBytes = 10000 });
        db.ArchiveRootProjects.Add(new ArchiveRootProject { RootId = root, ProjectId = project });
        db.DiskArchives.Add(new DiskArchive { Id = archive, RootId = root, RelativePath = "private-source.dt" });
        db.DiskArchiveVersions.Add(new DiskArchiveVersion { Id = version, ArchiveId = archive,
            Sha256 = Hash, SizeBytes = 10, Format = "dt", State = "Ready", EstimatedDbBytes = 100 });
        db.EnvironmentPools.Add(new EnvironmentPool { Id = pool, Enabled = true,
            ClusterId = Guid.NewGuid(), SqlServerId = Guid.NewGuid(), AgentScopeId = agent,
            IsolationPolicyId = "isolated", CapacityBytes = 100000, MaxEnvironmentBytes = 10000,
            MaxStagingBytes = 10000, MaxConcurrentProvisioning = 5, SupportsDt = true, SupportsBak = true,
            IsolationVerified = true, ScheduledJobsSuppressionVerified = true, ResourceLimitVerified = true });
        db.ProjectPools.Add(new ProjectPool { ProjectId = project, PoolId = pool, Enabled = true });
        db.EnvironmentQuotas.Add(new EnvironmentQuota { SubjectId = owner.UserId, MaxActiveEnvironments = 5,
            MaxConcurrentProvisioning = 5, MaxStorageBytes = 100000, MaxTtlDays = 30 });
        await db.SaveChangesAsync();
        var control = new SqlAgentControl(db);
        var ticket = await control.CreateEnrollmentAsync(admin, new CreateEnrollment(agent, "status-worker", pool), Ct);
        await control.RegisterAsync(new RegisterAgent(agent, ticket.EnrollmentId, ticket.Token, "status-worker"), Ct);
        var credential = await control.ApproveAsync(admin, agent, Ct);
        var order = await new SqlEnvironmentOrders(db).CreateAsync(owner, "status-order",
            new CreateEnvironmentRequest(project, "status", "archive", new ArchiveSource(version), 7), Ct);
        return new(admin, owner, project, root, order, credential);
    }

    private static async Task<StageStatusView?> StatusAsync(SqlServerTestDatabase sql, Seed seed, Actor? actor = null)
    {
        await using var db = sql.NewContext();
        return await new SqlStageStatus(db).GetAsync(actor ?? seed.Owner, seed.Order.EnvironmentId, Ct);
    }
    private static async Task<StageLeaseGrant> PollAsync(SqlServerTestDatabase sql, Seed seed)
    {
        await using var db = sql.NewContext();
        return Assert.IsType<StageLeaseGrant>(await new SqlArchiveStageControl(db,
            new ArchiveStageOptions { Enabled = true }).PollAsync(seed.Credential.Token, Ct));
    }
    private static async Task<ReportStagedArtifact> ReportAsync(SqlServerTestDatabase sql, Seed seed, StageLeaseGrant grant)
    {
        var report = new ReportStagedArtifact(grant.JobId, grant.FencingToken, grant.Plan.ReservationId,
            grant.Plan.ArchiveVersionId, Guid.NewGuid(), Hash, 10, "dt");
        await using var db = sql.NewContext();
        Assert.Equal("ContentVerified", (await new SqlArtifactReceipts(db).ReportAsync(seed.Credential.Token, report, Ct)).State);
        return report;
    }
    private static async Task<string> DurableStateAsync(SqlServerTestDatabase sql)
    {
        await using var db = sql.NewContext();
        // Capture only stable persisted values, excluding the read service's observation timestamp.
        return JsonSerializer.Serialize(new
        {
            Jobs = await db.EnvironmentJobs.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            Requests = await db.EnvironmentRequests.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            Environments = await db.TestEnvironments.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            Reservations = await db.EnvironmentReservations.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            Leases = await db.AgentLeases.AsNoTracking().OrderBy(x => x.JobId).ToListAsync(),
            Artifacts = await db.StagedArtifacts.AsNoTracking().OrderBy(x => x.ArtifactId).ToListAsync(),
            Audits = await db.AuditEvents.AsNoTracking().OrderBy(x => x.Id).ToListAsync()
        });
    }

    [SqlServerFact]
    public async Task Owner_and_administrator_can_inspect_but_foreign_or_revoked_project_membership_cannot()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        Assert.NotNull(await StatusAsync(sql, seed));
        Assert.NotNull(await StatusAsync(sql, seed, seed.Admin));
        Assert.Null(await StatusAsync(sql, seed, new Actor(Guid.NewGuid(), false)));
        await using (var db = sql.NewContext())
            await db.ProjectMemberships.Where(x => x.UserId == seed.Owner.UserId && x.ProjectId == seed.Project).ExecuteDeleteAsync();
        Assert.Null(await StatusAsync(sql, seed));
        Assert.NotNull(await StatusAsync(sql, seed, seed.Admin));
        await using (var db = sql.NewContext())
        {
            db.ProjectMemberships.Add(new ProjectMembership { UserId = seed.Owner.UserId, ProjectId = seed.Project });
            await db.SaveChangesAsync();
            await db.Projects.Where(x => x.Id == seed.Project).ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false));
        }
        Assert.Null(await StatusAsync(sql, seed));
        Assert.NotNull(await StatusAsync(sql, seed, seed.Admin));
    }

    [SqlServerFact]
    public async Task Pending_status_is_awaiting_stage_and_has_no_side_effects_or_audit()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var before = await DurableStateAsync(sql);
        var status = Assert.IsType<StageStatusView>(await StatusAsync(sql, seed));
        Assert.Equal(seed.Order.EnvironmentId, status.EnvironmentId);
        Assert.Equal(seed.Order.RequestId, status.RequestId);
        Assert.Equal(seed.Order.JobId, status.JobId);
        Assert.Equal("Pending", status.JobState); Assert.Equal("Pending", status.RequestState);
        Assert.Equal("Requested", status.EnvironmentState); Assert.Equal("Held", status.ReservationState);
        Assert.True(status.Unsettled); Assert.True(status.StagingBytes >= 10);
        Assert.Null(status.ArtifactId); Assert.Null(status.ArtifactState);
        Assert.Null(status.LeaseState); Assert.Null(status.LeaseExpiresAt);
        Assert.Equal("AwaitingStage", status.ReconciliationCode);
        Assert.NotEqual(default, status.ObservedAt);
        Assert.Equal(before, await DurableStateAsync(sql));
    }

    [SqlServerFact]
    public async Task Actual_stage_receipt_and_completion_progress_is_observed_without_releasing_resources()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var grant = await PollAsync(sql, seed);
        var active = Assert.IsType<StageStatusView>(await StatusAsync(sql, seed));
        Assert.Equal("StageActive", active.ReconciliationCode); Assert.Equal("Leased", active.JobState);
        Assert.Equal("Active", active.LeaseState); Assert.Equal(grant.ExpiresAt, active.LeaseExpiresAt);
        var report = await ReportAsync(sql, seed, grant);
        var recorded = Assert.IsType<StageStatusView>(await StatusAsync(sql, seed));
        Assert.Equal("ArtifactReported", recorded.ReconciliationCode);
        Assert.Equal(report.ArtifactId, recorded.ArtifactId); Assert.Equal("ContentVerified", recorded.ArtifactState);
        await using (var db = sql.NewContext())
            await new SqlStageCompletion(db, new ArchiveStageOptions { Enabled = true }).CompleteAsync(seed.Credential.Token,
                new CompleteStage(grant.JobId, grant.FencingToken, report.ArtifactId), Ct);
        var before = await DurableStateAsync(sql);
        var completed = Assert.IsType<StageStatusView>(await StatusAsync(sql, seed));
        Assert.Equal("StageCompleted", completed.ReconciliationCode); Assert.Equal("StageCompleted", completed.JobState);
        Assert.Equal("Completed", completed.LeaseState); Assert.Equal(report.ArtifactId, completed.ArtifactId);
        Assert.Equal("Requested", completed.EnvironmentState); Assert.Equal("Pending", completed.RequestState);
        Assert.Equal("Held", completed.ReservationState); Assert.True(completed.Unsettled); Assert.True(completed.StagingBytes >= 10);
        Assert.Equal(before, await DurableStateAsync(sql));
    }

    [SqlServerFact]
    public async Task Expired_active_read_reports_expiry_without_sweep_then_observes_control_plane_needs_attention()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var grant = await PollAsync(sql, seed);
        await using (var db = sql.NewContext())
            await db.AgentLeases.Where(x => x.JobId == grant.JobId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        var before = await DurableStateAsync(sql);
        var expired = Assert.IsType<StageStatusView>(await StatusAsync(sql, seed));
        Assert.Equal("LeaseExpired", expired.ReconciliationCode); Assert.Equal("Active", expired.LeaseState);
        Assert.Equal("Leased", expired.JobState); Assert.Equal(before, await DurableStateAsync(sql));
        await using (var db = sql.NewContext())
            Assert.Equal(409, (await Assert.ThrowsAsync<RequestRejectedException>(() =>
                new SqlArchiveStageControl(db, new ArchiveStageOptions { Enabled = true }).HeartbeatAsync(seed.Credential.Token,
                    new LeaseHeartbeat(grant.JobId, grant.FencingToken), Ct))).StatusCode);
        var afterSweep = await DurableStateAsync(sql);
        var attention = Assert.IsType<StageStatusView>(await StatusAsync(sql, seed));
        Assert.Equal("NeedsAttention", attention.ReconciliationCode); Assert.Equal("NeedsAttention", attention.LeaseState);
        Assert.Equal("Held", attention.ReservationState); Assert.True(attention.Unsettled);
        Assert.Equal(afterSweep, await DurableStateAsync(sql));
    }

    [SqlServerFact]
    public async Task Serialized_status_is_an_exact_metadata_allowlist_without_secrets_or_filesystem_claims()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var grant = await PollAsync(sql, seed); await ReportAsync(sql, seed, grant);
        // Historical inspection is not a fresh source execution grant. Root deactivation does not hide status.
        await using (var db = sql.NewContext())
            await db.ArchiveRoots.Where(x => x.Id == seed.Root).ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false));
        var view = Assert.IsType<StageStatusView>(await StatusAsync(sql, seed));
        var json = JsonSerializer.Serialize(view);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(new[] { "ArtifactId", "ArtifactState", "EnvironmentId", "EnvironmentState", "JobId", "JobState",
            "LeaseExpiresAt", "LeaseState", "ObservedAt", "ReconciliationCode", "RequestId", "RequestState",
            "ReservationState", "StagingBytes", "Unsettled" },
            document.RootElement.EnumerateObject().Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(seed.Credential.Token, json);
        Assert.DoesNotContain(seed.Credential.AgentId.ToString(), json);
        Assert.DoesNotContain("private-source.dt", json); Assert.DoesNotContain(Hash, json);
        Assert.DoesNotContain("Snapshot", json); Assert.DoesNotContain("Exists", json); Assert.DoesNotContain("VerifiedAt", json);
    }
}
// end
