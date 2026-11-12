using BuildSystem.Agent;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class TargetCreationAcknowledgementJournalTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "target-creation-acknowledgement-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Issued = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    private static TargetCreationIntent Intent()
    {
        var reservation = Guid.NewGuid(); var name = "bs_test_" + reservation.ToString("N");
        return new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2, reservation, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), name, name);
    }

    private static TargetCreationPermitBinding Binding(TargetCreationIntent intent) => new(
        Guid.NewGuid(), intent.OperationId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Issued, Issued.AddMinutes(5))
    { CredentialId = Guid.NewGuid(), EndpointMappingId = Guid.NewGuid() };

    private static TargetCreationAcknowledgement Ack(TargetCreationIntent intent, TargetCreationPermitBinding binding) =>
        new(intent, binding, Guid.NewGuid(), Guid.NewGuid(), Issued.AddSeconds(12), binding.StartBefore);

    private TargetCreationRecord Begin(AgentJournal journal, TargetCreationIntent intent, TargetCreationPermitBinding binding)
    {
        journal.SaveTargetCreationIntent(intent);
        journal.SaveTargetCreationPermitBinding(intent, binding);
        return journal.TryBeginTargetCreation(intent.OperationId, 1, binding.AttemptId).Record;
    }

    [Fact]
    public void AtomicAcknowledgementPersistsQuarantineAndNeverConveysAuthorityAcrossReopen()
    {
        var intent = Intent(); var binding = Binding(intent); var ack = Ack(intent, binding);
        TargetCreationRecord quarantined;
        using (var journal = new AgentJournal(directory))
        {
            Begin(journal, intent, binding);
            quarantined = journal.QuarantineTargetCreationWithAcknowledgement(intent.OperationId, 2, ack);
            Assert.Equal(TargetCreationState.ReconciliationRequired, quarantined.State);
            Assert.Equal(3, quarantined.Revision);
            Assert.Equal("OutcomeUnknown", quarantined.QuarantineReason);
            Assert.Equal(ack, journal.GetTargetCreationAcknowledgement(intent.OperationId));
            Assert.False(ack.Executable); Assert.False(ack.FreshAuthority); Assert.False(ack.RetryAllowed);
            Assert.False(ack.RestoreAllowed); Assert.False(ack.OwnershipEstablished);
            Assert.True(ack.ScheduledJobsMustRemainBlocked); Assert.True(ack.RequiresIndependentVerification);
            Assert.DoesNotContain(ack.ReportedInfobaseId.ToString(), ack.ToString());
            Assert.Throws<InvalidOperationException>(() => journal.QuarantineTargetCreationWithAcknowledgement(intent.OperationId, 2, ack));
            Assert.Equal(quarantined, journal.GetTargetCreationIntent(intent.OperationId));
        }
        using var reopened = new AgentJournal(directory);
        Assert.Equal(ack, reopened.GetTargetCreationAcknowledgement(intent.OperationId));
        Assert.Equal(quarantined, reopened.GetTargetCreationIntent(intent.OperationId));
        var replay = reopened.TryBeginTargetCreation(intent.OperationId, 1, binding.AttemptId);
        Assert.False(replay.NewlyRecorded);
        Assert.Equal(quarantined, replay.Record);
        Assert.Throws<InvalidOperationException>(() => reopened.SaveTargetCreationIntent(intent with { OperationId = Guid.NewGuid() }));
    }

    [Fact]
    public void MissingAcknowledgementDoesNotUpgradeAnOrdinaryQuarantine()
    {
        var intent = Intent(); var binding = Binding(intent); using var journal = new AgentJournal(directory);
        Assert.Null(journal.GetTargetCreationAcknowledgement(intent.OperationId));
        Begin(journal, intent, binding);
        var old = journal.QuarantineTargetCreation(intent.OperationId, 2, "OutcomeUnknown");
        Assert.Null(journal.GetTargetCreationAcknowledgement(intent.OperationId));
        Assert.Throws<InvalidOperationException>(() => journal.QuarantineTargetCreationWithAcknowledgement(intent.OperationId, 2, Ack(intent, binding)));
        Assert.Equal(old, journal.GetTargetCreationIntent(intent.OperationId));
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("state")]
    [InlineData("operation")]
    [InlineData("intent")]
    [InlineData("binding")]
    [InlineData("attempt")]
    public void FailedCorrelationOrCasDoesNotWriteAnOrphanAcknowledgement(string change)
    {
        var intent = Intent(); var binding = Binding(intent); using var journal = new AgentJournal(directory);
        var inFlight = Begin(journal, intent, binding);
        var ack = Ack(intent, binding);
        if (change == "state") inFlight = journal.QuarantineTargetCreation(intent.OperationId, 2, "OutcomeUnknown");
        if (change == "intent") ack = ack with { Intent = intent with { JobId = Guid.NewGuid() } };
        if (change == "binding") ack = ack with { Binding = binding with { AgentId = Guid.NewGuid() } };
        if (change == "attempt") ack = ack with { Binding = binding with { AttemptId = Guid.NewGuid() } };
        var operationId = change == "operation" ? Guid.NewGuid() : intent.OperationId;
        var revision = change == "revision" ? 1 : 2;
        Assert.ThrowsAny<Exception>(() => journal.QuarantineTargetCreationWithAcknowledgement(operationId, revision, ack));
        Assert.Equal(inFlight, journal.GetTargetCreationIntent(intent.OperationId));
        Assert.Null(journal.GetTargetCreationAcknowledgement(intent.OperationId));
    }

    [Theory]
    [InlineData("claim")]
    [InlineData("reported")]
    [InlineData("issuedBefore")]
    [InlineData("issuedAfter")]
    [InlineData("expires")]
    [InlineData("offsetIssued")]
    [InlineData("offsetExpires")]
    [InlineData("credential")]
    [InlineData("mapping")]
    public void InvalidClaimOrCredentialPinsNeverChangeInFlightRecord(string change)
    {
        var intent = Intent(); var binding = Binding(intent); using var journal = new AgentJournal(directory);
        var inFlight = Begin(journal, intent, binding); var ack = Ack(intent, binding);
        ack = change switch
        {
            "claim" => ack with { ClaimId = Guid.Empty },
            "reported" => ack with { ReportedInfobaseId = Guid.Empty },
            "issuedBefore" => ack with { ClaimIssuedAt = Issued.AddTicks(-1) },
            "issuedAfter" => ack with { ClaimIssuedAt = binding.StartBefore },
            "expires" => ack with { ClaimExpiresAt = binding.StartBefore.AddTicks(-1) },
            "offsetIssued" => ack with { ClaimIssuedAt = ack.ClaimIssuedAt.ToOffset(TimeSpan.FromHours(1)) },
            "offsetExpires" => ack with { ClaimExpiresAt = ack.ClaimExpiresAt.ToOffset(TimeSpan.FromHours(1)) },
            "credential" => ack with { Binding = binding with { CredentialId = null, EndpointMappingId = null } },
            _ => ack with { Binding = binding with { EndpointMappingId = Guid.Empty } }
        };
        Assert.Throws<ArgumentException>(() => journal.QuarantineTargetCreationWithAcknowledgement(intent.OperationId, 2, ack));
        Assert.Equal(inFlight, journal.GetTargetCreationIntent(intent.OperationId));
        Assert.Null(journal.GetTargetCreationAcknowledgement(intent.OperationId));
    }

    [Fact]
    public void LegacyUnpinnedBindingCannotBeAcknowledged()
    {
        var intent = Intent(); var binding = Binding(intent) with { CredentialId = null, EndpointMappingId = null };
        using var journal = new AgentJournal(directory); var record = Begin(journal, intent, binding);
        Assert.Throws<ArgumentException>(() => journal.QuarantineTargetCreationWithAcknowledgement(intent.OperationId, 2, Ack(intent, binding)));
        Assert.Null(journal.GetTargetCreationAcknowledgement(intent.OperationId));
        Assert.Equal(record, journal.GetTargetCreationIntent(intent.OperationId));
    }

    [Theory]
    [InlineData("claim")]
    [InlineData("reported_infobase")]
    [InlineData("claim_issued_at")]
    [InlineData("attempt")]
    public void PersistedTamperingFailsClosedWithoutReflectingRawContent(string column)
    {
        var intent = Intent(); var binding = Binding(intent); using (var journal = new AgentJournal(directory))
        {
            Begin(journal, intent, binding);
            journal.QuarantineTargetCreationWithAcknowledgement(intent.OperationId, 2, Ack(intent, binding));
        }
        var path = Path.Combine(directory, "agent-journal.sqlite");
        using (var db = new SqliteConnection($"Data Source={path}"))
        {
            db.Open();
            using (var drop = db.CreateCommand())
            {
                drop.CommandText = "DROP TRIGGER target_creation_acknowledgements_no_update";
                drop.ExecuteNonQuery();
            }
            using var update = db.CreateCommand();
            update.CommandText = $"UPDATE target_creation_acknowledgements SET {column}=$value";
            update.Parameters.AddWithValue("$value", "tampered-sensitive-value");
            update.ExecuteNonQuery();
        }
        using var reopened = new AgentJournal(directory);
        var error = Assert.Throws<InvalidOperationException>(() => reopened.GetTargetCreationAcknowledgement(intent.OperationId));
        Assert.DoesNotContain("tampered-sensitive-value", error.ToString());
    }

    [Fact]
    public void FailedQuarantineUpdateRollsBackTheAcknowledgementInsert()
    {
        var intent = Intent(); var binding = Binding(intent); using var journal = new AgentJournal(directory);
        var record = Begin(journal, intent, binding);
        using var db = new SqliteConnection($"Data Source={Path.Combine(directory, "agent-journal.sqlite")}");
        db.Open();
        using (var command = db.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER test_ignore_quarantine BEFORE UPDATE ON target_creation_intents
                BEGIN SELECT RAISE(IGNORE); END;
                """;
            command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidOperationException>(() => journal.QuarantineTargetCreationWithAcknowledgement(intent.OperationId, 2, Ack(intent, binding)));
        Assert.Equal(record, journal.GetTargetCreationIntent(intent.OperationId));
        Assert.Null(journal.GetTargetCreationAcknowledgement(intent.OperationId));
    }

    [Fact]
    public void ClaimCollisionCannotReplaceAnotherOperationAcknowledgement()
    {
        var first = Intent(); var firstBinding = Binding(first);
        var second = Intent(); var secondBinding = Binding(second);
        using var journal = new AgentJournal(directory);
        Begin(journal, first, firstBinding); Begin(journal, second, secondBinding);
        var firstAck = Ack(first, firstBinding);
        journal.QuarantineTargetCreationWithAcknowledgement(first.OperationId, 2, firstAck);
        var secondRecord = journal.GetTargetCreationIntent(second.OperationId);
        var collision = Ack(second, secondBinding) with { ClaimId = firstAck.ClaimId };
        Assert.Throws<SqliteException>(() => journal.QuarantineTargetCreationWithAcknowledgement(second.OperationId, 2, collision));
        Assert.Equal(firstAck, journal.GetTargetCreationAcknowledgement(first.OperationId));
        Assert.Null(journal.GetTargetCreationAcknowledgement(second.OperationId));
        Assert.Equal(secondRecord, journal.GetTargetCreationIntent(second.OperationId));
        using var db = new SqliteConnection($"Data Source={Path.Combine(directory, "agent-journal.sqlite")}");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO target_creation_acknowledgements
            (operation,permit,attempt,claim,reported_infobase,claim_issued_at,claim_expires_at)
            VALUES($operation,$permit,$attempt,$claim,$reported,$issued,$expires)
            """;
        command.Parameters.AddWithValue("$operation", second.OperationId.ToString());
        command.Parameters.AddWithValue("$permit", secondBinding.PermitId.ToString());
        command.Parameters.AddWithValue("$attempt", secondBinding.AttemptId.ToString());
        command.Parameters.AddWithValue("$claim", firstAck.ClaimId.ToString());
        command.Parameters.AddWithValue("$reported", collision.ReportedInfobaseId.ToString());
        command.Parameters.AddWithValue("$issued", collision.ClaimIssuedAt.ToString("O"));
        command.Parameters.AddWithValue("$expires", collision.ClaimExpiresAt.ToString("O"));
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        Assert.Equal(firstAck, journal.GetTargetCreationAcknowledgement(first.OperationId));
        Assert.Null(journal.GetTargetCreationAcknowledgement(second.OperationId));
    }

    [Fact]
    public void SqlReplaceUpdateAndDeleteCannotAlterARecordedAcknowledgement()
    {
        var intent = Intent(); var binding = Binding(intent); using var journal = new AgentJournal(directory);
        Begin(journal, intent, binding); var ack = Ack(intent, binding);
        journal.QuarantineTargetCreationWithAcknowledgement(intent.OperationId, 2, ack);
        var path = Path.Combine(directory, "agent-journal.sqlite");
        using var db = new SqliteConnection($"Data Source={path}"); db.Open();
        foreach (var sql in new[] {
            "DELETE FROM target_creation_acknowledgements",
            "UPDATE target_creation_acknowledgements SET claim='tampered-sensitive-value'",
            "INSERT OR REPLACE INTO target_creation_acknowledgements SELECT * FROM target_creation_acknowledgements" })
        {
            using var command = db.CreateCommand(); command.CommandText = sql;
            Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        }
        Assert.Equal(ack, journal.GetTargetCreationAcknowledgement(intent.OperationId));
    }
}
