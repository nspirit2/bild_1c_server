using BuildSystem.Agent.Tools;
using BuildSystem.Contracts;

namespace BuildSystem.Agent;

/// <summary>Fixed redacted observation failure, never provider diagnostics or credentials.</summary>
public sealed class TargetObservationExecutionException : Exception
{
    public TargetObservationExecutionException() : base("Target observation execution failed.") { }
}

/// <summary>
/// Performs one read-only RAC then SQL observation through the concrete paired observer.
/// The returned timestamp labels a completed historical observation, never freshness or authority.
/// This executor performs no journal, HTTP, lease, or target lifecycle writes.
/// </summary>
public sealed class TargetObservationExecutor : ITargetObservationExecutor
{
    private readonly Guid expectedAgentId;
    private readonly TargetPresenceObserver observer;
    private readonly Func<InfobaseInventoryObserverCredentials> credentialsProvider;

    public TargetObservationExecutor(Guid expectedAgentId, TargetPresenceObserver observer,
        Func<InfobaseInventoryObserverCredentials> credentialsProvider)
    {
        if (expectedAgentId == Guid.Empty) throw new ArgumentException("Invalid agent identity.", nameof(expectedAgentId));
        this.expectedAgentId = expectedAgentId;
        this.observer = observer ?? throw new ArgumentNullException(nameof(observer));
        this.credentialsProvider = credentialsProvider ?? throw new ArgumentNullException(nameof(credentialsProvider));
    }

    public async Task<ReportTargetObservation> ObserveAsync(TargetObservationAssignment assignment,
        CancellationToken ct = default)
    {
        try
        {
            // Validate every assignment GUID, including pins not consumed by the paired observer,
            // before calling a credential provider or either adapter.
            if (assignment is null || assignment.ObservationRequestId == Guid.Empty ||
                assignment.RestoreIntentId == Guid.Empty || assignment.AgentId != expectedAgentId ||
                assignment.OperationId == Guid.Empty || assignment.EnvironmentId == Guid.Empty ||
                assignment.StageJobId == Guid.Empty || assignment.FencingToken <= 0 ||
                assignment.ReservationId == Guid.Empty || assignment.TargetIncarnationId == Guid.Empty ||
                assignment.CanonicalClusterId == Guid.Empty || assignment.CanonicalSqlServerId == Guid.Empty ||
                assignment.RacEndpointMappingId == Guid.Empty || assignment.SqlEndpointMappingId == Guid.Empty ||
                assignment.Executable || !assignment.ScheduledJobsMustRemainBlocked)
                throw new TargetObservationExecutionException();
            ct.ThrowIfCancellationRequested();
            var request = new TargetPresenceRequest(assignment.OperationId, assignment.FencingToken,
                assignment.TargetIncarnationId, assignment.ReservationId, assignment.CanonicalClusterId,
                assignment.CanonicalSqlServerId, assignment.RacEndpointMappingId, assignment.SqlEndpointMappingId);
            var credentials = credentialsProvider();
            ct.ThrowIfCancellationRequested();
            var result = await observer.ObserveAsync(request, credentials, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var scope = result?.Scope;
            if (result is null || scope is null || result.Executable || !result.ScheduledJobsMustRemainBlocked ||
                scope.OperationId != request.OperationId || scope.FencingToken != request.FencingToken ||
                scope.TargetIncarnationId != request.TargetIncarnationId || scope.ReservationId != request.ReservationId ||
                scope.CanonicalClusterId != request.CanonicalClusterId || scope.CanonicalSqlServerId != request.CanonicalSqlServerId ||
                scope.RacEndpointMappingId != request.RacEndpointMappingId || scope.SqlEndpointMappingId != request.SqlEndpointMappingId ||
                scope.RacObservationId == Guid.Empty || scope.SqlObservationId == Guid.Empty ||
                scope.RacObservationId == scope.SqlObservationId)
                throw new TargetObservationExecutionException();
            var outcome = result.Observation switch
            {
                TargetPresenceObservation.BothObservedAbsent => TargetObservationOutcome.BothObservedAbsent,
                TargetPresenceObservation.InfobaseObservedPresent => TargetObservationOutcome.InfobaseObservedPresent,
                TargetPresenceObservation.SqlDatabaseObservedPresent => TargetObservationOutcome.SqlDatabaseObservedPresent,
                TargetPresenceObservation.BothObservedPresent => TargetObservationOutcome.BothObservedPresent,
                _ => throw new TargetObservationExecutionException()
            };
            // Only sample this historical completion label after the whole pair and its validation.
            return new(Guid.NewGuid(), assignment, scope.RacObservationId, scope.SqlObservationId,
                outcome, DateTimeOffset.UtcNow);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Never propagate a provider-supplied cancellation message or inner exception.
            throw new OperationCanceledException(ct);
        }
        catch (Exception)
        {
            throw new TargetObservationExecutionException();
        }
    }
}
