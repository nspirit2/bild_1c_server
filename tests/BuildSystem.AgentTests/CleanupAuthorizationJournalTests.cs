using System.Security.Cryptography;
using System.Text;
using BuildSystem.Agent;
using BuildSystem.Contracts;
using Xunit;
namespace BuildSystem.AgentTests;

public sealed class CleanupAuthorizationJournalTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "cleanup-authorization-tests", Guid.NewGuid().ToString("N"));
    private AgentJournal journal;
    private readonly StageCleanupAuthorization grant;
    public CleanupAuthorizationJournalTests()
    {
        journal = new(directory);
        var reservation = Guid.NewGuid(); var version = Guid.NewGuid();
        var identity = SHA256.HashData(Encoding.UTF8.GetBytes($"archive-stage-v1:{reservation:N}:{version:N}"));
        identity[6] = (byte)((identity[6] & 15) | 128); identity[8] = (byte)((identity[8] & 63) | 128);
        grant = new(Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new Guid(identity.AsSpan(0, 16), true), reservation, version, 3, new string('A', 64), 10, "dt"), DateTimeOffset.UtcNow);
    }
    private StageIntent Intent() => new(grant.Plan.JobId, grant.Plan.FencingToken, grant.Plan.ReservationId, grant.Plan.ArchiveVersionId, Guid.NewGuid(), "sample.dt", grant.Plan.Sha256, grant.Plan.SizeBytes, grant.Plan.Format);
    private void Stage(bool ackArtifact = true, bool ackCompletion = true, bool saveIntent = true)
    {
        var p = grant.Plan;
        journal.StoreGrant(new(p.JobId, p.FencingToken, DateTimeOffset.UtcNow.AddMinutes(3), "ArchiveStageOnly", "{}"));
        if (saveIntent) journal.SaveStageIntent(Intent());
        journal.EnqueueArtifact(new(p.JobId, p.FencingToken, p.ReservationId, p.ArchiveVersionId, p.ArtifactId, p.Sha256, p.SizeBytes, p.Format));
        if (!ackArtifact) return;
        journal.AcknowledgeArtifact(p.ArtifactId);
        journal.EnqueueCompletion(new(p.JobId, p.FencingToken, p.ArtifactId));
        if (ackCompletion) journal.AcknowledgeCompletion(new(p.JobId, p.FencingToken, p.ArtifactId, "StageCompleted"));
    }
    private void Reopen() { journal.Dispose(); journal = new(directory); }
    [Fact]
    public void ExactReplayAndReopenRetainSinglePreparedImmutableRecord()
    {
        Stage(); journal.StoreCleanupAuthorization(grant); Reopen(); journal.StoreCleanupAuthorization(grant);
        Assert.Equal(grant, journal.GetCleanupAuthorization(grant.AuthorizationId));
        Assert.Equal(grant, Assert.Single(journal.PendingCleanupAuthorizations()));
        Assert.True(journal.IsSettled(grant.Plan.JobId, grant.Plan.FencingToken));
        Assert.Equal("Acked", journal.ArtifactState(grant.Plan.ArtifactId));
        Assert.Equal("Acked", journal.CompletionState(grant.Plan.JobId, grant.Plan.FencingToken));
    }
    [Fact]
    public void MissingLocalLeaseRejectsAuthorization()
    {
        Assert.Throws<InvalidOperationException>(() => journal.StoreCleanupAuthorization(grant));
        Assert.Empty(journal.PendingCleanupAuthorizations());
    }
    [Fact]
    public void OperatorMarkSettledIsNotCompletionProof()
    {
        Stage(ackCompletion: false); journal.MarkSettled(grant.Plan.JobId, grant.Plan.FencingToken);
        Assert.Equal("Blocked", journal.CompletionState(grant.Plan.JobId, grant.Plan.FencingToken));
        Assert.Throws<InvalidOperationException>(() => journal.StoreCleanupAuthorization(grant));
    }
    [Fact]
    public void UnacknowledgedArtifactAndMissingCompletionRejectAuthorization()
    {
        Stage(ackArtifact: false); journal.MarkSettled(grant.Plan.JobId, grant.Plan.FencingToken);
        Assert.Throws<InvalidOperationException>(() => journal.StoreCleanupAuthorization(grant));
    }
    [Fact]
    public void MissingStageIntentRejectsEvenAcknowledgedCompletion()
    {
        Stage(saveIntent: false);
        Assert.Throws<InvalidOperationException>(() => journal.StoreCleanupAuthorization(grant));
    }
    [Fact]
    public void MatchingIdentityRequiresExactFenceAndMetadata()
    {
        Stage();
        Assert.Throws<InvalidOperationException>(() => journal.StoreCleanupAuthorization(grant with { Plan = grant.Plan with { FencingToken = 4 } }));
        Assert.Throws<InvalidOperationException>(() => journal.StoreCleanupAuthorization(grant with { Plan = grant.Plan with { SizeBytes = 11 } }));
        Assert.Throws<InvalidOperationException>(() => journal.StoreCleanupAuthorization(grant with { Plan = grant.Plan with { Sha256 = new string('a', 64) } }));
    }
    [Fact]
    public void DifferentAuthorizationCannotRebindSameRetirementOrArtifact()
    {
        Stage(); journal.StoreCleanupAuthorization(grant);
        Assert.Throws<InvalidOperationException>(() => journal.StoreCleanupAuthorization(grant with { AuthorizationId = Guid.NewGuid() }));
        Assert.Throws<InvalidOperationException>(() => journal.StoreCleanupAuthorization(grant with { AuthorizationId = Guid.NewGuid(), Plan = grant.Plan with { RetirementId = Guid.NewGuid() } }));
        Assert.Single(journal.PendingCleanupAuthorizations());
    }
    [Fact]
    public void SameAuthorizationCannotChangePayload()
    {
        Stage(); journal.StoreCleanupAuthorization(grant);
        Assert.Throws<InvalidOperationException>(() => journal.StoreCleanupAuthorization(grant with { AuthorizedAt = grant.AuthorizedAt.AddSeconds(1) }));
        Assert.Throws<InvalidOperationException>(() => journal.StoreCleanupAuthorization(grant with { Plan = grant.Plan with { RetirementId = Guid.NewGuid() } }));
        Assert.Equal(grant, journal.GetCleanupAuthorization(grant.AuthorizationId));
    }
    [Fact]
    public void OtherUnsettledLeaseBlocksBothFirstStoreAndExactReplay()
    {
        Stage(); journal.StoreCleanupAuthorization(grant);
        journal.StoreGrant(new(Guid.NewGuid(), 1, DateTimeOffset.UtcNow.AddMinutes(2), "ControlPlaneProbe", "{}"));
        Assert.Throws<InvalidOperationException>(() => journal.StoreCleanupAuthorization(grant));
        Assert.Single(journal.PendingCleanupAuthorizations());
    }
    [Fact]
    public void UnacknowledgedEventsBlockPreparationEvenWithSettledLeases()
    {
        Stage(ackCompletion: false);
        journal.Enqueue(new(Guid.NewGuid(), grant.Plan.JobId, grant.Plan.FencingToken, "WorkerUnsupported"));
        journal.AcknowledgeCompletion(new(grant.Plan.JobId, grant.Plan.FencingToken, grant.Plan.ArtifactId, "StageCompleted"));
        Assert.True(journal.IsSettled(grant.Plan.JobId, grant.Plan.FencingToken));
        Assert.Throws<InvalidOperationException>(() => journal.StoreCleanupAuthorization(grant));
        Assert.Empty(journal.PendingCleanupAuthorizations());
    }
    [Theory]
    [InlineData("authorization")]
    [InlineData("timestamp")]
    public void InvalidAuthorizationShapeIsRejectedBeforeWriting(string field)
    {
        Stage();
        var bad = field == "authorization" ? grant with { AuthorizationId = Guid.Empty } : grant with { AuthorizedAt = default };
        Assert.Throws<ArgumentException>(() => journal.StoreCleanupAuthorization(bad));
        Assert.Empty(journal.PendingCleanupAuthorizations());
    }
    [Fact]
    public void EquivalentTimestampOffsetReplaysCanonicalUtcPayload()
    {
        Stage(); journal.StoreCleanupAuthorization(grant);
        journal.StoreCleanupAuthorization(grant with { AuthorizedAt = grant.AuthorizedAt.ToOffset(TimeSpan.FromHours(3)) });
        Assert.Single(journal.PendingCleanupAuthorizations());
        Assert.Equal(TimeSpan.Zero, journal.GetCleanupAuthorization(grant.AuthorizationId)!.AuthorizedAt.Offset);
    }
    public void Dispose() { journal.Dispose(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
// end
