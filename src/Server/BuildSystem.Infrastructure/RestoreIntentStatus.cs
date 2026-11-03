using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildSystem.Application;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Read within the caller's serializable observation; never resolves authority or acquires scheduler locks.</summary>
internal static class RestoreIntentStatus
{
    internal static async Task<RestoreIntentStatusView?> ReadAsync(BuildSystemDbContext db,
        EnvironmentRecord environment, EnvironmentRequest request, EnvironmentJob job,
        EnvironmentReservation reservation, AgentLease? lease, StagedArtifact? artifact,
        string inconsistentCode, CancellationToken ct)
    {
        RequestRejectedException Inconsistent() => new(409, inconsistentCode);
        var artifactId = artifact?.ArtifactId;
        // A misplaced row is an inconsistency, not an absent hold. Do not sample only by environment.
        var rows = await db.RestoreIntents.AsNoTracking().Where(x => x.EnvironmentId == environment.Id ||
            x.StageJobId == job.Id || x.ReservationId == reservation.Id || x.ArtifactId == artifactId).Take(2).ToListAsync(ct);
        if (rows.Count > 1) throw Inconsistent();
        var intent = rows.SingleOrDefault();
        if (intent is null) return null;
        if (artifact is null || lease is null || intent.RestoreIntentId == Guid.Empty ||
            intent.EnvironmentId != environment.Id || intent.StageJobId != job.Id ||
            intent.RequestId != request.Id || intent.ReservationId != reservation.Id ||
            intent.ArtifactId != artifact.ArtifactId || intent.AgentId != artifact.AgentId ||
            intent.AgentId != lease.AgentId || intent.FencingToken != lease.FencingToken || intent.FencingToken <= 0 ||
            artifact.FencingToken != lease.FencingToken || artifact.JobId != job.Id ||
            artifact.ReservationId != reservation.Id || artifact.ArchiveVersionId != request.ArchiveVersionId ||
            intent.PoolId != environment.PoolId || intent.PoolId != reservation.PoolId ||
            intent.ArchiveVersionId != artifact.ArchiveVersionId || intent.Sha256 != artifact.Sha256 ||
            intent.SizeBytes != artifact.SizeBytes || intent.Format != artifact.Format || intent.Format != "dt" ||
            intent.SizeBytes <= 0 || intent.SizeBytes > reservation.StagingBytes ||
            intent.Sha256.Length != 64 || !intent.Sha256.All(Uri.IsHexDigit) ||
            intent.TargetIncarnation == Guid.Empty || intent.RequestedBy == Guid.Empty || intent.RequestedAt == default ||
            intent.Executable || !intent.ScheduledJobsMustRemainBlocked ||
            string.IsNullOrWhiteSpace(intent.PreparationProfileVersion) || intent.PreparationProfileVersion.Length > 128 ||
            job.State != "StageCompleted" || lease.State != "Completed" || artifact.State != "ContentVerified" ||
            reservation.State != "Held" || !environment.HasUnsettledProvisioningOperation ||
            environment.State is not ("Requested" or "Provisioning") || request.State is not ("Pending" or "Provisioning"))
            throw Inconsistent();
        var pool = await db.EnvironmentPools.AsNoTracking().SingleOrDefaultAsync(x => x.Id == intent.PoolId, ct);
        var version = await db.DiskArchiveVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == intent.ArchiveVersionId, ct);
        var archive = version is null ? null : await db.DiskArchives.AsNoTracking().SingleOrDefaultAsync(x => x.Id == version.ArchiveId, ct);
        var name = "bs_test_" + reservation.Id.ToString("N");
        if (pool is null || version is null || archive is null || intent.RootId != archive.RootId ||
            intent.CanonicalClusterId == Guid.Empty || intent.CanonicalSqlServerId == Guid.Empty ||
            intent.CanonicalClusterId != pool.ClusterId || intent.CanonicalClusterId != reservation.ClusterId ||
            intent.CanonicalSqlServerId != pool.SqlServerId || intent.CanonicalSqlServerId != reservation.SqlServerId ||
            intent.SqlDbName != name || intent.InfobaseName != name || reservation.SqlDbName != name || reservation.InfobaseName != name ||
            version.SizeBytes != intent.SizeBytes || version.Format != intent.Format ||
            !string.Equals(version.Sha256, intent.Sha256, StringComparison.OrdinalIgnoreCase) ||
            intent.SnapshotSha256 != Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(job.SnapshotJson))))
            throw Inconsistent();
        try
        {
            using var snapshot = JsonDocument.Parse(job.SnapshotJson);
            var s = snapshot.RootElement;
            Guid Id(string key) => s.GetProperty(key).GetGuid();
            if (Id("reservationId") != reservation.Id || Id("poolId") != intent.PoolId ||
                Id("sourceRootId") != intent.RootId || Id("sourceArchiveId") != archive.Id ||
                Id("archiveVersionId") != intent.ArchiveVersionId || Id("AgentScopeId") != intent.AgentId ||
                Id("SqlServerId") != intent.CanonicalSqlServerId || Id("ClusterId") != intent.CanonicalClusterId ||
                s.GetProperty("sourceRelativePath").GetString() != archive.RelativePath ||
                s.GetProperty("sqlDbName").GetString() != name || s.GetProperty("infobaseName").GetString() != name ||
                !string.Equals(s.GetProperty("Sha256").GetString(), intent.Sha256, StringComparison.OrdinalIgnoreCase) ||
                s.GetProperty("SizeBytes").GetInt64() != intent.SizeBytes || s.GetProperty("Format").GetString() != intent.Format ||
                s.GetProperty("stagingBytes").GetInt64() != reservation.StagingBytes ||
                s.GetProperty("PreparationProfileVersion").GetString() != intent.PreparationProfileVersion)
                throw Inconsistent();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        { throw Inconsistent(); }
        if (await db.StageRetirementIntents.AsNoTracking().AnyAsync(x => x.EnvironmentId == environment.Id ||
                x.JobId == job.Id || x.ReservationId == reservation.Id || x.ArtifactId == artifact.ArtifactId, ct) ||
            await db.StageCleanupAuthorizations.AsNoTracking().AnyAsync(x => x.ArtifactId == artifact.ArtifactId, ct) ||
            await db.StageCleanupResults.AsNoTracking().AnyAsync(x => x.ArtifactId == artifact.ArtifactId, ct) ||
            await db.StagingAllocationReleases.AsNoTracking().AnyAsync(x => x.ReservationId == reservation.Id, ct))
            throw Inconsistent();
        return new(intent.RestoreIntentId, intent.TargetIncarnation, intent.RequestedAt);
    }
}
// end
