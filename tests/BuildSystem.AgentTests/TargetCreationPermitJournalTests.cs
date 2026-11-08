using BuildSystem.Agent;
using BuildSystem.Contracts;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class TargetCreationPermitJournalTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "target-creation-permit-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset IssuedAt = new(2026, 11, 8, 6, 0, 0, TimeSpan.Zero);

    private static TargetCreationIntent Intent()
    {
        var reservation = Guid.NewGuid(); var name = "bs_test_" + reservation.ToString("N");
        return new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 42, reservation, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), name, name);
    }

    private static TargetCreationPermitBinding Binding(TargetCreationIntent intent) => new(
        Guid.NewGuid(), intent.OperationId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), IssuedAt, IssuedAt.AddMinutes(5));

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    private static void AssertReceipt(TargetCreationPermitBindingReceipt receipt, TargetCreationPermitBinding binding, bool newlyRecorded)
    {
        Assert.Equal(binding, receipt.Binding);
        Assert.Equal(newlyRecorded, receipt.NewlyRecorded);
        Assert.False(receipt.Executable);
        Assert.True(receipt.ScheduledJobsMustRemainBlocked);
    }

    [Fact]
    public void BindingPersistsAcrossDatabaseReopenWithoutBeginningAnAttempt()
    {
        var intent = Intent(); var binding = Binding(intent); TargetCreationRecord record;
        using (var journal = new AgentJournal(directory))
        {
            record = journal.SaveTargetCreationIntent(intent);
            AssertReceipt(journal.SaveTargetCreationPermitBinding(intent, binding), binding, true);
            Assert.Equal(record, journal.GetTargetCreationIntent(intent.OperationId));
        }
        using var reopened = new AgentJournal(directory);
        Assert.Equal(binding, reopened.GetTargetCreationPermitBinding(intent.OperationId));
        Assert.Equal(record, reopened.GetTargetCreationIntent(intent.OperationId));
        AssertReceipt(reopened.SaveTargetCreationPermitBinding(intent, binding), binding, false);
        Assert.Null(record.AttemptId); Assert.Equal(1, record.Revision);
    }

    [Theory]
    [InlineData(TargetCreationState.IntentRecorded, false)]
    [InlineData(TargetCreationState.CreationInFlight, false)]
    [InlineData(TargetCreationState.ReconciliationRequired, false)]
    [InlineData(TargetCreationState.ReconciliationRequired, true)]
    public void ExactReplayInEveryStatePreservesRecordAndBothSlotBarriers(TargetCreationState state, bool quarantineWithoutBegin)
    {
        var intent = Intent(); var binding = Binding(intent); TargetCreationRecord record;
        using (var journal = new AgentJournal(directory))
        {
            record = journal.SaveTargetCreationIntent(intent);
            journal.SaveTargetCreationPermitBinding(intent, binding);
            if (state != TargetCreationState.IntentRecorded && !quarantineWithoutBegin)
                record = journal.TryBeginTargetCreation(intent.OperationId, record.Revision, binding.AttemptId).Record;
            if (state == TargetCreationState.ReconciliationRequired)
                record = journal.QuarantineTargetCreation(intent.OperationId, record.Revision, "OutcomeUnknown");
        }
        using var reopened = new AgentJournal(directory);
        AssertReceipt(reopened.SaveTargetCreationPermitBinding(intent, binding), binding, false);
        Assert.Equal(record, reopened.GetTargetCreationIntent(intent.OperationId));
        Assert.Equal(record, Assert.Single(reopened.ListUnresolvedTargetCreationIntents()));
        foreach (var collision in new[] {
            intent with { OperationId = Guid.NewGuid(), CanonicalClusterId = Guid.NewGuid() },
            intent with { OperationId = Guid.NewGuid(), CanonicalSqlServerId = Guid.NewGuid() } })
            Assert.Throws<InvalidOperationException>(() => reopened.SaveTargetCreationIntent(collision));
        Assert.Equal(binding, reopened.GetTargetCreationPermitBinding(intent.OperationId));
    }

    [Theory]
    [InlineData("PermitId")]
    [InlineData("OperationId")]
    [InlineData("AttemptId")]
    [InlineData("RestoreIntentId")]
    [InlineData("ArtifactId")]
    [InlineData("AgentId")]
    public void EveryEmptyBindingGuidIsRejectedWithoutPersistence(string pin)
    {
        var intent = Intent(); var binding = Binding(intent);
        binding = pin switch {
            "PermitId" => binding with { PermitId = Guid.Empty }, "OperationId" => binding with { OperationId = Guid.Empty },
            "AttemptId" => binding with { AttemptId = Guid.Empty }, "RestoreIntentId" => binding with { RestoreIntentId = Guid.Empty },
            "ArtifactId" => binding with { ArtifactId = Guid.Empty }, _ => binding with { AgentId = Guid.Empty }
        };
        using var journal = new AgentJournal(directory); var record = journal.SaveTargetCreationIntent(intent);
        Assert.Throws<ArgumentException>(() => journal.SaveTargetCreationPermitBinding(intent, binding));
        Assert.Null(journal.GetTargetCreationPermitBinding(intent.OperationId));
        Assert.Equal(record, journal.GetTargetCreationIntent(intent.OperationId));
    }

    [Theory]
    [InlineData("OperationId")]
    [InlineData("EnvironmentId")]
    [InlineData("JobId")]
    [InlineData("FencingToken")]
    [InlineData("ReservationId")]
    [InlineData("TargetIncarnationId")]
    [InlineData("CanonicalClusterId")]
    [InlineData("CanonicalSqlServerId")]
    public void EveryValidButDifferentExpectedIntentPinIsRejected(string pin)
    {
        var intent = Intent(); var binding = Binding(intent); var reservation = Guid.NewGuid(); var name = "bs_test_" + reservation.ToString("N");
        var changed = pin switch {
            "OperationId" => intent with { OperationId = Guid.NewGuid() }, "EnvironmentId" => intent with { EnvironmentId = Guid.NewGuid() },
            "JobId" => intent with { JobId = Guid.NewGuid() }, "FencingToken" => intent with { FencingToken = 43 },
            "ReservationId" => intent with { ReservationId = reservation, InfobaseName = name, SqlDatabaseName = name },
            "TargetIncarnationId" => intent with { TargetIncarnationId = Guid.NewGuid() },
            "CanonicalClusterId" => intent with { CanonicalClusterId = Guid.NewGuid() },
            _ => intent with { CanonicalSqlServerId = Guid.NewGuid() }
        };
        using var journal = new AgentJournal(directory); var record = journal.SaveTargetCreationIntent(intent);
        if (pin == "OperationId") binding = binding with { OperationId = changed.OperationId };
        Assert.Throws<InvalidOperationException>(() => journal.SaveTargetCreationPermitBinding(changed, binding));
        Assert.Null(journal.GetTargetCreationPermitBinding(intent.OperationId));
        Assert.Equal(record, journal.GetTargetCreationIntent(intent.OperationId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InvalidExpectedTargetNameIsRejected(bool sqlName)
    {
        var intent = Intent(); var binding = Binding(intent);
        var changed = sqlName ? intent with { SqlDatabaseName = "secret-invalid-name" } : intent with { InfobaseName = "secret-invalid-name" };
        using var journal = new AgentJournal(directory); var record = journal.SaveTargetCreationIntent(intent);
        Assert.Throws<ArgumentException>(() => journal.SaveTargetCreationPermitBinding(changed, binding));
        Assert.Equal(record, journal.GetTargetCreationIntent(intent.OperationId));
        Assert.Null(journal.GetTargetCreationPermitBinding(intent.OperationId));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(299)]
    [InlineData(301)]
    public void ValidityWindowMustBeExactlyFiveMinutes(int seconds)
    {
        var intent = Intent(); var binding = Binding(intent) with { StartBefore = IssuedAt.AddSeconds(seconds) };
        using var journal = new AgentJournal(directory); var record = journal.SaveTargetCreationIntent(intent);
        Assert.Throws<ArgumentException>(() => journal.SaveTargetCreationPermitBinding(intent, binding));
        Assert.Null(journal.GetTargetCreationPermitBinding(intent.OperationId));
        Assert.Equal(record, journal.GetTargetCreationIntent(intent.OperationId));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void SubTickWindowDifferenceIsNotRoundedToFiveMinutes(int ticks)
    {
        var intent = Intent(); var binding = Binding(intent) with { StartBefore = IssuedAt.AddMinutes(5).AddTicks(ticks) };
        using var journal = new AgentJournal(directory); journal.SaveTargetCreationIntent(intent);
        Assert.Throws<ArgumentException>(() => journal.SaveTargetCreationPermitBinding(intent, binding));
        Assert.Null(journal.GetTargetCreationPermitBinding(intent.OperationId));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void NonUtcOffsetsAreRejectedEvenWhenTheyRepresentTheSameInstants(bool issueOffset, bool startOffset)
    {
        var intent = Intent(); var binding = Binding(intent);
        binding = binding with {
            IssuedAt = issueOffset ? binding.IssuedAt.ToOffset(TimeSpan.FromHours(1)) : binding.IssuedAt,
            StartBefore = startOffset ? binding.StartBefore.ToOffset(TimeSpan.FromHours(1)) : binding.StartBefore
        };
        using var journal = new AgentJournal(directory); journal.SaveTargetCreationIntent(intent);
        Assert.Throws<ArgumentException>(() => journal.SaveTargetCreationPermitBinding(intent, binding));
        Assert.Null(journal.GetTargetCreationPermitBinding(intent.OperationId));
    }

    [Fact]
    public void TimestampAtMaximumCannotOverflowValidationOrPersist()
    {
        var intent = Intent(); var binding = Binding(intent) with { IssuedAt = DateTimeOffset.MaxValue, StartBefore = DateTimeOffset.MaxValue };
        using var journal = new AgentJournal(directory); journal.SaveTargetCreationIntent(intent);
        Assert.Throws<ArgumentException>(() => journal.SaveTargetCreationPermitBinding(intent, binding));
        Assert.Null(journal.GetTargetCreationPermitBinding(intent.OperationId));
    }

    [Fact]
    public void AbsentLocalIntentCannotBeCreatedBySavingABinding()
    {
        var intent = Intent(); using var journal = new AgentJournal(directory);
        Assert.Null(journal.GetTargetCreationPermitBinding(intent.OperationId));
        Assert.Throws<InvalidOperationException>(() => journal.SaveTargetCreationPermitBinding(intent, Binding(intent)));
        Assert.Null(journal.GetTargetCreationIntent(intent.OperationId)); Assert.Empty(journal.ListUnresolvedTargetCreationIntents());
        Assert.Null(journal.GetTargetCreationPermitBinding(intent.OperationId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewBindingCannotBeRecordedAfterLocalBeginOrQuarantine(bool quarantine)
    {
        var intent = Intent(); var binding = Binding(intent); using var journal = new AgentJournal(directory);
        journal.SaveTargetCreationIntent(intent);
        var record = quarantine ? journal.QuarantineTargetCreation(intent.OperationId, 1, "Cancelled")
            : journal.TryBeginTargetCreation(intent.OperationId, 1, binding.AttemptId).Record;
        Assert.Throws<InvalidOperationException>(() => journal.SaveTargetCreationPermitBinding(intent, binding));
        Assert.Equal(record, journal.GetTargetCreationIntent(intent.OperationId));
        Assert.Null(journal.GetTargetCreationPermitBinding(intent.OperationId));
    }

    [Fact]
    public void NewBindingAfterBeginThenQuarantineCannotUseTheExistingAttempt()
    {
        var intent = Intent(); var binding = Binding(intent); using var journal = new AgentJournal(directory);
        journal.SaveTargetCreationIntent(intent); journal.TryBeginTargetCreation(intent.OperationId, 1, binding.AttemptId);
        var record = journal.QuarantineTargetCreation(intent.OperationId, 2, "OutcomeUnknown");
        Assert.Throws<InvalidOperationException>(() => journal.SaveTargetCreationPermitBinding(intent, binding));
        Assert.Equal(record, journal.GetTargetCreationIntent(intent.OperationId));
        Assert.Null(journal.GetTargetCreationPermitBinding(intent.OperationId));
    }

    [Theory]
    [InlineData("PermitId")]
    [InlineData("AttemptId")]
    [InlineData("RestoreIntentId")]
    [InlineData("ArtifactId")]
    [InlineData("AgentId")]
    [InlineData("Times")]
    public void ChangedBindingPayloadConflictsWithoutReplacingTheFirstBinding(string pin)
    {
        var intent = Intent(); var binding = Binding(intent);
        var changed = pin switch {
            "PermitId" => binding with { PermitId = Guid.NewGuid() }, "AttemptId" => binding with { AttemptId = Guid.NewGuid() },
            "RestoreIntentId" => binding with { RestoreIntentId = Guid.NewGuid() }, "ArtifactId" => binding with { ArtifactId = Guid.NewGuid() },
            "AgentId" => binding with { AgentId = Guid.NewGuid() },
            _ => binding with { IssuedAt = IssuedAt.AddTicks(1), StartBefore = IssuedAt.AddMinutes(5).AddTicks(1) }
        };
        using var journal = new AgentJournal(directory); var record = journal.SaveTargetCreationIntent(intent);
        journal.SaveTargetCreationPermitBinding(intent, binding);
        Assert.Throws<InvalidOperationException>(() => journal.SaveTargetCreationPermitBinding(intent, changed));
        Assert.Equal(binding, journal.GetTargetCreationPermitBinding(intent.OperationId));
        Assert.Equal(record, journal.GetTargetCreationIntent(intent.OperationId));
    }

    [Fact]
    public void BindingOperationMustMatchExpectedIntentEvenIfBothIntentsExist()
    {
        var first = Intent(); var second = Intent(); var binding = Binding(second);
        using var journal = new AgentJournal(directory); journal.SaveTargetCreationIntent(first); journal.SaveTargetCreationIntent(second);
        Assert.Throws<ArgumentException>(() => journal.SaveTargetCreationPermitBinding(first, binding));
        Assert.Null(journal.GetTargetCreationPermitBinding(first.OperationId)); Assert.Null(journal.GetTargetCreationPermitBinding(second.OperationId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PermitAndAttemptCannotBeReusedForAnotherOperation(bool reuseAttempt)
    {
        var first = Intent(); var second = Intent(); var binding = Binding(first); var other = Binding(second);
        other = reuseAttempt ? other with { AttemptId = binding.AttemptId } : other with { PermitId = binding.PermitId };
        using var journal = new AgentJournal(directory); journal.SaveTargetCreationIntent(first); var record = journal.SaveTargetCreationIntent(second);
        journal.SaveTargetCreationPermitBinding(first, binding);
        Assert.Throws<InvalidOperationException>(() => journal.SaveTargetCreationPermitBinding(second, other));
        Assert.Equal(binding, journal.GetTargetCreationPermitBinding(first.OperationId));
        Assert.Null(journal.GetTargetCreationPermitBinding(second.OperationId)); Assert.Equal(record, journal.GetTargetCreationIntent(second.OperationId));
    }

    [Fact]
    public void SecondJournalOwnerIsRejectedAndNextOwnerOnlyReceivesExactReplay()
    {
        var intent = Intent(); var binding = Binding(intent); TargetCreationRecord record;
        using (var first = new AgentJournal(directory))
        {
            record = first.SaveTargetCreationIntent(intent);
            AssertReceipt(first.SaveTargetCreationPermitBinding(intent, binding), binding, true);
            // AgentJournal has a process-wide directory ownership lock, not a multi-writer contract.
            Assert.Throws<IOException>(() => { using var second = new AgentJournal(directory); });
            Assert.Equal(record, first.GetTargetCreationIntent(intent.OperationId));
            Assert.Equal(binding, first.GetTargetCreationPermitBinding(intent.OperationId));
        }
        using var nextOwner = new AgentJournal(directory);
        AssertReceipt(nextOwner.SaveTargetCreationPermitBinding(intent, binding), binding, false);
        Assert.Equal(record, nextOwner.GetTargetCreationIntent(intent.OperationId));
    }

    [Fact]
    public void BoundAttemptRejectsDifferentBeginAndMatchingAttemptReplaysAfterQuarantine()
    {
        var intent = Intent(); var binding = Binding(intent); using var journal = new AgentJournal(directory);
        var record = journal.SaveTargetCreationIntent(intent); journal.SaveTargetCreationPermitBinding(intent, binding);
        Assert.Throws<InvalidOperationException>(() => journal.TryBeginTargetCreation(intent.OperationId, 1, Guid.NewGuid()));
        Assert.Equal(record, journal.GetTargetCreationIntent(intent.OperationId));
        var begin = journal.TryBeginTargetCreation(intent.OperationId, 1, binding.AttemptId);
        Assert.True(begin.NewlyRecorded); Assert.Equal(binding.AttemptId, begin.Record.AttemptId);
        var quarantined = journal.QuarantineTargetCreation(intent.OperationId, 2, "OutcomeUnknown");
        var replay = journal.TryBeginTargetCreation(intent.OperationId, 1, binding.AttemptId);
        Assert.False(replay.NewlyRecorded); Assert.Equal(quarantined, replay.Record);
        AssertReceipt(journal.SaveTargetCreationPermitBinding(intent, binding), binding, false);
    }

    [Theory]
    [InlineData("permit='00000000-0000-0000-0000-000000000000'")]
    [InlineData("attempt='not-a-guid'")]
    [InlineData("issued_at='not-a-timestamp'")]
    [InlineData("start_before='2026-11-08T06:04:59.0000000+00:00'")]
    [InlineData("issued_at='2026-11-08T07:00:00.0000000+01:00'")]
    public void RawCorruptBindingFailsClosedOnReadAndReplay(string assignment)
    {
        var intent = Intent(); var binding = Binding(intent);
        using (var journal = new AgentJournal(directory))
        {
            journal.SaveTargetCreationIntent(intent); journal.SaveTargetCreationPermitBinding(intent, binding);
        }
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "agent-journal.sqlite"), Pooling = false }.ToString()))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "PRAGMA ignore_check_constraints=ON"; command.ExecuteNonQuery();
            command.CommandText = "UPDATE target_creation_permit_bindings SET " + assignment;
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        using var reopened = new AgentJournal(directory);
        Assert.Throws<InvalidOperationException>(() => reopened.GetTargetCreationPermitBinding(intent.OperationId));
        Assert.Throws<InvalidOperationException>(() => reopened.SaveTargetCreationPermitBinding(intent, binding));
        var record = reopened.GetTargetCreationIntent(intent.OperationId)!;
        Assert.Equal(TargetCreationState.IntentRecorded, record.State); Assert.Equal(1, record.Revision); Assert.Null(record.AttemptId);
    }

    [Fact]
    public void RawLocalAttemptDriftMakesBindingUnreadableWithoutRepairingEitherRecord()
    {
        var intent = Intent(); var binding = Binding(intent); var differentAttempt = Guid.NewGuid();
        using (var journal = new AgentJournal(directory))
        {
            journal.SaveTargetCreationIntent(intent); journal.SaveTargetCreationPermitBinding(intent, binding);
        }
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "agent-journal.sqlite"), Pooling = false }.ToString()))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "UPDATE target_creation_intents SET state='CreationInFlight',revision=2,attempt=$attempt,attempt_revision=1";
            command.Parameters.AddWithValue("$attempt", differentAttempt.ToString()); Assert.Equal(1, command.ExecuteNonQuery());
        }
        using var reopened = new AgentJournal(directory);
        Assert.Throws<InvalidOperationException>(() => reopened.GetTargetCreationPermitBinding(intent.OperationId));
        Assert.Throws<InvalidOperationException>(() => reopened.SaveTargetCreationPermitBinding(intent, binding));
        var record = reopened.GetTargetCreationIntent(intent.OperationId)!;
        Assert.Equal(differentAttempt, record.AttemptId); Assert.Equal(2, record.Revision);
    }

    [Fact]
    public void BindingAndReceiptDiagnosticsRedactAllPinsAndNames()
    {
        var intent = Intent(); var binding = Binding(intent); using var journal = new AgentJournal(directory);
        journal.SaveTargetCreationIntent(intent); var receipt = journal.SaveTargetCreationPermitBinding(intent, binding);
        var conflict = Assert.Throws<InvalidOperationException>(() => journal.SaveTargetCreationPermitBinding(intent, binding with { AgentId = Guid.NewGuid() }));
        foreach (var diagnostic in new[] { binding.ToString(), receipt.ToString(), receipt.Binding.ToString(), conflict.Message })
        {
            foreach (var id in new[] { binding.PermitId, binding.OperationId, binding.AttemptId, binding.RestoreIntentId, binding.ArtifactId,
                binding.AgentId, intent.EnvironmentId, intent.JobId, intent.ReservationId, intent.TargetIncarnationId, intent.CanonicalClusterId, intent.CanonicalSqlServerId })
                Assert.DoesNotContain(id.ToString(), diagnostic);
            Assert.DoesNotContain(intent.InfobaseName, diagnostic); Assert.DoesNotContain(intent.SqlDatabaseName, diagnostic);
        }
        AssertReceipt(receipt, binding, true);
    }

    [Fact]
    public void LegacyLeaseSettlementDoesNotRemoveBindingOrChangeLocalBarrier()
    {
        var intent = Intent(); var binding = Binding(intent); using var journal = new AgentJournal(directory);
        var record = journal.SaveTargetCreationIntent(intent); journal.SaveTargetCreationPermitBinding(intent, binding);
        journal.StoreGrant(new LeaseGrant(intent.JobId, intent.FencingToken, IssuedAt.AddMinutes(5), "ArchiveStageOnly", "{}"));
        journal.MarkSettled(intent.JobId, intent.FencingToken);
        Assert.Equal(record, journal.GetTargetCreationIntent(intent.OperationId));
        Assert.Equal(binding, journal.GetTargetCreationPermitBinding(intent.OperationId));
        AssertReceipt(journal.SaveTargetCreationPermitBinding(intent, binding), binding, false);
    }

    [Fact]
    public void NullInputsAndEmptyLookupFailWithoutCreatingIntent()
    {
        var intent = Intent(); using var journal = new AgentJournal(directory);
        Assert.Throws<ArgumentException>(() => journal.SaveTargetCreationPermitBinding(null!, Binding(intent)));
        Assert.Throws<ArgumentException>(() => journal.SaveTargetCreationPermitBinding(intent, null!));
        Assert.Throws<ArgumentException>(() => journal.GetTargetCreationPermitBinding(Guid.Empty));
        Assert.Empty(journal.ListUnresolvedTargetCreationIntents());
    }
}
