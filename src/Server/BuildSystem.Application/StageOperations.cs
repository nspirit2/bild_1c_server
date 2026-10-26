namespace BuildSystem.Application;

public sealed record StageOperationsQuery(int Limit = 25, string? Cursor = null, string? State = null);

/// <summary>Server metadata only; not file presence, execution permission or archive validation.</summary>
public sealed record StageOperationView(Guid EnvironmentId, Guid RequestId, Guid JobId, string JobState,
    string EnvironmentState, string ReservationState, long StagingBytes, bool Unsettled, DateTimeOffset CreatedAt);

public sealed record StageOperationsPage(IReadOnlyList<StageOperationView> Items, string? NextCursor,
    DateTimeOffset ObservedAt);

public interface IStageOperations
{
    Task<StageOperationsPage> ListAsync(Actor actor, StageOperationsQuery query, CancellationToken ct);
}
// EOF
