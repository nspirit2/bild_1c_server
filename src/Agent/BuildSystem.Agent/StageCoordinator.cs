using BuildSystem.Agent.Archives;
using BuildSystem.Contracts;

namespace BuildSystem.Agent;

public enum StageOutcome { Enqueued, Blocked, Cancelled }

/// <summary>Standalone stage-only orchestration. Does not advertise capability, restore, or settle a lease.
/// The caller serializes all operations on this journal. A guard must obtain current server lease authority.</summary>
public sealed class StageCoordinator(AgentJournal journal, ArchiveStaging staging,
    Func<LeaseHeartbeat, CancellationToken, Task<LeaseStatus>> leaseGuard)
{
    public async Task<StageOutcome> ExecuteAsync(StageIntent intent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        journal.SaveStageIntent(intent); // Before filesystem side effects. Replay can never replace this identity.
        return await ResumeAsync(intent.JobId, intent.FencingToken, ct);
    }

    public async Task<StageOutcome> ResumeAsync(Guid jobId, long fence, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var intent = journal.GetStageIntent(jobId, fence) ?? throw new InvalidOperationException("No durable stage intent.");
        if (journal.StageIntentFailure(jobId, fence) is not null) return StageOutcome.Blocked;
        var lease = journal.Unsettled().SingleOrDefault(x => x.JobId == jobId && x.FencingToken == fence);
        if (lease is null || lease.Kind != "ArchiveStageOnly") return Block("LeaseRejected");
        if (lease.ExpiresAt <= DateTimeOffset.UtcNow) return Block("LeaseExpired");
        try
        {
            if (!await GuardAsync()) return Block("LeaseRejected");
            var fingerprint = new SourceFingerprint(intent.RootId, intent.RelativePath, intent.Sha256,
                intent.SizeBytes, intent.Format == "dt" ? ArchiveFormat.Dt : ArchiveFormat.Bak);
            // The same reservation/version has a deterministic artifact identity. Stage verifies an existing
            // published artifact on recovery and never needs to reread a missing/replaced source in that case.
            var remaining = journal.Unsettled().Single(x => x.JobId == jobId && x.FencingToken == fence).ExpiresAt - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return Block("LeaseExpired");
            using var copyDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            copyDeadline.CancelAfter(remaining);
            VerifiedArtifact artifact;
            try { artifact = await staging.StageAsync(new(intent.ReservationId, intent.ArchiveVersionId, fingerprint), copyDeadline.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && copyDeadline.IsCancellationRequested)
            { return Block("LeaseExpired"); }
            ct.ThrowIfCancellationRequested();
            if (!await GuardAsync()) return Block("LeaseRejected");
            if (!Guid.TryParseExact(artifact.ArtifactId, "N", out var artifactId) ||
                artifact.Size != intent.SizeBytes || !string.Equals(artifact.Sha256, intent.Sha256, StringComparison.OrdinalIgnoreCase))
                return Block("StageFailed");
            journal.EnqueueArtifact(new(jobId, fence, intent.ReservationId, intent.ArchiveVersionId,
                artifactId, intent.Sha256, intent.SizeBytes, intent.Format));
            // Exact replay preserves a prior delivery rejection; never claim this row is dispatchable.
            return journal.ArtifactState(artifactId) == "Blocked" ? StageOutcome.Blocked : StageOutcome.Enqueued;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Do not erase an artifact that might have been published before cancellation.
            journal.FailStageIntent(jobId, fence, "Cancelled");
            return StageOutcome.Cancelled;
        }
        catch (ArchiveException error)
        {
            return Block(error.Failure == ArchiveFailure.ArchiveChanged ? "ArchiveChanged" : "ArchiveUnavailable");
        }
        catch (HttpRequestException) { return Block("LeaseRejected"); }
        catch (OperationCanceledException) { return Block("LeaseRejected"); } // Guard timeout, not caller cancellation.

        StageOutcome Block(string reason)
        {
            journal.FailStageIntent(jobId, fence, reason);
            return StageOutcome.Blocked;
        }
        async Task<bool> GuardAsync()
        {
            ct.ThrowIfCancellationRequested();
            var current = journal.Unsettled().SingleOrDefault(x => x.JobId == jobId && x.FencingToken == fence);
            if (current is null || current.Kind != "ArchiveStageOnly" || current.ExpiresAt <= DateTimeOffset.UtcNow) return false;
            var status = await leaseGuard(new(jobId, fence), ct);
            ct.ThrowIfCancellationRequested();
            if (status is null || status.ExpiresAt <= DateTimeOffset.UtcNow) return false;
            journal.UpdateExpiry(jobId, fence, status.ExpiresAt);
            return true;
        }
    }
}
// end
