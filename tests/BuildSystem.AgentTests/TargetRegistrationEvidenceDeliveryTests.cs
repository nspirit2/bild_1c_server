using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using BuildSystem.Agent;
using BuildSystem.Agent.Execution;
using BuildSystem.Agent.Tools;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class TargetRegistrationEvidenceDeliveryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "evidence-delivery-" + Guid.NewGuid().ToString("N"));
    private const string Secret = "private-evidence-canary";
    private const string AckTable = "target_registration_evidence_delivery_acknowledgements";
    private static readonly DateTimeOffset Issued = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
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
    private static (TargetCreationAcknowledgement Ack, TargetRegistrationAssessmentScope Scope, DateTime Created)
        SeedEvidence(AgentJournal journal, DateTimeKind kind = DateTimeKind.Unspecified)
    {
        var ack = Ack(); Seed(journal, ack); var scope = Scope(ack); var created = new DateTime(2025, 1, 2, 3, 4, 5, kind);
        journal.RecordTargetRegistrationEvidence(ack.Intent.OperationId, Assessment(scope, created)); return (ack, scope, created);
    }
    private static TargetCreationAcknowledgement Ack()
    {
        var reservation = Guid.NewGuid(); var name = $"bs_test_{reservation:N}";
        var intent = new TargetCreationIntent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 7, reservation,
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), name, name);
        var binding = new TargetCreationPermitBinding(Guid.NewGuid(), intent.OperationId, Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Issued, Issued.AddMinutes(5))
        { CredentialId = Guid.NewGuid(), EndpointMappingId = Guid.NewGuid() };
        return new(intent, binding, Guid.NewGuid(), Guid.NewGuid(), Issued.AddSeconds(1), binding.StartBefore);
    }
    private static void Seed(AgentJournal journal, TargetCreationAcknowledgement ack, bool saveAck = true)
    {
        journal.SaveTargetCreationIntent(ack.Intent);
        journal.SaveTargetCreationPermitBinding(ack.Intent, ack.Binding);
        journal.TryBeginTargetCreation(ack.Intent.OperationId, 1, ack.Binding.AttemptId);
        if (saveAck) journal.QuarantineTargetCreationWithAcknowledgement(ack.Intent.OperationId, 2, ack);
        else journal.QuarantineTargetCreation(ack.Intent.OperationId, 2, "OutcomeUnknown");
    }
    private static TargetRegistrationAssessmentScope Scope(TargetCreationAcknowledgement ack)
    {
        var i = ack.Intent;
        return new(new(i.OperationId, i.FencingToken, i.TargetIncarnationId, i.ReservationId,
            i.CanonicalClusterId, i.CanonicalSqlServerId, Guid.NewGuid(), Guid.NewGuid(),
            ack.Binding.AttemptId, ack.ClaimId, ack.ReportedInfobaseId, Guid.NewGuid(),
            i.InfobaseName, "sql-host\\instance", i.SqlDatabaseName), Guid.NewGuid(), InfobaseRegistrationReadback.ObservedProfileId);
    }
    private static TargetRegistrationAssessmentResult Assessment(TargetRegistrationAssessmentScope expected, DateTime? created = null)
    {
        var s = expected.RegistrationScope;
        var output = $"infobase: {s.InfobaseId:D}\nname: {s.ExpectedInfobaseName}\ndbms: MSSQLServer\n" +
            $"db-server: {s.ExpectedSqlServer}\ndb-name: {s.ExpectedDatabaseName}\nscheduled-jobs-deny: on\n" +
            "db-user: \nsecurity-level: 0\nlicense-distribution: deny\nsessions-deny: on\n" +
            "denied-from: \ndenied-message: \ndenied-parameter: \ndenied-to: \npermission-code: private-evidence-canary\n" +
            "external-session-manager-connection-string: private-evidence-canary\nexternal-session-manager-required: no\n" +
            "security-profile-name: \nsafe-mode-security-profile-name: \nreserve-working-processes: no\ndescr: \n\n";
        var rac = InfobaseRegistrationReadback.Parse(s, 0, false, output, expected.ExpectedProfileId);
        var db = new SqlDatabaseObservedIdentity(s.ExpectedDatabaseName, 12, created ?? new DateTime(2025, 1, 2), "ONLINE");
        var observer = new SqlDatabaseObserver(() => new FakeConnection(db), new()
        { CanonicalSqlServerId = s.CanonicalSqlServerId, EndpointMappingId = s.SqlEndpointMappingId,
            ExpectedMachineName = "test-host", TimeoutSeconds = 10 });
        var sql = observer.ObserveAsync(new(s.OperationId, s.FencingToken, s.TargetIncarnationId,
            s.CanonicalSqlServerId, s.SqlEndpointMappingId, s.ReservationId, expected.SqlObservationId,
            s.ExpectedDatabaseName)).GetAwaiter().GetResult();
        return TargetRegistrationAssessment.Assess(expected, rac, sql);
    }
    private static DataTable Table(params object[][] rows)
    {
        var table = new DataTable();
        for (var i = 0; i < (rows.Length == 0 ? 4 : rows[0].Length); i++) table.Columns.Add("c" + i, typeof(object));
        foreach (var row in rows) table.Rows.Add(row);
        return table;
    }
    private sealed class FakeConnection : DbConnection
    {
        private readonly DataTable identity = Table(["test-host", "MSSQLSERVER", 1]);
        private readonly DataTable presence;
        public FakeConnection(SqlDatabaseObservedIdentity? database) => presence = database is null ? Table() :
            Table([database.Name, database.DatabaseId, database.CreateDate, database.State]);
        public Action? OnPresence { get; init; }
        public Action? OnDispose { get; init; }
        public bool Failure { get; init; }
        public bool WasDisposed { get; private set; }
        public List<FakeCommand> Commands { get; } = [];
        [AllowNull] public override string ConnectionString { get; set; } = Secret;
        public override string Database => "master";
        public override string DataSource => Secret;
        public override string ServerVersion => "1";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() { }
        public override void Open() => throw new NotSupportedException();
        public override Task OpenAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand()
        {
            var command = new FakeCommand(this, Commands.Count == 0); Commands.Add(command); return command;
        }
        public DbDataReader Execute(bool first)
        {
            if (Failure) throw new InvalidOperationException(Secret);
            if (!first) OnPresence?.Invoke();
            return (first ? identity : presence).CreateDataReader();
        }
        public override ValueTask DisposeAsync() { WasDisposed = true; OnDispose?.Invoke(); return ValueTask.CompletedTask; }
    }
    private sealed class FakeCommand(FakeConnection owner, bool first) : DbCommand
    {
        private readonly FakeParameters parameters = new();
        [AllowNull] public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = owner;
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => parameters;
        public override void Cancel() { }
        public override int ExecuteNonQuery() => throw new Xunit.Sdk.XunitException("mutation attempted");
        public override object ExecuteScalar() => throw new NotSupportedException();
        public override void Prepare() => throw new NotSupportedException();
        protected override DbParameter CreateDbParameter() => new FakeParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
            => Task.FromResult(owner.Execute(first));
    }
    private sealed class FakeParameter : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
        public override bool IsNullable { get; set; }
        [AllowNull] public override string ParameterName { get; set; } = "";
        [AllowNull] public override string SourceColumn { get; set; } = "";
        public override object? Value { get; set; }
        public override bool SourceColumnNullMapping { get; set; }
        public override int Size { get; set; }
        public override void ResetDbType() { }
    }
    private sealed class FakeParameters : DbParameterCollection
    {
        private readonly List<DbParameter> values = [];
        public override int Count => values.Count;
        public override object SyncRoot => this;
        public override int Add(object value) { values.Add((DbParameter)value); return values.Count - 1; }
        public override void AddRange(Array items) { foreach (var item in items) Add(item!); }
        public override void Clear() => values.Clear();
        public override bool Contains(object value) => values.Contains((DbParameter)value);
        public override bool Contains(string value) => IndexOf(value) >= 0;
        public override void CopyTo(Array array, int index) => ((ICollection)values).CopyTo(array, index);
        public override IEnumerator GetEnumerator() => values.GetEnumerator();
        public override int IndexOf(object value) => values.IndexOf((DbParameter)value);
        public override int IndexOf(string parameterName) => values.FindIndex(p => p.ParameterName == parameterName);
        public override void Insert(int index, object value) => values.Insert(index, (DbParameter)value);
        public override void Remove(object value) => values.Remove((DbParameter)value);
        public override void RemoveAt(int index) => values.RemoveAt(index);
        public override void RemoveAt(string parameterName) => RemoveAt(IndexOf(parameterName));
        protected override DbParameter GetParameter(int index) => values[index];
        protected override DbParameter GetParameter(string parameterName) => values[IndexOf(parameterName)];
        protected override void SetParameter(int index, DbParameter value) => values[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) => values[IndexOf(parameterName)] = value;
    }
}
