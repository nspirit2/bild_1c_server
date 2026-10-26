using System.Security.Cryptography;
using BuildSystem.Agent;
using BuildSystem.Agent.Archives;
using BuildSystem.Contracts;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class StageWorkerTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "stage-worker-tests", Guid.NewGuid().ToString("N"));
    private readonly AgentJournal journal;
    private readonly Fake transport = new();
    private readonly ArchiveStaging staging;
    private readonly StageLeaseGrant grant;
    public StageWorkerTests()
    {
        var source = Path.Combine(directory, "source"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "archive.dt"), "test archive bytes");
        var bytes = File.ReadAllBytes(Path.Combine(source, "archive.dt")); var root = Guid.NewGuid();
        staging = new ArchiveStaging([new(root, source, true)], Path.Combine(directory, "stage"), 100000, 1000000);
        journal = new AgentJournal(Path.Combine(directory, "journal"));
        grant = new(Guid.NewGuid(), 1, DateTimeOffset.UtcNow.AddMinutes(5), "ArchiveStageOnly",
            new(Guid.NewGuid(), Guid.NewGuid(), root, "archive.dt", Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length, "dt"));
        transport.Grant = grant;
    }
    private StageWorker Worker() => new(journal, transport, new StageCoordinator(journal, staging, transport.HeartbeatStageAsync), new(journal, transport));
    private void Store(string kind = "ArchiveStageOnly") => journal.StoreGrant(new(grant.JobId, grant.FencingToken, grant.ExpiresAt, kind, "{}"));

    [Fact]
    public async Task StageThenDeliverThenAckBlocksNewPollAndRestaging()
    {
        var worker = Worker(); await worker.WorkOnce(default);
        Assert.Single(journal.PendingArtifacts()); Assert.Equal(1, transport.Polls); Assert.Equal(2, transport.Beats);
        await worker.WorkOnce(default); await worker.WorkOnce(default);
        Assert.Empty(journal.PendingArtifacts()); Assert.Single(journal.Unsettled());
        Assert.Equal(1, transport.Reports); Assert.Equal(1, transport.Polls); Assert.Equal(2, transport.Beats);
    }

    [Fact]
    public async Task MatchingCompletionSettlesAndPermitsNextPollOnlyOnLaterIteration()
    {
        var worker = new StageWorker(journal, transport, new StageCoordinator(journal, staging, transport.HeartbeatStageAsync),
            new(journal, transport), new(journal, transport));
        await worker.WorkOnce(default); // Stage.
        await worker.WorkOnce(default); // Content receipt.
        Assert.Single(journal.Unsettled());
        await worker.WorkOnce(default); // Durable completion and matching receipt.
        Assert.Empty(journal.Unsettled());
        Assert.Equal(1, transport.Completions);
        Assert.Equal(1, transport.Polls);
        Assert.Empty(journal.Pending());
        transport.Grant = null;
        await worker.WorkOnce(default);
        Assert.Equal(2, transport.Polls);
    }

    [Fact]
    public async Task LostPollAfterStoredGrantRecoversSameLeaseBeforeIntent()
    {
        Store(); await Worker().WorkOnce(default);
        Assert.Single(journal.PendingArtifacts()); Assert.Equal(1, transport.Polls);
        Assert.NotNull(journal.GetStageIntent(grant.JobId, grant.FencingToken));
    }

    [Theory]
    [InlineData("ControlPlaneProbe")]
    [InlineData("ProvisionArchive")]
    [InlineData("Unknown")]
    public async Task OtherLeaseKindNeverTouchesStageEndpoints(string kind)
    {
        Store(kind); await Worker().WorkOnce(default);
        Assert.Equal(0, transport.Polls); Assert.Equal(0, transport.Beats); Assert.Equal(0, transport.Reports);
    }

    [Theory]
    [InlineData("ProvisionArchive", "archive.dt")]
    [InlineData("ArchiveStageOnly", "../archive.dt")]
    public async Task InvalidWireGrantNeverPersistsLease(string kind, string path)
    {
        transport.Grant = grant with { Kind = kind, Plan = grant.Plan with { RelativePath = path } };
        await Assert.ThrowsAnyAsync<Exception>(() => Worker().WorkOnce(default));
        Assert.Empty(journal.Unsettled()); Assert.Empty(journal.PendingArtifacts());
    }

    [Theory]
    [InlineData("reservation")]
    [InlineData("hash")]
    [InlineData("size")]
    [InlineData("format")]
    public async Task MissingPlanFieldCannotPinLease(string field)
    {
        var p = grant.Plan;
        p = field switch {
            "reservation" => p with { ReservationId = Guid.Empty },
            "hash" => p with { Sha256 = "" },
            "size" => p with { SizeBytes = 0 },
            _ => p with { Format = "unknown" }
        };
        transport.Grant = grant with { Plan = p };
        await Assert.ThrowsAnyAsync<Exception>(() => Worker().WorkOnce(default));
        Assert.Empty(journal.Unsettled()); Assert.Empty(journal.PendingArtifacts());
    }

    [Fact]
    public async Task ExistingIntentResumesWithoutPoll()
    {
        Store(); var p = grant.Plan;
        journal.SaveStageIntent(new(grant.JobId, grant.FencingToken, p.ReservationId, p.ArchiveVersionId, p.RootId, p.RelativePath, p.Sha256, p.SizeBytes, p.Format));
        await Worker().WorkOnce(default);
        Assert.Equal(0, transport.Polls); Assert.Equal(2, transport.Beats); Assert.Single(journal.PendingArtifacts());
    }

    [Fact]
    public async Task ReplayWithDifferentFenceCannotReplacePinnedLease()
    {
        Store(); transport.Grant = grant with { FencingToken = 2 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Worker().WorkOnce(default));
        Assert.Equal(1, Assert.Single(journal.Unsettled()).FencingToken); Assert.Empty(journal.PendingArtifacts());
    }

    [Fact]
    public async Task CallerCancelledBeforePollHasNoSideEffects()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Worker().WorkOnce(cancel.Token));
        Assert.Equal(0, transport.Polls); Assert.Empty(journal.Unsettled());
    }

    private sealed class Fake : IStageTransport, IArtifactTransport, IStageCompletionTransport
    {
        public StageLeaseGrant? Grant; public int Polls; public int Beats; public int Reports; public int Completions;
        public Task<StageCompletionReceipt> CompleteStageAsync(CompleteStage report, CancellationToken ct)
        { Completions++; return Task.FromResult(new StageCompletionReceipt(report.JobId, report.FencingToken, report.ArtifactId, "StageCompleted")); }
        public Task<StageLeaseGrant?> PollStageAsync(CancellationToken ct) { Polls++; return Task.FromResult(Grant); }
        public Task<LeaseStatus> HeartbeatStageAsync(LeaseHeartbeat heartbeat, CancellationToken ct)
        { Beats++; return Task.FromResult(new LeaseStatus(DateTimeOffset.UtcNow.AddMinutes(2))); }
        public Task<StagedArtifactReceipt> ReportArtifactAsync(ReportStagedArtifact report, CancellationToken ct)
        { Reports++; return Task.FromResult(new StagedArtifactReceipt(report.ArtifactId, "ContentVerified")); }
    }
    public void Dispose() { journal.Dispose(); Directory.Delete(directory, true); }
}
// end
