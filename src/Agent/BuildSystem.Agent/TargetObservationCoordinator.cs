using BuildSystem.Contracts;

namespace BuildSystem.Agent;

/// <summary>Redacted failure; no remote response, target identifier or inner exception is exposed.</summary>
public sealed class TargetObservationCoordinatorException : Exception
{
    public TargetObservationCoordinatorException() : base("Target observation coordination failed.") { }
}

/// <summary>Trusted, explicitly invoked paired observer. It must not mutate targets or schedule jobs.</summary>
public interface ITargetObservationExecutor
{
    Task<ReportTargetObservation> ObserveAsync(TargetObservationAssignment assignment, CancellationToken ct);
}

/// <summary>
/// Explicit observation only: reauthorize each request against the current server assignment, durably
/// pin the assignment before observation, then enqueue one complete historical report. This never delivers
/// the outbox, acknowledges reports or confers execution authority. The fail-fast gate is per instance only:
/// callers MUST serialize access to a shared AgentJournal across coordinator instances and processes on a
/// single host. It is not a global lock, durable lease or cross-process execution exclusion.
/// </summary>
public sealed class TargetObservationCoordinator
{
    private readonly AgentJournal journal;
    private readonly ITargetObservationTransport transport;
    private readonly ITargetObservationExecutor executor;
    private readonly SemaphoreSlim observationGate = new(1, 1);

    public TargetObservationCoordinator(AgentJournal journal, ITargetObservationTransport transport,
        ITargetObservationExecutor executor)
    {
        this.journal = journal ?? throw new ArgumentNullException(nameof(journal));
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public async Task<ReportTargetObservation> ObserveAsync(Guid requestId, CancellationToken ct = default)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("Invalid observation request.", nameof(requestId));
        ct.ThrowIfCancellationRequested();
        if (!observationGate.Wait(0)) throw new TargetObservationCoordinatorException();
        try
        {
            // Even a locally recorded or acknowledged request needs current server authorization.
            var assignment = await transport.ReadAsync(requestId, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!ValidAssignment(assignment, requestId)) throw new TargetObservationCoordinatorException();
            // The journal rejects an immutable-pin conflict before any historical report is returned.
            assignment = journal.SaveTargetObservationAssignment(assignment);
            ct.ThrowIfCancellationRequested();
            var historical = journal.GetTargetObservationReport(requestId);
            if (historical is not null) return historical;

            // On failure/cancellation before enqueue, only the assignment remains; explicit retry can
            // observe afresh. No partial observation is ever made durable by this coordinator.
            var report = await executor.ObserveAsync(assignment, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!ValidReport(report, assignment)) throw new TargetObservationCoordinatorException();
            journal.EnqueueTargetObservation(report);
            return report;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (Exception)
        {
            throw new TargetObservationCoordinatorException();
        }
        finally
        {
            observationGate.Release();
        }
    }

    private static bool ValidAssignment(TargetObservationAssignment? assignment, Guid requestId) =>
        assignment is not null && assignment.ObservationRequestId == requestId &&
        assignment.RestoreIntentId != Guid.Empty && assignment.AgentId != Guid.Empty &&
        assignment.OperationId != Guid.Empty && assignment.EnvironmentId != Guid.Empty &&
        assignment.StageJobId != Guid.Empty && assignment.FencingToken > 0 &&
        assignment.ReservationId != Guid.Empty && assignment.TargetIncarnationId != Guid.Empty &&
        assignment.CanonicalClusterId != Guid.Empty && assignment.CanonicalSqlServerId != Guid.Empty &&
        assignment.RacEndpointMappingId != Guid.Empty && assignment.SqlEndpointMappingId != Guid.Empty &&
        !assignment.Executable && assignment.ScheduledJobsMustRemainBlocked;

    private static bool ValidReport(ReportTargetObservation? report, TargetObservationAssignment assignment) =>
        report is not null && report.Assignment == assignment &&
        report.ReceiptId != Guid.Empty && report.RacObservationId != Guid.Empty &&
        report.SqlObservationId != Guid.Empty && report.RacObservationId != report.SqlObservationId &&
        Enum.IsDefined(report.Outcome) && report.ObservedAt != default &&
        !report.Executable && report.ScheduledJobsMustRemainBlocked &&
        !report.Assignment.Executable && report.Assignment.ScheduledJobsMustRemainBlocked;
}
