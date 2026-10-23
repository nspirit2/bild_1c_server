using BuildSystem.Agent;
using BuildSystem.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class ArtifactDeliveryTests
{
    private sealed class Transport : IArtifactTransport
    {
        public List<ReportStagedArtifact> Sent { get; } = [];
        public Exception? Failure { get; set; }
        public bool Mismatch { get; set; }
        public Task<StagedArtifactReceipt> ReportArtifactAsync(ReportStagedArtifact report, CancellationToken ct)
        {
            Sent.Add(report);
            if (Failure is { } error) { Failure = null; return Task.FromException<StagedArtifactReceipt>(error); }
            return Task.FromResult(new StagedArtifactReceipt(Mismatch ? Guid.NewGuid() : report.ArtifactId, "ContentVerified"));
        }
    }
    private static string DirectoryPath() => Path.Combine(Path.GetTempPath(), "bs-artifact-delivery-" + Guid.NewGuid().ToString("N"));
    private static ReportStagedArtifact Prepare(AgentJournal journal)
    {
        var report = new ReportStagedArtifact(Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('A', 64), 10, "dt");
        journal.StoreGrant(new(report.JobId, 1, DateTimeOffset.UtcNow.AddMinutes(2), "ProvisionArchive", "{}"));
        journal.EnqueueArtifact(report);
        return report;
    }
    [Fact]
    public async Task LostResponseAndRestartReplaySamePersistedPayloadThenAckWithoutSettlement()
    {
        var path = DirectoryPath();
        try
        {
            var transport = new Transport { Failure = new HttpRequestException("lost response") };
            ReportStagedArtifact report;
            using (var journal = new AgentJournal(path))
            {
                report = Prepare(journal);
                Assert.Equal(ArtifactDeliveryResult.RetryLater, await new ArtifactDelivery(journal, transport).FlushAsync(CancellationToken.None));
                Assert.Equal("Pending", journal.ArtifactState(report.ArtifactId));
            }
            using var reopened = new AgentJournal(path);
            Assert.Equal(ArtifactDeliveryResult.Acknowledged, await new ArtifactDelivery(reopened, transport).FlushAsync(CancellationToken.None));
            Assert.Equal(transport.Sent[0], transport.Sent[1]);
            Assert.Equal(report, transport.Sent[1]);
            Assert.Equal("Acked", reopened.ArtifactState(report.ArtifactId));
            Assert.Single(reopened.Unsettled());
            Assert.Equal(ArtifactDeliveryResult.Empty, await new ArtifactDelivery(reopened, transport).FlushAsync(CancellationToken.None));
        }
        finally { Directory.Delete(path, true); }
    }
    [Theory]
    [InlineData(401, "AuthRejected")]
    [InlineData(403, "AuthRejected")]
    [InlineData(409, "LeaseRejected")]
    [InlineData(410, "LeaseRejected")]
    public async Task PermanentRejectionIsDurablyBlockedAndNeverAutomaticallyRetried(int code, string failure)
    {
        var path = DirectoryPath();
        try
        {
            var transport = new Transport { Failure = new HttpRequestException("rejected", null, (HttpStatusCode)code) };
            using (var journal = new AgentJournal(path))
            {
                Prepare(journal);
                Assert.Equal(ArtifactDeliveryResult.Blocked, await new ArtifactDelivery(journal, transport).FlushAsync(CancellationToken.None));
                Assert.Equal(failure, Assert.Single(journal.PendingArtifacts()).FailureCode);
            }
            using var reopened = new AgentJournal(path);
            Assert.Equal(ArtifactDeliveryResult.Blocked, await new ArtifactDelivery(reopened, transport).FlushAsync(CancellationToken.None));
            Assert.Single(transport.Sent);
        }
        finally { Directory.Delete(path, true); }
    }
    [Fact]
    public async Task ExpiredLeaseBlocksBeforeHttp()
    {
        var path = DirectoryPath();
        try
        {
            using var journal = new AgentJournal(path);
            var report = Prepare(journal);
            journal.UpdateExpiry(report.JobId, 1, DateTimeOffset.UtcNow.AddSeconds(-1));
            var transport = new Transport();
            Assert.Equal(ArtifactDeliveryResult.Blocked, await new ArtifactDelivery(journal, transport).FlushAsync(CancellationToken.None));
            Assert.Empty(transport.Sent);
            Assert.Equal("LeaseExpired", Assert.Single(journal.PendingArtifacts()).FailureCode);
        }
        finally { Directory.Delete(path, true); }
    }
    [Fact]
    public async Task WrongReceiptCannotAcknowledgeOutbox()
    {
        var path = DirectoryPath();
        try
        {
            using var journal = new AgentJournal(path);
            var report = Prepare(journal);
            var transport = new Transport { Mismatch = true };
            Assert.Equal(ArtifactDeliveryResult.Blocked, await new ArtifactDelivery(journal, transport).FlushAsync(CancellationToken.None));
            Assert.Equal("Blocked", journal.ArtifactState(report.ArtifactId));
            Assert.Single(journal.Unsettled());
        }
        finally { Directory.Delete(path, true); }
    }
    private sealed class ProbeTransport : IAgentTransport
    {
        public int Polls { get; private set; }
        public int Events { get; private set; }
        public Task CapabilitiesAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<LeaseGrant?> PollAsync(CancellationToken ct) { Polls++; return Task.FromResult<LeaseGrant?>(null); }
        public Task<LeaseStatus> HeartbeatAsync(LeaseHeartbeat value, CancellationToken ct) => throw new InvalidOperationException();
        public Task ReportAsync(ReportAgentEvent value, CancellationToken ct) { Events++; return Task.CompletedTask; }
    }

    [Fact]
    public async Task ProbeWorkerDrainsPersistedReportButNeverPollsOrSendsProbeRecoveryForProvisioningLease()
    {
        var path = DirectoryPath();
        try
        {
            using var journal = new AgentJournal(path);
            var report = Prepare(journal);
            var probe = new ProbeTransport();
            var artifacts = new Transport();
            var worker = new ProbeWorker(probe, journal, NullLogger<ProbeWorker>.Instance, new ArtifactDelivery(journal, artifacts));
            await worker.WorkOnce(CancellationToken.None);
            await worker.WorkOnce(CancellationToken.None);
            Assert.Single(artifacts.Sent);
            Assert.Equal("Acked", journal.ArtifactState(report.ArtifactId));
            Assert.Single(journal.Unsettled());
            Assert.Equal(0, probe.Polls);
            Assert.Equal(0, probe.Events);
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public async Task CallerCancellationCannotAcknowledgeReport()
    {
        var path = DirectoryPath();
        try
        {
            using var journal = new AgentJournal(path);
            var report = Prepare(journal);
            using var ct = new CancellationTokenSource();
            ct.Cancel();
            var transport = new Transport();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ArtifactDelivery(journal, transport).FlushAsync(ct.Token));
            Assert.Equal("Pending", journal.ArtifactState(report.ArtifactId));
            Assert.Empty(transport.Sent);
        }
        finally { Directory.Delete(path, true); }
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task TransientResponseKeepsPending(int status)
    {
        var path = DirectoryPath();
        try
        {
            using var journal = new AgentJournal(path);
            var report = Prepare(journal);
            var transport = new Transport { Failure = new HttpRequestException("transient", null, (HttpStatusCode)status) };
            Assert.Equal(ArtifactDeliveryResult.RetryLater, await new ArtifactDelivery(journal, transport).FlushAsync(CancellationToken.None));
            Assert.Equal("Pending", journal.ArtifactState(report.ArtifactId));
        }
        finally { Directory.Delete(path, true); }
    }
}
// end
