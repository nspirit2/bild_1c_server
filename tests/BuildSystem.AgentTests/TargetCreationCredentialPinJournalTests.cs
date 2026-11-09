using BuildSystem.Agent;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class TargetCreationCredentialPinJournalTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "target-creation-credential-pin-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset IssuedAt = new(2026, 11, 8, 6, 0, 0, TimeSpan.Zero);

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    private static TargetCreationIntent Intent()
    {
        var reservation = Guid.NewGuid(); var name = "bs_test_" + reservation.ToString("N");
        return new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 42, reservation, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), name, name);
    }

    // Keep the original eight-positional-argument construction source compatible.
    private static TargetCreationPermitBinding LegacyBinding(TargetCreationIntent intent) => new(
        Guid.NewGuid(), intent.OperationId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), IssuedAt, IssuedAt.AddMinutes(5));

    private static TargetCreationPermitBinding BoundBinding(TargetCreationIntent intent) => LegacyBinding(intent) with
    {
        CredentialId = Guid.NewGuid(), EndpointMappingId = Guid.NewGuid()
    };

    private SqliteConnection OpenRaw()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "agent-journal.sqlite"), Pooling = false, ForeignKeys = true
        }.ToString());
        db.Open(); return db;
    }

    private static int Execute(SqliteConnection db, string sql, params object[] values)
    {
        using var command = db.CreateCommand(); command.CommandText = sql;
        for (var index = 0; index < values.Length; index++) command.Parameters.AddWithValue("$" + index, values[index]);
        return command.ExecuteNonQuery();
    }

    private static long Count(SqliteConnection db, string table)
    {
        using var command = db.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM " + table;
        return (long)command.ExecuteScalar()!;
    }

    private static void AssertReceipt(TargetCreationPermitBindingReceipt receipt, TargetCreationPermitBinding binding, bool newlyRecorded)
    {
        Assert.Equal(binding, receipt.Binding); Assert.Equal(newlyRecorded, receipt.NewlyRecorded);
        Assert.False(receipt.Executable); Assert.True(receipt.ScheduledJobsMustRemainBlocked);
    }

    private static TargetCreationRecord Advance(AgentJournal journal, TargetCreationIntent intent, TargetCreationPermitBinding binding, int state)
    {
        var record = journal.GetTargetCreationIntent(intent.OperationId)!;
        if (state is 1 or 2) record = journal.TryBeginTargetCreation(intent.OperationId, record.Revision, binding.AttemptId).Record;
        if (state is 2 or 3) record = journal.QuarantineTargetCreation(intent.OperationId, record.Revision, "OutcomeUnknown");
        return record;
    }

    [Fact]
    public void EightArgumentConstructorRemainsLegacyAndWithPinsParticipatesInRecordEquality()
    {
        var legacy = LegacyBinding(Intent());
        Assert.Null(legacy.CredentialId); Assert.Null(legacy.EndpointMappingId);
        var bound = legacy with { CredentialId = Guid.NewGuid(), EndpointMappingId = Guid.NewGuid() };
        Assert.NotEqual(legacy, bound); Assert.Equal(bound, bound with { });
        Assert.NotEqual(bound, bound with { CredentialId = Guid.NewGuid() });
        Assert.NotEqual(bound, bound with { EndpointMappingId = Guid.NewGuid() });
        var (permit, operation, attempt, restore, artifact, agent, issued, deadline) = bound;
        Assert.Equal((legacy.PermitId, legacy.OperationId, legacy.AttemptId, legacy.RestoreIntentId, legacy.ArtifactId, legacy.AgentId, legacy.IssuedAt, legacy.StartBefore),
            (permit, operation, attempt, restore, artifact, agent, issued, deadline));
    }

    [Fact]
    public void BoundPairPersistsAcrossReopenWithoutChangingIntentOrGrantingAuthority()
    {
        var intent = Intent(); var binding = BoundBinding(intent); TargetCreationRecord record;
        using (var journal = new AgentJournal(directory))
        {
            record = journal.SaveTargetCreationIntent(intent);
            AssertReceipt(journal.SaveTargetCreationPermitBinding(intent, binding), binding, true);
            Assert.Equal(record, journal.GetTargetCreationIntent(intent.OperationId));
        }
        using var reopened = new AgentJournal(directory);
        Assert.Equal(binding, reopened.GetTargetCreationPermitBinding(intent.OperationId));
        AssertReceipt(reopened.SaveTargetCreationPermitBinding(intent, binding), binding, false);
        Assert.Equal(record, reopened.GetTargetCreationIntent(intent.OperationId));
        Assert.Null(record.AttemptId); Assert.Equal(1, record.Revision);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void BoundExactReplayPreservesEveryStateIncludingBothQuarantinePaths(int state)
    {
        var intent = Intent(); var binding = BoundBinding(intent); TargetCreationRecord record;
        using (var journal = new AgentJournal(directory))
        {
            journal.SaveTargetCreationIntent(intent); journal.SaveTargetCreationPermitBinding(intent, binding);
            record = Advance(journal, intent, binding, state);
        }
        using var reopened = new AgentJournal(directory);
        AssertReceipt(reopened.SaveTargetCreationPermitBinding(intent, binding), binding, false);
        Assert.Equal(record, reopened.GetTargetCreationIntent(intent.OperationId));
        Assert.Equal(record, Assert.Single(reopened.ListUnresolvedTargetCreationIntents()));
    }

    public static IEnumerable<object[]> Conflicts()
    {
        foreach (var state in new[] { 0, 1, 2, 3 })
            foreach (var change in new[] { "credential", "mapping", "unbind", "bind" })
                yield return new object[] { state, change };
    }

    [Theory]
    [MemberData(nameof(Conflicts))]
    public void ChangedPinsAndBoundUnboundTransitionsConflictInEveryState(int state, string change)
    {
        var intent = Intent(); var binding = change == "bind" ? LegacyBinding(intent) : BoundBinding(intent);
        var changed = change switch
        {
            "credential" => binding with { CredentialId = Guid.NewGuid() },
            "mapping" => binding with { EndpointMappingId = Guid.NewGuid() },
            "unbind" => binding with { CredentialId = null, EndpointMappingId = null },
            _ => binding with { CredentialId = Guid.NewGuid(), EndpointMappingId = Guid.NewGuid() }
        };
        TargetCreationRecord record;
        using (var journal = new AgentJournal(directory))
        {
            journal.SaveTargetCreationIntent(intent); journal.SaveTargetCreationPermitBinding(intent, binding);
            record = Advance(journal, intent, binding, state);
        }
        using var reopened = new AgentJournal(directory);
        Assert.Throws<InvalidOperationException>(() => reopened.SaveTargetCreationPermitBinding(intent, changed));
        Assert.Equal(binding, reopened.GetTargetCreationPermitBinding(intent.OperationId));
        Assert.Equal(record, reopened.GetTargetCreationIntent(intent.OperationId));
        AssertReceipt(reopened.SaveTargetCreationPermitBinding(intent, binding), binding, false);
    }

    [Theory]
    [InlineData(null, "valid")]
    [InlineData("valid", null)]
    [InlineData("empty", null)]
    [InlineData(null, "empty")]
    [InlineData("empty", "valid")]
    [InlineData("valid", "empty")]
    [InlineData("empty", "empty")]
    public void InvalidPairsAreRejectedBeforeDatabaseAccessAndDoNotPersist(string? credential, string? mapping)
    {
        static Guid? Pin(string? value) => value is null ? null : value == "empty" ? Guid.Empty : Guid.NewGuid();
        var intent = Intent(); var binding = LegacyBinding(intent) with { CredentialId = Pin(credential), EndpointMappingId = Pin(mapping) };
        var journal = new AgentJournal(directory);
        var record = journal.SaveTargetCreationIntent(intent);
        Assert.Throws<ArgumentException>(() => journal.SaveTargetCreationPermitBinding(intent, binding));
        Assert.Equal(record, journal.GetTargetCreationIntent(intent.OperationId));
        Assert.Null(journal.GetTargetCreationPermitBinding(intent.OperationId));
        journal.Dispose();
        // A disposed connection would fail if validation reached BeginTransaction/read/write.
        Assert.Throws<ArgumentException>(() => journal.SaveTargetCreationPermitBinding(intent, binding));
        using var db = OpenRaw();
        Assert.Equal(0L, Count(db, "target_creation_permit_bindings"));
        Assert.Equal(0L, Count(db, "target_creation_permit_credential_pins"));
    }

    [Fact]
    public void LegacyBindingHasNoSidecarRowAndReplaysWithBothPinsNull()
    {
        var intent = Intent(); var binding = LegacyBinding(intent);
        using (var journal = new AgentJournal(directory))
        {
            journal.SaveTargetCreationIntent(intent); AssertReceipt(journal.SaveTargetCreationPermitBinding(intent, binding), binding, true);
        }
        using (var db = OpenRaw()) Assert.Equal(0L, Count(db, "target_creation_permit_credential_pins"));
        using var reopened = new AgentJournal(directory);
        AssertReceipt(reopened.SaveTargetCreationPermitBinding(intent, binding), binding, false);
        Assert.Null(reopened.GetTargetCreationPermitBinding(intent.OperationId)!.CredentialId);
        Assert.Null(reopened.GetTargetCreationPermitBinding(intent.OperationId)!.EndpointMappingId);
    }

    [Fact]
    public void ReopeningOldDatabaseCreatesSidecarWithoutInventingPinsOrChangingBaseSchema()
    {
        var intent = Intent(); var binding = LegacyBinding(intent); TargetCreationRecord record; string schema;
        using (var journal = new AgentJournal(directory))
        {
            record = journal.SaveTargetCreationIntent(intent); journal.SaveTargetCreationPermitBinding(intent, binding);
        }
        using (var db = OpenRaw())
        {
            using var command = db.CreateCommand();
            command.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='target_creation_permit_bindings'";
            schema = (string)command.ExecuteScalar()!;
            // This temp fixture is an actual legacy-shaped database: original populated base table, no sidecar.
            Assert.Equal(0L, Count(db, "target_creation_permit_credential_pins"));
            Execute(db, "DROP TABLE target_creation_permit_credential_pins");
        }
        using (var reopened = new AgentJournal(directory))
        {
            Assert.Equal(binding, reopened.GetTargetCreationPermitBinding(intent.OperationId));
            Assert.Equal(record, reopened.GetTargetCreationIntent(intent.OperationId));
            AssertReceipt(reopened.SaveTargetCreationPermitBinding(intent, binding), binding, false);
        }
        using var inspected = OpenRaw();
        Assert.Equal(0L, Count(inspected, "target_creation_permit_credential_pins"));
        using var schemaCommand = inspected.CreateCommand();
        schemaCommand.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='target_creation_permit_bindings'";
        Assert.Equal(schema, (string)schemaCommand.ExecuteScalar()!);
    }

    [Fact]
    public void SidecarContainsOnlyPermitAndCredentialIdentityPair()
    {
        var intent = Intent(); var binding = BoundBinding(intent);
        using var journal = new AgentJournal(directory);
        journal.SaveTargetCreationIntent(intent); journal.SaveTargetCreationPermitBinding(intent, binding);
        using var db = OpenRaw(); using var command = db.CreateCommand();
        command.CommandText = "PRAGMA table_info(target_creation_permit_credential_pins)";
        var columns = new List<string>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(1));
        Assert.Equal(new[] { "permit", "credential", "endpoint_mapping" }, columns);
        command.CommandText = "SELECT permit,credential,endpoint_mapping FROM target_creation_permit_credential_pins";
        using var row = command.ExecuteReader(); Assert.True(row.Read());
        Assert.Equal(binding.PermitId.ToString(), row.GetString(0));
        Assert.Equal(binding.CredentialId!.Value.ToString(), row.GetString(1));
        Assert.Equal(binding.EndpointMappingId!.Value.ToString(), row.GetString(2)); Assert.False(row.Read());
    }

    [Fact]
    public void SidecarForeignKeyRejectsOrphanPermit()
    {
        using var journal = new AgentJournal(directory); using var db = OpenRaw();
        var error = Assert.Throws<SqliteException>(() => Execute(db,
            "INSERT INTO target_creation_permit_credential_pins(permit,credential,endpoint_mapping) VALUES($0,$1,$2)",
            Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString()));
        Assert.Equal(19, error.SqliteErrorCode); Assert.Equal(0L, Count(db, "target_creation_permit_credential_pins"));
    }

    [Theory]
    [InlineData("UPDATE target_creation_permit_credential_pins SET credential=$0")]
    [InlineData("UPDATE target_creation_permit_credential_pins SET endpoint_mapping=$0")]
    [InlineData("UPDATE target_creation_permit_credential_pins SET credential=credential")]
    [InlineData("DELETE FROM target_creation_permit_credential_pins WHERE permit=$0")]
    public void SidecarUpdateAndDeleteAreRejectedEvenForNoOpUpdate(string sql)
    {
        var intent = Intent(); var binding = BoundBinding(intent);
        using var journal = new AgentJournal(directory);
        journal.SaveTargetCreationIntent(intent); journal.SaveTargetCreationPermitBinding(intent, binding);
        using var db = OpenRaw();
        var value = sql.StartsWith("DELETE", StringComparison.Ordinal) ? binding.PermitId : Guid.NewGuid();
        var error = Assert.Throws<SqliteException>(() => sql.Contains("$0", StringComparison.Ordinal)
            ? Execute(db, sql, value.ToString()) : Execute(db, sql));
        Assert.Equal(19, error.SqliteErrorCode);
        Assert.Equal(binding, journal.GetTargetCreationPermitBinding(intent.OperationId));
    }

    [Fact]
    public void SidecarInsertOrReplaceCannotBypassImmutableIdentityPins()
    {
        var intent = Intent(); var binding = BoundBinding(intent);
        using var journal = new AgentJournal(directory);
        journal.SaveTargetCreationIntent(intent); journal.SaveTargetCreationPermitBinding(intent, binding);
        using var db = OpenRaw();
        var error = Assert.Throws<SqliteException>(() => Execute(db,
            "INSERT OR REPLACE INTO target_creation_permit_credential_pins(permit,credential,endpoint_mapping) VALUES($0,$1,$2)",
            binding.PermitId.ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString()));
        Assert.Equal(19, error.SqliteErrorCode);
        Assert.Equal(binding, journal.GetTargetCreationPermitBinding(intent.OperationId));
        Assert.Equal(1L, Count(db, "target_creation_permit_credential_pins"));
    }

    [Fact]
    public void SidecarInsertFailureRollsBackBaseBindingAndLeavesRetryPossible()
    {
        var intent = Intent(); var binding = BoundBinding(intent);
        using var journal = new AgentJournal(directory); var record = journal.SaveTargetCreationIntent(intent);
        using var db = OpenRaw();
        Execute(db, "CREATE TRIGGER test_reject_credential_pin BEFORE INSERT ON target_creation_permit_credential_pins BEGIN SELECT RAISE(ABORT,'test sidecar failure'); END");
        Assert.Throws<SqliteException>(() => journal.SaveTargetCreationPermitBinding(intent, binding));
        Assert.Equal(0L, Count(db, "target_creation_permit_bindings"));
        Assert.Equal(0L, Count(db, "target_creation_permit_credential_pins"));
        Assert.Null(journal.GetTargetCreationPermitBinding(intent.OperationId));
        Assert.Equal(record, journal.GetTargetCreationIntent(intent.OperationId));
        Execute(db, "DROP TRIGGER test_reject_credential_pin");
        AssertReceipt(journal.SaveTargetCreationPermitBinding(intent, binding), binding, true);
        Assert.Equal(1L, Count(db, "target_creation_permit_bindings"));
        Assert.Equal(1L, Count(db, "target_creation_permit_credential_pins"));
    }

    [Theory]
    [InlineData(null, "valid")]
    [InlineData("valid", null)]
    [InlineData(null, null)]
    [InlineData("empty", "valid")]
    [InlineData("valid", "empty")]
    [InlineData("garbage", "valid")]
    [InlineData("valid", "garbage")]
    [InlineData("uppercase", "valid")]
    [InlineData("valid", "uppercase")]
    public void MalformedPersistedSidecarFailsClosedOnReadAndReplay(string? credential, string? mapping)
    {
        static object RawPin(string? value) => value switch
        {
            null => DBNull.Value, "empty" => Guid.Empty.ToString(), "garbage" => "not-a-guid",
            "uppercase" => "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA", _ => Guid.NewGuid().ToString()
        };
        var intent = Intent(); var binding = BoundBinding(intent); TargetCreationRecord record;
        using (var journal = new AgentJournal(directory))
        {
            record = journal.SaveTargetCreationIntent(intent); journal.SaveTargetCreationPermitBinding(intent, binding);
        }
        using (var db = OpenRaw())
        {
            // Deliberately corrupt only this disposable test database, bypassing checks and immutability.
            Execute(db, "DROP TABLE target_creation_permit_credential_pins");
            Execute(db, "CREATE TABLE target_creation_permit_credential_pins(permit TEXT PRIMARY KEY,credential TEXT,endpoint_mapping TEXT)");
            Execute(db, "INSERT INTO target_creation_permit_credential_pins VALUES($0,$1,$2)", binding.PermitId.ToString(), RawPin(credential), RawPin(mapping));
        }
        using var reopened = new AgentJournal(directory);
        Assert.Throws<InvalidOperationException>(() => reopened.GetTargetCreationPermitBinding(intent.OperationId));
        Assert.Throws<InvalidOperationException>(() => reopened.SaveTargetCreationPermitBinding(intent, binding));
        Assert.Equal(record, reopened.GetTargetCreationIntent(intent.OperationId));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NewBoundBindingCannotBeIntroducedAfterBeginOrQuarantine(int state)
    {
        var intent = Intent(); var binding = BoundBinding(intent);
        using var journal = new AgentJournal(directory); journal.SaveTargetCreationIntent(intent);
        var record = Advance(journal, intent, binding, state);
        Assert.Throws<InvalidOperationException>(() => journal.SaveTargetCreationPermitBinding(intent, binding));
        Assert.Equal(record, journal.GetTargetCreationIntent(intent.OperationId));
        Assert.Null(journal.GetTargetCreationPermitBinding(intent.OperationId));
        using var db = OpenRaw(); Assert.Equal(0L, Count(db, "target_creation_permit_credential_pins"));
    }

    [Fact]
    public void BoundBindingWithoutIntentCannotCreateIntentOrSidecar()
    {
        var intent = Intent(); using var journal = new AgentJournal(directory);
        Assert.Throws<InvalidOperationException>(() => journal.SaveTargetCreationPermitBinding(intent, BoundBinding(intent)));
        Assert.Null(journal.GetTargetCreationIntent(intent.OperationId)); Assert.Empty(journal.ListUnresolvedTargetCreationIntents());
        using var db = OpenRaw(); Assert.Equal(0L, Count(db, "target_creation_permit_credential_pins"));
    }

    [Fact]
    public void DifferentOperationsMayPinSameCredentialAndMappingWithoutMakingThemAuthority()
    {
        var first = Intent(); var second = Intent(); var binding = BoundBinding(first);
        var other = BoundBinding(second) with { CredentialId = binding.CredentialId, EndpointMappingId = binding.EndpointMappingId };
        using var journal = new AgentJournal(directory); journal.SaveTargetCreationIntent(first); journal.SaveTargetCreationIntent(second);
        AssertReceipt(journal.SaveTargetCreationPermitBinding(first, binding), binding, true);
        AssertReceipt(journal.SaveTargetCreationPermitBinding(second, other), other, true);
        Assert.Equal(2, journal.ListUnresolvedTargetCreationIntents().Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BoundPermitAndAttemptSlotsRemainUniqueAcrossOperations(bool reusePermit)
    {
        var first = Intent(); var second = Intent(); var binding = BoundBinding(first); var other = BoundBinding(second);
        other = reusePermit ? other with { PermitId = binding.PermitId } : other with { AttemptId = binding.AttemptId };
        using var journal = new AgentJournal(directory); journal.SaveTargetCreationIntent(first); journal.SaveTargetCreationIntent(second);
        journal.SaveTargetCreationPermitBinding(first, binding);
        Assert.Throws<InvalidOperationException>(() => journal.SaveTargetCreationPermitBinding(second, other));
        Assert.Equal(binding, journal.GetTargetCreationPermitBinding(first.OperationId));
        Assert.Null(journal.GetTargetCreationPermitBinding(second.OperationId));
        using var db = OpenRaw(); Assert.Equal(1L, Count(db, "target_creation_permit_credential_pins"));
    }

    [Fact]
    public void PinDiagnosticsRedactCredentialAndEndpointMappingAndRetainInertReceiptFlags()
    {
        var intent = Intent(); var binding = BoundBinding(intent); using var journal = new AgentJournal(directory);
        journal.SaveTargetCreationIntent(intent); var receipt = journal.SaveTargetCreationPermitBinding(intent, binding);
        var changed = binding with { CredentialId = Guid.NewGuid(), EndpointMappingId = Guid.NewGuid() };
        var conflict = Assert.Throws<InvalidOperationException>(() => journal.SaveTargetCreationPermitBinding(intent, changed));
        foreach (var diagnostic in new[] { binding.ToString(), receipt.ToString(), receipt.Binding.ToString(), conflict.Message })
        {
            foreach (var id in new[] { binding.PermitId, binding.OperationId, binding.AttemptId, binding.RestoreIntentId, binding.ArtifactId,
                binding.AgentId, binding.CredentialId!.Value, binding.EndpointMappingId!.Value, changed.CredentialId!.Value, changed.EndpointMappingId!.Value })
                Assert.DoesNotContain(id.ToString(), diagnostic);
            Assert.DoesNotContain(intent.InfobaseName, diagnostic); Assert.DoesNotContain(intent.SqlDatabaseName, diagnostic);
        }
        AssertReceipt(receipt, binding, true);
        AssertReceipt(journal.SaveTargetCreationPermitBinding(intent, binding), binding, false);
    }

    [Fact]
    public void CredentialPinsDoNotAllowDifferentAttemptAndSurviveMatchingAttemptQuarantine()
    {
        var intent = Intent(); var binding = BoundBinding(intent); using var journal = new AgentJournal(directory);
        var original = journal.SaveTargetCreationIntent(intent); journal.SaveTargetCreationPermitBinding(intent, binding);
        Assert.Throws<InvalidOperationException>(() => journal.TryBeginTargetCreation(intent.OperationId, 1, Guid.NewGuid()));
        Assert.Equal(original, journal.GetTargetCreationIntent(intent.OperationId));
        journal.TryBeginTargetCreation(intent.OperationId, 1, binding.AttemptId);
        var record = journal.QuarantineTargetCreation(intent.OperationId, 2, "OutcomeUnknown");
        Assert.Equal(record, journal.TryBeginTargetCreation(intent.OperationId, 1, binding.AttemptId).Record);
        AssertReceipt(journal.SaveTargetCreationPermitBinding(intent, binding), binding, false);
    }

    [Fact]
    public void SidecarImmutabilityDoesNotInstallBroadBaseTableUpdateTriggers()
    {
        var intent = Intent(); var binding = LegacyBinding(intent);
        using (var journal = new AgentJournal(directory))
        {
            journal.SaveTargetCreationIntent(intent); journal.SaveTargetCreationPermitBinding(intent, binding);
        }
        using (var db = OpenRaw())
        {
            // Existing corruption/compatibility fixtures intentionally retain raw access to the unchanged base table.
            Assert.Equal(1, Execute(db, "UPDATE target_creation_permit_bindings SET agent=$0", Guid.NewGuid().ToString()));
        }
        using var reopened = new AgentJournal(directory);
        var read = reopened.GetTargetCreationPermitBinding(intent.OperationId)!;
        Assert.NotEqual(binding.AgentId, read.AgentId); Assert.Null(read.CredentialId); Assert.Null(read.EndpointMappingId);
    }
}