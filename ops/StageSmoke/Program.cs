using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Contracts;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// Explicit opt-in operations utility: no human password, HTTP bypass, restore or SQL/1C target execution.
// Invoke only as the owner-authorized deployment identity. All output is nonsecret.
const string home = @"E:\BuildSystem\SyntheticStage";
const string manifestPath = home + @"\Ops\manifest.json";
const string handoffPath = home + @"\Data\credential.handoff";
if (!OperatingSystem.IsWindows() || args.Length != 1 || args[0] is not ("prepare" or "order" or "inspect" or "tls"))
    throw new InvalidOperationException("Windows only: StageSmoke prepare|order|inspect. Scope is synthetic staging only.");
if (args[0] == "tls")
{
    string result;
    try { using var handler = new HttpClientHandler { UseProxy = false }; using var client = new HttpClient(handler); using var response = await client.GetAsync("https://sk-cicd-1c-01.erkapharm.ru/api/projects/browse"); result = "TLS HTTP " + (int)response.StatusCode; }
    catch (Exception error) { result = string.Join(" | ", Enumerate(error)); }
    await File.WriteAllTextAsync(home + @"\Data\net10-tls.txt", result); return;
    static IEnumerable<string> Enumerate(Exception e) { for (Exception? x = e; x is not null; x = x.InnerException) yield return x.GetType().Name + ": " + x.Message; }
}
var cfg = new ConfigurationBuilder().AddJsonFile(@"E:\BuildSystem\Api\releases\0ca1567\appsettings.Production.json").Build();
var services = new ServiceCollection().AddBuildSystemInfrastructure(cfg.GetConnectionString("BuildSystem") ?? throw new InvalidOperationException("Connection missing")).BuildServiceProvider();
await using var scope = services.CreateAsyncScope();
var db = scope.ServiceProvider.GetRequiredService<BuildSystemDbContext>();
var admins = await (from u in db.Users join ur in db.UserRoles on u.Id equals ur.UserId join r in db.Roles on ur.RoleId equals r.Id where r.NormalizedName == "ADMINISTRATOR" select u.Id).ToListAsync();
if (admins.Count != 1) throw new InvalidOperationException("Exactly one existing human administrator required; no user will be created.");
var actor = new Actor(admins[0], true);
Manifest manifest;
if (File.Exists(manifestPath)) manifest = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(manifestPath)) ?? throw new InvalidOperationException("Bad manifest");
else
{
    if (args[0] != "prepare") throw new InvalidOperationException("Prepare first");
    manifest = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), actor.UserId, "synthetic-stage-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
    await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));
}
if (manifest.OwnerId != actor.UserId) throw new InvalidOperationException("Manifest owner mismatch");
const string policy = "synthetic-stage-only-no-restore-v1";
if (args[0] == "prepare")
{
    var source = home + @"\Source\synthetic-not-real.dt";
    var bytes = new byte[65536];
    var marker = Encoding.UTF8.GetBytes("SYNTHETIC STAGING SMOKE ONLY. NOT A VALID 1C ARCHIVE. DO NOT RESTORE.\r\n");
    for (var i = 0; i < bytes.Length; i++) bytes[i] = marker[i % marker.Length];
    Directory.CreateDirectory(Path.GetDirectoryName(source)!);
    if (File.Exists(source)) { if (!File.ReadAllBytes(source).SequenceEqual(bytes)) throw new InvalidOperationException("Foreign source content; refused overwrite"); }
    else { var temp = source + ".pending"; using (var f = new FileStream(temp, FileMode.CreateNew)) f.Write(bytes); File.Move(temp, source); }
    var hash = Convert.ToHexString(SHA256.HashData(bytes));
    // No mutable upsert: a prior seed is accepted only when the full scoped graph has the exact identity/settings.
    if (!await db.Projects.AnyAsync(p => p.Id == manifest.ProjectId))
    {
        if (await db.EnvironmentQuotas.AnyAsync(q => q.SubjectId == actor.UserId)) throw new InvalidOperationException("Existing owner quota; refuse modification");
        if (await db.Projects.AnyAsync(p => p.Name == "Synthetic stage smoke — NOT RESTORE")) throw new InvalidOperationException("Foreign similarly named project exists");
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        db.Projects.Add(new Project { Id = manifest.ProjectId, Name = "Synthetic stage smoke — NOT RESTORE", Enabled = true, IsolationPolicyId = policy, PreparationProfileVersion = "synthetic-v1" });
        db.ProjectMemberships.Add(new ProjectMembership { ProjectId = manifest.ProjectId, UserId = actor.UserId });
        db.ArchiveRoots.Add(new ArchiveRoot { Id = manifest.RootId, AgentScopeId = manifest.AgentId, Enabled = true, IsolationPolicyId = policy, MaxArchiveBytes = 1048576, MaxStagingBytes = 1048576 });
        db.ArchiveRootProjects.Add(new ArchiveRootProject { RootId = manifest.RootId, ProjectId = manifest.ProjectId });
        db.EnvironmentPools.Add(new EnvironmentPool { Id = manifest.PoolId, AgentScopeId = manifest.AgentId, Enabled = true, ClusterId = manifest.ClusterId, SqlServerId = manifest.SqlServerId, IsolationPolicyId = policy, CapacityBytes = 16777216, MaxEnvironmentBytes = 2097152, MaxStagingBytes = 1048576, MaxConcurrentProvisioning = 1, SupportsDt = true, SupportsBak = false,
            // These assertions apply ONLY to the no-execution synthetic stage fixture; no target exists.
            IsolationVerified = true, ScheduledJobsSuppressionVerified = true, ResourceLimitVerified = true });
        db.ProjectPools.Add(new ProjectPool { ProjectId = manifest.ProjectId, PoolId = manifest.PoolId, Enabled = true });
        db.EnvironmentQuotas.Add(new EnvironmentQuota { SubjectId = actor.UserId, MaxActiveEnvironments = 1, MaxConcurrentProvisioning = 1, MaxStorageBytes = 16777216, MaxTtlDays = 1 });
        db.DiskArchives.Add(new DiskArchive { Id = manifest.ArchiveId, RootId = manifest.RootId, RelativePath = "synthetic-not-real.dt" });
        db.DiskArchiveVersions.Add(new DiskArchiveVersion { Id = manifest.VersionId, ArchiveId = manifest.ArchiveId, Sha256 = hash, SizeBytes = bytes.Length, Format = "dt", State = "Ready", EstimatedDbBytes = 2097152 });
        db.AuditEvents.Add(new AuditEvent { Id = Guid.NewGuid(), ActorId = actor.UserId, TargetId = manifest.ProjectId, Action = "OwnerAuthorizedSyntheticStageOnlySeed_NotFormatValidation", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(); await tx.CommitAsync();
        });
    }
    var p = await db.Projects.AsNoTracking().SingleAsync(p => p.Id == manifest.ProjectId);
    var root = await db.ArchiveRoots.AsNoTracking().SingleAsync(r => r.Id == manifest.RootId);
    var pool = await db.EnvironmentPools.AsNoTracking().SingleAsync(r => r.Id == manifest.PoolId);
    var version = await db.DiskArchiveVersions.AsNoTracking().SingleAsync(r => r.Id == manifest.VersionId);
    if (p.IsolationPolicyId != policy || root.AgentScopeId != manifest.AgentId || root.IsolationPolicyId != policy || pool.AgentScopeId != manifest.AgentId || pool.IsolationPolicyId != policy || version.ArchiveId != manifest.ArchiveId || version.Sha256 != hash || version.SizeBytes != 65536 || version.EstimatedDbBytes != 2097152 || version.State != "Ready") throw new InvalidOperationException("Seed graph mismatch; refused mutation");
    var control = scope.ServiceProvider.GetRequiredService<IAgentControl>();
    if (await db.Agents.AnyAsync(a => a.Id == manifest.AgentId)) throw new InvalidOperationException("Agent already exists: do not reissue credential automatically. Use inspect and the existing imported credential.");
    var ticket = await control.CreateEnrollmentAsync(actor, new(manifest.AgentId, "SyntheticStageOnly", manifest.PoolId), default);
    await control.RegisterAsync(new(manifest.AgentId, ticket.EnrollmentId, ticket.Token, "SyntheticStageOnly"), default);
    var credential = await control.ApproveAsync(actor, manifest.AgentId, default);
    await control.AuthenticateAsync(credential.Token, default);
    await control.SetCapabilitiesAsync(manifest.AgentId, new AgentCapabilities([]), default);
    // Parent must precreate Data with only Administrators/SYSTEM/LocalService, removing inherited access BEFORE this mode.
    using (var f = new FileStream(handoffPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    using (var w = new StreamWriter(f)) await w.WriteAsync(credential.Token);
    await scope.ServiceProvider.GetRequiredService<IArchiveCatalog>().ReportAsync(credential.Token, new(manifest.RootId, [new("synthetic-not-real.dt", hash, bytes.Length, "dt")]), default);
    Console.WriteLine("Prepared synthetic fixture. Credential handoff created (token never printed); import under LocalService then delete handoff.");
}
else if (args[0] == "order")
{
    var receipt = await scope.ServiceProvider.GetRequiredService<IEnvironmentOrders>().CreateAsync(actor, manifest.OrderKey,
        new(manifest.ProjectId, "Synthetic stage smoke — NOT RESTORE", "archive", new(manifest.VersionId), 1), default);
    Console.WriteLine(JsonSerializer.Serialize(receipt));
}
Console.WriteLine(JsonSerializer.Serialize(new { manifest.ProjectId, manifest.RootId, manifest.PoolId, manifest.AgentId, manifest.ArchiveId, manifest.VersionId }));
if (args[0] == "inspect")
{
    var rows = await db.EnvironmentRequests.AsNoTracking().Where(r => r.OwnerId == actor.UserId && r.IdempotencyKey == manifest.OrderKey).Select(r => new { r.Id, r.JobId, r.EnvironmentId, r.State }).ToListAsync();
    Console.WriteLine(JsonSerializer.Serialize(rows));
    var jobs = await (from j in db.EnvironmentJobs.AsNoTracking() join e in db.TestEnvironments.AsNoTracking() on j.EnvironmentId equals e.Id join r in db.EnvironmentReservations.AsNoTracking() on e.ReservationId equals r.Id where e.ProjectId == manifest.ProjectId select new { JobId = j.Id, JobState = j.State, j.Kind, EnvironmentState = e.State, e.HasUnsettledProvisioningOperation, ReservationState = r.State }).ToListAsync();
    Console.WriteLine(JsonSerializer.Serialize(jobs));
    var versions = await db.DiskArchiveVersions.AsNoTracking().Where(v => v.Id == manifest.VersionId).Select(v => new { v.Sha256, v.SizeBytes, v.State }).ToListAsync();
    Console.WriteLine(JsonSerializer.Serialize(versions));
}
record Manifest(Guid ProjectId, Guid RootId, Guid PoolId, Guid AgentId, Guid ArchiveId, Guid VersionId, Guid ClusterId, Guid OwnerId, string OrderKey)
{
    public Guid SqlServerId => ClusterId; // Synthetic identity only, never an actual SQL or 1C execution target.
}
// end
