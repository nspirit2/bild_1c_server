using BuildSystem.Agent.Archives;
using BuildSystem.Contracts;

namespace BuildSystem.Agent;

public enum StageOutcome { Enqueued, Blocked, Cancelled }

/// <summary>Stage-only orchestration. All journal access belongs to this serialized pump;
/// the concurrent staging task performs filesystem work only. Never restores or settles a lease.</summary>
public sealed class StageCoordinator
{
    private readonly AgentJournal journal;
    private readonly Func<StageRequest, CancellationToken, Task<VerifiedArtifact>> stage;
    private readonly Func<LeaseHeartbeat, CancellationToken, Task<LeaseStatus>> leaseGuard;
    private readonly TimeProvider clock;
    private readonly TimeSpan renewalInterval;

    public StageCoordinator(AgentJournal journal, ArchiveStaging staging,
        Func<LeaseHeartbeat, CancellationToken, Task<LeaseStatus>> leaseGuard,
        TimeProvider? clock = null, TimeSpan? renewalInterval = null)
        : this(journal, staging.StageAsync, leaseGuard, clock, renewalInterval) { }

    internal StageCoordinator(AgentJournal journal,
        Func<StageRequest, CancellationToken, Task<VerifiedArtifact>> stage,
        Func<LeaseHeartbeat, CancellationToken, Task<LeaseStatus>> leaseGuard,
        TimeProvider? clock = null, TimeSpan? renewalInterval = null)
    {
        this.journal = journal;
        this.stage = stage;
        this.leaseGuard = leaseGuard;
        this.clock = clock ?? TimeProvider.System;
        this.renewalInterval = renewalInterval ?? TimeSpan.FromSeconds(30);
        if (this.renewalInterval <= TimeSpan.Zero || this.renewalInterval > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(renewalInterval));
    }

    public async Task<StageOutcome> ExecuteAsync(StageIntent intent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        journal.SaveStageIntent(intent); // Pin identity before filesystem side effects.
        return await ResumeAsync(intent.JobId, intent.FencingToken, ct);
    }

    public async Task<StageOutcome> ResumeAsync(Guid jobId, long fence, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var intent = journal.GetStageIntent(jobId, fence) ?? throw new InvalidOperationException("No durable stage intent.");
        if (journal.StageIntentFailure(jobId, fence) is not null) return StageOutcome.Blocked;
        var lease = journal.Unsettled().SingleOrDefault(x => x.JobId == jobId && x.FencingToken == fence);
        if (lease is null || lease.Kind != "ArchiveStageOnly") return Block("LeaseRejected");
        var expiry = lease.ExpiresAt;
        if (expiry <= clock.GetUtcNow()) return Block("LeaseExpired");
        try
        {
            await GuardAsync(allowShortening: true);
            var fingerprint = new SourceFingerprint(intent.RootId, intent.RelativePath, intent.Sha256,
                intent.SizeBytes, intent.Format == "dt" ? ArchiveFormat.Dt : ArchiveFormat.Bak);
            using var deadline = new CancellationTokenSource(Remaining(), clock);
            using var copyCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            var copy = stage(new(intent.ReservationId, intent.ArchiveVersionId, fingerprint), copyCancellation.Token);
            VerifiedArtifact artifact;
            try
            {
                while (!copy.IsCompleted)
                {
                    using var tickCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
                    var tick = Task.Delay(renewalInterval, clock, tickCancellation.Token);
                    await Task.WhenAny(copy, tick);
                    // Prefer completed filesystem work over an unnecessary heartbeat, even on a tie.
                    if (copy.IsCompleted)
                    {
                        tickCancellation.Cancel();
                        try { await tick; } catch (OperationCanceledException) { }
                        break;
                    }
                    await tick;
                    await GuardAsync();
                    // A fired deadline cannot be revived by an in-flight response.
                    if (deadline.IsCancellationRequested) throw new LeaseFailure("LeaseExpired");
                    deadline.CancelAfter(Remaining());
                }
                artifact = await copy;
                ct.ThrowIfCancellationRequested();
                if (deadline.IsCancellationRequested) throw new LeaseFailure("LeaseExpired");
            }
            catch
            {
                // Observe filesystem completion before touching terminal journal state or returning.
                // A cancellation-insensitive OS operation may delay this; it never receives an enqueue.
                copyCancellation.Cancel();
                try { await copy; } catch (Exception) { }
                throw;
            }
            await GuardAsync(); // Mandatory post-copy authority check, including old-lease deadline.
            if (!Guid.TryParseExact(artifact.ArtifactId, "N", out var artifactId) ||
                artifact.Size != intent.SizeBytes || !string.Equals(artifact.Sha256, intent.Sha256, StringComparison.OrdinalIgnoreCase))
                return Block("StageFailed");
            journal.EnqueueArtifact(new(jobId, fence, intent.ReservationId, intent.ArchiveVersionId,
                artifactId, intent.Sha256, intent.SizeBytes, intent.Format));
            return journal.ArtifactState(artifactId) == "Blocked" ? StageOutcome.Blocked : StageOutcome.Enqueued;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            journal.FailStageIntent(jobId, fence, "Cancelled");
            return StageOutcome.Cancelled;
        }
        catch (LeaseFailure error) { return Block(error.Code); }
        catch (ArchiveException error)
        {
            return Block(error.Failure == ArchiveFailure.ArchiveChanged ? "ArchiveChanged" : "ArchiveUnavailable");
        }
        catch (HttpRequestException) { return Block("LeaseRejected"); }
        catch (OperationCanceledException)
        { return Block(expiry <= clock.GetUtcNow() ? "LeaseExpired" : "LeaseRejected"); }

        StageOutcome Block(string reason)
        {
            journal.FailStageIntent(jobId, fence, reason);
            return StageOutcome.Blocked;
        }
        TimeSpan Remaining()
        {
            var remaining = expiry - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero) throw new LeaseFailure("LeaseExpired");
            return remaining;
        }
        async Task GuardAsync(bool allowShortening = false)
        {
            ct.ThrowIfCancellationRequested();
            var current = journal.Unsettled().SingleOrDefault(x => x.JobId == jobId && x.FencingToken == fence);
            if (current is null || current.Kind != "ArchiveStageOnly") throw new LeaseFailure("LeaseRejected");
            // The old expiry bounds the request and the response, including final verification.
            using var guardDeadline = new CancellationTokenSource(Remaining(), clock);
            using var guardCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, guardDeadline.Token);
            LeaseStatus status;
            try { status = await leaseGuard(new(jobId, fence), guardCancellation.Token); }
            catch (Exception error) when (error is InvalidOperationException or IOException)
            { throw new LeaseFailure("LeaseRejected"); }
            ct.ThrowIfCancellationRequested();
            var now = clock.GetUtcNow();
            if (guardDeadline.IsCancellationRequested || expiry <= now) throw new LeaseFailure("LeaseExpired");
            // Before copy starts, a shorter authoritative grant is conservative. Once copying,
            // renewal/final checks may never regress the established deadline.
            if (status is null || status.ExpiresAt <= now || (!allowShortening && status.ExpiresAt < expiry))
                throw new LeaseFailure("LeaseRejected");
            journal.UpdateExpiry(jobId, fence, status.ExpiresAt);
            expiry = status.ExpiresAt;
        }
    }

    private sealed class LeaseFailure(string code) : Exception("Stage lease authority rejected.")
    {
        public string Code { get; } = code;
    }
}
// end
