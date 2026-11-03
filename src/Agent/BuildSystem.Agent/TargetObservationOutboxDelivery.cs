using BuildSystem.Contracts;

namespace BuildSystem.Agent;

/// <summary>A redacted delivery failure. The historical receipt remains pending for explicit retry.</summary>
public sealed class TargetObservationOutboxException : Exception
{
    public TargetObservationOutboxException() : base("Target observation outbox delivery failed.") { }
}

/// <summary>
/// Explicit, bounded delivery of a durable historical snapshot only. This does not acquire assignments,
/// perform observations, or grant execution authority. Callers must otherwise serialize journal access.
/// </summary>
public sealed class TargetObservationOutboxDelivery
{
    private readonly AgentJournal journal;
    private readonly ITargetObservationTransport transport;
    private readonly SemaphoreSlim deliveryGate = new(1, 1);

    public TargetObservationOutboxDelivery(AgentJournal journal, ITargetObservationTransport transport)
    {
        this.journal = journal ?? throw new ArgumentNullException(nameof(journal));
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public async Task<int> DeliverAsync(int limit = 100, CancellationToken ct = default)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        ct.ThrowIfCancellationRequested();
        // Reject overlapping/reentrant calls rather than silently scheduling another delivery pass.
        if (!deliveryGate.Wait(0)) throw new TargetObservationOutboxException();
        try
        {
            var pending = journal.PendingTargetObservations(limit);
            var delivered = 0;
            foreach (var report in pending)
            {
                ct.ThrowIfCancellationRequested();
                var receipt = await transport.ReportAsync(report, ct).ConfigureAwait(false);
                // A lost ACK is safe: preserve the same historical identity and payload for retry.
                ct.ThrowIfCancellationRequested();
                if (receipt is null || receipt.ReceiptId != report.ReceiptId ||
                    receipt.ObservationRequestId != report.Assignment.ObservationRequestId ||
                    !string.Equals(receipt.State, "Recorded", StringComparison.Ordinal) ||
                    receipt.Executable || !receipt.ScheduledJobsMustRemainBlocked)
                    throw new TargetObservationOutboxException();
                journal.AckTargetObservation(report.ReceiptId);
                delivered++;
            }
            return delivered;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Do not expose a transport-supplied cancellation message or inner exception.
            throw new OperationCanceledException(ct);
        }
        catch (Exception)
        {
            throw new TargetObservationOutboxException();
        }
        finally
        {
            deliveryGate.Release();
        }
    }
}
