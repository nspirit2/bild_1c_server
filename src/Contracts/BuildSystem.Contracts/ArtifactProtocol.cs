namespace BuildSystem.Contracts;

/// <summary>Immutable metadata attestation only; contains no filesystem location or Ready claim.</summary>
public sealed record ReportStagedArtifact(Guid JobId, long FencingToken, Guid ReservationId,
    Guid ArchiveVersionId, Guid ArtifactId, string Sha256, long SizeBytes, string Format);

public sealed record StagedArtifactReceipt(Guid ArtifactId, string State);
// end
