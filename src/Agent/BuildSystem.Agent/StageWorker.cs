using BuildSystem.Contracts;

namespace BuildSystem.Agent;

/// <summary>Opt-in stage-only step. Caller serializes this worker with all journal/dispatcher use.
/// No capability announcement, probe fallback, restoration, or automatic reconciliation.
/// A matching stage-completion receipt is the only automatic local stage lease settlement.</summary>
public sealed class StageWorker(AgentJournal journal, IStageTransport transport, StageCoordinator coordinator,
    ArtifactDelivery delivery, StageCompletionDelivery? completions = null)
{
    public async Task WorkOnce(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (journal.PendingArtifacts().Count != 0)
        {
            await delivery.FlushAsync(ct);
            return;
        }
        if (journal.PendingCompletions().Count != 0)
        {
            if (completions is not null) await completions.FlushAsync(ct);
            return;
        }
        var prior = journal.Unsettled().SingleOrDefault();
        if (prior is not null)
        {
            if (prior.Kind != "ArchiveStageOnly") return;
            // Artifact ack alone is not settlement; persist completion before its first HTTP call.
            if (journal.AckedArtifactForLease(prior.JobId, prior.FencingToken) is { } acknowledged)
            {
                if (completions is not null)
                {
                    journal.EnqueueCompletion(new(prior.JobId, prior.FencingToken, acknowledged.ArtifactId));
                    await completions.FlushAsync(ct);
                }
                return;
            }
            if (prior.ExpiresAt <= DateTimeOffset.UtcNow || journal.HasArtifactForLease(prior.JobId, prior.FencingToken)) return;
            if (journal.GetStageIntent(prior.JobId, prior.FencingToken) is not null)
            {
                await coordinator.ResumeAsync(prior.JobId, prior.FencingToken, ct);
                return;
            }
        }
        var grant = await transport.PollStageAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (grant is null) return;
        if (grant.Kind != "ArchiveStageOnly" || grant.JobId == Guid.Empty || grant.FencingToken <= 0 ||
            grant.ExpiresAt <= DateTimeOffset.UtcNow || grant.Plan is null)
            throw new InvalidOperationException("Invalid stage grant.");
        if (prior is not null && (prior.JobId != grant.JobId || prior.FencingToken != grant.FencingToken ||
            prior.ExpiresAt != grant.ExpiresAt)) throw new InvalidOperationException("Stage grant replay conflict.");
        var p = grant.Plan;
        var intent = new StageIntent(grant.JobId, grant.FencingToken, p.ReservationId, p.ArchiveVersionId,
            p.RootId, p.RelativePath, p.Sha256, p.SizeBytes, p.Format);
        AgentJournal.ValidateStageIntent(intent); // Validate all wire inputs before persisting a lease.
        journal.StoreGrant(new LeaseGrant(grant.JobId, grant.FencingToken, grant.ExpiresAt, grant.Kind, "{}"));
        await coordinator.ExecuteAsync(intent, ct);
    }
}
// end
