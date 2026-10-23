using System.Text.Json.Nodes;
using BuildSystem.Application;
using BuildSystem.Contracts;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildSystem.IntegrationTests;

public sealed class SqlArtifactReceiptTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly string Hash = new('A', 64);

    private sealed record Seed(Actor Administrator, Guid Pool, Guid Root, Guid Version,
        OrderReceipt Order, AgentCredential Credential, ReportStagedArtifact Report);

    private static async Task<Seed> PrepareAsync(SqlServerTestDatabase sql)
    {
        await sql.ApplyMigrationsAsync();
        var admin = new Actor(Guid.NewGuid(), true);
        var owner = new Actor(Guid.NewGuid(), false);
        var project = Guid.NewGuid();
        var pool = Guid.NewGuid();
        var root = Guid.NewGuid();
        var archive = Guid.NewGuid();
        var version = Guid.NewGuid();
        var agent = Guid.NewGuid();
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

        var control = new SqlAgentControl(db);
        var ticket = await control.CreateEnrollmentAsync(admin, new CreateEnrollment(agent, "receipt-worker", pool), Ct);
        await control.RegisterAsync(new RegisterAgent(agent, ticket.EnrollmentId, ticket.Token, "receipt-worker"), Ct);
        var credential = await control.ApproveAsync(admin, agent, Ct);
        var order = await new SqlEnvironmentOrders(db).CreateAsync(owner, "receipt-order",
            new CreateEnvironmentRequest(project, "receipt", "archive", new ArchiveSource(version), 7), Ct);
        var environment = await db.TestEnvironments.SingleAsync(x => x.Id == order.EnvironmentId);

        // Fixture-only dormant ProvisionArchive lease. The public scheduler remains probe-only;
        // this is NOT evidence that PollAsync issues provisioning jobs or enables an executor.
        (await db.EnvironmentJobs.SingleAsync(x => x.Id == order.JobId)).State = "Leased";
        db.AgentLeases.Add(new AgentLease { JobId = order.JobId, AgentId = agent, FencingToken = 7,
            State = "Active", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) });
        await db.SaveChangesAsync();
        return new Seed(admin, pool, root, version, order, credential,
            new ReportStagedArtifact(order.JobId, 7, environment.ReservationId, version, Guid.NewGuid(), Hash, 10, "dt"));
    }

    private static async Task<StagedArtifactReceipt> ReportAsync(SqlServerTestDatabase sql, Seed seed,
        ReportStagedArtifact? report = null, string? credential = null)
    {
        await using var db = sql.NewContext();
        return await new SqlArtifactReceipts(db).ReportAsync(credential ?? seed.Credential.Token, report ?? seed.Report, Ct);
    }

    private static async Task ConflictAsync(SqlServerTestDatabase sql, Seed seed, ReportStagedArtifact report)
    {
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => ReportAsync(sql, seed, report));
        Assert.Equal(409, error.StatusCode);
    }

    [SqlServerFact]
    public async Task Live_receipt_and_exact_replay_persist_one_content_verified_artifact_without_ready_or_release()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        int audits;
        await using (var db = sql.NewContext()) audits = await db.AuditEvents.CountAsync();
        var first = await ReportAsync(sql, seed);
        Assert.Equal(seed.Report.ArtifactId, first.ArtifactId);
        Assert.Equal("ContentVerified", first.State);
        Assert.Equal(first, await ReportAsync(sql, seed));
        await using var check = sql.NewContext();
        var artifact = Assert.Single(await check.StagedArtifacts.AsNoTracking().ToListAsync());
        Assert.Equal(seed.Report.ArtifactId, artifact.ArtifactId);
        Assert.Equal("ContentVerified", artifact.State);
        Assert.Equal(audits + 1, await check.AuditEvents.CountAsync());
        Assert.Equal("Held", (await check.EnvironmentReservations.SingleAsync()).State);
        var environment = await check.TestEnvironments.SingleAsync();
        Assert.Equal("Requested", environment.State);
        Assert.True(environment.HasUnsettledProvisioningOperation);
        Assert.Null(environment.ExpiresAt);
        Assert.Equal("Pending", (await check.EnvironmentRequests.SingleAsync()).State);
        Assert.Equal("Leased", (await check.EnvironmentJobs.SingleAsync()).State);
    }

    [SqlServerFact]
    public async Task Stale_fence_and_expired_lease_reject_even_exact_receipt_replay()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        await ReportAsync(sql, seed);
        await ConflictAsync(sql, seed, seed.Report with { FencingToken = seed.Report.FencingToken + 1 });
        await using (var db = sql.NewContext())
            await db.AgentLeases.Where(x => x.JobId == seed.Order.JobId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        await ConflictAsync(sql, seed, seed.Report);
        await using var check = sql.NewContext();
        Assert.Equal(1, await check.StagedArtifacts.CountAsync());
        Assert.Equal("Held", (await check.EnvironmentReservations.SingleAsync()).State);
    }

    [SqlServerFact]
    public async Task Rotated_and_revoked_credentials_cannot_replay_previously_accepted_receipt()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        await ReportAsync(sql, seed);
        AgentCredential rotated;
        await using (var db = sql.NewContext())
            rotated = await new SqlAgentControl(db).RotateAsync(seed.Administrator, seed.Credential.AgentId, Ct);
        var old = await Assert.ThrowsAsync<RequestRejectedException>(() => ReportAsync(sql, seed));
        Assert.Equal(401, old.StatusCode);
        Assert.Equal("ContentVerified", (await ReportAsync(sql, seed, credential: rotated.Token)).State);
        await using (var db = sql.NewContext())
            await new SqlAgentControl(db).RevokeAsync(seed.Administrator, seed.Credential.AgentId, Ct);
        var revoked = await Assert.ThrowsAsync<RequestRejectedException>(() => ReportAsync(sql, seed, credential: rotated.Token));
        Assert.Equal(401, revoked.StatusCode);
        await using var check = sql.NewContext();
        Assert.Equal(1, await check.StagedArtifacts.CountAsync());
    }

    [SqlServerFact]
    public async Task Conflicting_content_version_reservation_and_artifact_identity_are_atomic()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        int audits;
        await using (var db = sql.NewContext()) audits = await db.AuditEvents.CountAsync();
        var mismatches = new[] {
            seed.Report with { Sha256 = new string('B', 64) },
            seed.Report with { SizeBytes = 11 },
            seed.Report with { Format = "bak" },
            seed.Report with { ArchiveVersionId = Guid.NewGuid() },
            seed.Report with { ReservationId = Guid.NewGuid() },
            seed.Report with { JobId = Guid.NewGuid() }
        };
        foreach (var report in mismatches) await ConflictAsync(sql, seed, report);
        await using (var db = sql.NewContext())
        {
            Assert.Empty(await db.StagedArtifacts.ToListAsync());
            Assert.Equal(audits, await db.AuditEvents.CountAsync());
        }
        await ReportAsync(sql, seed);
        await ConflictAsync(sql, seed, seed.Report with { ArtifactId = Guid.NewGuid() });
        foreach (var report in mismatches) await ConflictAsync(sql, seed, report);
        await using var check = sql.NewContext();
        Assert.Equal(1, await check.StagedArtifacts.CountAsync());
        Assert.Equal(audits + 1, await check.AuditEvents.CountAsync());
    }

    [SqlServerFact]
    public async Task Probe_job_and_nonactive_lease_cannot_attest_staged_content()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        // Deliberate corruption bypasses the EF immutable-intent guard only in this fixture.
        await using (var db = sql.NewContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [EnvironmentJobs] SET [Kind] = 'ControlPlaneProbe' WHERE [Id] = {seed.Order.JobId}");
        await ConflictAsync(sql, seed, seed.Report);
        await using (var db = sql.NewContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [EnvironmentJobs] SET [Kind] = 'ProvisionArchive' WHERE [Id] = {seed.Order.JobId}");
            await db.AgentLeases.Where(x => x.JobId == seed.Order.JobId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "NeedsAttention"));
        }
        await ConflictAsync(sql, seed, seed.Report);
        await using var check = sql.NewContext();
        Assert.Empty(await check.StagedArtifacts.ToListAsync());
    }

    [SqlServerFact]
    public async Task Snapshot_and_exact_agent_scope_bindings_are_checked_before_receipt_insert()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var seed = await PrepareAsync(sql);
        string original;
        await using (var db = sql.NewContext()) original = (await db.EnvironmentJobs.SingleAsync()).SnapshotJson;
        foreach (var key in new[] { "reservationId", "poolId", "archiveVersionId", "sourceRootId", "sourceArchiveId", "AgentScopeId", "Sha256", "SizeBytes", "Format", "stagingBytes" })
        {
            var snapshot = JsonNode.Parse(original)!.AsObject();
            snapshot[key] = key switch
            {
                "Sha256" => JsonValue.Create(new string('B', 64)),
                "Format" => JsonValue.Create("bak"),
                "SizeBytes" => JsonValue.Create(11),
                "stagingBytes" => JsonValue.Create(9),
                _ => JsonValue.Create(Guid.NewGuid())
            };
            var changed = snapshot.ToJsonString();
            // Intent snapshots are immutable in production; SQL here deliberately simulates corrupt durable input.
            await using (var db = sql.NewContext())
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [EnvironmentJobs] SET [SnapshotJson] = {changed} WHERE [Id] = {seed.Order.JobId}");
            await ConflictAsync(sql, seed, seed.Report);
        }
        await using (var db = sql.NewContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [EnvironmentJobs] SET [SnapshotJson] = {original} WHERE [Id] = {seed.Order.JobId}");
            await db.ArchiveRoots.Where(x => x.Id == seed.Root)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.AgentScopeId, Guid.NewGuid()));
        }
        await ConflictAsync(sql, seed, seed.Report);
        await using (var db = sql.NewContext())
        {
            await db.ArchiveRoots.Where(x => x.Id == seed.Root)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.AgentScopeId, seed.Credential.AgentId));
            await db.EnvironmentPools.Where(x => x.Id == seed.Pool)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.AgentScopeId, Guid.NewGuid()));
        }
        await ConflictAsync(sql, seed, seed.Report);
        await using var check = sql.NewContext();
        Assert.Empty(await check.StagedArtifacts.ToListAsync());
        Assert.Equal("Held", (await check.EnvironmentReservations.SingleAsync()).State);
    }
}
