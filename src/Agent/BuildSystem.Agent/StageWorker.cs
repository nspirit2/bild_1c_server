using BuildSystem.Contracts;

namespace BuildSystem.Agent;

/// <summary>Opt-in stage-only step. Caller serializes this worker with all journal/dispatcher use.
/// No capability announcement, probe fallback, restoration, lease settlement, or automatic reconciliation.</summary>
public sealed class StageWorker(AgentJournal journal, IStageTransport transport, StageCoordinator coordinator,
    ArtifactDelivery delivery)
{
    public async Task WorkOnce(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (journal.PendingArtifacts().Count != 0)
        {
            await delivery.FlushAsync(ct);
            return;
        }
        var prior = journal.Unsettled().SingleOrDefault();
        if (prior is not null)
        {
            if (prior.Kind != "ArchiveStageOnly" || prior.ExpiresAt <= DateTimeOffset.UtcNow) return;
            // Acked artifact does not complete its lease. Never re-stage or poll a new job afterward.
            if (journal.HasArtifactForLease(prior.JobId, prior.FencingToken)) return;
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
