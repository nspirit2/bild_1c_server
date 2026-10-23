using System.Data;
using BuildSystem.Application;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

public sealed class SqlArchiveCatalog(BuildSystemDbContext db) : IArchiveCatalog
{
    public async Task<ArchiveInventoryReceipt> ReportAsync(string credential, ArchiveInventoryReport report, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(credential) || credential.Length > 256) throw Reject(401, "AgentUnauthorized");
        if (report is null || report.RootId == Guid.Empty || report.Entries is null || report.Entries.Length > 100)
            throw Reject(400, "InvalidArchiveInventory");
        var entries = report.Entries.Select(Validate).ToArray();
        if (entries.Select(x => x.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Length)
            throw Reject(400, "DuplicateArchivePath");
        var hash = TokenProtector.Hash(credential);
        // The configured SQL execution strategy retries the entire serializable transaction, bounded to three retries.
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                // Same lock as approve/rotate/revoke: an earlier HTTP authentication is never authoritative here.
                await LockAsync("BuildSystem:agent-control", ct);
                var agent = await db.Agents.SingleOrDefaultAsync(x => x.TokenHash == hash && x.State == "Approved", ct);
                if (agent is null || !TokenProtector.Verify(credential, agent.TokenHash!)) throw Reject(401, "AgentUnauthorized");
                await LockAsync("BuildSystem:root:" + report.RootId.ToString("N"), ct);
                var root = await db.ArchiveRoots.SingleOrDefaultAsync(x => x.Id == report.RootId, ct);
                if (root is null || !root.Enabled || root.AgentScopeId != agent.Id) throw Reject(403, "ArchiveRootForbidden");
                if (!await db.EnvironmentPools.AnyAsync(x => x.Id == agent.PoolId && x.Enabled, ct)) throw Reject(409, "PoolUnavailable");
                var receipts = new List<ArchiveInventoryItemReceipt>();
                foreach (var entry in entries)
                {
                    if (entry.SizeBytes > root.MaxArchiveBytes) throw Reject(400, "ArchiveTooLarge");
                    var archive = await db.DiskArchives.SingleOrDefaultAsync(x => x.RootId == root.Id && x.RelativePath == entry.RelativePath, ct);
                    if (archive is not null && !string.Equals(archive.RelativePath, entry.RelativePath, StringComparison.OrdinalIgnoreCase))
                        throw Reject(409, "ArchivePathConflict");
                    if (archive is null)
                    {
                        archive = new DiskArchive { Id = Guid.NewGuid(), RootId = root.Id, RelativePath = entry.RelativePath };
                        db.DiskArchives.Add(archive);
                    }
                    var versions = await db.DiskArchiveVersions.Where(x => x.ArchiveId == archive.Id).ToListAsync(ct);
                    var version = versions.SingleOrDefault(x => string.Equals(x.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase));
                    foreach (var prior in versions.Where(x => x != version)) prior.State = "Unavailable";
                    if (version is not null)
                    {
                        if (version.SizeBytes != entry.SizeBytes || version.Format != entry.Format) throw Reject(409, "ArchiveFingerprintConflict");
                        // Replay never reactivates Unavailable or downgrades an independently validated Ready version.
                    }
                    else
                    {
                        version = new DiskArchiveVersion { Id = Guid.NewGuid(), ArchiveId = archive.Id, Sha256 = entry.Sha256,
                            SizeBytes = entry.SizeBytes, Format = entry.Format, State = "Discovered", EstimatedDbBytes = 0 };
                        db.DiskArchiveVersions.Add(version);
                    }
                    receipts.Add(new(archive.Id, version.Id, version.State));
                }
                var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(ct);
                db.AuditEvents.Add(new AuditEvent { Id = Guid.NewGuid(), ActorId = agent.Id, Action = "ArchiveInventoryReported", TargetId = root.Id, CreatedAt = now });
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return new ArchiveInventoryReceipt(root.Id, receipts.ToArray());
            }
            catch
            {
                // Disposal rolls back ALL changes, including prior entries. Never retain failed tracked mutations on reuse/retry.
                db.ChangeTracker.Clear();
                throw;
            }
        });
    }

    private static ArchiveInventoryEntry Validate(ArchiveInventoryEntry entry)
    {
        if (entry is null || string.IsNullOrEmpty(entry.RelativePath) || entry.RelativePath.Length > 400 ||
            entry.Sha256 is null || entry.Sha256.Length != 64 || !entry.Sha256.All(Uri.IsHexDigit) || entry.SizeBytes <= 0 ||
            entry.Format is not ("dt" or "bak")) throw Reject(400, "InvalidArchiveEntry");
        var path = entry.RelativePath.Replace('\\', '/');
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment.Length > 255 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.Any(c => char.IsControl(c) || ":<>\"|?*".Contains(c))) throw Reject(400, "InvalidArchivePath");
            var stem = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" or "CONIN$" or "CONOUT$" ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                 "123456789¹²³".Contains(stem[3]))) throw Reject(400, "InvalidArchivePath");
        }
        if (!path.EndsWith("." + entry.Format, StringComparison.OrdinalIgnoreCase)) throw Reject(400, "ArchiveFormatMismatch");
        return entry with { RelativePath = path, Sha256 = entry.Sha256.ToUpperInvariant() };
    }

    private Task LockAsync(string resource, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync(
        $"DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=15000; IF @r IN (-1, -3) THROW 51001, 'Transient catalog lock contention', 1; IF @r < 0 THROW 51000, 'Catalog lock cancelled or invalid', 1;", ct);
    private static RequestRejectedException Reject(int status, string code) => new(status, code);
}
