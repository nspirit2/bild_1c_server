using System.Text.Json;
using System.Text.Json.Nodes;
using BuildSystem.Application;
using BuildSystem.Contracts;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildSystem.IntegrationTests;

public sealed class SqlArchiveStageTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly string Hash = new('A', 64);
    private sealed record Seed(Actor Administrator, Guid Pool, Guid Root, Guid Version,
        OrderReceipt Order, AgentCredential Credential, Guid Reservation);

    private static async Task<Seed> PrepareAsync(SqlServerTestDatabase sql)
    {
        await sql.ApplyMigrationsAsync();
        var admin = new Actor(Guid.NewGuid(), true);
        var owner = new Actor(Guid.NewGuid(), false);
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
        db.DiskArchives.Add(new DiskArchive { Id = archive, RootId = root, RelativePath = "sample.dt" });
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
        var credential = await EnrollAsync(db, admin, pool, agent, "stage-worker");
        var order = await new SqlEnvironmentOrders(db).CreateAsync(owner, "stage-order",
            new CreateEnvironmentRequest(project, "stage", "archive", new ArchiveSource(version), 7), Ct);
        var environment = await db.TestEnvironments.SingleAsync(x => x.Id == order.EnvironmentId);
        // No fixture-created lease and no forced job transition: dedicated stage poll must issue it.
        return new(admin, pool, root, version, order, credential, environment.ReservationId);
    }

    private static async Task<AgentCredential> EnrollAsync(BuildSystemDbContext db, Actor admin,
        Guid pool, Guid agent, string name)
    {
        var control = new SqlAgentControl(db);
        var ticket = await control.CreateEnrollmentAsync(admin, new CreateEnrollment(agent, name, pool), Ct);
        await control.RegisterAsync(new RegisterAgent(agent, ticket.EnrollmentId, ticket.Token, name), Ct);
        return await control.ApproveAsync(admin, agent, Ct);
    }

    private static async Task<StageLeaseGrant?> PollAsync(SqlServerTestDatabase sql, string token)
    {
        await using var db = sql.NewContext();
        return await new SqlArchiveStageControl(db, new ArchiveStageOptions { Enabled = true }).PollAsync(token, Ct);
    }

    private static async Task<LeaseStatus> HeartbeatAsync(SqlServerTestDatabase sql, string token, StageLeaseGrant grant)
    {
        await using var db = sql.NewContext();
        return await new SqlArchiveStageControl(db, new ArchiveStageOptions { Enabled = true })
            .HeartbeatAsync(token, new LeaseHeartbeat(grant.JobId, grant.FencingToken), Ct);
    }

    private static async Task AssertNoIssuanceAsync(SqlServerTestDatabase sql, string token)
    {
        // An ineligible candidate may be skipped or explicitly rejected, but must never acquire authority.
        try { Assert.Null(await PollAsync(sql, token)); }
        catch (RequestRejectedException rejection) { Assert.Equal(409, rejection.StatusCode); }
        await using var db = sql.NewContext();
        Assert.Empty(await db.AgentLeases.ToListAsync());
        Assert.Equal("Pending", (await db.EnvironmentJobs.SingleAsync()).State);
        Assert.Equal("Held", (await db.EnvironmentReservations.SingleAsync()).State);
    }

    [SqlServerFact]
    public async Task Issuance_replay_heartbeat_and_content_receipt_never_provision_or_release()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        var grant = Assert.IsType<StageLeaseGrant>(await PollAsync(sql, seed.Credential.Token));
        Assert.Equal(seed.Order.JobId, grant.JobId);
        Assert.Equal("ArchiveStageOnly", grant.Kind);
        Assert.True(grant.FencingToken > 0);
        Assert.Equal(new StagePlan(seed.Reservation, seed.Version, seed.Root, "sample.dt", Hash, 10, "dt"), grant.Plan);
        var replay = Assert.IsType<StageLeaseGrant>(await PollAsync(sql, seed.Credential.Token));
        Assert.Equal(JsonSerializer.Serialize(grant), JsonSerializer.Serialize(replay));
        var status = await HeartbeatAsync(sql, seed.Credential.Token, grant);
        Assert.True(status.ExpiresAt >= grant.ExpiresAt);
        await using var db = sql.NewContext();
        var report = new ReportStagedArtifact(grant.JobId, grant.FencingToken, seed.Reservation,
            seed.Version, Guid.NewGuid(), Hash, 10, "dt");
        var receipt = await new SqlArtifactReceipts(db).ReportAsync(seed.Credential.Token, report, Ct);
        Assert.Equal("ContentVerified", receipt.State);
        Assert.Equal(report.ArtifactId, receipt.ArtifactId);
        Assert.Equal("Leased", (await db.EnvironmentJobs.SingleAsync()).State);
        Assert.Equal("Active", (await db.AgentLeases.SingleAsync()).State);
        Assert.Equal("Pending", (await db.EnvironmentRequests.SingleAsync()).State);
        Assert.Equal("Held", (await db.EnvironmentReservations.SingleAsync()).State);
        var environment = await db.TestEnvironments.SingleAsync();
        Assert.Equal("Requested", environment.State);
        Assert.True(environment.HasUnsettledProvisioningOperation);
        Assert.Null(environment.ExpiresAt);
    }

    [SqlServerFact]
    public async Task Concurrent_poll_for_same_agent_returns_one_identical_lease()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        var grants = await Task.WhenAll(PollAsync(sql, seed.Credential.Token), PollAsync(sql, seed.Credential.Token));
        var first = Assert.IsType<StageLeaseGrant>(grants[0]);
        var second = Assert.IsType<StageLeaseGrant>(grants[1]);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        await using var db = sql.NewContext();
        Assert.Single(await db.AgentLeases.ToListAsync());
    }

    [SqlServerFact]
    public async Task Default_disabled_service_rejects_without_issuing_lease()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        await using var db = sql.NewContext();
        var service = new SqlArchiveStageControl(db, new ArchiveStageOptions());
        var rejection = await Assert.ThrowsAsync<RequestRejectedException>(() => service.PollAsync(seed.Credential.Token, Ct));
        Assert.Equal(409, rejection.StatusCode);
        Assert.Equal("StageDisabled", rejection.Code);
        Assert.Empty(await db.AgentLeases.ToListAsync());
        Assert.Equal("Pending", (await db.EnvironmentJobs.SingleAsync()).State);
    }

    [SqlServerFact]
    public async Task Wrong_scope_same_pool_agent_changed_path_and_unavailable_version_cannot_issue()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        AgentCredential other;
        await using (var db = sql.NewContext())
            other = await EnrollAsync(db, seed.Administrator, seed.Pool, Guid.NewGuid(), "other-stage-worker");
        await AssertNoIssuanceAsync(sql, other.Token);
        await using (var db = sql.NewContext())
            await db.ArchiveRoots.Where(x => x.Id == seed.Root).ExecuteUpdateAsync(s => s.SetProperty(x => x.AgentScopeId, other.AgentId));
        await AssertNoIssuanceAsync(sql, seed.Credential.Token);
        await using (var db = sql.NewContext())
        {
            await db.ArchiveRoots.Where(x => x.Id == seed.Root).ExecuteUpdateAsync(s => s.SetProperty(x => x.AgentScopeId, seed.Credential.AgentId));
            await db.DiskArchives.ExecuteUpdateAsync(s => s.SetProperty(x => x.RelativePath, "substituted.dt"));
        }
        await AssertNoIssuanceAsync(sql, seed.Credential.Token);
        await using (var db = sql.NewContext())
        {
            await db.DiskArchives.ExecuteUpdateAsync(s => s.SetProperty(x => x.RelativePath, "sample.dt"));
            await db.DiskArchiveVersions.Where(x => x.Id == seed.Version).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Unavailable"));
        }
        await AssertNoIssuanceAsync(sql, seed.Credential.Token);
    }

    [SqlServerFact]
    public async Task Corrupt_snapshot_and_disabled_root_or_pool_cannot_issue()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        string original;
        await using (var db = sql.NewContext()) original = (await db.EnvironmentJobs.SingleAsync()).SnapshotJson;
        var changed = JsonNode.Parse(original)!.AsObject(); changed["sourceRelativePath"] = "../escape.dt";
        var corrupt = changed.ToJsonString();
        // Deliberate fixture corruption bypasses the production immutable snapshot guard.
        await using (var db = sql.NewContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [EnvironmentJobs] SET [SnapshotJson]={corrupt} WHERE [Id]={seed.Order.JobId}");
        await AssertNoIssuanceAsync(sql, seed.Credential.Token);
        await using (var db = sql.NewContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [EnvironmentJobs] SET [SnapshotJson]={original} WHERE [Id]={seed.Order.JobId}");
            await db.ArchiveRoots.Where(x => x.Id == seed.Root).ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false));
        }
        await AssertNoIssuanceAsync(sql, seed.Credential.Token);
        await using (var db = sql.NewContext())
        {
            await db.ArchiveRoots.Where(x => x.Id == seed.Root).ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, true));
            await db.EnvironmentPools.Where(x => x.Id == seed.Pool).ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false));
        }
        await AssertNoIssuanceAsync(sql, seed.Credential.Token);
    }

    [SqlServerFact]
    public async Task Stale_heartbeat_commits_needs_attention_without_releasing_resources()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        var grant = Assert.IsType<StageLeaseGrant>(await PollAsync(sql, seed.Credential.Token));
        await using (var db = sql.NewContext())
            await db.AgentLeases.ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        var rejection = await Assert.ThrowsAsync<RequestRejectedException>(() => HeartbeatAsync(sql, seed.Credential.Token, grant));
        Assert.Equal(409, rejection.StatusCode);
        await using var check = sql.NewContext();
        Assert.Equal("NeedsAttention", (await check.AgentLeases.SingleAsync()).State);
        Assert.Equal("NeedsAttention", (await check.EnvironmentJobs.SingleAsync()).State);
        Assert.Equal("Held", (await check.EnvironmentReservations.SingleAsync()).State);
        Assert.True((await check.TestEnvironments.SingleAsync()).HasUnsettledProvisioningOperation);
        Assert.Empty(await check.StagedArtifacts.ToListAsync());
    }

    [SqlServerFact]
    public async Task Rotation_and_revocation_recheck_current_raw_token_for_poll_and_heartbeat()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        var grant = Assert.IsType<StageLeaseGrant>(await PollAsync(sql, seed.Credential.Token));
        AgentCredential rotated;
        await using (var db = sql.NewContext())
            rotated = await new SqlAgentControl(db).RotateAsync(seed.Administrator, seed.Credential.AgentId, Ct);
        var oldPoll = await Assert.ThrowsAsync<RequestRejectedException>(() => PollAsync(sql, seed.Credential.Token));
        Assert.Equal(401, oldPoll.StatusCode);
        var oldHeartbeat = await Assert.ThrowsAsync<RequestRejectedException>(() => HeartbeatAsync(sql, seed.Credential.Token, grant));
        Assert.Equal(401, oldHeartbeat.StatusCode);
        Assert.NotNull(await PollAsync(sql, rotated.Token));
        await HeartbeatAsync(sql, rotated.Token, grant);
        await using (var db = sql.NewContext())
            await new SqlAgentControl(db).RevokeAsync(seed.Administrator, seed.Credential.AgentId, Ct);
        var revoked = await Assert.ThrowsAsync<RequestRejectedException>(() => PollAsync(sql, rotated.Token));
        Assert.Equal(401, revoked.StatusCode);
        var revokedHeartbeat = await Assert.ThrowsAsync<RequestRejectedException>(() => HeartbeatAsync(sql, rotated.Token, grant));
        Assert.Equal(401, revokedHeartbeat.StatusCode);
        await using var check = sql.NewContext();
        Assert.Equal("Held", (await check.EnvironmentReservations.SingleAsync()).State);
        Assert.Equal("NeedsAttention", (await check.AgentLeases.SingleAsync()).State);
    }

    [SqlServerFact]
    public async Task Stage_grant_is_allowlisted_and_probe_and_stage_heartbeats_are_separate()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        var grant = Assert.IsType<StageLeaseGrant>(await PollAsync(sql, seed.Credential.Token));
        var json = JsonSerializer.Serialize(grant);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(new[] { "ExpiresAt", "FencingToken", "JobId", "Kind", "Plan" },
            document.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { "ArchiveVersionId", "Format", "RelativePath", "ReservationId", "RootId", "Sha256", "SizeBytes" },
            document.RootElement.GetProperty("Plan").EnumerateObject().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        foreach (var forbidden in new[] { "SnapshotJson", "ClusterId", "SqlServerId", "preparation", "Password", "ConnectionString" })
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sample.dt", json);
        Assert.Contains(Hash, json);
        await using (var db = sql.NewContext())
        {
            var probe = new SqlAgentControl(db);
            Assert.Equal(seed.Credential.AgentId, await probe.AuthenticateAsync(seed.Credential.Token, Ct));
            var rejected = await Assert.ThrowsAsync<RequestRejectedException>(() => probe.HeartbeatAsync(seed.Credential.AgentId,
                new LeaseHeartbeat(grant.JobId, grant.FencingToken), Ct));
            Assert.Equal(409, rejected.StatusCode);
            // Simulate a probe fixture: durable kind is immutable in production, never rewritten by stage service.
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [EnvironmentJobs] SET [Kind]='ControlPlaneProbe', [State]='Running' WHERE [Id]={grant.JobId}");
        }
        var stageReject = await Assert.ThrowsAsync<RequestRejectedException>(() => HeartbeatAsync(sql, seed.Credential.Token, grant));
        Assert.Equal(409, stageReject.StatusCode);
        await using var check = sql.NewContext();
        Assert.Equal("Active", (await check.AgentLeases.SingleAsync()).State);
        Assert.Equal("Held", (await check.EnvironmentReservations.SingleAsync()).State);
    }
}
// end
