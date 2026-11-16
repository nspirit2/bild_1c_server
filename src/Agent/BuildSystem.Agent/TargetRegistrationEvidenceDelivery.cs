namespace BuildSystem.Agent;

/// <summary>
/// Explicit, default-off delivery of an inert historical report derived solely from the saved
/// creation acknowledgement and paired registration evidence. This is not an observation, a
/// physical-operation retry, ownership, authority, or permission to resume scheduled jobs.
/// The per-instance busy guard is NOT a global journal lock: callers must externally serialize
/// ALL access to the single-owner journal, including other delivery instances and journal getters.
/// A failed transport may already have been accepted remotely even when the local acknowledgement
/// was not entered; AcknowledgementMayHaveCompleted describes ONLY uncertain local ACK persistence.
/// A later explicit call uses the same saved claim and evidence, never a new observation or claim.
/// Synchronous journal work cannot be detached or preempted and may commit after cancellation.
/// </summary>
public sealed class TargetRegistrationEvidenceDelivery
{
    private readonly AgentJournal journal;
    private readonly ITargetRegistrationEvidenceTransport transport;
    private readonly TimeProvider clock;
    private readonly bool enabled;
    private readonly TimeSpan timeout;
    private int busy;

    public TargetRegistrationEvidenceDelivery(AgentJournal journal, ITargetRegistrationEvidenceTransport transport,
        TargetRegistrationEvidenceDeliveryOptions options, TimeProvider? clock = null)
    {
        this.journal = journal ?? throw Error("InvalidOptions");
        this.transport = transport ?? throw Error("InvalidOptions");
        if (options is null) throw Error("InvalidOptions");
        this.clock = clock ?? TimeProvider.System;
        enabled = options.Enabled;
        if (!enabled) return;
        if (options.TotalTimeoutSeconds is < 1 or > 120) throw Error("InvalidOptions");
        timeout = TimeSpan.FromSeconds(options.TotalTimeoutSeconds);
    }

    /// <summary>
    /// One explicit operation only. An existing local ACK returns historical metadata without
    /// contacting the transport. No batch, automatic retry, fresh observations or authority.
    /// </summary>
    public async Task<TargetRegistrationEvidenceDeliveryResult> DeliverOperationAsync(Guid operationId,
        CancellationToken ct = default)
    {
        if (!enabled) throw Error("Disabled");
        if (operationId == Guid.Empty) throw Error("InvalidRequest");
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) throw Error("Busy");
        var acknowledgementEntered = false;
        try
        {
            var started = clock.GetTimestamp();
            var previous = started;
            // The timer and linked token cover transport and every synchronous journal step too.
            // Their disposal is inside this try so a disposal failure is classified by ACK-entry.
            TargetRegistrationEvidenceDeliveryResult result;
            using (var timer = new CancellationTokenSource(timeout, clock))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token))
            {
                void CheckDeadline()
                {
                    var now = clock.GetTimestamp();
                    var elapsed = clock.GetElapsedTime(started, now);
                    if (now < previous || elapsed < TimeSpan.Zero || elapsed >= timeout ||
                        linked.IsCancellationRequested)
                        throw Error("Unavailable");
                    previous = now;
                }

                CheckDeadline();
                var existing = journal.GetTargetRegistrationEvidenceDeliveryAcknowledgement(operationId);
                CheckDeadline();
                if (existing is not null)
                {
                    result = new(operationId, false, existing);
                }
                else
                {
                    // This projection must come from journal's strict saved-ACK/evidence read;
                    // callers cannot supply or modify the report used for delivery.
                    CheckDeadline();
                    var report = journal.PrepareTargetRegistrationEvidenceReport(operationId);
                    CheckDeadline();
                    var receipt = await transport.ReportAsync(report, linked.Token).ConfigureAwait(false);
                    CheckDeadline();
                    if (!ValidReceipt(receipt, report, operationId)) throw Error("Unavailable");
                    // Re-read immediately before persistence; Record revalidates the current
                    // report atomically inside its own transaction as the final defense.
                    var current = journal.PrepareTargetRegistrationEvidenceReport(operationId);
                    CheckDeadline();
                    if (current != report) throw Error("Unavailable");
                    CheckDeadline();
                    acknowledgementEntered = true;
                    var acknowledged = journal.RecordTargetRegistrationEvidenceDeliveryAcknowledgement(
                        operationId, report, receipt);
                    CheckDeadline();
                    result = new(operationId, true, acknowledged);
                }
            }
            // Include timer/link cleanup in the whole deadline. After local ACK entry, any
            // failure (even a clock failure here) is an unconfirmed local persistence result.
            var completed = clock.GetTimestamp();
            var total = clock.GetElapsedTime(started, completed);
            if (completed < previous || total < TimeSpan.Zero || total >= timeout || ct.IsCancellationRequested)
                throw Error("Unavailable");
            return result;
        }
        catch
        {
            throw acknowledgementEntered
                ? new TargetRegistrationEvidenceDeliveryException(
                    "TargetRegistrationEvidenceDeliveryAcknowledgementUnconfirmed", true)
                : Error("Unavailable");
        }
        finally { Interlocked.Exchange(ref busy, 0); }
    }

    private static bool ValidReceipt(TargetRegistrationEvidenceDeliveryReceipt? receipt,
        TargetRegistrationEvidenceReport report, Guid operationId) =>
        receipt is not null && receipt.ClaimId == report.ClaimId && receipt.OperationId == operationId &&
        string.Equals(receipt.Status, "Recorded", StringComparison.Ordinal) &&
        receipt.AcceptedAt != default && receipt.AcceptedAt.Offset == TimeSpan.Zero &&
        !receipt.Executable && !receipt.OwnershipEstablished && !receipt.RestoreAllowed &&
        !receipt.RetryAllowed && !receipt.FreshAuthority &&
        receipt.RequiresIndependentVerification && receipt.ScheduledJobsMustRemainBlocked;

    private static TargetRegistrationEvidenceDeliveryException Error(string suffix) =>
        new("TargetRegistrationEvidenceDelivery" + suffix);
}
