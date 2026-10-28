namespace BuildSystem.Application;

/// <summary>Historical cleanup/accounting metadata, never a current filesystem presence claim.</summary>
public sealed record CleanupStatusView(Guid EnvironmentId, Guid JobId, Guid? ArtifactId, Guid? RetirementId,
    Guid? AuthorizationId, Guid? ResultId, Guid? ReleaseId, string CleanupState,
    long ReservedStagingBytes, long HeldStagingBytes, long ReleasedStagingBytes, DateTimeOffset ObservedAt);

public interface ICleanupStatus
{
    Task<CleanupStatusView?> GetAsync(Actor actor, Guid environmentId, CancellationToken ct);
}
// end
