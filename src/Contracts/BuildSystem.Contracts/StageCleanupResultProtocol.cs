namespace BuildSystem.Contracts;

/// <summary>Only a report of locally confirmed deletion, not a server-verified filesystem proof.</summary>
public sealed record ReportStageCleanup(Guid ResultId, Guid AuthorizationId, Guid RetirementId,
    Guid JobId, Guid ArtifactId, long FencingToken);

/// <summary>DeletedReported does not release staging quota or settle any environment.</summary>
public sealed record StageCleanupResultReceipt(Guid ResultId, Guid AuthorizationId, Guid ArtifactId,
    string State, DateTimeOffset ReceivedAt);
// end
