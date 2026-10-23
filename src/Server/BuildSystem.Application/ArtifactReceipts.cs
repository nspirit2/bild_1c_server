namespace BuildSystem.Application;

/// <summary>Metadata attested by the currently leased worker; no filesystem path or Ready claim.</summary>
public sealed record ReportStagedArtifact(Guid JobId, long FencingToken, Guid ReservationId,
    Guid ArchiveVersionId, Guid ArtifactId, string Sha256, long SizeBytes, string Format);

public sealed record StagedArtifactReceipt(Guid ArtifactId, string State);

public interface IArtifactReceipts
{
    Task<StagedArtifactReceipt> ReportAsync(string credential, ReportStagedArtifact report, CancellationToken ct);
}
