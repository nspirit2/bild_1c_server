using System.Globalization;
using System.Text.Json;
using BuildSystem.Agent;
using Microsoft.Data.Sqlite;
using Xunit;
using static BuildSystem.TestFixtures.TargetRegistrationEvidenceDeliverySeed;

namespace BuildSystem.AgentTests;

public sealed class TargetRegistrationEvidenceDeliveryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "evidence-delivery-" + Guid.NewGuid().ToString("N"));
    private const string Secret = "private-evidence-canary";
    private const string AckTable = "target_registration_evidence_delivery_acknowledgements";
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)] [InlineData(DateTimeKind.Utc)] [InlineData(DateTimeKind.Local)]
    public void SavedProjectionHasExactlyThirteenHistoricalFieldsAndPreservesProviderDateKind(DateTimeKind kind)
    {
        using var journal = new AgentJournal(directory);
        var (ack, scope, created) = SeedEvidence(journal, kind);
        var report = journal.PrepareTargetRegistrationEvidenceReport(ack.Intent.OperationId);
        var s = scope.RegistrationScope;
        Assert.Equal(new TargetRegistrationEvidenceReport(ack.ClaimId, ack.ReportedInfobaseId,
            s.ObservationId, scope.SqlObservationId, s.RacEndpointMappingId, s.SqlEndpointMappingId,
            scope.ExpectedProfileId, s.ExpectedInfobaseName, s.ExpectedSqlServer, s.ExpectedDatabaseName,
            12, created.ToString("O", CultureInfo.InvariantCulture), "ONLINE"), report);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(report));
        Assert.Equal(new[] { "ClaimId", "ReportedInfobaseId", "RacObservationId", "SqlObservationId",
            "RacEndpointMappingId", "SqlEndpointMappingId", "RegistrationProfileId", "InfobaseName",
            "SqlServer", "DatabaseName", "SqlDatabaseId", "SqlCreateDate", "SqlState" },
            json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("TargetRegistrationEvidenceReport [historical; details omitted]", report.ToString());
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(report));
    }

    [Fact]
    public void RecordExactReplayIsIdempotentAndReopenPreservesServerAcceptanceAndQuarantine()
    {
        Guid operation; TargetRegistrationEvidenceReport report; TargetRegistrationEvidenceDeliveryReceipt receipt;
        using (var journal = new AgentJournal(directory))
        {
            var (ack, _, _) = SeedEvidence(journal); operation = ack.Intent.OperationId;
            var before = journal.GetTargetCreationIntent(operation);
            report = journal.PrepareTargetRegistrationEvidenceReport(operation); receipt = Receipt(operation, report);
            var saved = journal.RecordTargetRegistrationEvidenceDeliveryAcknowledgement(operation, report, receipt);
            var replay = journal.RecordTargetRegistrationEvidenceDeliveryAcknowledgement(operation, report, receipt);
            Assert.Equal(saved.Receipt, replay.Receipt); Assert.Equal(report, saved.Report);
            Assert.Equal(before, journal.GetTargetCreationIntent(operation));
            Assert.Equal(ack, journal.GetTargetCreationAcknowledgement(operation));
            Assert.False(journal.TryBeginTargetCreation(operation, 1, ack.Binding.AttemptId).NewlyRecorded);
            AssertInert(saved);
        }
        using var reopened = new AgentJournal(directory);
        var recovered = Assert.IsType<TargetRegistrationEvidenceDeliveryAcknowledgement>(reopened.GetTargetRegistrationEvidenceDeliveryAcknowledgement(operation));
        Assert.Equal(report, recovered.Report); Assert.Equal(receipt, recovered.Receipt); Assert.Equal(operation, recovered.OperationId);
        Assert.Equal(1, CountAcknowledgements());
    }

    [Theory]
    [InlineData("claim")] [InlineData("operation")] [InlineData("status")]
    [InlineData("offset")] [InlineData("default")] [InlineData("report")]
    public void InvalidReceiptOrExpectedProjectionIsRejectedAtomically(string mismatch)
    {
        using var journal = new AgentJournal(directory); var (ack, _, _) = SeedEvidence(journal);
        var operation = ack.Intent.OperationId; var before = journal.GetTargetCreationIntent(operation);
        var report = journal.PrepareTargetRegistrationEvidenceReport(operation); var receipt = Receipt(operation, report);
        receipt = mismatch switch
        {
            "claim" => receipt with { ClaimId = Guid.NewGuid() },
            "operation" => receipt with { OperationId = Guid.NewGuid() },
            "status" => receipt with { Status = Secret },
            "offset" => receipt with { AcceptedAt = receipt.AcceptedAt.ToOffset(TimeSpan.FromHours(1)) },
            "default" => receipt with { AcceptedAt = default }, _ => receipt
        };
        if (mismatch == "report") report = report with { SqlDatabaseId = 13 };
        RejectJournal(() => journal.RecordTargetRegistrationEvidenceDeliveryAcknowledgement(operation, report, receipt));
        Assert.Null(journal.GetTargetRegistrationEvidenceDeliveryAcknowledgement(operation));
        Assert.Equal(before, journal.GetTargetCreationIntent(operation)); Assert.Equal(0, CountAcknowledgements());
    }

    [Fact]
    public void ConflictingReplayCannotReplaceOriginalAcceptedAt()
    {
        using var journal = new AgentJournal(directory); var (ack, _, _) = SeedEvidence(journal);
        var op = ack.Intent.OperationId; var report = journal.PrepareTargetRegistrationEvidenceReport(op);
        var receipt = Receipt(op, report);
        journal.RecordTargetRegistrationEvidenceDeliveryAcknowledgement(op, report, receipt);
        RejectJournal(() => journal.RecordTargetRegistrationEvidenceDeliveryAcknowledgement(op, report,
            receipt with { AcceptedAt = receipt.AcceptedAt.AddSeconds(1) }));
        Assert.Equal(receipt, journal.GetTargetRegistrationEvidenceDeliveryAcknowledgement(op)!.Receipt);
    }

    [Theory]
    [InlineData("UPDATE {0} SET accepted_at='private-evidence-canary'")]
    [InlineData("DELETE FROM {0}")]
    [InlineData("INSERT OR REPLACE INTO {0} SELECT * FROM {0}")]
    public void OrdinarySqlCannotRewriteDeliveryAcknowledgement(string sql)
    {
        using var journal = new AgentJournal(directory); var (ack, _, _) = SeedEvidence(journal);
        var op = ack.Intent.OperationId; var report = journal.PrepareTargetRegistrationEvidenceReport(op);
        var receipt = Receipt(op, report); journal.RecordTargetRegistrationEvidenceDeliveryAcknowledgement(op, report, receipt);
        Assert.Throws<SqliteException>(() => Sql(string.Format(CultureInfo.InvariantCulture, sql, AckTable)));
        Assert.Equal(receipt, journal.GetTargetRegistrationEvidenceDeliveryAcknowledgement(op)!.Receipt);
    }

    [Fact]
    public async Task SuccessfulDeliveryCommitsOnceThenReopenedExplicitCallSkipsTransport()
    {
        Guid op; TargetRegistrationEvidenceReport report;
        using (var journal = new AgentJournal(directory))
        {
            var (ack, _, _) = SeedEvidence(journal); op = ack.Intent.OperationId;
            report = journal.PrepareTargetRegistrationEvidenceReport(op); var before = journal.GetTargetCreationIntent(op);
            var transport = new FakeTransport(r => Task.FromResult(Receipt(op, r)));
            var result = await Delivery(journal, transport).DeliverOperationAsync(op, default);
            Assert.True(result.DeliveredThisCall); Assert.Equal(op, result.OperationId);
            Assert.Equal(report, Assert.Single(transport.Reports)); AssertInert(result.Acknowledgement);
            Assert.False(result.Executable); Assert.False(result.RetryAllowed); Assert.False(result.FreshAuthority);
            Assert.Equal(before, journal.GetTargetCreationIntent(op));
        }
        using var reopened = new AgentJournal(directory);
        var unused = new FakeTransport(_ => throw new InvalidOperationException(Secret));
        var recovered = await Delivery(reopened, unused).DeliverOperationAsync(op, default);
        Assert.False(recovered.DeliveredThisCall); Assert.Empty(unused.Reports); Assert.Equal(report, recovered.Acknowledgement.Report);
    }

    [Fact]
    public async Task LostRemoteAcknowledgementRequiresExplicitRetryOfIdenticalSavedReport()
    {
        using var journal = new AgentJournal(directory); var (ack, _, _) = SeedEvidence(journal);
        var op = ack.Intent.OperationId; var accepted = new HashSet<Guid>(); var requests = 0;
        var transport = new FakeTransport(report =>
        {
            accepted.Add(report.ClaimId);
            if (++requests == 1) throw new InvalidOperationException(Secret);
            return Task.FromResult(Receipt(op, report));
        });
        var coordinator = Delivery(journal, transport);
        await RejectDelivery(() => coordinator.DeliverOperationAsync(op, default), "TargetRegistrationEvidenceDeliveryUnavailable");
        Assert.Null(journal.GetTargetRegistrationEvidenceDeliveryAcknowledgement(op)); Assert.Single(transport.Reports);
        var result = await coordinator.DeliverOperationAsync(op, default);
        Assert.True(result.DeliveredThisCall); Assert.Equal(2, transport.Reports.Count);
        Assert.Equal(transport.Reports[0], transport.Reports[1]); Assert.Single(accepted);
    }

    [Fact]
    public async Task ConcurrentExplicitDeliveryFailsBusyWithoutSecondTransportCall()
    {
        using var journal = new AgentJournal(directory); var (ack, _, _) = SeedEvidence(journal);
        var op = ack.Intent.OperationId;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport(async report =>
        {
            entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(30)); return Receipt(op, report);
        });
        var coordinator = Delivery(journal, transport); var first = coordinator.DeliverOperationAsync(op, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await RejectDelivery(() => coordinator.DeliverOperationAsync(op, default), "TargetRegistrationEvidenceDeliveryBusy");
            Assert.Single(transport.Reports);
        }
        finally { release.TrySetResult(); await first.WaitAsync(TimeSpan.FromSeconds(30)); }
    }

    [Theory]
    [InlineData(0)] [InlineData(121)] [InlineData(-1)]
    public void InvalidTimeoutFailsBeforeDisposedJournalOrTransport(int timeout)
    {
        var journal = new AgentJournal(directory); journal.Dispose();
        var transport = new FakeTransport(_ => throw new InvalidOperationException(Secret));
        var error = Assert.Throws<TargetRegistrationEvidenceDeliveryException>(() =>
            new TargetRegistrationEvidenceDelivery(journal, transport, new() { Enabled = true, TotalTimeoutSeconds = timeout }));
        Assert.Equal("TargetRegistrationEvidenceDeliveryInvalidOptions", error.Code);
        Assert.Null(error.InnerException); Assert.Empty(transport.Reports);
    }

    [Fact]
    public async Task DefaultDisabledDoesNotReadDisposedJournalOrCallTransport()
    {
        var journal = new AgentJournal(directory); journal.Dispose();
        var transport = new FakeTransport(_ => throw new InvalidOperationException(Secret));
        var coordinator = new TargetRegistrationEvidenceDelivery(journal, transport, new());
        await RejectDelivery(() => coordinator.DeliverOperationAsync(Guid.NewGuid(), default), "TargetRegistrationEvidenceDeliveryDisabled");
        Assert.Empty(transport.Reports);
    }

    [Fact]
    public async Task EmptyOperationFailsBeforeDisposedJournalOrTransport()
    {
        var journal = new AgentJournal(directory); journal.Dispose();
        var transport = new FakeTransport(_ => throw new InvalidOperationException(Secret));
        await RejectDelivery(() => Delivery(journal, transport).DeliverOperationAsync(Guid.Empty, default), "TargetRegistrationEvidenceDeliveryInvalidRequest");
        Assert.Empty(transport.Reports);
    }

    [Theory]
    [InlineData("ABORT,'private-evidence-canary'")] [InlineData("IGNORE")]
    public async Task FailedOrIgnoredLocalInsertIsUnconfirmedAndLeavesNoAcknowledgement(string failure)
    {
        using var journal = new AgentJournal(directory); var (ack, _, _) = SeedEvidence(journal);
        var op = ack.Intent.OperationId; var before = journal.GetTargetCreationIntent(op);
        Sql($"CREATE TRIGGER test_delivery_failure BEFORE INSERT ON {AckTable} BEGIN SELECT RAISE({failure}); END;");
        var transport = new FakeTransport(r => Task.FromResult(Receipt(op, r)));
        await RejectDelivery(() => Delivery(journal, transport).DeliverOperationAsync(op, default),
            "TargetRegistrationEvidenceDeliveryAcknowledgementUnconfirmed", true);
        Assert.Single(transport.Reports); Assert.Equal(0, CountAcknowledgements());
        Assert.Null(journal.GetTargetRegistrationEvidenceDeliveryAcknowledgement(op));
        Assert.Equal(before, journal.GetTargetCreationIntent(op));
    }

    [Theory]
    [InlineData("before")] [InlineData("during")] [InlineData("after")]
    public async Task CancellationBeforeLocalAcknowledgementNeverPersistsDelivery(string stage)
    {
        using var journal = new AgentJournal(directory); var (ack, _, _) = SeedEvidence(journal);
        var op = ack.Intent.OperationId; using var cancel = new CancellationTokenSource();
        var transport = new FakeTransport(report =>
        {
            cancel.Cancel();
            if (stage == "during") throw new OperationCanceledException(cancel.Token);
            return Task.FromResult(Receipt(op, report));
        });
        if (stage == "before") cancel.Cancel();
        await RejectDelivery(() => Delivery(journal, transport).DeliverOperationAsync(op, cancel.Token),
            "TargetRegistrationEvidenceDeliveryUnavailable");
        Assert.Equal(stage == "before" ? 0 : 1, transport.Reports.Count);
        Assert.Equal(0, CountAcknowledgements()); Assert.Null(journal.GetTargetRegistrationEvidenceDeliveryAcknowledgement(op));
    }

    [Theory]
    [InlineData("claim", "'private-evidence-canary'")]
    [InlineData("accepted_at", "'2020-01-01T00:00:02+00:00'")]
    [InlineData("accepted_at", "X'0102'")]
    public void StrictDeliveryGetterRejectsNoncanonicalOrCorruptRows(string column, string value)
    {
        using var journal = new AgentJournal(directory); var (ack, _, _) = SeedEvidence(journal);
        var op = ack.Intent.OperationId; var report = journal.PrepareTargetRegistrationEvidenceReport(op);
        journal.RecordTargetRegistrationEvidenceDeliveryAcknowledgement(op, report, Receipt(op, report));
        Sql($"DROP TRIGGER {AckTable}_no_update; UPDATE {AckTable} SET {column}={value}");
        RejectJournal(() => journal.GetTargetRegistrationEvidenceDeliveryAcknowledgement(op));
    }

    [Theory]
    [InlineData("ack")] [InlineData("evidence")]
    public void DeliveryReadsRevalidateStrictNestedCreationAcknowledgementAndEvidence(string target)
    {
        using var journal = new AgentJournal(directory); var (ack, _, _) = SeedEvidence(journal);
        var op = ack.Intent.OperationId; var report = journal.PrepareTargetRegistrationEvidenceReport(op);
        journal.RecordTargetRegistrationEvidenceDeliveryAcknowledgement(op, report, Receipt(op, report));
        Sql(target == "ack"
            ? "DROP TRIGGER target_creation_acknowledgements_no_update; UPDATE target_creation_acknowledgements SET claim='private-evidence-canary'"
            : "DROP TRIGGER target_registration_evidence_no_update; UPDATE target_registration_evidence SET state='OFFLINE'");
        RejectJournal(() => journal.PrepareTargetRegistrationEvidenceReport(op));
        RejectJournal(() => journal.GetTargetRegistrationEvidenceDeliveryAcknowledgement(op));
    }

    [Fact]
    public async Task ForeignUnconfirmedExceptionFromTransportCannotClaimThisCallsLocalPersistence()
    {
        using var journal = new AgentJournal(directory); var (ack, _, _) = SeedEvidence(journal);
        var op = ack.Intent.OperationId;
        Sql($"CREATE TRIGGER test_delivery_failure BEFORE INSERT ON {AckTable} BEGIN SELECT RAISE(IGNORE); END;");
        var original = await Assert.ThrowsAsync<TargetRegistrationEvidenceDeliveryException>(() =>
            Delivery(journal, new FakeTransport(r => Task.FromResult(Receipt(op, r)))).DeliverOperationAsync(op, default));
        Assert.True(original.AcknowledgementMayHaveCompleted); Assert.Equal(0, CountAcknowledgements());
        Sql("DROP TRIGGER test_delivery_failure");
        await RejectDelivery(() => Delivery(journal, new FakeTransport(_ => throw original)).DeliverOperationAsync(op, default),
            "TargetRegistrationEvidenceDeliveryUnavailable");
        Assert.Equal(0, CountAcknowledgements());
    }

    [Theory]
    [InlineData("start")] [InlineData("budget")] [InlineData("backwards")]
    public async Task BrokenOrExpiredClockBeforePersistenceNeverCreatesAcknowledgement(string failure)
    {
        using var journal = new AgentJournal(directory); var (ack, _, _) = SeedEvidence(journal);
        var op = ack.Intent.OperationId; var ticks = 0L;
        var clock = new CallbackClock(() =>
        {
            if (failure == "start") throw new InvalidOperationException(Secret);
            return failure == "backwards" ? --ticks : ticks++ * 120;
        });
        var transport = new FakeTransport(r => Task.FromResult(Receipt(op, r)));
        await RejectDelivery(() => Delivery(journal, transport, clock).DeliverOperationAsync(op, default),
            "TargetRegistrationEvidenceDeliveryUnavailable");
        Assert.Empty(transport.Reports); Assert.Equal(0, CountAcknowledgements());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PostCommitDeadlineOrCancellationIsUnconfirmedButLaterExplicitCallReadsSavedAcknowledgement(bool cancelAfterCommit)
    {
        using var journal = new AgentJournal(directory); var (ack, _, _) = SeedEvidence(journal);
        var op = ack.Intent.OperationId; using var cancel = new CancellationTokenSource();
        var clock = new CallbackClock(() =>
        {
            if (CountAcknowledgements() == 0) return 0;
            if (cancelAfterCommit) cancel.Cancel();
            return cancelAfterCommit ? 0 : 120;
        });
        var transport = new FakeTransport(r => Task.FromResult(Receipt(op, r)));
        await RejectDelivery(() => Delivery(journal, transport, clock).DeliverOperationAsync(op, cancel.Token),
            "TargetRegistrationEvidenceDeliveryAcknowledgementUnconfirmed", true);
        Assert.Equal(1, CountAcknowledgements());
        var unused = new FakeTransport(_ => throw new InvalidOperationException(Secret));
        var result = await Delivery(journal, unused).DeliverOperationAsync(op, default);
        Assert.False(result.DeliveredThisCall); Assert.Empty(unused.Reports);
    }

    [Fact]
    public void MissingEvidenceCannotBeProjectedOrAcknowledged()
    {
        using var journal = new AgentJournal(directory); var ack = Ack(); Seed(journal, ack);
        RejectJournal(() => journal.PrepareTargetRegistrationEvidenceReport(ack.Intent.OperationId));
        Assert.Null(journal.GetTargetRegistrationEvidenceDeliveryAcknowledgement(ack.Intent.OperationId));
    }

    private sealed class CallbackClock(Func<long> timestamp) : TimeProvider
    {
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => timestamp();
    }

    private static TargetRegistrationEvidenceDelivery Delivery(AgentJournal journal, FakeTransport transport, TimeProvider? clock = null)
        => new(journal, transport, new() { Enabled = true, TotalTimeoutSeconds = 30 }, clock);
    private static TargetRegistrationEvidenceDeliveryReceipt Receipt(Guid operation, TargetRegistrationEvidenceReport report)
        => new(report.ClaimId, operation, "Recorded", Issued.AddSeconds(2));
    private static void AssertInert(TargetRegistrationEvidenceDeliveryAcknowledgement ack)
    {
        Assert.False(ack.Executable); Assert.False(ack.OwnershipEstablished); Assert.False(ack.RestoreAllowed);
        Assert.False(ack.RetryAllowed); Assert.False(ack.FreshAuthority);
        Assert.True(ack.RequiresIndependentVerification); Assert.True(ack.ScheduledJobsMustRemainBlocked);
        Assert.False(ack.Receipt.Executable); Assert.False(ack.Receipt.OwnershipEstablished); Assert.False(ack.Receipt.RestoreAllowed);
        Assert.False(ack.Receipt.RetryAllowed); Assert.False(ack.Receipt.FreshAuthority);
        Assert.True(ack.Receipt.RequiresIndependentVerification); Assert.True(ack.Receipt.ScheduledJobsMustRemainBlocked);
    }
    private static void RejectJournal(Action action)
    {
        var error = Assert.Throws<TargetRegistrationEvidenceDeliveryUnavailableException>(action);
        Assert.Equal("TargetRegistrationEvidenceDeliveryUnavailable", error.Code);
        Assert.Equal(error.Code, error.Message); Assert.Equal(error.Code, error.ToString()); Assert.Null(error.InnerException);
    }
    private static async Task RejectDelivery(Func<Task<TargetRegistrationEvidenceDeliveryResult>> action, string code, bool uncertain = false)
    {
        var error = await Assert.ThrowsAsync<TargetRegistrationEvidenceDeliveryException>(action);
        Assert.Equal(code, error.Code); Assert.Equal(code, error.Message); Assert.Equal(code, error.ToString());
        Assert.Equal(uncertain, error.AcknowledgementMayHaveCompleted); Assert.Null(error.InnerException);
        Assert.DoesNotContain(Secret, error.ToString());
    }
    private sealed class FakeTransport(Func<TargetRegistrationEvidenceReport, Task<TargetRegistrationEvidenceDeliveryReceipt>> callback)
        : ITargetRegistrationEvidenceTransport
    {
        public List<TargetRegistrationEvidenceReport> Reports { get; } = [];
        public Task<TargetRegistrationEvidenceDeliveryReceipt> ReportAsync(TargetRegistrationEvidenceReport report, CancellationToken cancellationToken)
        { Reports.Add(report); return callback(report); }
    }
    private SqliteConnection Raw()
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "agent-journal.sqlite")};Pooling=False");
        connection.Open(); return connection;
    }
    private void Sql(string sql)
    {
        using var connection = Raw(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    private long CountAcknowledgements()
    {
        using var connection = Raw(); using var command = connection.CreateCommand(); command.CommandText = $"SELECT COUNT(*) FROM {AckTable}";
        return (long)command.ExecuteScalar()!;
    }
}
