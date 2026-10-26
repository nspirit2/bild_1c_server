using BuildSystem.Agent;
using BuildSystem.Contracts;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class ArtifactJournalTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "artifact-journal-tests", Guid.NewGuid().ToString("N"));
    private static LeaseGrant Grant(string kind = "ArchiveStageOnly") => new(Guid.NewGuid(), 42, DateTimeOffset.UtcNow.AddMinutes(5), kind, "never-store-snapshot");
    private static ReportStagedArtifact Report(LeaseGrant grant) => new(grant.JobId, grant.FencingToken,
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), 123, "dt");
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Fact]
    public void ReopenCanonicalReplayAndAckRetainLeaseAndEvents()
    {
        var grant = Grant(); var report = Report(grant); var eventId = Guid.NewGuid();
        using (var journal = new AgentJournal(_directory))
        {
            journal.StoreGrant(grant); journal.EnqueueArtifact(report);
            journal.Enqueue(new(eventId, grant.JobId, grant.FencingToken, "LeaseOutcomeUnknown"));
            Assert.Equal("ArchiveStageOnly", Assert.Single(journal.Unsettled()).Kind);
        }
        using (var journal = new AgentJournal(_directory))
        {
            journal.EnqueueArtifact(report with { Sha256 = report.Sha256.ToUpperInvariant() });
            var item = Assert.Single(journal.PendingArtifacts());
            Assert.Equal(report with { Sha256 = report.Sha256.ToUpperInvariant() }, item.Report);
            Assert.Equal(grant.ExpiresAt, item.ExpiresAt); Assert.Equal("Pending", item.State);
            journal.AcknowledgeArtifact(report.ArtifactId);
            Assert.Empty(journal.PendingArtifacts()); Assert.Single(journal.Unsettled());
            Assert.Single(journal.Pending()); Assert.False(journal.IsSettled(grant.JobId, grant.FencingToken));
        }
        using (var journal = new AgentJournal(_directory))
        {
            journal.MarkSettled(grant.JobId, grant.FencingToken);
            journal.EnqueueArtifact(report);
            Assert.Equal("Acked", journal.ArtifactState(report.ArtifactId));
            Assert.Empty(journal.PendingArtifacts());
        }
        Assert.DoesNotContain("never-store-snapshot", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(_directory, "agent-journal.sqlite"))));
    }

    [Fact]
    public void ImmutablePayloadAndBindingsSurviveAcknowledgement()
    {
        var grant = Grant(); var report = Report(grant);
        using var journal = new AgentJournal(_directory);
        journal.StoreGrant(grant); journal.EnqueueArtifact(report); journal.AcknowledgeArtifact(report.ArtifactId);
        foreach (var changed in new[] { report with { SizeBytes = 124 }, report with { Sha256 = new string('b', 64) },
            report with { ReservationId = Guid.NewGuid() }, report with { ArchiveVersionId = Guid.NewGuid() },
            report with { Format = "bak" }, report with { JobId = Guid.NewGuid() }, report with { FencingToken = 43 } })
            Assert.Throws<InvalidOperationException>(() => journal.EnqueueArtifact(changed));
        Assert.Throws<SqliteException>(() => journal.EnqueueArtifact(report with { ArtifactId = Guid.NewGuid() }));
        journal.MarkSettled(grant.JobId, grant.FencingToken);
        var other = Grant(); journal.StoreGrant(other);
        Assert.Throws<SqliteException>(() => journal.EnqueueArtifact(report with { JobId = other.JobId, ArtifactId = Guid.NewGuid() }));
        Assert.Equal("Acked", journal.ArtifactState(report.ArtifactId)); Assert.Empty(journal.PendingArtifacts());
    }

    [Fact]
    public void ExpirationAndOverridePreserveBlockedRowsAcrossReopen()
    {
        var grant = Grant(); var report = Report(grant);
        using (var journal = new AgentJournal(_directory))
        {
            journal.StoreGrant(grant); journal.EnqueueArtifact(report);
            journal.UpdateExpiry(grant.JobId, grant.FencingToken, DateTimeOffset.UtcNow.AddMinutes(-1));
            var item = Assert.Single(journal.PendingArtifacts());
            Assert.Equal("Blocked", item.State); Assert.Equal("LeaseExpired", item.FailureCode);
            journal.MarkSettled(grant.JobId, grant.FencingToken);
        }
        using (var journal = new AgentJournal(_directory))
        {
            var item = Assert.Single(journal.PendingArtifacts()); Assert.Equal("LeaseExpired", item.FailureCode);
            Assert.Throws<InvalidOperationException>(() => journal.AcknowledgeArtifact(report.ArtifactId));
            journal.EnqueueArtifact(report); Assert.Equal("Blocked", journal.ArtifactState(report.ArtifactId));
        }
    }

    [Fact]
    public void ProbeAcknowledgementCannotSettleArtifactLeaseAndOverrideDoesNotAckArtifact()
    {
        var grant = Grant(); var report = Report(grant); var eventId = Guid.NewGuid();
        using var journal = new AgentJournal(_directory);
        journal.StoreGrant(grant); journal.EnqueueArtifact(report);
        journal.Enqueue(new(eventId, grant.JobId, grant.FencingToken, "ProbeCompleted")); journal.Acknowledge(eventId);
        Assert.Single(journal.Unsettled()); Assert.Equal("Pending", journal.ArtifactState(report.ArtifactId));
        journal.MarkSettled(grant.JobId, grant.FencingToken);
        var item = Assert.Single(journal.PendingArtifacts()); Assert.Equal("Blocked", item.State); Assert.Equal("LeaseRejected", item.FailureCode);
    }

    [Theory]
    [InlineData("LeaseExpired")]
    [InlineData("LeaseRejected")]
    [InlineData("ReceiptMismatch")]
    [InlineData("AuthRejected")]
    public void ClosedFailureCodesAreDurableAndCannotBeReplaced(string code)
    {
        var grant = Grant(); var report = Report(grant);
        using (var journal = new AgentJournal(_directory))
        {
            journal.StoreGrant(grant); journal.EnqueueArtifact(report); journal.BlockArtifact(report.ArtifactId, code);
            journal.BlockArtifact(report.ArtifactId, code);
            Assert.Throws<ArgumentException>(() => journal.BlockArtifact(report.ArtifactId, "secret diagnostic"));
            Assert.Throws<InvalidOperationException>(() => journal.BlockArtifact(report.ArtifactId, code == "LeaseExpired" ? "AuthRejected" : "LeaseExpired"));
        }
        using var reopened = new AgentJournal(_directory);
        Assert.Equal(code, Assert.Single(reopened.PendingArtifacts()).FailureCode); Assert.Single(reopened.Unsettled());
    }

    [Fact]
    public void InvalidMetadataAndNonProvisionLeasesCannotEnqueue()
    {
        var grant = Grant(); var report = Report(grant);
        using var journal = new AgentJournal(_directory);
        journal.StoreGrant(grant);
        Assert.Throws<ArgumentException>(() => journal.EnqueueArtifact(null!));
        foreach (var invalid in new[] { report with { JobId = Guid.Empty }, report with { FencingToken = 0 },
            report with { ReservationId = Guid.Empty }, report with { ArchiveVersionId = Guid.Empty },
            report with { ArtifactId = Guid.Empty }, report with { SizeBytes = 0 }, report with { SizeBytes = -1 },
            report with { Sha256 = null! }, report with { Sha256 = "AA" }, report with { Sha256 = new string('g', 64) },
            report with { Format = null! }, report with { Format = "dtbak" } })
            Assert.Throws<ArgumentException>(() => journal.EnqueueArtifact(invalid));
        Assert.Throws<InvalidOperationException>(() => journal.EnqueueArtifact(report with { FencingToken = 43 }));
        journal.UpdateExpiry(grant.JobId, grant.FencingToken, DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Throws<InvalidOperationException>(() => journal.EnqueueArtifact(report));
        journal.MarkSettled(grant.JobId, grant.FencingToken);
        Assert.Throws<InvalidOperationException>(() => journal.EnqueueArtifact(report));
        var probe = Grant("ControlPlaneProbe"); journal.StoreGrant(probe);
        Assert.Throws<InvalidOperationException>(() => journal.EnqueueArtifact(Report(probe))); Assert.Empty(journal.PendingArtifacts());
    }

    [Fact]
    public void OldSchemaMigrationRetainsEventsAndUnknownKindFailsClosed()
    {
        var grant = Grant(); var eventId = Guid.NewGuid(); Directory.CreateDirectory(_directory);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "agent-journal.sqlite"), Pooling = false }.ToString()))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = """
                CREATE TABLE leases(job TEXT NOT NULL,fence INTEGER NOT NULL,expiry TEXT NOT NULL,settled INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(job,fence));
                CREATE TABLE events(id TEXT PRIMARY KEY,job TEXT NOT NULL,fence INTEGER NOT NULL,code TEXT NOT NULL,acked INTEGER NOT NULL DEFAULT 0,FOREIGN KEY(job,fence) REFERENCES leases(job,fence));
                INSERT INTO leases(job,fence,expiry) VALUES($job,42,$expiry);
                INSERT INTO events(id,job,fence,code) VALUES($id,$job,42,'LeaseOutcomeUnknown');
                """;
            command.Parameters.AddWithValue("$job", grant.JobId.ToString()); command.Parameters.AddWithValue("$expiry", grant.ExpiresAt.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$id", eventId.ToString()); command.ExecuteNonQuery();
        }
        using (var journal = new AgentJournal(_directory))
        {
            Assert.Equal("Unknown", Assert.Single(journal.Unsettled()).Kind); Assert.Single(journal.Pending());
            journal.StoreGrant(grant);
            Assert.Throws<InvalidOperationException>(() => journal.EnqueueArtifact(Report(grant)));
            Assert.Equal("Unknown", Assert.Single(journal.Unsettled()).Kind);
        }
        using var reopened = new AgentJournal(_directory);
        Assert.Equal("Unknown", Assert.Single(reopened.Unsettled()).Kind); Assert.Single(reopened.Pending());
    }
}
