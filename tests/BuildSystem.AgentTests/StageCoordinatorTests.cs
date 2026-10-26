using System.Security.Cryptography;
using BuildSystem.Agent;
using BuildSystem.Agent.Archives;
using BuildSystem.Contracts;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class StageCoordinatorTests
{
    [Fact]
    public async Task SuccessEnqueuesExactArtifactWithoutAcknowledgingOrSettlingLease()
    {
        using var fixture = new Fixture();
        var coordinator = fixture.Coordinator();

        Assert.Equal(StageOutcome.Enqueued, await coordinator.ExecuteAsync(fixture.Intent, default));

        var item = Assert.Single(fixture.Journal.PendingArtifacts());
        var artifactId = Guid.ParseExact(Path.GetFileName(Assert.Single(fixture.ArtifactDirectories())), "N");
        Assert.Equal(new ReportStagedArtifact(fixture.Intent.JobId, fixture.Intent.FencingToken,
            fixture.Intent.ReservationId, fixture.Intent.ArchiveVersionId, artifactId,
            fixture.Intent.Sha256.ToUpperInvariant(), fixture.Intent.SizeBytes, fixture.Intent.Format), item.Report);
        Assert.Equal("Pending", item.State);
        Assert.Equal(2, fixture.GuardCalls);
        Assert.Null(fixture.Journal.StageIntentFailure(fixture.Intent.JobId, fixture.Intent.FencingToken));
        Assert.Empty(fixture.Journal.Pending());
        Assert.Single(fixture.Journal.Unsettled());
        Assert.False(fixture.Journal.IsSettled(fixture.Intent.JobId, fixture.Intent.FencingToken));
        Assert.Equal(fixture.Contents, await File.ReadAllBytesAsync(fixture.StagedData()));
    }

    [Fact]
    public async Task DeliveryBlockedReplayCannotClaimDispatchableOutcome()
    {
        using var fixture = new Fixture();
        var coordinator = fixture.Coordinator();
        Assert.Equal(StageOutcome.Enqueued, await coordinator.ExecuteAsync(fixture.Intent, default));
        var report = Assert.Single(fixture.Journal.PendingArtifacts()).Report;
        fixture.Journal.BlockArtifact(report.ArtifactId, "ReceiptMismatch");
        Assert.Equal(StageOutcome.Blocked, await coordinator.ResumeAsync(fixture.Intent.JobId, fixture.Intent.FencingToken, default));
        var blocked = Assert.Single(fixture.Journal.PendingArtifacts());
        Assert.Equal("Blocked", blocked.State);
        Assert.Equal("ReceiptMismatch", blocked.FailureCode);
        Assert.Single(fixture.ArtifactDirectories());
    }

    [Fact]
    public async Task SecondGuardRejectionPreservesPublishedFileButNeverEnqueues()
    {
        using var fixture = new Fixture();
        var calls = 0;
        var coordinator = fixture.Coordinator((_, _) =>
        {
            calls++;
            if (calls == 2)
            {
                Assert.Equal(fixture.Contents, File.ReadAllBytes(fixture.StagedData()));
                return Task.FromResult(new LeaseStatus(DateTimeOffset.UtcNow.AddMinutes(-1)));
            }
            return Task.FromResult(new LeaseStatus(DateTimeOffset.UtcNow.AddMinutes(1)));
        });

        Assert.Equal(StageOutcome.Blocked, await coordinator.ExecuteAsync(fixture.Intent, default));
        Assert.Equal(2, calls);
        Assert.Equal("LeaseRejected", fixture.Journal.StageIntentFailure(fixture.Intent.JobId, fixture.Intent.FencingToken));
        Assert.Empty(fixture.Journal.PendingArtifacts());
        Assert.Equal(fixture.Contents, await File.ReadAllBytesAsync(fixture.StagedData()));
        Assert.Single(fixture.Journal.Unsettled());
    }

    [Fact]
    public async Task CrashAfterPublicationResumesWithoutSourceAndRetainsArtifactIdentity()
    {
        using var fixture = new Fixture();
        fixture.Journal.SaveStageIntent(fixture.Intent);
        var staged = await fixture.Staging.StageAsync(fixture.Request());
        Assert.Empty(fixture.Journal.PendingArtifacts());
        fixture.ReopenJournal();
        File.Delete(fixture.SourcePath);

        Assert.Equal(StageOutcome.Enqueued, await fixture.Coordinator().ResumeAsync(
            fixture.Intent.JobId, fixture.Intent.FencingToken, default));
        Assert.Equal(Guid.ParseExact(staged.ArtifactId, "N"), Assert.Single(fixture.Journal.PendingArtifacts()).Report.ArtifactId);
        Assert.Equal(2, fixture.GuardCalls);
        Assert.Single(fixture.ArtifactDirectories());
        Assert.Equal(fixture.Contents, await File.ReadAllBytesAsync(fixture.StagedData()));
    }

    [Fact]
    public async Task DifferentIntentWhileFirstGuardIsPendingCannotOverwriteDurablePins()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = fixture.Coordinator(async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return new LeaseStatus(DateTimeOffset.UtcNow.AddMinutes(1));
        });
        var first = coordinator.ExecuteAsync(fixture.Intent, default);
        await entered.Task;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ExecuteAsync(
                fixture.Intent with { ArchiveVersionId = Guid.NewGuid(), Sha256 = new string('b', 64) }, default));
            Assert.Equal(fixture.Intent with { Sha256 = fixture.Intent.Sha256.ToUpperInvariant() },
                fixture.Journal.GetStageIntent(fixture.Intent.JobId, fixture.Intent.FencingToken));
            Assert.Empty(fixture.ArtifactDirectories());
        }
        finally { release.TrySetResult(); }
        Assert.Equal(StageOutcome.Enqueued, await first);
        Assert.Equal(fixture.Intent.ArchiveVersionId, Assert.Single(fixture.Journal.PendingArtifacts()).Report.ArchiveVersionId);
    }

    [Fact]
    public async Task SourceChangedAfterPinsIsArchiveChangedWithoutOutbox()
    {
        using var fixture = new Fixture();
        fixture.Journal.SaveStageIntent(fixture.Intent);
        var changed = fixture.Contents.ToArray();
        changed[0] ^= 0xff;
        await File.WriteAllBytesAsync(fixture.SourcePath, changed);

        Assert.Equal(StageOutcome.Blocked, await fixture.Coordinator().ResumeAsync(
            fixture.Intent.JobId, fixture.Intent.FencingToken, default));
        Assert.Equal("ArchiveChanged", fixture.Journal.StageIntentFailure(fixture.Intent.JobId, fixture.Intent.FencingToken));
        Assert.Equal(1, fixture.GuardCalls);
        Assert.Empty(fixture.Journal.PendingArtifacts());
        Assert.Empty(fixture.ArtifactDirectories());
    }

    [Fact]
    public async Task ExpiredLocalLeaseAndProbeGrantCannotReachGuardOrStaging()
    {
        using (var fixture = new Fixture())
        {
            fixture.Journal.SaveStageIntent(fixture.Intent);
            fixture.Journal.UpdateExpiry(fixture.Intent.JobId, fixture.Intent.FencingToken, DateTimeOffset.UtcNow.AddMinutes(-1));
            File.Delete(fixture.SourcePath);
            Assert.Equal(StageOutcome.Blocked, await fixture.Coordinator().ResumeAsync(
                fixture.Intent.JobId, fixture.Intent.FencingToken, default));
            Assert.Equal("LeaseExpired", fixture.Journal.StageIntentFailure(fixture.Intent.JobId, fixture.Intent.FencingToken));
            Assert.Equal(0, fixture.GuardCalls);
            Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.StagingPath));
            Assert.Empty(fixture.Journal.PendingArtifacts());
        }
        using (var fixture = new Fixture("ControlPlaneProbe"))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator().ExecuteAsync(fixture.Intent, default));
            Assert.Null(fixture.Journal.GetStageIntent(fixture.Intent.JobId, fixture.Intent.FencingToken));
            Assert.Equal(0, fixture.GuardCalls);
            Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.StagingPath));
            Assert.Empty(fixture.Journal.PendingArtifacts());
        }
    }

    [Fact]
    public async Task PreCancelledExecutionDoesNotPersistIntentOrTouchStaging()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Coordinator().ExecuteAsync(fixture.Intent, cancellation.Token));
        Assert.Null(fixture.Journal.GetStageIntent(fixture.Intent.JobId, fixture.Intent.FencingToken));
        Assert.Equal(0, fixture.GuardCalls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.StagingPath));
        Assert.Empty(fixture.Journal.PendingArtifacts());
    }

    [Fact]
    public async Task GuardReturningAuthorityAfterCallerCancellationCannotStageAndCancellationIsDurable()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var coordinator = fixture.Coordinator((_, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            cancellation.Cancel();
            return Task.FromResult(new LeaseStatus(DateTimeOffset.UtcNow.AddMinutes(1)));
        });

        Assert.Equal(StageOutcome.Cancelled, await coordinator.ExecuteAsync(fixture.Intent, cancellation.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.StagingPath));
        Assert.Empty(fixture.Journal.PendingArtifacts());
        fixture.ReopenJournal();
        Assert.Equal("Cancelled", fixture.Journal.StageIntentFailure(fixture.Intent.JobId, fixture.Intent.FencingToken));
        Assert.Equal(StageOutcome.Blocked, await fixture.Coordinator().ResumeAsync(
            fixture.Intent.JobId, fixture.Intent.FencingToken, default));
        Assert.Equal(0, fixture.GuardCalls);
        Assert.Empty(fixture.Journal.PendingArtifacts());
    }

    [Fact]
    public async Task CompletedArtifactReplayRetainsSingleIdentityAndAcknowledgement()
    {
        using var fixture = new Fixture();
        var coordinator = fixture.Coordinator();
        Assert.Equal(StageOutcome.Enqueued, await coordinator.ExecuteAsync(fixture.Intent, default));
        var original = Assert.Single(fixture.Journal.PendingArtifacts()).Report;
        File.Delete(fixture.SourcePath);
        Assert.Equal(StageOutcome.Enqueued, await coordinator.ResumeAsync(fixture.Intent.JobId, fixture.Intent.FencingToken, default));
        Assert.Equal(original, Assert.Single(fixture.Journal.PendingArtifacts()).Report);
        fixture.Journal.AcknowledgeArtifact(original.ArtifactId);
        fixture.ReopenJournal();

        Assert.Equal(StageOutcome.Enqueued, await fixture.Coordinator().ResumeAsync(fixture.Intent.JobId, fixture.Intent.FencingToken, default));
        Assert.Equal("Acked", fixture.Journal.ArtifactState(original.ArtifactId));
        Assert.Empty(fixture.Journal.PendingArtifacts());
        Assert.Single(fixture.ArtifactDirectories());
        Assert.Single(fixture.Journal.Unsettled());
        Assert.Equal(6, fixture.GuardCalls);
        Assert.Equal(fixture.Contents, await File.ReadAllBytesAsync(fixture.StagedData()));
    }

    [Fact]
    public async Task CorruptArtifactAfterCrashIsRejectedRatherThanEnqueuedOrReplaced()
    {
        using var fixture = new Fixture();
        fixture.Journal.SaveStageIntent(fixture.Intent);
        await fixture.Staging.StageAsync(fixture.Request());
        var corrupt = fixture.Contents.ToArray();
        corrupt[^1] ^= 0xff;
        await File.WriteAllBytesAsync(fixture.StagedData(), corrupt);
        fixture.ReopenJournal();
        File.Delete(fixture.SourcePath);

        Assert.Equal(StageOutcome.Blocked, await fixture.Coordinator().ResumeAsync(
            fixture.Intent.JobId, fixture.Intent.FencingToken, default));
        Assert.NotNull(fixture.Journal.StageIntentFailure(fixture.Intent.JobId, fixture.Intent.FencingToken));
        Assert.Equal(1, fixture.GuardCalls);
        Assert.Empty(fixture.Journal.PendingArtifacts());
        Assert.Equal(corrupt, await File.ReadAllBytesAsync(fixture.StagedData()));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "stage-coordinator-tests", Guid.NewGuid().ToString("N"));
        public byte[] Contents { get; } = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
        public string SourcePath { get; }
        public string StagingPath { get; }
        private string DataPath => Path.Combine(directory, "journal");
        public AgentJournal Journal { get; private set; }
        public ArchiveStaging Staging { get; }
        public StageIntent Intent { get; }
        public int GuardCalls { get; private set; }

        public Fixture(string kind = "ProvisionArchive")
        {
            var sourceRoot = Path.Combine(directory, "source");
            Directory.CreateDirectory(sourceRoot);
            SourcePath = Path.Combine(sourceRoot, "archive.dt");
            File.WriteAllBytes(SourcePath, Contents);
            StagingPath = Path.Combine(directory, "staging");
            var rootId = Guid.NewGuid();
            Staging = new ArchiveStaging([new(rootId, sourceRoot, true)], StagingPath, 1024 * 1024, 4 * 1024 * 1024);
            var grant = new LeaseGrant(Guid.NewGuid(), 7, DateTimeOffset.UtcNow.AddMinutes(5), kind, "not-persisted");
            Intent = new(grant.JobId, grant.FencingToken, Guid.NewGuid(), Guid.NewGuid(), rootId,
                "archive.dt", Convert.ToHexString(SHA256.HashData(Contents)).ToLowerInvariant(), Contents.LongLength, "dt");
            Journal = new AgentJournal(DataPath);
            Journal.StoreGrant(grant);
        }

        public StageCoordinator Coordinator(Func<LeaseHeartbeat, CancellationToken, Task<LeaseStatus>>? guard = null) =>
            new(Journal, Staging, guard ?? ((heartbeat, _) =>
            {
                Assert.Equal(new LeaseHeartbeat(Intent.JobId, Intent.FencingToken), heartbeat);
                GuardCalls++;
                return Task.FromResult(new LeaseStatus(DateTimeOffset.UtcNow.AddMinutes(1)));
            }));

        public StageRequest Request() => new(Intent.ReservationId, Intent.ArchiveVersionId,
            new(Intent.RootId, Intent.RelativePath, Intent.Sha256, Intent.SizeBytes, ArchiveFormat.Dt));
        public string[] ArtifactDirectories() => Directory.GetDirectories(StagingPath);
        public string StagedData() => Path.Combine(Assert.Single(ArtifactDirectories()), "data");
        public void ReopenJournal() { Journal.Dispose(); Journal = new AgentJournal(DataPath); }
        public void Dispose() { Journal.Dispose(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
