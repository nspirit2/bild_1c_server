using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Contracts;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildSystem.IntegrationTests;

// Explicit disposable SQL opt-in. DeletedReported is an agent assertion, not filesystem proof or quota release.
public sealed class SqlStageCleanupResultTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly string Hash = new('A', 64);
    private sealed record Seed(Actor Admin, Actor Owner, Guid Project, Guid Root, Guid Pool, Guid Version,
        OrderReceipt Order, AgentCredential Credential, StageLeaseGrant Grant, StageCleanupAuthorization Authorization);
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
        db.Projects.Add(new Project { Id = project, Name = "cleanup-result", IsolationPolicyId = "isolated", PreparationProfileVersion = "v1" });
        db.ProjectMemberships.Add(new ProjectMembership { UserId = owner.UserId, ProjectId = project });
        db.ArchiveRoots.Add(new ArchiveRoot { Id = root, AgentScopeId = agent, Enabled = true, IsolationPolicyId = "isolated", MaxArchiveBytes = 10000, MaxStagingBytes = 10000 });
        db.ArchiveRootProjects.Add(new ArchiveRootProject { RootId = root, ProjectId = project });
        db.DiskArchives.Add(new DiskArchive { Id = archive, RootId = root, RelativePath = "cleanup-result.dt" });
        db.DiskArchiveVersions.Add(new DiskArchiveVersion { Id = version, ArchiveId = archive, Sha256 = Hash, SizeBytes = 10, Format = "dt", State = "Ready", EstimatedDbBytes = 100 });
        db.EnvironmentPools.Add(Pool(pool, agent)); db.ProjectPools.Add(new ProjectPool { ProjectId = project, PoolId = pool, Enabled = true });
        db.EnvironmentQuotas.Add(new EnvironmentQuota { SubjectId = owner.UserId, MaxActiveEnvironments = 5, MaxConcurrentProvisioning = 5, MaxStorageBytes = 100000, MaxTtlDays = 30 });
        await db.SaveChangesAsync();
        var control = new SqlAgentControl(db); var ticket = await control.CreateEnrollmentAsync(admin, new(agent, "result-worker", pool), Ct);
        await control.RegisterAsync(new(agent, ticket.EnrollmentId, ticket.Token, "result-worker"), Ct);
        var credential = await control.ApproveAsync(admin, agent, Ct);
        var order = await new SqlEnvironmentOrders(db).CreateAsync(owner, "cleanup-result-order", new(project, "cleanup", "archive", new(version), 7), Ct);
        var grant = Assert.IsType<StageLeaseGrant>(await new SqlArchiveStageControl(db, new ArchiveStageOptions { Enabled = true }).PollAsync(credential.Token, Ct));
        var artifact = ArtifactId(grant.Plan.ReservationId, version);
        await new SqlArtifactReceipts(db).ReportAsync(credential.Token, new(grant.JobId, grant.FencingToken, grant.Plan.ReservationId, version, artifact, Hash, 10, "dt"), Ct);
        await new SqlStageCompletion(db, new ArchiveStageOptions { Enabled = true }).CompleteAsync(credential.Token, new(grant.JobId, grant.FencingToken, artifact), Ct);
        var retirement = await new SqlStageRetirement(db).RequestAsync(owner, order.EnvironmentId, new(order.JobId, artifact), Ct);
        var authorization = await new SqlStageCleanup(db, new StageCleanupOptions { Enabled = true }).ClaimAsync(credential.Token, new(retirement.RetirementId, artifact), Ct);
        return new(admin, owner, project, root, pool, version, order, credential, grant, authorization);
    }
    private static ReportStageCleanup Report(Seed seed) => new(Guid.NewGuid(), seed.Authorization.AuthorizationId,
        seed.Authorization.Plan.RetirementId, seed.Order.JobId, seed.Authorization.Plan.ArtifactId, seed.Grant.FencingToken);
    private static async Task<StageCleanupResultReceipt> ReportAsync(SqlServerTestDatabase sql, Seed seed,
        ReportStageCleanup report, string? token = null)
    {
        await using var db = sql.NewContext();
        return await new SqlStageCleanupResults(db, new StageCleanupResultOptions { Enabled = true })
            .ReportAsync(token ?? seed.Credential.Token, report, Ct);
    }
    private static async Task AssertRetainedAsync(SqlServerTestDatabase sql, Seed seed)
    {
        await using var db = sql.NewContext();
        Assert.Equal("StageCompleted", (await db.EnvironmentJobs.SingleAsync(j => j.Id == seed.Order.JobId)).State);
        Assert.Equal("Completed", (await db.AgentLeases.SingleAsync(l => l.JobId == seed.Order.JobId)).State);
        var artifact = await db.StagedArtifacts.SingleAsync(a => a.ArtifactId == seed.Authorization.Plan.ArtifactId);
        Assert.Equal("ContentVerified", artifact.State); Assert.Equal(Hash, artifact.Sha256); Assert.Equal(10, artifact.SizeBytes);
        var environment = await db.TestEnvironments.SingleAsync(e => e.Id == seed.Order.EnvironmentId);
        Assert.Equal("Requested", environment.State); Assert.True(environment.HasUnsettledProvisioningOperation); Assert.Null(environment.ExpiresAt);
        var reservation = await db.EnvironmentReservations.SingleAsync(r => r.Id == seed.Grant.Plan.ReservationId);
        Assert.Equal("Held", reservation.State); Assert.Equal(100, reservation.ReservedBytes); Assert.Equal(10, reservation.StagingBytes);
        Assert.Equal("Pending", (await db.EnvironmentRequests.SingleAsync(q => q.Id == seed.Order.RequestId)).State);
        Assert.Equal("Requested", (await db.StageRetirementIntents.SingleAsync()).State);
        var authorization = Assert.Single(await db.StageCleanupAuthorizations.ToListAsync());
        Assert.Equal(seed.Authorization.AuthorizationId, authorization.AuthorizationId);
        Assert.Equal(seed.Authorization.Plan, JsonSerializer.Deserialize<StageRetirementPlan>(authorization.PlanJson));
    }
    [SqlServerFact]
    public async Task Six_concurrent_exact_reports_return_one_receipt_and_audit_without_releasing_allocations()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql); var report = Report(seed);
        int audits; await using (var db = sql.NewContext()) audits = await db.AuditEvents.CountAsync();
        var receipts = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => ReportAsync(sql, seed, report)));
        var first = receipts[0]; Assert.All(receipts, receipt => Assert.Equal(first, receipt));
        Assert.Equal(first, await ReportAsync(sql, seed, report));
        Assert.Equal(report.ResultId, first.ResultId); Assert.Equal(report.AuthorizationId, first.AuthorizationId);
        Assert.Equal(report.ArtifactId, first.ArtifactId); Assert.Equal("DeletedReported", first.State); Assert.NotEqual(default, first.ReceivedAt);
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(first, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(new[] { "artifactId", "authorizationId", "receivedAt", "resultId", "state" }, wire.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
        await using (var db = sql.NewContext())
        {
            var row = Assert.Single(await db.StageCleanupResults.ToListAsync());
            Assert.Equal(first.ResultId, row.ResultId); Assert.Equal(first.AuthorizationId, row.AuthorizationId);
            Assert.Equal(first.ArtifactId, row.ArtifactId); Assert.Equal(seed.Credential.AgentId, row.AgentId);
            Assert.Equal(seed.Grant.FencingToken, row.StageFencingToken); Assert.Equal(first.ReceivedAt, row.ReceivedAt);
            Assert.Equal(audits + 1, await db.AuditEvents.CountAsync());
            Assert.Single(await db.AuditEvents.Where(a => a.Action == "StageCleanupDeletedReported").ToListAsync());
        }
        await AssertRetainedAsync(sql, seed);
    }
    [SqlServerFact]
    public async Task Foreign_scope_wrong_pinned_ids_and_result_id_collision_are_rejected()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql); var report = Report(seed);
        AgentCredential other;
        await using (var db = sql.NewContext())
        {
            var pool = Guid.NewGuid(); var agent = Guid.NewGuid(); db.EnvironmentPools.Add(Pool(pool, agent)); await db.SaveChangesAsync();
            var control = new SqlAgentControl(db); var ticket = await control.CreateEnrollmentAsync(seed.Admin, new(agent, "foreign-result", pool), Ct);
            await control.RegisterAsync(new(agent, ticket.EnrollmentId, ticket.Token, "foreign-result"), Ct); other = await control.ApproveAsync(seed.Admin, agent, Ct);
        }
        foreach (var wrong in new[] { report with { AuthorizationId = Guid.NewGuid() }, report with { RetirementId = Guid.NewGuid() },
            report with { JobId = Guid.NewGuid() }, report with { ArtifactId = Guid.NewGuid() }, report with { FencingToken = report.FencingToken + 1 } })
        {
            var error = await Assert.ThrowsAsync<RequestRejectedException>(() => ReportAsync(sql, seed, wrong)); Assert.Equal(409, error.StatusCode);
        }
        var foreign = await Assert.ThrowsAsync<RequestRejectedException>(() => ReportAsync(sql, seed, report, other.Token)); Assert.Equal(409, foreign.StatusCode);
        await using (var db = sql.NewContext()) Assert.Empty(await db.StageCleanupResults.ToListAsync());
        var first = await ReportAsync(sql, seed, report);
        // One authorization cannot be rebound to another result identity, nor an existing result to another artifact.
        foreach (var wrong in new[] { report with { ResultId = Guid.NewGuid() }, report with { ArtifactId = Guid.NewGuid() } })
        {
            var error = await Assert.ThrowsAsync<RequestRejectedException>(() => ReportAsync(sql, seed, wrong)); Assert.Equal(409, error.StatusCode);
        }
        Assert.Equal(first, await ReportAsync(sql, seed, report));
        await using (var db = sql.NewContext()) Assert.Single(await db.StageCleanupResults.ToListAsync());
        await AssertRetainedAsync(sql, seed);
    }
    [SqlServerFact]
    public async Task Rotation_rejects_old_token_new_generation_replays_receipt_and_revocation_denies()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql); var report = Report(seed);
        var first = await ReportAsync(sql, seed, report); AgentCredential rotated;
        await using (var db = sql.NewContext()) rotated = await new SqlAgentControl(db).RotateAsync(seed.Admin, seed.Credential.AgentId, Ct);
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => ReportAsync(sql, seed, report)); Assert.Equal(401, error.StatusCode);
        Assert.Equal(first, await ReportAsync(sql, seed, report, rotated.Token));
        await using (var db = sql.NewContext()) await new SqlAgentControl(db).RevokeAsync(seed.Admin, rotated.AgentId, Ct);
        error = await Assert.ThrowsAsync<RequestRejectedException>(() => ReportAsync(sql, seed, report, rotated.Token)); Assert.Equal(401, error.StatusCode);
        await using (var db = sql.NewContext()) Assert.Single(await db.StageCleanupResults.ToListAsync());
        await AssertRetainedAsync(sql, seed);
    }
    [SqlServerFact]
    public async Task Historical_deletion_report_is_accepted_despite_new_lease_and_disabled_current_bindings()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql); var report = Report(seed);
        Guid otherJob;
        await using (var db = sql.NewContext())
        {
            var order = await new SqlEnvironmentOrders(db).CreateAsync(seed.Owner, "new-work-before-result", new(seed.Project, "second", "archive", new(seed.Version), 7), Ct);
            var grant = Assert.IsType<StageLeaseGrant>(await new SqlArchiveStageControl(db, new ArchiveStageOptions { Enabled = true }).PollAsync(seed.Credential.Token, Ct));
            otherJob = grant.JobId; Assert.Equal(order.JobId, otherJob);
            await db.Projects.Where(p => p.Id == seed.Project).ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, false));
            await db.ArchiveRoots.Where(r => r.Id == seed.Root).ExecuteUpdateAsync(s => s.SetProperty(r => r.Enabled, false));
            await db.ProjectPools.Where(p => p.ProjectId == seed.Project).ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, false));
            await db.EnvironmentPools.Where(p => p.Id == seed.Pool).ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, false));
        }
        var first = await ReportAsync(sql, seed, report); Assert.Equal("DeletedReported", first.State);
        Assert.Equal(first, await ReportAsync(sql, seed, report));
        await using (var db = sql.NewContext())
        {
            Assert.Equal("Active", (await db.AgentLeases.SingleAsync(l => l.JobId == otherJob)).State);
            Assert.Single(await db.StageCleanupResults.ToListAsync());
        }
        await AssertRetainedAsync(sql, seed);
    }
    [SqlServerFact]
    public async Task Persisted_result_rejects_mutation_and_deletion_through_SaveChanges()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql); var report = Report(seed);
        var first = await ReportAsync(sql, seed, report);
        await using (var db = sql.NewContext())
        {
            var row = await db.StageCleanupResults.SingleAsync(); db.Entry(row).Property("ArtifactId").CurrentValue = Guid.NewGuid();
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await using (var db = sql.NewContext())
        {
            db.StageCleanupResults.Remove(await db.StageCleanupResults.SingleAsync());
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        Assert.Equal(first, await ReportAsync(sql, seed, report)); await AssertRetainedAsync(sql, seed);
    }
}
// end
