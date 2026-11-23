namespace BuildSystem.Application;

/// <summary>Explicit cancellation of an ordinary archive order before any lease was issued.</summary>
public sealed record PendingArchiveCancellationRequest(Guid ExpectedJobId);

/// <summary>Proof of a metadata transition, not of physical inspection or deletion.</summary>
public sealed record PendingArchiveCancellationReceipt(Guid EnvironmentId, Guid RequestId, Guid JobId,
    Guid ReservationId, Guid RootId, Guid PoolId, Guid ActorId, DateTimeOffset CancelledAt);

public interface IPendingArchiveCancellations
{
    Task<PendingArchiveCancellationReceipt> CancelAsync(Actor actor, Guid environmentId,
        PendingArchiveCancellationRequest input, CancellationToken ct);
}
