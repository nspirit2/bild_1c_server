using BuildSystem.Agent;
using BuildSystem.Contracts;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class JournalTests
{
    private static string TemporaryDirectory() => Path.Combine(Path.GetTempPath(), "agent-journal-tests", Guid.NewGuid().ToString("N"));
    private static LeaseGrant Grant(Guid job, DateTimeOffset expiry) => new(job, 42, expiry, "ControlPlaneProbe", "{\"secret\":\"never-save-me\"}");

    [Fact]
    public void ReopenReplaysIdenticalEventUntilAcknowledged()
    {
        var directory = TemporaryDirectory(); var job = Guid.NewGuid(); var id = Guid.NewGuid();
        var grant = Grant(job, DateTimeOffset.UtcNow.AddMinutes(5));
        var report = new ReportAgentEvent(id, job, 42, "ProbeCompleted");
        try
        {
            using (var journal = new AgentJournal(directory))
            {
                journal.StoreGrant(grant);
                journal.Enqueue(report);
                Assert.Equal(report, Assert.Single(journal.Pending()));
            }
            using (var journal = new AgentJournal(directory))
            {
                Assert.Single(journal.Unsettled());
                journal.StoreGrant(grant); // same grant redelivery cannot create another lease
                journal.Enqueue(report); // same idempotency key and payload is harmless
                Assert.Equal(report, Assert.Single(journal.Pending()));
                journal.Acknowledge(id);
                Assert.Empty(journal.Pending());
                Assert.Empty(journal.Unsettled());
                Assert.True(journal.IsSettled(job, 42));
                journal.StoreGrant(grant); // a lost poll response must not unsettle a finished probe
                Assert.True(journal.IsSettled(job, 42));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ConflictAndDuplicateLeaseEventsAreRejected()
    {
        var directory = TemporaryDirectory(); var job = Guid.NewGuid(); var id = Guid.NewGuid(); var expiry = DateTimeOffset.UtcNow.AddMinutes(5);
        try
        {
            using var journal = new AgentJournal(directory);
            journal.StoreGrant(Grant(job, expiry));
            Assert.Throws<InvalidOperationException>(() => journal.StoreGrant(Grant(job, expiry.AddSeconds(1))));
            journal.Enqueue(new(id, job, 42, "ProbeCompleted"));
            Assert.Throws<InvalidOperationException>(() => journal.Enqueue(new(id, job, 42, "WorkerUnsupported")));
            Assert.Throws<InvalidOperationException>(() => journal.Enqueue(new(Guid.NewGuid(), job, 42, "ProbeCompleted")));
            Assert.Throws<ArgumentException>(() => journal.Enqueue(new(Guid.NewGuid(), job, 42, "RunShell")));
            Assert.Single(journal.Pending());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void UnknownOutcomeAckRetainsUnsettledLeaseAndNoSecretOrSnapshotIsStored()
    {
        var directory = TemporaryDirectory(); var job = Guid.NewGuid(); var expiry = DateTimeOffset.UtcNow.AddMinutes(3); var id = Guid.NewGuid();
        try
        {
            using (var journal = new AgentJournal(directory))
            {
                journal.StoreGrant(Grant(job, expiry));
                journal.Enqueue(new(id, job, 42, "LeaseOutcomeUnknown"));
                journal.Acknowledge(id);
                Assert.Single(journal.Unsettled());
                Assert.Empty(journal.Pending());
                Assert.True(journal.HasEvent(job, 42, "LeaseOutcomeUnknown"));
                Assert.Throws<InvalidOperationException>(() => journal.StoreGrant(Grant(Guid.NewGuid(), expiry)));
            }
            var bytes = File.ReadAllBytes(Path.Combine(directory, "agent-journal.sqlite"));
            Assert.DoesNotContain("never-save-me", System.Text.Encoding.UTF8.GetString(bytes));
            using var reopened = new AgentJournal(directory);
            Assert.Single(reopened.Unsettled());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void JournalDirectoryAllowsOnlyOneLiveOwner()
    {
        var directory = TemporaryDirectory();
        try
        {
            using (var owner = new AgentJournal(directory))
                Assert.Throws<IOException>(() => new AgentJournal(directory));
            using var nextOwner = new AgentJournal(directory);
            Assert.Empty(nextOwner.Unsettled());
        }
        finally { Directory.Delete(directory, true); }
    }
}
