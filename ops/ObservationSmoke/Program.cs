using System.Security.Cryptography;
using System.Text.Json;
using BuildSystem.Agent;
using BuildSystem.Agent.Archives;
using BuildSystem.Application;
using BuildSystem.Contracts;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// Owner-authorized real-byte staging and NONEXECUTABLE correlation only. No executor is instantiated.
// Fixture assertions below are NOT production isolation, resource-limit or scheduled-job proof.
try { await Ops.Run(args); return 0; }
catch { Console.Error.WriteLine("OpsFailure"); return 5; } // Never print exception/inner/provider/credential bodies.

static class Ops
{
    const string Home = @"E:\BuildSystem\ObservationSmoke-20261104";
    const string Original = @"E:\BuildSystem\OneC\Smoke\ac40a41e29f04b64982add83b5461cc3\source.dt";
    const string Hash = "DF76D7E118E018C93149B71CD18B79947C20B0C389640EA48D361041B30CB0D3";
    const string Policy = "observation-only-no-target-execution";
    const string Name = "Observation stage-only — NO TARGET EXECUTION";
    const long Size = 16687989, GiB = 1073741824, MiB = 1048576;
    static string ManifestPath => Home + @"\Ops\manifest.json";
    static string TokenPath => Home + @"\Data\agent-token.dpapi";
    static readonly Guid Cluster = Guid.Parse("3ca4be26-0f43-4910-956e-b43c6aebdaa4");
    private static void RequireOwnerOptIn()
    {
        if (Environment.GetEnvironmentVariable("BUILDSYSTEM_OBSERVATION_SMOKE_APPROVED") != "20261104-stage-only")
            throw new InvalidOperationException("ExplicitOwnerOptInRequired");
    }
    public static async Task Run(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 1 || args[0] is not ("prepare" or "stage" or "intent" or "inspect" or "credentials" or "tls" or "diagnose")) throw new InvalidOperationException();
        // Explicit local opt-in only after independent review and passing CI; never service startup.
        RequireOwnerOptIn();
        // Parent provisions a CLOSED Admin/SYSTEM/deployment-identity ACL before any directory/file creation.
        if (!Directory.Exists(Home) || (File.GetAttributes(Home) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException();
        using var gate = new FileStream(Home + @"\operation.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var cfg = new ConfigurationBuilder().AddJsonFile(@"E:\BuildSystem\Api\releases\9cd2901\appsettings.Production.json").Build();
        var registrations = new ServiceCollection().AddBuildSystemInfrastructure(cfg.GetConnectionString("BuildSystem") ?? throw new InvalidOperationException());
        // Deployment-local options only: never modifies API host configuration.
        registrations.AddSingleton(new ArchiveStageOptions { Enabled = true });
        await using var initial = registrations.BuildServiceProvider();
        await using var scope = initial.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BuildSystemDbContext>();
        var admins = await (from u in db.Users join ur in db.UserRoles on u.Id equals ur.UserId join r in db.Roles on ur.RoleId equals r.Id where r.NormalizedName == "ADMINISTRATOR" select u.Id).Distinct().ToListAsync();
        if (admins.Count != 1) throw new InvalidOperationException();
        var actor = new Actor(admins[0], true);
        Manifest m;
        if (File.Exists(ManifestPath)) m = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(ManifestPath)) ?? throw new InvalidOperationException();
        else
        {
            if (args[0] != "prepare") throw new InvalidOperationException();
            m = new() { OwnerId = actor.UserId };
            Directory.CreateDirectory(Home + @"\Ops");
            Save(m, false); // Durable independent identities BEFORE any metadata mutation.
        }
        if (m.OwnerId != actor.UserId || m.ClusterId != Cluster || m.SqlServerId == m.ClusterId) throw new InvalidOperationException();
        registrations.AddSingleton(new RestoreIntentOptions { Enabled = true, CanonicalMappings = new() { [m.PoolId] = new(m.ClusterId, m.SqlServerId) } });
        registrations.AddSingleton(new TargetObservationOptions { Enabled = true, EndpointMappings = new() { [m.PoolId] = new(m.RacMappingId, m.SqlMappingId) } });
        await using var provider = registrations.BuildServiceProvider();
        if (args[0] == "diagnose")
        {
            using var journal = new AgentJournal(Home + @"\Data");
            var id = m.Observation!.ObservationRequestId;
            Console.WriteLine(JsonSerializer.Serialize(new { AssignmentRetained = journal.GetTargetObservationAssignment(id) is not null,
                ReportRetained = journal.GetTargetObservationReport(id) is not null,
                PendingReport = journal.GetPendingTargetObservationReport(id) is not null,
                ReceiptId = journal.GetTargetObservationReport(id)?.ReceiptId,
                Outcome = journal.GetTargetObservationReport(id)?.Outcome.ToString(),
                UnsettledStage = journal.Unsettled().Count }));
            using var transport = new TargetObservationHttpClient(new() { BaseUri = new("https://sk-cicd-1c-01.erkapharm.ru"), AgentId = m.AgentId }, () => TokenStore.Load(TokenPath));
            try { var assignment = await transport.ReadAsync(id, default); Console.WriteLine("ProductionTransportAssignmentReadOk=" + (assignment == m.Observation)); }
            catch (TargetObservationTransportException) { Console.WriteLine("ProductionTransportAssignmentReadFailed"); }
            using var directHandler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false };
            using var directHttp = new HttpClient(directHandler) { Timeout = TimeSpan.FromSeconds(20) };
            directHttp.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TokenStore.Load(TokenPath));
            try { using var response = await directHttp.GetAsync("https://sk-cicd-1c-01.erkapharm.ru/api/agents/target-observations/" + id);
                Console.WriteLine("DefaultDnsDirectHttp=" + (int)response.StatusCode); }
            catch (Exception ex) { Console.WriteLine("DefaultDnsDirectFailureType=" + ex.GetBaseException().GetType().Name); }
            Console.WriteLine(JsonSerializer.Serialize(await db.TargetObservationReceipts.AsNoTracking().Where(r => r.ObservationRequestId == id)
                .Select(r => new { r.ReceiptId, r.Outcome, r.Executable, r.ScheduledJobsMustRemainBlocked, r.ObservedAt, r.AcceptedAt }).ToListAsync()));
            Console.WriteLine("RecordedAuditCount=" + await db.AuditEvents.CountAsync(a => a.Action == "TargetObservationRecorded" && db.TargetObservationReceipts.Any(r => r.ObservationRequestId == id && r.ReceiptId == a.TargetId)));
            Console.WriteLine(JsonSerializer.Serialize(await db.TestEnvironments.AsNoTracking().Where(e => e.Id == m.Order!.EnvironmentId).Select(e => new { e.State, e.HasUnsettledProvisioningOperation }).SingleAsync()));
            // No executor, coordinator or report delivery is instantiated in this mode.
        }
        else if (args[0] == "tls")
        {
            // Exact IPv4 connect pin avoids the machine's ::1 DNS entry without changing hosts/DNS,
            // while normal URI/SNI/certificate-chain validation remains fully enabled.
            using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false, ConnectCallback = async (_, ct) =>
            {
                var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                try { await socket.ConnectAsync("10.21.0.149", 443, ct); return new System.Net.Sockets.NetworkStream(socket, true); }
                catch { socket.Dispose(); throw; }
            }};
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            try {
                var result = await http.GetAsync("https://sk-cicd-1c-01.erkapharm.ru/api/auth/me"); Console.WriteLine("PinnedVerifiedTlsHttp" + (int)result.StatusCode);
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TokenStore.Load(TokenPath));
                var assignmentResponse = await http.GetAsync("https://sk-cicd-1c-01.erkapharm.ru/api/agents/target-observations/" + m.Observation!.ObservationRequestId);
                Console.WriteLine("AssignmentGetHttp" + (int)assignmentResponse.StatusCode);
            }
            catch (Exception ex) { Console.WriteLine("VerifiedTlsProbeFailed:" + ex.GetBaseException().GetType().Name); }
        }
        else if (args[0] == "credentials")
        {
            await VerifyGraph(db, m);
            var cipher = await File.ReadAllBytesAsync(@"E:\BuildSystem\OneC\Secrets\admin.dpapi");
            var plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.LocalMachine);
            try
            {
                using var envelope = JsonDocument.Parse(plain);
                var fields = envelope.RootElement;
                if (fields.GetProperty("ClusterId").GetGuid() != m.ClusterId) throw new InvalidOperationException();
                ObservationCredentialStore.Import(Home + @"\Data", fields.GetProperty("ClusterUser").GetString()!, fields.GetProperty("ClusterPassword").GetString()!);
                Console.WriteLine("Observation credentials imported CurrentUser; no secret output.");
            }
            finally { CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(cipher); }
        }
        else if (args[0] == "prepare")
        {
            Directory.CreateDirectory(Home + @"\Source"); Directory.CreateDirectory(Home + @"\Data");
            await VerifyBytes(Original);
            var source = Home + @"\Source\source.dt";
            if (!File.Exists(source)) File.Copy(Original, source, overwrite: false);
            await VerifyBytes(source);
            if (!await db.Projects.AnyAsync(p => p.Id == m.ProjectId))
                await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                {
                    db.ChangeTracker.Clear();
                    await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
                    var quota = await db.EnvironmentQuotas.SingleAsync(q => q.SubjectId == actor.UserId);
                    if (quota.MaxActiveEnvironments != 1 || quota.MaxStorageBytes != 16 * MiB || quota.MaxConcurrentProvisioning != 1 || quota.MaxTtlDays != 1) throw new InvalidOperationException();
                    quota.MaxActiveEnvironments = 2; quota.MaxStorageBytes = 2 * GiB; quota.MaxConcurrentProvisioning = 2;
                    db.AuditEvents.Add(new() { Id = Guid.NewGuid(), ActorId = actor.UserId, TargetId = actor.UserId, Action = "OwnerApprovedQuota_1_16MiB_C1_To_2_2GiB_C2_Ttl1", CreatedAt = DateTimeOffset.UtcNow });
                    db.Projects.Add(new() { Id = m.ProjectId, Name = Name, Enabled = true, IsolationPolicyId = Policy, PreparationProfileVersion = "observation-stage-only-v1" });
                    db.ProjectMemberships.Add(new() { ProjectId = m.ProjectId, UserId = actor.UserId });
                    db.ArchiveRoots.Add(new() { Id = m.RootId, AgentScopeId = m.AgentId, Enabled = true, IsolationPolicyId = Policy, MaxArchiveBytes = 32 * MiB, MaxStagingBytes = 64 * MiB });
                    db.ArchiveRootProjects.Add(new() { RootId = m.RootId, ProjectId = m.ProjectId });
                    db.EnvironmentPools.Add(new() { Id = m.PoolId, AgentScopeId = m.AgentId, Enabled = true, ClusterId = m.ClusterId, SqlServerId = m.SqlServerId, IsolationPolicyId = Policy, CapacityBytes = 2 * GiB, MaxEnvironmentBytes = GiB, MaxStagingBytes = 64 * MiB, MaxConcurrentProvisioning = 1, SupportsDt = true, SupportsBak = false,
                        // No target readiness is asserted: explicit archive-stage-only admission only.
                        IsolationVerified = false, ScheduledJobsSuppressionVerified = false, ResourceLimitVerified = false });
                    db.ProjectPools.Add(new() { ProjectId = m.ProjectId, PoolId = m.PoolId, Enabled = true });
                    db.DiskArchives.Add(new() { Id = m.ArchiveId, RootId = m.RootId, RelativePath = "source.dt" });
                    // Ready is admission metadata for this mandatory-no-executor fixture, NOT format/restore validation.
                    db.DiskArchiveVersions.Add(new() { Id = m.VersionId, ArchiveId = m.ArchiveId, Sha256 = Hash, SizeBytes = Size, Format = "dt", State = "Ready", EstimatedDbBytes = GiB });
                    db.AuditEvents.Add(new() { Id = Guid.NewGuid(), ActorId = actor.UserId, TargetId = m.ProjectId, Action = "OwnerApprovedRealBytesStageOnlySeed_NoExecutor_NotProductionProof", CreatedAt = DateTimeOffset.UtcNow });
                    await db.SaveChangesAsync(); await tx.CommitAsync();
                });
            await VerifyGraph(db, m);
            var control = scope.ServiceProvider.GetRequiredService<IAgentControl>();
            if (!await db.Agents.AnyAsync(a => a.Id == m.AgentId))
            {
                if (File.Exists(TokenPath)) throw new InvalidOperationException();
                var ticket = await control.CreateEnrollmentAsync(actor, new(m.AgentId, "ObservationStageOnly", m.PoolId), default);
                await control.RegisterAsync(new(m.AgentId, ticket.EnrollmentId, ticket.Token, "ObservationStageOnly"), default);
                var credential = await control.ApproveAsync(actor, m.AgentId, default);
                TokenStore.Import(TokenPath, credential.Token); // CurrentUser DPAPI, NO plaintext handoff.
            }
            var token = TokenStore.Load(TokenPath);
            if (await control.AuthenticateAsync(token, default) != m.AgentId) throw new InvalidOperationException();
            await control.SetCapabilitiesAsync(m.AgentId, new AgentCapabilities([]), default);
            await scope.ServiceProvider.GetRequiredService<IArchiveCatalog>().ReportAsync(token, new(m.RootId, [new("source.dt", Hash, Size, "dt")]), default);
            Console.WriteLine("Prepared. Stage-only fixture assertions are not production proof; no executor.");
        }
        else if (args[0] == "stage")
        {
            await VerifyGraph(db, m); await VerifyBytes(Home + @"\Source\source.dt");
            await using var orderScope = provider.CreateAsyncScope();
            var order = await orderScope.ServiceProvider.GetRequiredService<IEnvironmentOrders>().CreateAsync(actor, m.OrderKey, new(m.ProjectId, Name, "archive-stage-only", new(m.VersionId), 1), default);
            m.Order = order;
            Save(m);
            var adapter = new ServerAdapters(provider, TokenStore.Load(TokenPath));
            using var journal = new AgentJournal(Home + @"\Data");
            var staging = new ArchiveStaging([new BuildSystem.Agent.Archives.ArchiveRoot(m.RootId, Home + @"\Source", true)], Home + @"\Staging", 32 * MiB, 64 * MiB);
            var worker = new StageWorker(journal, adapter, new StageCoordinator(journal, staging, adapter.HeartbeatStageAsync), new ArtifactDelivery(journal, adapter), new StageCompletionDelivery(journal, adapter));
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            for (var i = 0; i < 8; i++)
            {
                await worker.WorkOnce(deadline.Token); // Pump serialized; only filesystem copy overlaps lease guards.
                db.ChangeTracker.Clear();
                if (await db.EnvironmentJobs.AnyAsync(j => j.Id == order.JobId && j.Kind == "ObserveArchiveStage" && j.State == "StageCompleted") &&
                    journal.Unsettled().Count == 0 && journal.PendingArtifacts().Count == 0 && journal.PendingCompletions().Count == 0)
                {
                    m.ArtifactId = await db.StagedArtifacts.Where(a => a.JobId == order.JobId && a.AgentId == m.AgentId && a.ArchiveVersionId == m.VersionId).Select(a => a.ArtifactId).SingleAsync();
                    Save(m); Console.WriteLine("StageCompleted; no target execution."); return;
                }
            }
            throw new InvalidOperationException(); // No eligibility/quota bypass, retries are bounded.
        }
        else if (args[0] == "intent")
        {
            await VerifyGraph(db, m);
            if (m.Order is null || m.ArtifactId is null) throw new InvalidOperationException();
            await using var intentScope = provider.CreateAsyncScope();
            m.RestoreIntent = await intentScope.ServiceProvider.GetRequiredService<IRestoreIntents>().RequestAsync(actor, m.Order.EnvironmentId, new(m.Order.JobId, m.ArtifactId.Value), default);
            Save(m);
            m.Observation = await intentScope.ServiceProvider.GetRequiredService<ITargetObservations>().RequestAsync(actor, m.Order.EnvironmentId, m.RestoreIntent.RestoreIntentId, default);
            Save(m); Console.WriteLine("Nonexecutable restore intent and observation assignment retained; NO target commands.");
        }
        else
        {
            Console.WriteLine(JsonSerializer.Serialize(m)); // Allowlisted metadata only; never config, credentials or snapshots.
            Console.WriteLine(JsonSerializer.Serialize(await db.TestEnvironments.AsNoTracking().Where(e => e.ProjectId == m.ProjectId).Select(e => new { e.Id, e.State, e.HasUnsettledProvisioningOperation }).ToListAsync()));
            if (m.Order is { } order) Console.WriteLine(JsonSerializer.Serialize(await db.EnvironmentJobs.AsNoTracking().Where(j => j.Id == order.JobId).Select(j => new { j.Id, j.Kind, j.State }).ToListAsync()));
        }
    }
    static async Task VerifyBytes(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length != Size || Convert.ToHexString(await SHA256.HashDataAsync(file)) != Hash) throw new InvalidOperationException();
    }
    static void Save(Manifest m, bool overwrite = true)
    {
        var temp = ManifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(stream, m); stream.Flush(true); }
        File.Move(temp, ManifestPath, overwrite);
    }
    static async Task VerifyGraph(BuildSystemDbContext db, Manifest m)
    {
        var p = await db.Projects.AsNoTracking().SingleAsync(p => p.Id == m.ProjectId);
        var root = await db.ArchiveRoots.AsNoTracking().SingleAsync(r => r.Id == m.RootId);
        var pool = await db.EnvironmentPools.AsNoTracking().SingleAsync(p => p.Id == m.PoolId);
        var a = await db.DiskArchives.AsNoTracking().SingleAsync(a => a.Id == m.ArchiveId);
        var v = await db.DiskArchiveVersions.AsNoTracking().SingleAsync(v => v.Id == m.VersionId);
        var q = await db.EnvironmentQuotas.AsNoTracking().SingleAsync(q => q.SubjectId == m.OwnerId);
        if (!p.Enabled || p.Name != Name || p.IsolationPolicyId != Policy || p.PreparationProfileVersion != "observation-stage-only-v1" || !root.Enabled || root.AgentScopeId != m.AgentId || root.IsolationPolicyId != Policy || root.MaxArchiveBytes != 32 * MiB || root.MaxStagingBytes != 64 * MiB || !pool.Enabled || pool.AgentScopeId != m.AgentId || pool.IsolationPolicyId != Policy || pool.ClusterId != m.ClusterId || pool.SqlServerId != m.SqlServerId || pool.CapacityBytes != 2 * GiB || pool.MaxEnvironmentBytes != GiB || pool.MaxStagingBytes != 64 * MiB || pool.MaxConcurrentProvisioning != 1 || !pool.SupportsDt || pool.SupportsBak || pool.IsolationVerified || pool.ResourceLimitVerified || pool.ScheduledJobsSuppressionVerified || a.RootId != m.RootId || a.RelativePath != "source.dt" || v.ArchiveId != m.ArchiveId || v.Sha256 != Hash || v.SizeBytes != Size || v.Format != "dt" || v.State != "Ready" || v.EstimatedDbBytes != GiB || q.MaxActiveEnvironments != 2 || q.MaxStorageBytes != 2 * GiB || q.MaxConcurrentProvisioning != 2 || q.MaxTtlDays != 1) throw new InvalidOperationException();
        if (!await db.ProjectMemberships.AnyAsync(x => x.ProjectId == m.ProjectId && x.UserId == m.OwnerId) || !await db.ArchiveRootProjects.AnyAsync(x => x.RootId == m.RootId && x.ProjectId == m.ProjectId) || !await db.ProjectPools.AnyAsync(x => x.ProjectId == m.ProjectId && x.PoolId == m.PoolId && x.Enabled)) throw new InvalidOperationException();
    }
    sealed class Manifest
    {
        public Guid OwnerId { get; init; }
        public Guid ProjectId { get; init; } = Guid.NewGuid(); public Guid RootId { get; init; } = Guid.NewGuid();
        public Guid PoolId { get; init; } = Guid.NewGuid(); public Guid AgentId { get; init; } = Guid.NewGuid();
        public Guid ArchiveId { get; init; } = Guid.NewGuid(); public Guid VersionId { get; init; } = Guid.NewGuid();
        public Guid ClusterId { get; init; } = Cluster; public Guid SqlServerId { get; init; } = Guid.NewGuid();
        public Guid RacMappingId { get; init; } = Guid.NewGuid(); public Guid SqlMappingId { get; init; } = Guid.NewGuid();
        public string OrderKey { get; init; } = "observation-stage-" + Guid.NewGuid().ToString("N");
        public OrderReceipt? Order { get; set; } public Guid? ArtifactId { get; set; }
        public RestoreIntentReceipt? RestoreIntent { get; set; } public TargetObservationAssignment? Observation { get; set; }
    }
}

// A fresh provider scope per awaited server operation: no concurrent/shared server DbContext.
sealed class ServerAdapters(ServiceProvider provider, string credential) : IStageTransport, IArtifactTransport, IStageCompletionTransport
{
    public async Task<StageLeaseGrant?> PollStageAsync(CancellationToken ct)
    { await using var s = provider.CreateAsyncScope(); return await s.ServiceProvider.GetRequiredService<IArchiveStageControl>().PollAsync(credential, ct); }
    public async Task<LeaseStatus> HeartbeatStageAsync(LeaseHeartbeat heartbeat, CancellationToken ct)
    { await using var s = provider.CreateAsyncScope(); return await s.ServiceProvider.GetRequiredService<IArchiveStageControl>().HeartbeatAsync(credential, heartbeat, ct); }
    public async Task<StagedArtifactReceipt> ReportArtifactAsync(ReportStagedArtifact report, CancellationToken ct)
    { await using var s = provider.CreateAsyncScope(); return await s.ServiceProvider.GetRequiredService<IArtifactReceipts>().ReportAsync(credential, report, ct); }
    public async Task<StageCompletionReceipt> CompleteStageAsync(CompleteStage report, CancellationToken ct)
    { await using var s = provider.CreateAsyncScope(); return await s.ServiceProvider.GetRequiredService<IStageCompletion>().CompleteAsync(credential, report, ct); }
}
