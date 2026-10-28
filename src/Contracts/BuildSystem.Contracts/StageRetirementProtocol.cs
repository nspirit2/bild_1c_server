namespace BuildSystem.Contracts;

/// <summary>
/// Read-only inspection of a pinned retirement intent. This is NOT a lease, cleanup authority,
/// execution instruction, deletion receipt or resource-release permission. No deadline is implied.
/// </summary>
public sealed record StageRetirementPlan(Guid RetirementId, Guid EnvironmentId, Guid JobId,
    Guid ArtifactId, Guid ReservationId, Guid ArchiveVersionId, long FencingToken,
    string Sha256, long SizeBytes, string Format);
// end
