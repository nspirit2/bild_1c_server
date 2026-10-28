using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Contracts;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildSystem.IntegrationTests;

// Explicit disposable SQL opt-in only. These facts do not execute file cleanup or release resources.
public sealed class SqlStageCleanupTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly string Hash = new('A', 64);
    private sealed record Seed(Actor Admin, Actor Owner, Guid Project, Guid Root, Guid Pool,
        Guid Version, OrderReceipt Order, AgentCredential Credential, StageLeaseGrant Grant,
        Guid ArtifactId, Guid RetirementId);
    private static Guid ArtifactId(Guid reservation, Guid version)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"archive-stage-v1:{reservation:N}:{version:N}"));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x80); bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes.AsSpan(0, 16), bigEndian: true);
    }
    private static EnvironmentPool Pool(Guid id, Guid agent) => new() { Id = id, AgentScopeId = agent, Enabled = true,
        ClusterId = Guid.NewGuid(), SqlServerId = Guid.NewGuid(), IsolationPolicyId = "isolated", CapacityBytes = 100000,
        MaxEnvironmentBytes = 10000, MaxStagingBytes = 10000, MaxConcurrentProvisioning = 5, SupportsDt = true,
        SupportsBak = true, IsolationVerified = true, ScheduledJobsSuppressionVerified = true, ResourceLimitVerified = true };
    private static async Task<Seed> PrepareAsync(SqlServerTestDatabase sql)
    {
        await sql.ApplyMigrationsAsync();
        var admin = new Actor(Guid.NewGuid(), true); var owner = new Actor(Guid.NewGuid(), false);
        var project = Guid.NewGuid(); var root = Guid.NewGuid(); var pool = Guid.NewGuid();
        var archive = Guid.NewGuid(); var version = Guid.NewGuid(); var agent = Guid.NewGuid();
        await using var db = sql.NewContext();
        db.Users.AddRange(new BuildSystemUser { Id = admin.UserId, UserName = "admin", NormalizedUserName = "ADMIN" },
            new BuildSystemUser { Id = owner.UserId, UserName = "owner", NormalizedUserName = "OWNER" });
        db.Projects.Add(new Project { Id = project, Name = "stage-cleanup", IsolationPolicyId = "isolated", PreparationProfileVersion = "v1" });
        db.ProjectMemberships.Add(new ProjectMembership { UserId = owner.UserId, ProjectId = project });
        db.ArchiveRoots.Add(new ArchiveRoot { Id = root, AgentScopeId = agent, Enabled = true, IsolationPolicyId = "isolated", MaxArchiveBytes = 10000, MaxStagingBytes = 10000 });
        db.ArchiveRootProjects.Add(new ArchiveRootProject { RootId = root, ProjectId = project });
        db.DiskArchives.Add(new DiskArchive { Id = archive, RootId = root, RelativePath = "stage-cleanup.dt" });
        db.DiskArchiveVersions.Add(new DiskArchiveVersion { Id = version, ArchiveId = archive, Sha256 = Hash, SizeBytes = 10, Format = "dt", State = "Ready", EstimatedDbBytes = 100 });
        db.EnvironmentPools.Add(Pool(pool, agent)); db.ProjectPools.Add(new ProjectPool { ProjectId = project, PoolId = pool, Enabled = true });
        db.EnvironmentQuotas.Add(new EnvironmentQuota { SubjectId = owner.UserId, MaxActiveEnvironments = 5, MaxConcurrentProvisioning = 5, MaxStorageBytes = 100000, MaxTtlDays = 30 });
        await db.SaveChangesAsync();
        var control = new SqlAgentControl(db); var ticket = await control.CreateEnrollmentAsync(admin, new(agent, "cleanup-worker", pool), Ct);
        await control.RegisterAsync(new(agent, ticket.EnrollmentId, ticket.Token, "cleanup-worker"), Ct);
        var credential = await control.ApproveAsync(admin, agent, Ct);
        var order = await new SqlEnvironmentOrders(db).CreateAsync(owner, "stage-cleanup-order", new(project, "cleanup", "archive", new(version), 7), Ct);
        var grant = Assert.IsType<StageLeaseGrant>(await new SqlArchiveStageControl(db, new ArchiveStageOptions { Enabled = true }).PollAsync(credential.Token, Ct));
        var artifact = ArtifactId(grant.Plan.ReservationId, version);
        await new SqlArtifactReceipts(db).ReportAsync(credential.Token, new(grant.JobId, grant.FencingToken, grant.Plan.ReservationId, version, artifact, Hash, 10, "dt"), Ct);
        await new SqlStageCompletion(db, new ArchiveStageOptions { Enabled = true }).CompleteAsync(credential.Token, new(grant.JobId, grant.FencingToken, artifact), Ct);
        var retirement = await new SqlStageRetirement(db).RequestAsync(owner, order.EnvironmentId, new(order.JobId, artifact), Ct);
        return new(admin, owner, project, root, pool, version, order, credential, grant, artifact, retirement.RetirementId);
    }
    private static async Task<StageCleanupAuthorization> ClaimAsync(SqlServerTestDatabase sql, Seed seed,
        string? token = null, ClaimStageCleanup? request = null)
    {
        await using var db = sql.NewContext();
        return await new SqlStageCleanup(db, new StageCleanupOptions { Enabled = true }).ClaimAsync(token ?? seed.Credential.Token,
            request ?? new(seed.RetirementId, seed.ArtifactId), Ct);
    }
    private static async Task AssertRetainedAsync(SqlServerTestDatabase sql, Seed seed)
    {
        await using var db = sql.NewContext();
        Assert.Equal("StageCompleted", (await db.EnvironmentJobs.SingleAsync(j => j.Id == seed.Order.JobId)).State);
        Assert.Equal("Completed", (await db.AgentLeases.SingleAsync(l => l.JobId == seed.Order.JobId)).State);
        var artifact = await db.StagedArtifacts.SingleAsync(a => a.ArtifactId == seed.ArtifactId);
        Assert.Equal("ContentVerified", artifact.State); Assert.Equal(Hash, artifact.Sha256); Assert.Equal(10, artifact.SizeBytes);
        var environment = await db.TestEnvironments.SingleAsync(e => e.Id == seed.Order.EnvironmentId);
        Assert.Equal("Requested", environment.State); Assert.True(environment.HasUnsettledProvisioningOperation); Assert.Null(environment.ExpiresAt);
        var reservation = await db.EnvironmentReservations.SingleAsync(r => r.Id == seed.Grant.Plan.ReservationId);
        Assert.Equal("Held", reservation.State); Assert.Equal(100, reservation.ReservedBytes); Assert.Equal(10, reservation.StagingBytes);
        Assert.Equal("Pending", (await db.EnvironmentRequests.SingleAsync(q => q.Id == seed.Order.RequestId)).State);
        Assert.Equal("Requested", (await db.StageRetirementIntents.SingleAsync()).State);
    }
    [SqlServerFact]
    public async Task Exact_replay_returns_one_durable_authorization_one_audit_and_retains_allocations()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        int audits; await using (var db = sql.NewContext()) audits = await db.AuditEvents.CountAsync();
        var receipts = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => ClaimAsync(sql, seed)));
        var first = receipts[0]; Assert.All(receipts, receipt => Assert.Equal(first, receipt));
        Assert.Equal(first, await ClaimAsync(sql, seed));
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(first, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(new[] { "authorizationId", "authorizedAt", "plan" }, wire.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(10, wire.RootElement.GetProperty("plan").EnumerateObject().Count());
        Assert.NotEqual(Guid.Empty, first.AuthorizationId); Assert.NotEqual(default, first.AuthorizedAt);
        Assert.Equal(seed.RetirementId, first.Plan.RetirementId); Assert.Equal(seed.ArtifactId, first.Plan.ArtifactId);
        Assert.Equal(seed.Order.EnvironmentId, first.Plan.EnvironmentId); Assert.Equal(seed.Order.JobId, first.Plan.JobId);
        Assert.Equal(seed.Grant.Plan.ReservationId, first.Plan.ReservationId); Assert.Equal(seed.Version, first.Plan.ArchiveVersionId);
        Assert.Equal(seed.Grant.FencingToken, first.Plan.FencingToken); Assert.Equal(Hash, first.Plan.Sha256);
        Assert.Equal(10, first.Plan.SizeBytes); Assert.Equal("dt", first.Plan.Format);
        await using (var db = sql.NewContext())
        {
            var row = Assert.Single(await db.StageCleanupAuthorizations.ToListAsync());
            Assert.Equal(first.AuthorizationId, row.AuthorizationId); Assert.Equal(seed.RetirementId, row.RetirementId);
            Assert.Equal(seed.ArtifactId, row.ArtifactId); Assert.Equal(seed.Credential.AgentId, row.AgentId);
            Assert.Equal(seed.Grant.FencingToken, row.StageFencingToken); Assert.Equal(first.AuthorizedAt, row.AuthorizedAt);
            Assert.Equal(first.Plan, JsonSerializer.Deserialize<StageRetirementPlan>(row.PlanJson));
            Assert.Equal(audits + 1, await db.AuditEvents.CountAsync());
            Assert.Single(await db.AuditEvents.Where(a => a.Action == "StageCleanupAuthorized").ToListAsync());
        }
        await AssertRetainedAsync(sql, seed);
    }
    [SqlServerFact]
    public async Task Foreign_agent_or_wrong_expected_identity_cannot_obtain_authorization()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        AgentCredential other;
        await using (var db = sql.NewContext())
        {
            var pool = Guid.NewGuid(); var agent = Guid.NewGuid(); db.EnvironmentPools.Add(Pool(pool, agent)); await db.SaveChangesAsync();
            var control = new SqlAgentControl(db); var ticket = await control.CreateEnrollmentAsync(seed.Admin, new(agent, "foreign", pool), Ct);
            await control.RegisterAsync(new(agent, ticket.EnrollmentId, ticket.Token, "foreign"), Ct); other = await control.ApproveAsync(seed.Admin, agent, Ct);
        }
        foreach (var claim in new[] { (other.Token, new ClaimStageCleanup(seed.RetirementId, seed.ArtifactId)),
            (seed.Credential.Token, new ClaimStageCleanup(Guid.NewGuid(), seed.ArtifactId)),
            (seed.Credential.Token, new ClaimStageCleanup(seed.RetirementId, Guid.NewGuid())) })
        {
            var error = await Assert.ThrowsAsync<RequestRejectedException>(() => ClaimAsync(sql, seed, claim.Item1, claim.Item2));
            Assert.Equal(409, error.StatusCode); Assert.Equal("CleanupClaimMismatch", error.Code);
        }
        await using (var db = sql.NewContext()) Assert.Empty(await db.StageCleanupAuthorizations.ToListAsync());
        await AssertRetainedAsync(sql, seed);
    }
    [SqlServerFact]
    public async Task Rotation_rejects_old_generation_new_token_replays_same_authorization_and_revocation_denies()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var first = await ClaimAsync(sql, seed); AgentCredential rotated;
        await using (var db = sql.NewContext()) rotated = await new SqlAgentControl(db).RotateAsync(seed.Admin, seed.Credential.AgentId, Ct);
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => ClaimAsync(sql, seed)); Assert.Equal(401, error.StatusCode);
        Assert.Equal(first, await ClaimAsync(sql, seed, rotated.Token));
        await using (var db = sql.NewContext()) await new SqlAgentControl(db).RevokeAsync(seed.Admin, rotated.AgentId, Ct);
        error = await Assert.ThrowsAsync<RequestRejectedException>(() => ClaimAsync(sql, seed, rotated.Token)); Assert.Equal(401, error.StatusCode);
        await using (var db = sql.NewContext()) Assert.Single(await db.StageCleanupAuthorizations.ToListAsync());
        await AssertRetainedAsync(sql, seed);
    }
    [SqlServerFact]
    public async Task Disabled_bindings_and_active_or_needs_attention_lease_prevent_authorization()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        await using (var db = sql.NewContext()) await db.ArchiveRoots.Where(r => r.Id == seed.Root).ExecuteUpdateAsync(s => s.SetProperty(r => r.Enabled, false));
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => ClaimAsync(sql, seed)); Assert.Equal(409, error.StatusCode);
        await using (var db = sql.NewContext())
        {
            await db.ArchiveRoots.Where(r => r.Id == seed.Root).ExecuteUpdateAsync(s => s.SetProperty(r => r.Enabled, true));
            await db.ProjectPools.Where(p => p.ProjectId == seed.Project).ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, false));
        }
        error = await Assert.ThrowsAsync<RequestRejectedException>(() => ClaimAsync(sql, seed)); Assert.Equal(409, error.StatusCode);
        Guid otherJob;
        await using (var db = sql.NewContext())
        {
            await db.ProjectPools.Where(p => p.ProjectId == seed.Project).ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, true));
            var order = await new SqlEnvironmentOrders(db).CreateAsync(seed.Owner, "second-cleanup-order", new(seed.Project, "second", "archive", new(seed.Version), 7), Ct);
            var grant = Assert.IsType<StageLeaseGrant>(await new SqlArchiveStageControl(db, new ArchiveStageOptions { Enabled = true }).PollAsync(seed.Credential.Token, Ct));
            otherJob = grant.JobId; Assert.Equal(order.JobId, otherJob);
            await db.AgentLeases.Where(l => l.JobId == otherJob).ExecuteUpdateAsync(s => s.SetProperty(l => l.ExpiresAt, DateTimeOffset.UtcNow.AddDays(-1)));
        }
        error = await Assert.ThrowsAsync<RequestRejectedException>(() => ClaimAsync(sql, seed)); Assert.Equal(409, error.StatusCode);
        await using (var db = sql.NewContext())
        {
            Assert.Equal("Active", (await db.AgentLeases.SingleAsync(l => l.JobId == otherJob)).State);
            await db.AgentLeases.Where(l => l.JobId == otherJob).ExecuteUpdateAsync(s => s.SetProperty(l => l.State, "NeedsAttention"));
        }
        error = await Assert.ThrowsAsync<RequestRejectedException>(() => ClaimAsync(sql, seed)); Assert.Equal(409, error.StatusCode);
        await using (var db = sql.NewContext()) Assert.Empty(await db.StageCleanupAuthorizations.ToListAsync());
        await AssertRetainedAsync(sql, seed);
    }
    [SqlServerFact]
    public async Task Persisted_authorization_rejects_mutation_and_deletion_through_SaveChanges()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        var first = await ClaimAsync(sql, seed);
        await using (var db = sql.NewContext())
        {
            var row = await db.StageCleanupAuthorizations.SingleAsync();
            db.Entry(row).Property("ArtifactId").CurrentValue = Guid.NewGuid();
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await using (var db = sql.NewContext())
        {
            db.StageCleanupAuthorizations.Remove(await db.StageCleanupAuthorizations.SingleAsync());
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        Assert.Equal(first, await ClaimAsync(sql, seed)); await AssertRetainedAsync(sql, seed);
    }
}
// end
