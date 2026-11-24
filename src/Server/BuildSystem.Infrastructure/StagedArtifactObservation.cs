using BuildSystem.Application;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Shared receipt validation for the serializable stage and cleanup observations.</summary>
internal static class StagedArtifactObservation
{
    internal sealed record Receipt(StagedArtifact Artifact, DiskArchiveVersion Version);

    internal static async Task<Receipt?> ReadAsync(BuildSystemDbContext db, EnvironmentJob job,
        EnvironmentRequest request, EnvironmentReservation reservation, AgentLease? lease, string inconsistentCode,
        CancellationToken ct)
    {
        var candidates = await db.StagedArtifacts.AsNoTracking()
            .Where(a => a.JobId == job.Id || a.ReservationId == reservation.Id).Take(2).ToListAsync(ct);
        if (candidates.Count > 1) throw Inconsistent();
        var artifact = candidates.SingleOrDefault();
        if (artifact is null) return null;
        if (lease is null || artifact.JobId != job.Id || artifact.State != "ContentVerified" ||
            artifact.AgentId != lease.AgentId || artifact.FencingToken != lease.FencingToken ||
            artifact.ReservationId != reservation.Id || artifact.ArchiveVersionId != request.ArchiveVersionId ||
            artifact.SizeBytes <= 0 || artifact.SizeBytes > reservation.StagingBytes) throw Inconsistent();
        var version = await db.DiskArchiveVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == artifact.ArchiveVersionId, ct);
        if (version is null || version.SizeBytes != artifact.SizeBytes || version.Format != artifact.Format ||
            !string.Equals(version.Sha256, artifact.Sha256, StringComparison.OrdinalIgnoreCase)) throw Inconsistent();
        return new Receipt(artifact, version);

        RequestRejectedException Inconsistent() => new(409, inconsistentCode);
    }
}
