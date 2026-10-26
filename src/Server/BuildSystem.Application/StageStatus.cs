namespace BuildSystem.Application;

/// <summary>Observed lifecycle metadata only; neither file existence nor archive format validation.</summary>
public sealed record StageStatusView(Guid EnvironmentId, Guid RequestId, Guid JobId,
    string JobState, string EnvironmentState, string RequestState, string ReservationState,
    long StagingBytes, bool Unsettled, Guid? ArtifactId, string? ArtifactState,
    string? LeaseState, DateTimeOffset? LeaseExpiresAt, string ReconciliationCode, DateTimeOffset ObservedAt);

public interface IStageStatus
{
    Task<StageStatusView?> GetAsync(Actor actor, Guid environmentId, CancellationToken ct);
}
// EOF
