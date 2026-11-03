using BuildSystem.Agent;
using BuildSystem.Contracts;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class TargetCreationJournalTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "target-creation-tests", Guid.NewGuid().ToString("N"));
    private static TargetCreationIntent Intent()
    {
        var reservation = Guid.NewGuid(); var name = "bs_test_" + reservation.ToString("N");
        return new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 42, reservation, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), name, name);
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    [Theory]
    [InlineData(TargetCreationState.IntentRecorded)]
    [InlineData(TargetCreationState.CreationInFlight)]
    [InlineData(TargetCreationState.ReconciliationRequired)]
    public void EveryStateReopensWithoutRecoveryAndExactSavePreservesAllPins(TargetCreationState state)
    {
        var intent = Intent(); TargetCreationRecord record;
        using (var journal = new AgentJournal(directory))
        {
            record = journal.SaveTargetCreationIntent(intent);
            if (state != TargetCreationState.IntentRecorded)
                record = journal.TryBeginTargetCreation(intent.OperationId, record.Revision, Guid.NewGuid()).Record;
            if (state == TargetCreationState.ReconciliationRequired)
                record = journal.QuarantineTargetCreation(intent.OperationId, record.Revision, "OutcomeUnknown");
        }
        using var reopened = new AgentJournal(directory);
        Assert.Equal(state, record.State);
        Assert.Equal(record, reopened.GetTargetCreationIntent(intent.OperationId));
        Assert.Equal(record, reopened.SaveTargetCreationIntent(intent));
        Assert.Equal(record, Assert.Single(reopened.ListUnresolvedTargetCreationIntents()));
    }

    [Fact]
    public void EveryChangedPinConflictsAndCannotChangeExistingRecord()
    {
        var intent = Intent(); using var journal = new AgentJournal(directory);
        var saved = journal.SaveTargetCreationIntent(intent);
        var otherReservation = Guid.NewGuid(); var otherName = "bs_test_" + otherReservation.ToString("N");
        foreach (var changed in new[] {
            intent with { EnvironmentId = Guid.NewGuid() }, intent with { JobId = Guid.NewGuid() },
            intent with { FencingToken = 43 }, intent with { TargetIncarnationId = Guid.NewGuid() },
            intent with { CanonicalClusterId = Guid.NewGuid() }, intent with { CanonicalSqlServerId = Guid.NewGuid() },
            intent with { ReservationId = otherReservation, InfobaseName = otherName, SqlDatabaseName = otherName } })
            Assert.Throws<InvalidOperationException>(() => journal.SaveTargetCreationIntent(changed));
        // Name-only changes and reservation-only changes violate the strict planner contract.
        foreach (var changed in new[] { intent with { InfobaseName = otherName }, intent with { SqlDatabaseName = otherName }, intent with { ReservationId = otherReservation } })
            Assert.Throws<ArgumentException>(() => journal.SaveTargetCreationIntent(changed));
        Assert.Throws<InvalidOperationException>(() => journal.SaveTargetCreationIntent(intent with { OperationId = Guid.NewGuid() }));
        Assert.Equal(saved, journal.GetTargetCreationIntent(intent.OperationId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EitherPartialSlotCollisionBlocksIndependentlyEvenAfterQuarantine(bool sqlCollision)
    {
        var intent = Intent(); using var journal = new AgentJournal(directory);
        journal.SaveTargetCreationIntent(intent);
        var attempt = journal.TryBeginTargetCreation(intent.OperationId, 1, Guid.NewGuid()).Record;
        journal.QuarantineTargetCreation(intent.OperationId, attempt.Revision, "TargetConflict");
        var other = intent with { OperationId = Guid.NewGuid(), EnvironmentId = Guid.NewGuid(), JobId = Guid.NewGuid(), TargetIncarnationId = Guid.NewGuid() };
        other = sqlCollision ? other with { CanonicalClusterId = Guid.NewGuid() } : other with { CanonicalSqlServerId = Guid.NewGuid() };
        Assert.Throws<InvalidOperationException>(() => journal.SaveTargetCreationIntent(other));
        Assert.Single(journal.ListUnresolvedTargetCreationIntents());
        // Both independently different canonical locations permit a distinct local intent.
        var distinct = other with { CanonicalClusterId = Guid.NewGuid(), CanonicalSqlServerId = Guid.NewGuid() };
        journal.SaveTargetCreationIntent(distinct);
        Assert.Equal(2, journal.ListUnresolvedTargetCreationIntents().Count);
    }

    [Fact]
    public void BeginIsCasAndExactDuplicateIsOnlyPersistenceReceiptAcrossReopenAndQuarantine()
    {
        var intent = Intent(); var attemptId = Guid.NewGuid();
        using (var journal = new AgentJournal(directory))
        {
            journal.SaveTargetCreationIntent(intent);
            Assert.Throws<InvalidOperationException>(() => journal.TryBeginTargetCreation(intent.OperationId, 2, attemptId));
            var first = journal.TryBeginTargetCreation(intent.OperationId, 1, attemptId);
            Assert.True(first.NewlyRecorded); Assert.Equal(2, first.Record.Revision);
            Assert.Equal(TargetCreationState.CreationInFlight, first.Record.State);
            Assert.Equal(attemptId, first.Record.AttemptId); Assert.Equal(1, first.Record.AttemptExpectedRevision);
            Assert.False(journal.TryBeginTargetCreation(intent.OperationId, 1, attemptId).NewlyRecorded);
            Assert.Throws<InvalidOperationException>(() => journal.TryBeginTargetCreation(intent.OperationId, 2, attemptId));
            Assert.Throws<InvalidOperationException>(() => journal.TryBeginTargetCreation(intent.OperationId, 1, Guid.NewGuid()));
            Assert.Throws<InvalidOperationException>(() => journal.TryBeginTargetCreation(intent.OperationId, 2, Guid.NewGuid()));
        }
        using var reopened = new AgentJournal(directory);
        Assert.False(reopened.TryBeginTargetCreation(intent.OperationId, 1, attemptId).NewlyRecorded);
        Assert.Throws<InvalidOperationException>(() => reopened.QuarantineTargetCreation(intent.OperationId, 1, "OutcomeUnknown"));
        var quarantined = reopened.QuarantineTargetCreation(intent.OperationId, 2, "OutcomeUnknown");
        Assert.Equal(3, quarantined.Revision); Assert.Equal(attemptId, quarantined.AttemptId);
        Assert.Equal(1, quarantined.AttemptExpectedRevision); Assert.Equal("OutcomeUnknown", quarantined.QuarantineReason);
        var duplicate = reopened.TryBeginTargetCreation(intent.OperationId, 1, attemptId);
        Assert.False(duplicate.NewlyRecorded); Assert.Equal(quarantined, duplicate.Record);
        Assert.Throws<InvalidOperationException>(() => reopened.TryBeginTargetCreation(intent.OperationId, 3, Guid.NewGuid()));
        Assert.Throws<InvalidOperationException>(() => reopened.QuarantineTargetCreation(intent.OperationId, 3, "TargetConflict"));
        Assert.Equal(quarantined, reopened.SaveTargetCreationIntent(intent));
    }

    [Fact]
    public void QuarantineBeforeBeginRetainsLocksAndNeverAllowsAnAttempt()
    {
        var intent = Intent(); using var journal = new AgentJournal(directory); journal.SaveTargetCreationIntent(intent);
        var record = journal.QuarantineTargetCreation(intent.OperationId, 1, "Cancelled");
        Assert.Equal(2, record.Revision); Assert.Null(record.AttemptId); Assert.Null(record.AttemptExpectedRevision);
        Assert.Throws<InvalidOperationException>(() => journal.TryBeginTargetCreation(intent.OperationId, 2, Guid.NewGuid()));
        Assert.Throws<InvalidOperationException>(() => journal.SaveTargetCreationIntent(intent with { OperationId = Guid.NewGuid() }));
    }

    [Fact]
    public void LegacyStageGrantExpiryAndSettlementDoNotChangeBarrierOrAuthorizeAnything()
    {
        var intent = Intent(); using var journal = new AgentJournal(directory);
        var record = journal.SaveTargetCreationIntent(intent); // No lease is required or implied.
        journal.StoreGrant(new LeaseGrant(intent.JobId, intent.FencingToken, DateTimeOffset.UtcNow.AddMinutes(5), "ArchiveStageOnly", "{}"));
        journal.SaveStageIntent(new(intent.JobId, intent.FencingToken, intent.ReservationId, Guid.NewGuid(), Guid.NewGuid(), "archive.dt", new string('A', 64), 100, "dt"));
        journal.UpdateExpiry(intent.JobId, intent.FencingToken, DateTimeOffset.UtcNow.AddMinutes(-1));
        journal.MarkSettled(intent.JobId, intent.FencingToken);
        Assert.Equal(record, journal.GetTargetCreationIntent(intent.OperationId));
        var begun = journal.TryBeginTargetCreation(intent.OperationId, 1, Guid.NewGuid()).Record;
        journal.MarkSettled(intent.JobId, intent.FencingToken);
        Assert.Equal(begun, journal.GetTargetCreationIntent(intent.OperationId));
        Assert.Throws<InvalidOperationException>(() => journal.TryBeginTargetCreation(intent.OperationId, 2, Guid.NewGuid()));
    }

    [Fact]
    public void InvalidPinsNamesOperationsRevisionsAndReasonsFailWithoutPersistenceOrSensitiveDiagnostics()
    {
        var intent = Intent(); using var journal = new AgentJournal(directory);
        foreach (var invalid in new[] {
            intent with { OperationId = Guid.Empty }, intent with { EnvironmentId = Guid.Empty }, intent with { JobId = Guid.Empty },
            intent with { FencingToken = 0 }, intent with { FencingToken = -1 }, intent with { ReservationId = Guid.Empty },
            intent with { TargetIncarnationId = Guid.Empty }, intent with { CanonicalClusterId = Guid.Empty }, intent with { CanonicalSqlServerId = Guid.Empty } })
            Assert.Throws<ArgumentException>(() => journal.SaveTargetCreationIntent(invalid));
        foreach (var name in new[] { null, "", intent.InfobaseName.ToUpperInvariant(), intent.InfobaseName + " ", "bs_test_" + Guid.NewGuid().ToString("N"), "../secret", "bs_test_" + intent.ReservationId.ToString("D"), intent.InfobaseName + "\0" })
        {
            Assert.Throws<ArgumentException>(() => journal.SaveTargetCreationIntent(intent with { InfobaseName = name! }));
            Assert.Throws<ArgumentException>(() => journal.SaveTargetCreationIntent(intent with { SqlDatabaseName = name! }));
        }
        Assert.Throws<ArgumentException>(() => journal.SaveTargetCreationIntent(null!));
        Assert.Throws<ArgumentException>(() => journal.GetTargetCreationIntent(Guid.Empty));
        Assert.Throws<ArgumentException>(() => journal.TryBeginTargetCreation(Guid.Empty, 1, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => journal.TryBeginTargetCreation(intent.OperationId, 0, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => journal.TryBeginTargetCreation(intent.OperationId, 1, Guid.Empty));
        Assert.Throws<InvalidOperationException>(() => journal.TryBeginTargetCreation(intent.OperationId, 1, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => journal.QuarantineTargetCreation(Guid.Empty, 1, "Cancelled"));
        Assert.Throws<ArgumentException>(() => journal.QuarantineTargetCreation(intent.OperationId, 0, "Cancelled"));
        Assert.Throws<ArgumentException>(() => journal.QuarantineTargetCreation(intent.OperationId, 1, "secret arbitrary text"));
        Assert.Throws<InvalidOperationException>(() => journal.QuarantineTargetCreation(intent.OperationId, 1, "Cancelled"));
        Assert.Null(journal.GetTargetCreationIntent(intent.OperationId)); Assert.Empty(journal.ListUnresolvedTargetCreationIntents());
        var record = journal.SaveTargetCreationIntent(intent);
        var receipt = journal.TryBeginTargetCreation(intent.OperationId, 1, Guid.NewGuid());
        foreach (var diagnostic in new[] { intent.ToString(), record.ToString(), receipt.ToString(), receipt.Record.ToString(),
            Assert.Throws<InvalidOperationException>(() => journal.SaveTargetCreationIntent(intent with { EnvironmentId = Guid.NewGuid() })).Message })
        {
            Assert.DoesNotContain(intent.OperationId.ToString(), diagnostic); Assert.DoesNotContain(intent.JobId.ToString(), diagnostic);
            Assert.DoesNotContain(intent.InfobaseName, diagnostic); Assert.DoesNotContain(intent.CanonicalSqlServerId.ToString(), diagnostic);
        }
    }

    [Fact]
    public void LegacyJournalWithoutTargetTableUpgradesWithoutChangingExistingGrant()
    {
        var intent = Intent();
        var grant = new LeaseGrant(intent.JobId, intent.FencingToken, DateTimeOffset.UtcNow.AddMinutes(5), "ArchiveStageOnly", "{}");
        using (var journal = new AgentJournal(directory)) journal.StoreGrant(grant);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "agent-journal.sqlite"), Pooling = false }.ToString()))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "DROP TABLE target_creation_intents"; command.ExecuteNonQuery();
        }
        using var reopened = new AgentJournal(directory);
        var lease = Assert.Single(reopened.Unsettled());
        Assert.Equal(grant.JobId, lease.JobId); Assert.Equal(grant.FencingToken, lease.FencingToken); Assert.Equal(grant.Kind, lease.Kind);
        Assert.Empty(reopened.ListUnresolvedTargetCreationIntents());
        Assert.Equal(TargetCreationState.IntentRecorded, reopened.SaveTargetCreationIntent(intent).State);
        Assert.Equal(lease, Assert.Single(reopened.Unsettled()));
    }

    [Fact]
    public void ListIsDeterministicBoundedAndSchemaHasNoLeaseForeignKey()
    {
        using (var journal = new AgentJournal(directory))
        {
            var first = journal.SaveTargetCreationIntent(Intent()); journal.SaveTargetCreationIntent(Intent());
            Assert.Equal(first, Assert.Single(journal.ListUnresolvedTargetCreationIntents(1)));
            Assert.Equal(2, journal.ListUnresolvedTargetCreationIntents(1000).Count);
            Assert.Throws<ArgumentOutOfRangeException>(() => journal.ListUnresolvedTargetCreationIntents(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => journal.ListUnresolvedTargetCreationIntents(1001));
        }
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "agent-journal.sqlite"), Pooling = false }.ToString());
        db.Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA foreign_key_list(target_creation_intents)";
        using (var reader = command.ExecuteReader()) Assert.False(reader.Read());
        foreach (var assignment in new[] { "attempt='invalid-pair',attempt_revision=NULL", "attempt=NULL,attempt_revision=1", "attempt='invalid-pair',attempt_revision=0" })
        {
            command.CommandText = "UPDATE target_creation_intents SET " + assignment;
            Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        }
    }
}
