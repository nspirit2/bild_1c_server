namespace BuildSystem.Contracts;

/// <summary>Stage-only allowlist; no restoration target, provisioning snapshot or absolute filesystem path.</summary>
public sealed record StagePlan(Guid ReservationId, Guid ArchiveVersionId, Guid RootId, string RelativePath,
    string Sha256, long SizeBytes, string Format);
public sealed record StageLeaseGrant(Guid JobId, long FencingToken, DateTimeOffset ExpiresAt, string Kind, StagePlan Plan);

// EOF
