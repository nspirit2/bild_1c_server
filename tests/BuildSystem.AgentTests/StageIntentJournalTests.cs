using BuildSystem.Agent;
using BuildSystem.Contracts;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class StageIntentJournalTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "stage-intent-tests", Guid.NewGuid().ToString("N"));
    private static LeaseGrant Grant(string kind = "ProvisionArchive") => new(Guid.NewGuid(), 42, DateTimeOffset.UtcNow.AddMinutes(5), kind, "never-store-snapshot");
    private static StageIntent Intent(LeaseGrant grant) => new(grant.JobId, grant.FencingToken, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "folder/archive.dt", new string('a', 64), 123, "dt");
    private static ReportStagedArtifact Report(StageIntent intent) => new(intent.JobId, intent.FencingToken, intent.ReservationId, intent.ArchiveVersionId, Guid.NewGuid(), intent.Sha256, intent.SizeBytes, intent.Format);
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Fact]
    public void ReopenCanonicalShaAndExpiredFailedReplayPreserveIntent()
    {
        var grant = Grant(); var intent = Intent(grant);
        using (var journal = new AgentJournal(_directory))
        {
            journal.StoreGrant(grant);
            Assert.Equal(intent with { Sha256 = intent.Sha256.ToUpperInvariant() }, journal.SaveStageIntent(intent));
            journal.FailStageIntent(grant.JobId, grant.FencingToken, "ArchiveChanged");
            journal.UpdateExpiry(grant.JobId, grant.FencingToken, DateTimeOffset.UtcNow.AddMinutes(-1));
        }
        using var reopened = new AgentJournal(_directory);
        Assert.Equal(intent with { Sha256 = intent.Sha256.ToUpperInvariant() }, reopened.SaveStageIntent(intent));
        Assert.Equal("ArchiveChanged", reopened.StageIntentFailure(grant.JobId, grant.FencingToken));
        reopened.MarkSettled(grant.JobId, grant.FencingToken);
        Assert.Equal(reopened.GetStageIntent(grant.JobId, grant.FencingToken), reopened.SaveStageIntent(intent));
        Assert.Equal("ArchiveChanged", reopened.StageIntentFailure(grant.JobId, grant.FencingToken));
    }

    [Fact]
    public void ConflictingPayloadAndReservationVersionRebindingFail()
    {
        var grant = Grant(); var intent = Intent(grant);
        using var journal = new AgentJournal(_directory); journal.StoreGrant(grant); journal.SaveStageIntent(intent);
        foreach (var changed in new[] { intent with { ReservationId = Guid.NewGuid() }, intent with { ArchiveVersionId = Guid.NewGuid() },
            intent with { RootId = Guid.NewGuid() }, intent with { RelativePath = "other.dt" }, intent with { Sha256 = new string('b', 64) },
            intent with { SizeBytes = 124 }, intent with { RelativePath = "archive.bak", Format = "bak" } })
            Assert.Throws<InvalidOperationException>(() => journal.SaveStageIntent(changed));
        journal.MarkSettled(grant.JobId, grant.FencingToken); var other = Grant(); journal.StoreGrant(other);
        Assert.Throws<SqliteException>(() => journal.SaveStageIntent(intent with { JobId = other.JobId }));
    }

    [Fact]
    public void ExpiredSettledMissingAndProbeLeasesCannotCreateIntent()
    {
        var grant = Grant(); var intent = Intent(grant);
        using var journal = new AgentJournal(_directory);
        Assert.Throws<InvalidOperationException>(() => journal.SaveStageIntent(intent));
        journal.StoreGrant(grant); journal.UpdateExpiry(grant.JobId, grant.FencingToken, DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Throws<InvalidOperationException>(() => journal.SaveStageIntent(intent));
        journal.MarkSettled(grant.JobId, grant.FencingToken);
        Assert.Throws<InvalidOperationException>(() => journal.SaveStageIntent(intent));
        var probe = Grant("ControlPlaneProbe"); journal.StoreGrant(probe);
        Assert.Throws<InvalidOperationException>(() => journal.SaveStageIntent(Intent(probe)));
        Assert.Null(journal.GetStageIntent(grant.JobId, grant.FencingToken));
    }

    [Fact]
    public void WindowsEscapesAndInvalidMetadataAreRejectedWithoutPersistence()
    {
        var grant = Grant(); var intent = Intent(grant);
        using var journal = new AgentJournal(_directory); journal.StoreGrant(grant);
        foreach (var path in new[] { "", "/archive.dt", "C:/archive.dt", "C:archive.dt", "\\\\host\\archive.dt", "folder\\archive.dt", "../archive.dt", "folder/../archive.dt", "folder//archive.dt", "./archive.dt", "folder./archive.dt", "folder /archive.dt", "archive.dt:stream", "CON.dt", "CON .dt", "com¹.dt", "LPT9/archive.dt", "CONOUT$.dt", "a\0.dt", "a?.dt", "archive.bak", new string('x', 256) + ".dt", new string('x', 200) + "/" + new string('y', 200) + ".dt" })
            Assert.Throws<ArgumentException>(() => journal.SaveStageIntent(intent with { RelativePath = path }));
        foreach (var invalid in new[] { intent with { RootId = Guid.Empty }, intent with { JobId = Guid.Empty }, intent with { FencingToken = 0 }, intent with { ReservationId = Guid.Empty }, intent with { ArchiveVersionId = Guid.Empty }, intent with { Sha256 = "AA" }, intent with { Sha256 = new string('g', 64) }, intent with { SizeBytes = 0 }, intent with { Format = "zip" } })
            Assert.Throws<ArgumentException>(() => journal.SaveStageIntent(invalid));
        Assert.Null(journal.GetStageIntent(grant.JobId, grant.FencingToken));
    }

    [Fact]
    public void ArtifactsMustMatchIntentAndFailureIsClosedImmutableAndBlocksPending()
    {
        var grant = Grant(); var intent = Intent(grant); var report = Report(intent);
        using var journal = new AgentJournal(_directory); journal.StoreGrant(grant); journal.SaveStageIntent(intent);
        foreach (var changed in new[] { report with { ReservationId = Guid.NewGuid() }, report with { ArchiveVersionId = Guid.NewGuid() }, report with { Sha256 = new string('b', 64) }, report with { SizeBytes = 124 }, report with { Format = "bak" } })
            Assert.Throws<InvalidOperationException>(() => journal.EnqueueArtifact(changed));
        journal.EnqueueArtifact(report); journal.FailStageIntent(grant.JobId, grant.FencingToken, "StageFailed");
        journal.FailStageIntent(grant.JobId, grant.FencingToken, "StageFailed");
        Assert.Throws<ArgumentException>(() => journal.FailStageIntent(grant.JobId, grant.FencingToken, "secret diagnostic"));
        Assert.Throws<InvalidOperationException>(() => journal.FailStageIntent(grant.JobId, grant.FencingToken, "Cancelled"));
        journal.MarkSettled(grant.JobId, grant.FencingToken); journal.SaveStageIntent(intent); journal.EnqueueArtifact(report);
        var blocked = Assert.Single(journal.PendingArtifacts()); Assert.Equal("Blocked", blocked.State); Assert.Equal("StageFailed", blocked.FailureCode);
        Assert.Equal("StageFailed", journal.StageIntentFailure(grant.JobId, grant.FencingToken));
        Assert.Throws<InvalidOperationException>(() => journal.AcknowledgeArtifact(report.ArtifactId));
    }

    [Fact]
    public void SchemaHasOnlyRelativePathMetadataAndForeignKey()
    {
        var grant = Grant(); var intent = Intent(grant);
        using (var journal = new AgentJournal(_directory)) { journal.StoreGrant(grant); journal.SaveStageIntent(intent); }
        // Windows denies direct file reads while SQLite owns an open handle. Close all DB
        // resources before the independent byte-level assertion; do not weaken journal locking.
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "agent-journal.sqlite"), Pooling = false }.ToString()))
        {
            db.Open();
            using var command = db.CreateCommand(); command.CommandText = "PRAGMA table_info(stage_intents)";
            var columns = new List<string>(); using (var reader = command.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(1));
            Assert.Equal(new[] { "job", "fence", "reservation", "version", "root", "relative_path", "sha", "size", "format", "failure" }, columns);
            command.CommandText = "PRAGMA foreign_key_list(stage_intents)"; using (var reader = command.ExecuteReader()) { Assert.True(reader.Read()); Assert.Equal("leases", reader.GetString(2)); }
        }
        Assert.DoesNotContain("never-store-snapshot", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(_directory, "agent-journal.sqlite"))));
    }
}