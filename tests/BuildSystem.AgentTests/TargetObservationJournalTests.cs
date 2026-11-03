using System.Text.Json;
using BuildSystem.Agent;
using BuildSystem.Contracts;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class TargetObservationJournalTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "target-observation-tests", Guid.NewGuid().ToString("N"));
    private static TargetObservationAssignment Assignment() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 42, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static ReportTargetObservation Report(TargetObservationAssignment assignment) => new(Guid.NewGuid(), assignment,
        Guid.NewGuid(), Guid.NewGuid(), TargetObservationOutcome.BothObservedAbsent,
        new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(123));
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    private void Raw(string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = Path.Combine(directory, "agent-journal.sqlite"), Pooling = false }.ToString());
        db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    private static IEnumerable<TargetObservationAssignment> Changed(TargetObservationAssignment a)
    {
        yield return a with { RestoreIntentId = Guid.NewGuid() };
        yield return a with { AgentId = Guid.NewGuid() };
        yield return a with { OperationId = Guid.NewGuid() };
        yield return a with { EnvironmentId = Guid.NewGuid() };
        yield return a with { StageJobId = Guid.NewGuid() };
        yield return a with { FencingToken = a.FencingToken + 1 };
        yield return a with { ReservationId = Guid.NewGuid() };
        yield return a with { TargetIncarnationId = Guid.NewGuid() };
        yield return a with { CanonicalClusterId = Guid.NewGuid() };
        yield return a with { CanonicalSqlServerId = Guid.NewGuid() };
        yield return a with { RacEndpointMappingId = Guid.NewGuid() };
        yield return a with { SqlEndpointMappingId = Guid.NewGuid() };
    }

    [Fact]
    public void AddressedHistoricalLookupRetainsAcknowledgedReportAndRejectsCorruption()
    {
        var assignment = Assignment(); var report = Report(assignment);
        using (var journal = new AgentJournal(directory))
        {
            Assert.Null(journal.GetTargetObservationReport(assignment.ObservationRequestId));
            Assert.Throws<ArgumentException>(() => journal.GetTargetObservationReport(Guid.Empty));
            journal.SaveTargetObservationAssignment(assignment);
            Assert.Null(journal.GetTargetObservationReport(assignment.ObservationRequestId));
            journal.EnqueueTargetObservation(report);
            Assert.Equal(report, journal.GetTargetObservationReport(assignment.ObservationRequestId));
            journal.AckTargetObservation(report.ReceiptId);
        }
        using (var journal = new AgentJournal(directory))
        {
            Assert.Empty(journal.PendingTargetObservations());
            Assert.Equal(report, journal.GetTargetObservationReport(assignment.ObservationRequestId));
        }
        Raw("UPDATE target_observation_reports SET observed_at='invalid' WHERE request='" + assignment.ObservationRequestId + "'");
        using var corrupt = new AgentJournal(directory);
        Assert.Throws<InvalidOperationException>(() => corrupt.GetTargetObservationReport(assignment.ObservationRequestId));
    }

    [Fact]
    public void AssignmentAndPendingReceiptSurviveReopenThenAckRemainsRetained()
    {
        var a = Assignment(); var r = Report(a);
        using (var j = new AgentJournal(directory)) { j.SaveTargetObservationAssignment(a); j.EnqueueTargetObservation(r); }
        using (var j = new AgentJournal(directory))
        {
            Assert.Equal(a, j.GetTargetObservationAssignment(a.ObservationRequestId));
            Assert.Equal(r, Assert.Single(j.PendingTargetObservations())); j.AckTargetObservation(r.ReceiptId);
        }
        using var reopened = new AgentJournal(directory);
        Assert.Empty(reopened.PendingTargetObservations());
        Assert.Equal(a, reopened.SaveTargetObservationAssignment(a));
        reopened.AckTargetObservation(r.ReceiptId); reopened.EnqueueTargetObservation(r);
        Assert.Empty(reopened.PendingTargetObservations());
        Assert.Throws<InvalidOperationException>(() => reopened.EnqueueTargetObservation(r with { ReceiptId = Guid.NewGuid() }));
    }

    [Fact]
    public void LostAcknowledgementExactReplayDoesNotCreateAnotherReceipt()
    {
        var a = Assignment(); var r = Report(a); using var j = new AgentJournal(directory);
        j.SaveTargetObservationAssignment(a); j.EnqueueTargetObservation(r); j.EnqueueTargetObservation(r);
        Assert.Equal(r, Assert.Single(j.PendingTargetObservations()));
        j.AckTargetObservation(r.ReceiptId); j.EnqueueTargetObservation(r);
        Assert.Empty(j.PendingTargetObservations());
        Assert.False(r.Executable); Assert.True(r.ScheduledJobsMustRemainBlocked);
    }

    [Fact]
    public void AllAssignmentPinsAreImmutableAndExactSaveIdempotent()
    {
        var a = Assignment(); using var j = new AgentJournal(directory); j.SaveTargetObservationAssignment(a);
        foreach (var changed in Changed(a))
            Assert.Throws<InvalidOperationException>(() => j.SaveTargetObservationAssignment(changed));
        Assert.Equal(a, j.SaveTargetObservationAssignment(a));
        Assert.Equal(a, j.GetTargetObservationAssignment(a.ObservationRequestId));
    }

    [Fact]
    public void AllAssignmentPinsMustMatchWhenEnqueuing()
    {
        var a = Assignment(); var r = Report(a); using var j = new AgentJournal(directory); j.SaveTargetObservationAssignment(a);
        foreach (var changed in Changed(a).Append(a with { ObservationRequestId = Guid.NewGuid() }))
            Assert.Throws<InvalidOperationException>(() => j.EnqueueTargetObservation(r with { Assignment = changed }));
        Assert.Empty(j.PendingTargetObservations());
    }

    [Fact]
    public void EveryChangedReportPayloadConflictsEvenAfterAcknowledgement()
    {
        var a = Assignment(); var r = Report(a); using var j = new AgentJournal(directory);
        j.SaveTargetObservationAssignment(a); j.EnqueueTargetObservation(r);
        var changes = new[] { r with { ReceiptId = Guid.NewGuid() }, r with { RacObservationId = Guid.NewGuid() },
            r with { SqlObservationId = Guid.NewGuid() }, r with { ObservedAt = r.ObservedAt.AddTicks(1) },
            r with { Outcome = TargetObservationOutcome.InfobaseObservedPresent },
            r with { Outcome = TargetObservationOutcome.SqlDatabaseObservedPresent },
            r with { Outcome = TargetObservationOutcome.BothObservedPresent } };
        foreach (var changed in changes) Assert.Throws<InvalidOperationException>(() => j.EnqueueTargetObservation(changed));
        j.AckTargetObservation(r.ReceiptId);
        foreach (var changed in changes) Assert.Throws<InvalidOperationException>(() => j.EnqueueTargetObservation(changed));
        Assert.Empty(j.PendingTargetObservations());
    }

    [Fact]
    public void UnknownRequestAndReceiptAreRejected()
    {
        using var j = new AgentJournal(directory); var a = Assignment();
        Assert.Null(j.GetTargetObservationAssignment(a.ObservationRequestId));
        Assert.Throws<InvalidOperationException>(() => j.EnqueueTargetObservation(Report(a)));
        Assert.Throws<InvalidOperationException>(() => j.AckTargetObservation(Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => j.AckTargetObservation(Guid.Empty));
        Assert.Throws<ArgumentException>(() => j.GetTargetObservationAssignment(Guid.Empty));
    }

    [Fact]
    public void EmptyPinsAndNonpositiveFencesFailBeforePersistence()
    {
        var a = Assignment(); using var j = new AgentJournal(directory);
        foreach (var property in typeof(TargetObservationAssignment).GetProperties().Where(p => p.PropertyType == typeof(Guid)))
        {
            var invalid = a with { }; property.SetValue(invalid, Guid.Empty);
            Assert.Throws<ArgumentException>(() => j.SaveTargetObservationAssignment(invalid));
            Assert.Throws<ArgumentException>(() => j.EnqueueTargetObservation(Report(invalid)));
        }
        Assert.Throws<ArgumentException>(() => j.SaveTargetObservationAssignment(a with { FencingToken = 0 }));
        Assert.Throws<ArgumentException>(() => j.SaveTargetObservationAssignment(a with { FencingToken = -1 }));
        Assert.Throws<ArgumentException>(() => j.SaveTargetObservationAssignment(null!));
        Assert.Null(j.GetTargetObservationAssignment(a.ObservationRequestId));
    }

    [Fact]
    public void InvalidReportsAreRejectedWithoutPartialRows()
    {
        var a = Assignment(); var r = Report(a); using var j = new AgentJournal(directory); j.SaveTargetObservationAssignment(a);
        foreach (var invalid in new[] { r with { ReceiptId = Guid.Empty }, r with { RacObservationId = Guid.Empty },
            r with { SqlObservationId = Guid.Empty }, r with { SqlObservationId = r.RacObservationId },
            r with { Outcome = (TargetObservationOutcome)4 }, r with { Outcome = (TargetObservationOutcome)(-1) },
            r with { ObservedAt = default }, r with { Assignment = null! } })
            Assert.Throws<ArgumentException>(() => j.EnqueueTargetObservation(invalid));
        Assert.Throws<ArgumentException>(() => j.EnqueueTargetObservation(null!));
        Assert.Empty(j.PendingTargetObservations());
    }

    [Fact]
    public void BenignOffsetsCanonicalizeToUtcAndReplayAsSameInstant()
    {
        var a = Assignment(); var r = Report(a); using var j = new AgentJournal(directory);
        j.SaveTargetObservationAssignment(a); j.EnqueueTargetObservation(r with { ObservedAt = r.ObservedAt.ToOffset(TimeSpan.FromHours(3)) });
        var stored = Assert.Single(j.PendingTargetObservations());
        Assert.Equal(TimeSpan.Zero, stored.ObservedAt.Offset); Assert.Equal(r, stored); j.EnqueueTargetObservation(r);
    }

    [Fact]
    public void PendingLimitIsStableBoundedAndReadOnly()
    {
        using var j = new AgentJournal(directory); var reports = new List<ReportTargetObservation>();
        for (var i = 0; i < 102; i++) { var a = Assignment(); var r = Report(a); j.SaveTargetObservationAssignment(a); j.EnqueueTargetObservation(r); reports.Add(r); }
        Assert.Equal(reports.Take(100), j.PendingTargetObservations());
        Assert.Equal(reports.Take(2), j.PendingTargetObservations(2));
        Assert.Equal(reports, j.PendingTargetObservations(1000));
        foreach (var limit in new[] { -1, 0, 1001 }) Assert.Throws<ArgumentOutOfRangeException>(() => j.PendingTargetObservations(limit));
        Assert.True(((ICollection<ReportTargetObservation>)j.PendingTargetObservations()).IsReadOnly);
        j.AckTargetObservation(reports[0].ReceiptId); Assert.Equal(reports[1], Assert.Single(j.PendingTargetObservations(1)));
    }

    [Fact]
    public void MultipleRequestsCanObserveSameTargetButReceiptIdentityIsGlobal()
    {
        var a = Assignment(); var b = a with { ObservationRequestId = Guid.NewGuid() }; var r = Report(a);
        using var j = new AgentJournal(directory); j.SaveTargetObservationAssignment(a); j.SaveTargetObservationAssignment(b);
        j.EnqueueTargetObservation(r);
        Assert.Throws<InvalidOperationException>(() => j.EnqueueTargetObservation(r with { Assignment = b }));
        j.EnqueueTargetObservation(Report(b)); Assert.Equal(2, j.PendingTargetObservations().Count);
    }

    [Fact]
    public void HardPolicyAndDerivedNamesCannotBeOverriddenByJson()
    {
        var a = Assignment(); var r = Report(a);
        var json = JsonSerializer.Serialize(r).Replace("\"Executable\":false", "\"Executable\":true")
            .Replace("\"ScheduledJobsMustRemainBlocked\":true", "\"ScheduledJobsMustRemainBlocked\":false")
            .Replace(a.InfobaseName, "attacker-path");
        var parsed = JsonSerializer.Deserialize<ReportTargetObservation>(json)!;
        Assert.False(parsed.Executable); Assert.True(parsed.ScheduledJobsMustRemainBlocked);
        Assert.False(parsed.Assignment.Executable); Assert.True(parsed.Assignment.ScheduledJobsMustRemainBlocked);
        Assert.Equal("bs_test_" + a.ReservationId.ToString("N"), parsed.Assignment.InfobaseName);
        Assert.Equal(parsed.Assignment.InfobaseName, parsed.Assignment.SqlDatabaseName);
    }

    [Fact]
    public void ToStringAndConflictsAreRedacted()
    {
        var a = Assignment(); var r = Report(a); using var j = new AgentJournal(directory); j.SaveTargetObservationAssignment(a);
        foreach (var text in new[] { a.ToString(), r.ToString(), Assert.Throws<InvalidOperationException>(() =>
            j.SaveTargetObservationAssignment(a with { AgentId = Guid.NewGuid() })).Message })
        {
            Assert.DoesNotContain(a.AgentId.ToString(), text); Assert.DoesNotContain(a.InfobaseName, text);
            Assert.DoesNotContain(r.ReceiptId.ToString(), text);
        }
        Assert.Equal(4, Enum.GetValues<TargetObservationOutcome>().Length);
    }

    [Theory]
    [InlineData("target_observation_assignments", "agent='malformed'")]
    [InlineData("target_observation_assignments", "fence=0")]
    [InlineData("target_observation_assignments", "fence=1.5")]
    [InlineData("target_observation_reports", "outcome=99")]
    [InlineData("target_observation_reports", "outcome=1.5")]
    [InlineData("target_observation_reports", "observed_at='not-a-timestamp'")]
    [InlineData("target_observation_reports", "observed_at='0001-01-01T00:00:00.0000000+00:00'")]
    [InlineData("target_observation_reports", "observed_at='2026-01-02T06:04:05.0000123+03:00'")]
    [InlineData("target_observation_reports", "sql_observation=rac_observation")]
    [InlineData("target_observation_reports", "acked=2")]
    public void RawMalformedRowsFailClosedOnReplayAndAck(string table, string update)
    {
        var a = Assignment(); var r = Report(a);
        using (var j = new AgentJournal(directory)) { j.SaveTargetObservationAssignment(a); j.EnqueueTargetObservation(r); }
        Raw("PRAGMA ignore_check_constraints=ON; UPDATE " + table + " SET " + update);
        using var reopened = new AgentJournal(directory);
        Assert.ThrowsAny<Exception>(() => reopened.EnqueueTargetObservation(r));
        Assert.ThrowsAny<Exception>(() => reopened.AckTargetObservation(r.ReceiptId));
        if (!update.StartsWith("acked=")) Assert.ThrowsAny<Exception>(() => reopened.PendingTargetObservations());
    }

    [Fact]
    public void SqlChecksRejectInvalidOutcomeAckAndFence()
    {
        var a = Assignment(); using (var j = new AgentJournal(directory)) { j.SaveTargetObservationAssignment(a); j.EnqueueTargetObservation(Report(a)); }
        foreach (var sql in new[] { "UPDATE target_observation_reports SET outcome=4", "UPDATE target_observation_reports SET acked=2",
            "UPDATE target_observation_assignments SET fence=0", "UPDATE target_observation_reports SET observed_at=NULL" })
            Assert.Throws<SqliteException>(() => Raw(sql));
    }

    [Fact]
    public void ObservationOutboxNeverMutatesLeasesOrCreationOrStageIntents()
    {
        var a = Assignment(); var r = Report(a); using var j = new AgentJournal(directory);
        j.StoreGrant(new(a.StageJobId, a.FencingToken, DateTimeOffset.UtcNow.AddMinutes(5), "ArchiveStageOnly", "{}"));
        var creation = new TargetCreationIntent(a.OperationId, a.EnvironmentId, a.StageJobId, a.FencingToken,
            a.ReservationId, a.TargetIncarnationId, a.CanonicalClusterId, a.CanonicalSqlServerId, a.InfobaseName, a.SqlDatabaseName);
        var saved = j.SaveTargetCreationIntent(creation); var lease = Assert.Single(j.Unsettled());
        j.SaveTargetObservationAssignment(a); j.EnqueueTargetObservation(r); j.AckTargetObservation(r.ReceiptId);
        Assert.Equal(lease, Assert.Single(j.Unsettled())); Assert.False(j.IsSettled(a.StageJobId, a.FencingToken));
        Assert.Equal(saved, j.GetTargetCreationIntent(a.OperationId));
        j.MarkSettled(a.StageJobId, a.FencingToken); j.EnqueueTargetObservation(r);
        Assert.Empty(j.PendingTargetObservations()); Assert.Equal(saved, j.GetTargetCreationIntent(a.OperationId));
    }

    [Fact]
    public void LegacyInitializationIsAdditiveAndPreservesGrant()
    {
        var a = Assignment();
        using (var j = new AgentJournal(directory)) j.StoreGrant(new(a.StageJobId, a.FencingToken, DateTimeOffset.UtcNow.AddHours(1), "ArchiveStageOnly", "{}"));
        Raw("DROP TABLE target_observation_reports; DROP TABLE target_observation_assignments;");
        using var reopened = new AgentJournal(directory);
        Assert.Equal(a.StageJobId, Assert.Single(reopened.Unsettled()).JobId);
        Assert.Empty(reopened.PendingTargetObservations()); reopened.SaveTargetObservationAssignment(a);
        Assert.Equal(a, reopened.GetTargetObservationAssignment(a.ObservationRequestId));
    }
}