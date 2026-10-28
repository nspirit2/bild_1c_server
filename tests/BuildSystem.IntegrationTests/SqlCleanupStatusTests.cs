using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Contracts;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildSystem.IntegrationTests;

// Explicit disposable SQL opt-in. Logical staging release never changes historical bytes, DB reservations or environments.
public sealed class SqlCleanupStatusTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly string Hash = new('A', 64);
    private sealed record Seed(Actor Admin, Actor Owner, Guid Project, Guid Root, Guid Pool, Guid Version,
        OrderReceipt Order, AgentCredential Credential, StageLeaseGrant Grant, StageCleanupAuthorization? Authorization, Guid ResultId);
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
    private static async Task<Seed> PrepareAsync(SqlServerTestDatabase sql, int depth = 4)
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
        if (depth < 0) return new(admin, owner, project, root, pool, version, order, credential, grant, null, Guid.Empty);
        var artifact = ArtifactId(grant.Plan.ReservationId, version);
        await new SqlArtifactReceipts(db).ReportAsync(credential.Token, new(grant.JobId, grant.FencingToken, grant.Plan.ReservationId, version, artifact, Hash, 10, "dt"), Ct);
        await new SqlStageCompletion(db, new ArchiveStageOptions { Enabled = true }).CompleteAsync(credential.Token, new(grant.JobId, grant.FencingToken, artifact), Ct);
        if (depth == 0) return new(admin, owner, project, root, pool, version, order, credential, grant, null, Guid.Empty);
        var retirement = await new SqlStageRetirement(db).RequestAsync(owner, order.EnvironmentId, new(order.JobId, artifact), Ct);
        if (depth == 1) return new(admin, owner, project, root, pool, version, order, credential, grant, null, Guid.Empty);
        var authorization = await new SqlStageCleanup(db, new StageCleanupOptions { Enabled = true }).ClaimAsync(credential.Token, new(retirement.RetirementId, artifact), Ct);
        var resultId = Guid.NewGuid();
        if (depth >= 3) await new SqlStageCleanupResults(db, new StageCleanupResultOptions { Enabled = true }).ReportAsync(credential.Token,
            new(resultId, authorization.AuthorizationId, retirement.RetirementId, order.JobId, artifact, grant.FencingToken), Ct);
        if (depth >= 4) await new SqlStagingAllocationRelease(db, new StagingAllocationReleaseOptions { Enabled = true }).ReleaseAsync(admin, resultId, new(authorization.AuthorizationId, grant.Plan.ReservationId), Ct);
        return new(admin, owner, project, root, pool, version, order, credential, grant, authorization, resultId);
    }
    private static async Task<CleanupStatusView?> ReadAsync(SqlServerTestDatabase sql, Seed seed, Actor? actor = null)
    {
        await using var db = sql.NewContext();
        return await new SqlCleanupStatus(db).GetAsync(actor ?? seed.Owner, seed.Order.EnvironmentId, Ct);
    }

    [SqlServerFact]
    public async Task Current_enabled_project_owner_or_admin_acl_is_required_even_for_admin()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        Assert.NotNull(await ReadAsync(sql, seed)); Assert.NotNull(await ReadAsync(sql, seed, seed.Admin));
        Assert.Null(await ReadAsync(sql, seed, new(Guid.NewGuid(), false)));
        await using (var db = sql.NewContext()) { db.ProjectMemberships.Remove(await db.ProjectMemberships.SingleAsync()); await db.SaveChangesAsync(); }
        Assert.Null(await ReadAsync(sql, seed)); Assert.NotNull(await ReadAsync(sql, seed, seed.Admin));
        await using (var db = sql.NewContext()) { (await db.Projects.SingleAsync()).Enabled = false; await db.SaveChangesAsync(); }
        Assert.Null(await ReadAsync(sql, seed, seed.Admin));
    }
    [SqlServerFact]
    public async Task Bounded_chain_states_and_net_bytes_include_none_without_artifact()
    {
        foreach (var depth in new[] {-1, 0, 1, 2, 3, 4})
        {
            await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql, depth);
            var view = Assert.IsType<CleanupStatusView>(await ReadAsync(sql, seed));
            Assert.Equal(depth switch { <= 0 => "None", 1 => "IntentRequested", 2 => "Authorized", 3 => "DeletedReported", _ => "StagingReleased" }, view.CleanupState);
            Assert.Equal(seed.Order.EnvironmentId, view.EnvironmentId); Assert.Equal(seed.Order.JobId, view.JobId);
            Assert.Equal(10, view.ReservedStagingBytes); Assert.Equal(depth == 4 ? 0 : 10, view.HeldStagingBytes); Assert.Equal(depth == 4 ? 10 : 0, view.ReleasedStagingBytes);
            Assert.Equal(depth >= 0, view.ArtifactId.HasValue); Assert.Equal(depth >= 1, view.RetirementId.HasValue);
            Assert.Equal(depth >= 2, view.AuthorizationId.HasValue); Assert.Equal(depth >= 3, view.ResultId.HasValue); Assert.Equal(depth >= 4, view.ReleaseId.HasValue);
            Assert.NotEqual(default, view.ObservedAt);
        }
    }
    [SqlServerFact]
    public async Task Wire_is_an_exact_metadata_allowlist_without_secrets_or_paths()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(await ReadAsync(sql, seed), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(new[] { "artifactId", "authorizationId", "cleanupState", "environmentId", "heldStagingBytes", "jobId", "observedAt", "releaseId", "releasedStagingBytes", "reservedStagingBytes", "resultId", "retirementId" },
            json.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
    }
    [SqlServerFact]
    public async Task Repeated_reads_do_not_mutate_audit_or_lifecycle_or_reservations()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        int audit; await using(var db = sql.NewContext()) audit = await db.AuditEvents.CountAsync();
        var a = Assert.IsType<CleanupStatusView>(await ReadAsync(sql, seed)); var b = Assert.IsType<CleanupStatusView>(await ReadAsync(sql, seed));
        Assert.Equal(a with { ObservedAt = b.ObservedAt }, b);
        await using var verify = sql.NewContext(); Assert.Equal(audit, await verify.AuditEvents.CountAsync());
        Assert.Equal("StageCompleted", (await verify.EnvironmentJobs.SingleAsync()).State); Assert.Equal("Completed", (await verify.AgentLeases.SingleAsync()).State);
        var r = await verify.EnvironmentReservations.SingleAsync(); Assert.Equal("Held", r.State); Assert.Equal(10, r.StagingBytes); Assert.Equal(100, r.ReservedBytes);
        Assert.True((await verify.TestEnvironments.SingleAsync()).HasUnsettledProvisioningOperation);
        Assert.Equal("Pending", (await verify.EnvironmentRequests.SingleAsync()).State);
    }
    [SqlServerFact]
    public async Task Malformed_immutable_tuple_is_rejected_with_cleanup_status_inconsistent()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        // Deliberate isolated-fixture corruption via direct SQL bypasses the application's immutable EF guard.
        await using (var db = sql.NewContext()) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE StagingAllocationReleases SET ReleasedBytes={9L} WHERE ResultId={seed.ResultId}");
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => ReadAsync(sql, seed));
        Assert.Equal(409, error.StatusCode); Assert.Equal("CleanupStatusInconsistent", error.Code);
        await using (var db = sql.NewContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE StagingAllocationReleases SET ReleasedBytes={10L} WHERE ResultId={seed.ResultId}");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE StageCleanupAuthorizations SET PlanJson={"{}"} WHERE AuthorizationId={seed.Authorization!.AuthorizationId}");
        }
        error = await Assert.ThrowsAsync<RequestRejectedException>(() => ReadAsync(sql, seed));
        Assert.Equal(409, error.StatusCode); Assert.Equal("CleanupStatusInconsistent", error.Code);
    }
    [SqlServerFact]
    public async Task Disabled_current_root_pool_bindings_do_not_hide_enabled_project_history()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); var seed = await PrepareAsync(sql);
        await using (var db = sql.NewContext())
        {
            (await db.ArchiveRoots.SingleAsync()).Enabled = false; (await db.EnvironmentPools.SingleAsync()).Enabled = false;
            (await db.ProjectPools.SingleAsync()).Enabled = false; db.ArchiveRootProjects.Remove(await db.ArchiveRootProjects.SingleAsync()); await db.SaveChangesAsync();
        }
        var view = Assert.IsType<CleanupStatusView>(await ReadAsync(sql, seed)); Assert.Equal("StagingReleased", view.CleanupState); Assert.Equal(0, view.HeldStagingBytes);
    }
}
// end
