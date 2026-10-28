using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using BuildSystem.Agent;
using BuildSystem.Agent.Archives;
using BuildSystem.Application;
using BuildSystem.Contracts;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildSystem.IntegrationTests;

// Real disposable SQL, real local files/SQLite, real AgentTransport serialization. The HTTP handler
// dispatches directly to authenticated application ports: this is not a web-host, TLS or network test.
public sealed class SqlCleanupEndToEndTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static AgentTransport Transport(string credential, HttpMessageHandler handler) =>
        (AgentTransport)Activator.CreateInstance(typeof(AgentTransport), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null, args: [new Uri("https://loopback.invalid"), credential, handler], culture: null)!;
    private sealed class Files : IDisposable
    {
        public string Home { get; } = Path.Combine(Path.GetTempPath(), "bs-cleanup-e2e", Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Home, "source");
        public string Staging => Path.Combine(Home, "staging");
        public string Journal => Path.Combine(Home, "journal");
        public byte[] Bytes { get; } = "SYNTHETIC ONLY: these are not a valid 1C archive"u8.ToArray();
        public Files() { Directory.CreateDirectory(Source); File.WriteAllBytes(Path.Combine(Source, "archive.dt"), Bytes); }
        public void Dispose()
        {
            var path = Path.GetFullPath(Home);
            var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "bs-cleanup-e2e")) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(expected, StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture cleanup path.");
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
    private sealed record Seed(Actor Admin, Actor Owner, OrderReceipt Order, StageLeaseGrant Lease,
        AgentCredential Credential, StageCleanupAuthorization Authorization, ArchiveStaging Staging);
    private static async Task<Seed> Prepare(SqlServerTestDatabase sql, Files files, AgentJournal journal)
    {
        await sql.ApplyMigrationsAsync();
        var admin = new Actor(Guid.NewGuid(), true); var owner = new Actor(Guid.NewGuid(), false);
        var project = Guid.NewGuid(); var root = Guid.NewGuid(); var pool = Guid.NewGuid();
        var archive = Guid.NewGuid(); var version = Guid.NewGuid(); var agent = Guid.NewGuid();
        var hash = Convert.ToHexString(SHA256.HashData(files.Bytes));
        await using var db = sql.NewContext();
        db.Users.AddRange(new BuildSystemUser { Id = admin.UserId, UserName = "admin", NormalizedUserName = "ADMIN" },
            new BuildSystemUser { Id = owner.UserId, UserName = "owner", NormalizedUserName = "OWNER" });
        db.Projects.Add(new Project { Id = project, Name = "cleanup-e2e", IsolationPolicyId = "isolated", PreparationProfileVersion = "v1" });
        db.ProjectMemberships.Add(new ProjectMembership { UserId = owner.UserId, ProjectId = project });
        db.ArchiveRoots.Add(new BuildSystem.Infrastructure.ArchiveRoot { Id = root, AgentScopeId = agent, Enabled = true, IsolationPolicyId = "isolated", MaxArchiveBytes = 10000, MaxStagingBytes = 10000 });
        db.ArchiveRootProjects.Add(new ArchiveRootProject { RootId = root, ProjectId = project });
        db.DiskArchives.Add(new DiskArchive { Id = archive, RootId = root, RelativePath = "archive.dt" });
        db.DiskArchiveVersions.Add(new DiskArchiveVersion { Id = version, ArchiveId = archive, Sha256 = hash, SizeBytes = files.Bytes.Length, Format = "dt", State = "Ready", EstimatedDbBytes = 100 });
        db.EnvironmentPools.Add(new EnvironmentPool { Id = pool, AgentScopeId = agent, Enabled = true,
            ClusterId = Guid.NewGuid(), SqlServerId = Guid.NewGuid(), IsolationPolicyId = "isolated", CapacityBytes = 100000,
            MaxEnvironmentBytes = 10000, MaxStagingBytes = 10000, MaxConcurrentProvisioning = 5, SupportsDt = true,
            SupportsBak = true, IsolationVerified = true, ScheduledJobsSuppressionVerified = true, ResourceLimitVerified = true });
        db.ProjectPools.Add(new ProjectPool { ProjectId = project, PoolId = pool, Enabled = true });
        db.EnvironmentQuotas.Add(new EnvironmentQuota { SubjectId = owner.UserId, MaxActiveEnvironments = 5, MaxConcurrentProvisioning = 5, MaxStorageBytes = 100000, MaxTtlDays = 30 });
        await db.SaveChangesAsync();
        var control = new SqlAgentControl(db); var ticket = await control.CreateEnrollmentAsync(admin, new(agent, "e2e-worker", pool), Ct);
        await control.RegisterAsync(new(agent, ticket.EnrollmentId, ticket.Token, "e2e-worker"), Ct);
        var credential = await control.ApproveAsync(admin, agent, Ct);
        var order = await new SqlEnvironmentOrders(db).CreateAsync(owner, "cleanup-e2e-order", new(project, "cleanup", "archive", new(version), 7), Ct);
        var lease = Assert.IsType<StageLeaseGrant>(await new SqlArchiveStageControl(db, new ArchiveStageOptions { Enabled = true }).PollAsync(credential.Token, Ct));
        var staging = new ArchiveStaging([new(root, files.Source, true)], files.Staging, 10000, 10000);
        var fingerprint = await staging.FingerprintAsync(root, "archive.dt", Ct);
        var artifact = await staging.StageAsync(new(lease.Plan.ReservationId, version, fingerprint), Ct);
        var artifactId = Guid.Parse(artifact.ArtifactId);
        journal.StoreGrant(new(lease.JobId, lease.FencingToken, lease.ExpiresAt, "ArchiveStageOnly", "{}"));
        journal.SaveStageIntent(new(lease.JobId, lease.FencingToken, lease.Plan.ReservationId, version, root, "archive.dt", hash, files.Bytes.Length, "dt"));
        var report = new ReportStagedArtifact(lease.JobId, lease.FencingToken, lease.Plan.ReservationId, version, artifactId, hash, files.Bytes.Length, "dt");
        journal.EnqueueArtifact(report);
        var artifactReceipt = await new SqlArtifactReceipts(db).ReportAsync(credential.Token, report, Ct);
        Assert.Equal(artifactId, artifactReceipt.ArtifactId); journal.AcknowledgeArtifact(artifactId);
        var complete = new CompleteStage(lease.JobId, lease.FencingToken, artifactId);
        journal.EnqueueCompletion(complete);
        var completed = await new SqlStageCompletion(db, new ArchiveStageOptions { Enabled = true }).CompleteAsync(credential.Token, complete, Ct);
        journal.AcknowledgeCompletion(completed);
        var intent = await new SqlStageRetirement(db).RequestAsync(owner, order.EnvironmentId, new(order.JobId, artifactId), Ct);
        var authorization = await new SqlStageCleanup(db, new StageCleanupOptions { Enabled = true }).ClaimAsync(credential.Token, new(intent.RetirementId, artifactId), Ct);
        journal.StoreCleanupAuthorization(authorization);
        await new StageCleanupCoordinator(journal, staging).ExecuteAsync(authorization.AuthorizationId, Ct);
        Assert.Equal("DeletedLocal", journal.GetCleanupLocalState(authorization.AuthorizationId));
        return new(admin, owner, order, lease, credential, authorization, staging);
    }
    private sealed class ResultHandler(SqlServerTestDatabase sql, string credential, bool loseFirstResponse) : HttpMessageHandler
    {
        public List<ReportStageCleanup> Reports { get; } = [];
        public List<StageCleanupResultReceipt> Receipts { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/agent/stage/cleanup-result", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(credential, request.Headers.Authorization?.Parameter);
            var input = await request.Content!.ReadFromJsonAsync<ReportStageCleanup>(ct);
            Assert.NotNull(input); Reports.Add(input);
            await using var db = sql.NewContext();
            var receipt = await new SqlStageCleanupResults(db, new StageCleanupResultOptions { Enabled = true }).ReportAsync(request.Headers.Authorization!.Parameter!, input, ct);
            Receipts.Add(receipt);
            if (loseFirstResponse && Receipts.Count == 1) throw new HttpRequestException("Simulated response loss after durable server commit.");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(receipt) };
        }
    }
    private static async Task ReleaseAndAssert(SqlServerTestDatabase sql, Files files, AgentJournal journal, Seed seed, ReportStageCleanup result)
    {
        Assert.Equal("Acked", journal.GetCleanupResultState(result.ResultId)); Assert.Empty(journal.PendingCleanupResults());
        await using var db = sql.NewContext();
        var release = new SqlStagingAllocationRelease(db, new StagingAllocationReleaseOptions { Enabled = true });
        var expected = new ReleaseStagingAllocation(seed.Authorization.AuthorizationId, seed.Lease.Plan.ReservationId);
        var first = await release.ReleaseAsync(seed.Admin, result.ResultId, expected, Ct);
        Assert.Equal(first, await release.ReleaseAsync(seed.Admin, result.ResultId, expected, Ct));
        var status = Assert.IsType<CleanupStatusView>(await new SqlCleanupStatus(db).GetAsync(seed.Owner, seed.Order.EnvironmentId, Ct));
        Assert.Equal(0, status.HeldStagingBytes); Assert.Equal(files.Bytes.Length, status.ReleasedStagingBytes);
        Assert.Equal(result.ResultId, status.ResultId); Assert.Equal(first.ReleaseId, status.ReleaseId);
        var reservation = await db.EnvironmentReservations.SingleAsync(x => x.Id == seed.Lease.Plan.ReservationId);
        Assert.Equal("Held", reservation.State); Assert.Equal(100, reservation.ReservedBytes); Assert.Equal(files.Bytes.Length, reservation.StagingBytes);
        var environment = await db.TestEnvironments.SingleAsync(x => x.Id == seed.Order.EnvironmentId);
        Assert.Equal("Requested", environment.State); Assert.True(environment.HasUnsettledProvisioningOperation);
        Assert.Single(await db.TestEnvironments.ToListAsync()); Assert.Equal("Pending", (await db.EnvironmentRequests.SingleAsync()).State);
        Assert.Single(await db.StageCleanupResults.ToListAsync()); Assert.Single(await db.StagingAllocationReleases.ToListAsync());
        Assert.Equal(1, await db.AuditEvents.CountAsync(x => x.Action == "StageCleanupDeletedReported"));
        Assert.Equal(1, await db.AuditEvents.CountAsync(x => x.Action == "StagingAllocationReleased"));
        Assert.Equal(files.Bytes, await File.ReadAllBytesAsync(Path.Combine(files.Source, "archive.dt")));
        var id = seed.Authorization.Plan.ArtifactId.ToString("N");
        Assert.False(Directory.Exists(Path.Combine(files.Staging, id)));
        Assert.True(File.Exists(Path.Combine(files.Staging, ".retired-" + id + ".json")));
        Assert.True(File.Exists(Path.Combine(files.Staging, ".deleted-" + id + ".json")));
    }
    [SqlServerFact]
    public async Task Real_local_cleanup_outbox_delivery_receipt_and_admin_release_preserve_source_and_database_quota()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); using var files = new Files(); using var journal = new AgentJournal(files.Journal);
        var seed = await Prepare(sql, files, journal); var result = Assert.Single(journal.PendingCleanupResults());
        var handler = new ResultHandler(sql, seed.Credential.Token, false);
        using var transport = Transport(seed.Credential.Token, handler);
        Assert.True(await new CleanupResultDelivery(journal, transport).DeliverOneAsync(Ct));
        Assert.False(await new CleanupResultDelivery(journal, transport).DeliverOneAsync(Ct));
        Assert.Equal(result, Assert.Single(handler.Reports));
        await ReleaseAndAssert(sql, files, journal, seed, result);
    }
    [SqlServerFact]
    public async Task Response_lost_after_server_commit_reopens_pending_same_result_then_acks_same_receipt_once()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync(); using var files = new Files();
        Seed seed; ReportStageCleanup result; var firstHandler = default(ResultHandler)!;
        using (var journal = new AgentJournal(files.Journal))
        {
            seed = await Prepare(sql, files, journal); result = Assert.Single(journal.PendingCleanupResults());
            firstHandler = new(sql, seed.Credential.Token, true);
            using var transport = Transport(seed.Credential.Token, firstHandler);
            await Assert.ThrowsAsync<HttpRequestException>(() => new CleanupResultDelivery(journal, transport).DeliverOneAsync(Ct));
            Assert.Equal("Pending", journal.GetCleanupResultState(result.ResultId)); Assert.Equal(result, Assert.Single(journal.PendingCleanupResults()));
        }
        using var reopened = new AgentJournal(files.Journal); Assert.Equal(result, Assert.Single(reopened.PendingCleanupResults()));
        var secondHandler = new ResultHandler(sql, seed.Credential.Token, false);
        using var retryTransport = Transport(seed.Credential.Token, secondHandler);
        Assert.True(await new CleanupResultDelivery(reopened, retryTransport).DeliverOneAsync(Ct));
        Assert.Equal(Assert.Single(firstHandler.Receipts), Assert.Single(secondHandler.Receipts));
        Assert.Equal(result, Assert.Single(secondHandler.Reports));
        await ReleaseAndAssert(sql, files, reopened, seed, result);
    }
}
// end
