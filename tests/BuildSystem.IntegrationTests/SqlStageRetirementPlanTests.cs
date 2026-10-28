using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Contracts;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildSystem.IntegrationTests;

// These facts require explicit disposable SQL opt-in. No in-memory locking substitute.
public sealed class SqlStageRetirementPlanTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly string Hash = new('A', 64);
    private sealed record Seed(Actor Admin, Actor Owner, Guid Project, Guid Root, Guid Pool,
        Guid Version, OrderReceipt Order, AgentCredential Credential, StageLeaseGrant Grant, Guid ArtifactId);

    private static Guid ArtifactId(Guid reservation, Guid version)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"archive-stage-v1:{reservation:N}:{version:N}"));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes.AsSpan(0, 16), bigEndian: true);
    }
    private static async Task<Seed> PrepareAsync(SqlServerTestDatabase sql)
    {
        await sql.ApplyMigrationsAsync();
        var admin = new Actor(Guid.NewGuid(), true); var owner = new Actor(Guid.NewGuid(), false);
        var project = Guid.NewGuid(); var root = Guid.NewGuid(); var pool = Guid.NewGuid();
        var archive = Guid.NewGuid(); var version = Guid.NewGuid(); var agent = Guid.NewGuid();
        await using var db = sql.NewContext();
        db.Users.AddRange(new BuildSystemUser { Id = admin.UserId, UserName = "admin", NormalizedUserName = "ADMIN" },
            new BuildSystemUser { Id = owner.UserId, UserName = "owner", NormalizedUserName = "OWNER" });
        db.Projects.Add(new Project { Id = project, Name = "retirement-plan", IsolationPolicyId = "isolated", PreparationProfileVersion = "v1" });
        db.ProjectMemberships.Add(new ProjectMembership { UserId = owner.UserId, ProjectId = project });
        db.ArchiveRoots.Add(new ArchiveRoot { Id = root, AgentScopeId = agent, Enabled = true, IsolationPolicyId = "isolated", MaxArchiveBytes = 10000, MaxStagingBytes = 10000 });
        db.ArchiveRootProjects.Add(new ArchiveRootProject { RootId = root, ProjectId = project });
        db.DiskArchives.Add(new DiskArchive { Id = archive, RootId = root, RelativePath = "retirement-plan.dt" });
        db.DiskArchiveVersions.Add(new DiskArchiveVersion { Id = version, ArchiveId = archive, Sha256 = Hash, SizeBytes = 10, Format = "dt", State = "Ready", EstimatedDbBytes = 100 });
        db.EnvironmentPools.Add(Pool(pool, agent));
        db.ProjectPools.Add(new ProjectPool { ProjectId = project, PoolId = pool, Enabled = true });
        db.EnvironmentQuotas.Add(new EnvironmentQuota { SubjectId = owner.UserId, MaxActiveEnvironments = 5, MaxConcurrentProvisioning = 5, MaxStorageBytes = 100000, MaxTtlDays = 30 });
        await db.SaveChangesAsync();
        var control = new SqlAgentControl(db);
        var ticket = await control.CreateEnrollmentAsync(admin, new(agent, "plan-worker", pool), Ct);
        await control.RegisterAsync(new(agent, ticket.EnrollmentId, ticket.Token, "plan-worker"), Ct);
        var credential = await control.ApproveAsync(admin, agent, Ct);
        var order = await new SqlEnvironmentOrders(db).CreateAsync(owner, "retirement-plan-order", new(project, "plan", "archive", new(version), 7), Ct);
        var grant = Assert.IsType<StageLeaseGrant>(await new SqlArchiveStageControl(db, new ArchiveStageOptions { Enabled = true }).PollAsync(credential.Token, Ct));
        var artifact = ArtifactId(grant.Plan.ReservationId, version);
        await new SqlArtifactReceipts(db).ReportAsync(credential.Token, new(grant.JobId, grant.FencingToken, grant.Plan.ReservationId, version, artifact, Hash, 10, "dt"), Ct);
        await new SqlStageCompletion(db, new ArchiveStageOptions { Enabled = true }).CompleteAsync(credential.Token, new(grant.JobId, grant.FencingToken, artifact), Ct);
        await new SqlStageRetirement(db).RequestAsync(owner, order.EnvironmentId, new(order.JobId, artifact), Ct);
        return new(admin, owner, project, root, pool, version, order, credential, grant, artifact);
    }
    private static EnvironmentPool Pool(Guid id, Guid agent) => new() { Id = id, AgentScopeId = agent, Enabled = true,
        ClusterId = Guid.NewGuid(), SqlServerId = Guid.NewGuid(), IsolationPolicyId = "isolated", CapacityBytes = 100000,
        MaxEnvironmentBytes = 10000, MaxStagingBytes = 10000, MaxConcurrentProvisioning = 5, SupportsDt = true,
        SupportsBak = true, IsolationVerified = true, ScheduledJobsSuppressionVerified = true, ResourceLimitVerified = true };
    private static async Task<StageRetirementPlan?> PollAsync(SqlServerTestDatabase sql, string token)
    {
        await using var db = sql.NewContext();
        return await new SqlStageRetirementPlans(db, new StageRetirementOptions { Enabled = true }).PollAsync(token, Ct);
    }
    private static async Task AssertRetainedAsync(SqlServerTestDatabase sql, Seed seed, int audits)
    {
        await using var db = sql.NewContext();
        Assert.Equal(audits, await db.AuditEvents.CountAsync());
        Assert.Equal("StageCompleted", (await db.EnvironmentJobs.SingleAsync(j => j.Id == seed.Order.JobId)).State);
        Assert.Equal("Completed", (await db.AgentLeases.SingleAsync(l => l.JobId == seed.Order.JobId)).State);
        Assert.Equal("ContentVerified", (await db.StagedArtifacts.SingleAsync(a => a.ArtifactId == seed.ArtifactId)).State);
        var environment = await db.TestEnvironments.SingleAsync(e => e.Id == seed.Order.EnvironmentId);
        Assert.Equal("Requested", environment.State); Assert.True(environment.HasUnsettledProvisioningOperation); Assert.Null(environment.ExpiresAt);
        var reservation = await db.EnvironmentReservations.SingleAsync(r => r.Id == seed.Grant.Plan.ReservationId);
        Assert.Equal("Held", reservation.State); Assert.Equal(100, reservation.ReservedBytes); Assert.Equal(10, reservation.StagingBytes);
        Assert.Equal("Pending", (await db.EnvironmentRequests.SingleAsync(q => q.Id == seed.Order.RequestId)).State);
        Assert.Equal("Requested", (await db.StageRetirementIntents.SingleAsync()).State);
    }
    [SqlServerFact]
    public async Task Exact_plan_replay_is_stable_allowlisted_and_has_no_lifecycle_or_audit_writes()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        int audits; await using (var db = sql.NewContext()) audits = await db.AuditEvents.CountAsync();
        var first = Assert.IsType<StageRetirementPlan>(await PollAsync(sql, seed.Credential.Token));
        Assert.Equal(first, await PollAsync(sql, seed.Credential.Token));
        Assert.Equal(seed.Order.EnvironmentId, first.EnvironmentId); Assert.Equal(seed.Order.JobId, first.JobId);
        Assert.Equal(seed.ArtifactId, first.ArtifactId); Assert.Equal(seed.Grant.Plan.ReservationId, first.ReservationId);
        Assert.Equal(seed.Version, first.ArchiveVersionId); Assert.Equal(seed.Grant.FencingToken, first.FencingToken);
        Assert.Equal(Hash, first.Sha256); Assert.Equal(10, first.SizeBytes); Assert.Equal("dt", first.Format);
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(first, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(new[] { "archiveVersionId", "artifactId", "environmentId", "fencingToken", "format", "jobId", "reservationId", "retirementId", "sha256", "sizeBytes" }, wire.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
        await AssertRetainedAsync(sql, seed, audits);
    }
    [SqlServerFact]
    public async Task Approved_foreign_agent_in_other_pool_receives_no_plan()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        AgentCredential other;
        await using (var db = sql.NewContext())
        {
            var pool = Guid.NewGuid(); var agent = Guid.NewGuid(); db.EnvironmentPools.Add(Pool(pool, agent)); await db.SaveChangesAsync();
            var control = new SqlAgentControl(db); var ticket = await control.CreateEnrollmentAsync(seed.Admin, new(agent, "foreign", pool), Ct);
            await control.RegisterAsync(new(agent, ticket.EnrollmentId, ticket.Token, "foreign"), Ct); other = await control.ApproveAsync(seed.Admin, agent, Ct);
        }
        int audits; await using (var db = sql.NewContext()) audits = await db.AuditEvents.CountAsync();
        Assert.Null(await PollAsync(sql, other.Token)); await AssertRetainedAsync(sql, seed, audits);
    }
    [SqlServerFact]
    public async Task Rotated_or_revoked_credentials_are_rejected_and_new_generation_can_read()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        AgentCredential rotated;
        await using (var db = sql.NewContext()) rotated = await new SqlAgentControl(db).RotateAsync(seed.Admin, seed.Credential.AgentId, Ct);
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => PollAsync(sql, seed.Credential.Token)); Assert.Equal(401, error.StatusCode);
        Assert.NotNull(await PollAsync(sql, rotated.Token));
        await using (var db = sql.NewContext()) await new SqlAgentControl(db).RevokeAsync(seed.Admin, rotated.AgentId, Ct);
        error = await Assert.ThrowsAsync<RequestRejectedException>(() => PollAsync(sql, rotated.Token)); Assert.Equal(401, error.StatusCode);
    }
    [SqlServerFact]
    public async Task Historical_completed_expiry_is_allowed_but_current_root_and_project_bindings_are_required()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        await using (var db = sql.NewContext()) await db.AgentLeases.Where(l => l.JobId == seed.Order.JobId).ExecuteUpdateAsync(s => s.SetProperty(l => l.ExpiresAt, DateTimeOffset.UtcNow.AddDays(-2)));
        Assert.NotNull(await PollAsync(sql, seed.Credential.Token));
        await using (var db = sql.NewContext()) await db.ArchiveRoots.Where(r => r.Id == seed.Root).ExecuteUpdateAsync(s => s.SetProperty(r => r.Enabled, false));
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => PollAsync(sql, seed.Credential.Token)); Assert.Equal(409, error.StatusCode);
        await using (var db = sql.NewContext())
        {
            await db.ArchiveRoots.Where(r => r.Id == seed.Root).ExecuteUpdateAsync(s => s.SetProperty(r => r.Enabled, true));
            await db.ProjectPools.Where(p => p.ProjectId == seed.Project).ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, false));
        }
        error = await Assert.ThrowsAsync<RequestRejectedException>(() => PollAsync(sql, seed.Credential.Token)); Assert.Equal(409, error.StatusCode);
        await using (var db = sql.NewContext())
        {
            await db.ProjectPools.Where(p => p.ProjectId == seed.Project).ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, true));
            await db.Projects.Where(p => p.Id == seed.Project).ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, false));
        }
        error = await Assert.ThrowsAsync<RequestRejectedException>(() => PollAsync(sql, seed.Credential.Token)); Assert.Equal(409, error.StatusCode);
    }
    [SqlServerFact]
    public async Task Another_active_or_needs_attention_lease_suppresses_plan_without_expiry_sweep()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        Guid otherJob;
        await using (var db = sql.NewContext())
        {
            var order = await new SqlEnvironmentOrders(db).CreateAsync(seed.Owner, "second-plan-order", new(seed.Project, "second", "archive", new(seed.Version), 7), Ct);
            var grant = Assert.IsType<StageLeaseGrant>(await new SqlArchiveStageControl(db, new ArchiveStageOptions { Enabled = true }).PollAsync(seed.Credential.Token, Ct));
            otherJob = grant.JobId; Assert.Equal(order.JobId, otherJob);
            // Keep an expired Active row: read-only plan polling must not perform the control-plane expiry sweep.
            await db.AgentLeases.Where(l => l.JobId == otherJob).ExecuteUpdateAsync(s => s.SetProperty(l => l.ExpiresAt, DateTimeOffset.UtcNow.AddDays(-1)));
        }
        int audits; await using (var db = sql.NewContext()) audits = await db.AuditEvents.CountAsync();
        Assert.Null(await PollAsync(sql, seed.Credential.Token));
        await using (var db = sql.NewContext())
        {
            Assert.Equal("Active", (await db.AgentLeases.SingleAsync(l => l.JobId == otherJob)).State);
            Assert.Equal(audits, await db.AuditEvents.CountAsync());
            await db.AgentLeases.Where(l => l.JobId == otherJob).ExecuteUpdateAsync(s => s.SetProperty(l => l.State, "NeedsAttention"));
        }
        Assert.Null(await PollAsync(sql, seed.Credential.Token)); await AssertRetainedAsync(sql, seed, audits);
    }
}
// end
