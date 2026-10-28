namespace BuildSystem.Contracts;

public sealed record ClaimStageCleanup(Guid ExpectedRetirementId, Guid ExpectedArtifactId);

/// <summary>
/// Durable non-expiring authority for one pinned staged artifact only. Not a general delete command,
/// not an authorization for SQL/1C resources, and never proof that deletion or quota release occurred.
/// Revoking a credential prevents future claims/replays but cannot undo an already delivered authorization.
/// Execution must additionally prove local ownership, journal settlement and crash-safe deletion identity.
/// </summary>
public sealed record StageCleanupAuthorization(Guid AuthorizationId, StageRetirementPlan Plan, DateTimeOffset AuthorizedAt);
// end
