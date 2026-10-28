namespace BuildSystem.Application;

/// <summary>Explicit request for future stage retirement; grants no deletion or quota-release authority.</summary>
public sealed record StageRetirementRequest(Guid ExpectedJobId, Guid ExpectedArtifactId);
public sealed record StageRetirementReceipt(Guid RetirementId, Guid EnvironmentId, Guid JobId,
    Guid ArtifactId, string State, DateTimeOffset CreatedAt);

public interface IStageRetirement
{
    Task<StageRetirementReceipt> RequestAsync(Actor actor, Guid environmentId,
        StageRetirementRequest input, CancellationToken ct);
}
// end
