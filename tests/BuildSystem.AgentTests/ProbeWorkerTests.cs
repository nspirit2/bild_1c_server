using BuildSystem.Agent;
using BuildSystem.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class ProbeWorkerTests
{
    private sealed class Transport : IAgentTransport
    {
        public LeaseGrant? Grant { get; set; }
        public List<ReportAgentEvent> Sent { get; } = [];
        public int PollCount { get; private set; }
        public bool LoseFirstResponse { get; set; }
        public Task CapabilitiesAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<LeaseGrant?> PollAsync(CancellationToken ct) { PollCount++; var value = Grant; Grant = null; return Task.FromResult(value); }
        public Task<LeaseStatus> HeartbeatAsync(LeaseHeartbeat value, CancellationToken ct) => Task.FromResult(new LeaseStatus(DateTimeOffset.UtcNow.AddMinutes(1)));
        public Task ReportAsync(ReportAgentEvent value, CancellationToken ct)
        {
            Sent.Add(value);
            if (LoseFirstResponse) { LoseFirstResponse = false; throw new HttpRequestException("simulated lost response"); }
            return Task.CompletedTask;
        }
    }
    private static string DirectoryPath() => Path.Combine(Path.GetTempPath(), "bs-agent-tests-" + Guid.NewGuid().ToString("N"));
    private static LeaseGrant Grant() => new(Guid.NewGuid(), 1, DateTimeOffset.UtcNow.AddMinutes(1), "ControlPlaneProbe", "{}");
    private static ProbeWorker Worker(Transport transport, AgentJournal journal) => new(transport, journal, NullLogger<ProbeWorker>.Instance);

    [Fact]
    public async Task LostReportResponseReplaysSameDurableEventWithoutExecutingAnotherGrant()
    {
        var path = DirectoryPath();
        try
        {
            var transport = new Transport { Grant = Grant(), LoseFirstResponse = true };
            using (var journal = new AgentJournal(path))
                await Assert.ThrowsAsync<HttpRequestException>(() => Worker(transport, journal).WorkOnce(CancellationToken.None));
            using (var reopened = new AgentJournal(path))
            {
                var pending = Assert.Single(reopened.Pending());
                await Worker(transport, reopened).WorkOnce(CancellationToken.None);
                Assert.Equal(pending, transport.Sent[1]);
                Assert.Empty(reopened.Pending());
                Assert.Empty(reopened.Unsettled());
            }
        }
        finally { Directory.Delete(path, recursive: true); }
    }
    [Fact]
    public async Task RestartWithUnfinishedGrantReportsUnknownAndNeverReexecutes()
    {
        var path = DirectoryPath();
        try
        {
            var grant = Grant();
            using var journal = new AgentJournal(path);
            journal.StoreGrant(grant);
            var transport = new Transport();
            var worker = Worker(transport, journal);
            await worker.WorkOnce(CancellationToken.None);
            await worker.WorkOnce(CancellationToken.None);
            Assert.Equal("LeaseOutcomeUnknown", Assert.Single(transport.Sent).Code);
            Assert.Single(journal.Unsettled());
            Assert.Equal(0, transport.PollCount);
        }
        finally { Directory.Delete(path, recursive: true); }
    }
    [Fact]
    public async Task ExpiredLocalLeaseBlocksReportingAndNewPoll()
    {
        var path = DirectoryPath();
        try
        {
            using var journal = new AgentJournal(path);
            journal.StoreGrant(Grant() with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) });
            var transport = new Transport();
            await Worker(transport, journal).WorkOnce(CancellationToken.None);
            Assert.Equal(0, transport.PollCount);
            Assert.Empty(transport.Sent);
        }
        finally { Directory.Delete(path, recursive: true); }
    }
    [Theory]
    [InlineData("http://localhost")]
    [InlineData("https://user:password@example.invalid")]
    [InlineData("https://example.invalid/path")]
    [InlineData("https://example.invalid/?token=secret")]
    public void TransportRejectsUnsafeOrigins(string origin) =>
        Assert.Throws<ArgumentException>(() => new AgentTransport(new Uri(origin), new string('A', 64)));
}
// end
