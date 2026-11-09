using BuildSystem.Agent;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class TargetCreationStartCoordinatorTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "creation-start-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Issued = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private const string Canary = "secret-canary";
    private static TargetCreationIntent Intent()
    {
        var reservation = Guid.NewGuid(); var name = "bs_test_" + reservation.ToString("N");
        return new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 17, reservation, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), name, name);
    }
    private static TargetCreationPermitBinding Binding(TargetCreationIntent i) =>
        new(Guid.NewGuid(), i.OperationId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Issued, Issued.AddMinutes(5))
        { CredentialId = Guid.NewGuid(), EndpointMappingId = Guid.NewGuid() };
    private static void Seed(AgentJournal j, TargetCreationIntent i, TargetCreationPermitBinding b)
    { j.SaveTargetCreationIntent(i); j.SaveTargetCreationPermitBinding(i, b); }
    private static TargetCreationStartCoordinator Coordinator(AgentJournal j, Port port, bool enabled = true) =>
        new(j, port, new TargetCreationStartCoordinatorOptions { Enabled = enabled });
    private sealed class Port : ITargetCreationPermitStartTransport
    {
        public int Calls;
        public Func<TargetCreationIntent, TargetCreationPermitBinding, CancellationToken, Task<TargetCreationPermitStartReceipt>> Send =
            (i, b, _) => Task.FromResult(new TargetCreationPermitStartReceipt(i, b, true, Issued.AddMinutes(1)));
        public Task<TargetCreationPermitStartReceipt> RecordStartAsync(TargetCreationIntent i, TargetCreationPermitBinding b, CancellationToken cancellationToken = default)
        { Calls++; return Send(i, b, cancellationToken); }
    }
    private static void Inert(TargetCreationStartCoordinationReceipt r)
    {
        Assert.False(r.Executable); Assert.False(r.FreshAuthority); Assert.False(r.RetryAllowed);
        Assert.True(r.ScheduledJobsMustRemainBlocked);
    }
    private static async Task FixedFailure(Func<Task> action)
    {
        var e = await Assert.ThrowsAsync<TargetCreationStartCoordinationException>(action);
        Assert.Contains(e.Message, new[] { "TargetCreationStartUnavailable", "TargetCreationStartReconciliationUnconfirmed" });
        Assert.Equal(e.Message, e.ToString()); Assert.Null(e.InnerException); Assert.DoesNotContain(Canary, e.ToString());
    }
    private static void SlotsRetained(AgentJournal j, TargetCreationIntent i)
    {
        Assert.Throws<InvalidOperationException>(() => j.SaveTargetCreationIntent(i with { OperationId = Guid.NewGuid(), CanonicalClusterId = Guid.NewGuid() }));
        Assert.Throws<InvalidOperationException>(() => j.SaveTargetCreationIntent(i with { OperationId = Guid.NewGuid(), CanonicalSqlServerId = Guid.NewGuid() }));
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Start_begins_durably_before_one_metadata_exchange_and_never_grants_authority(bool newlyRecorded)
    {
        var i = Intent(); var b = Binding(i); using var j = new AgentJournal(directory); Seed(j, i, b);
        var port = new Port { Send = (actual, binding, _) =>
        {
            Assert.Equal(i, actual); Assert.Equal(b, binding);
            var durable = j.GetTargetCreationIntent(i.OperationId)!;
            Assert.Equal(TargetCreationState.CreationInFlight, durable.State); Assert.Equal(2, durable.Revision);
            Assert.Equal(b.AttemptId, durable.AttemptId); Assert.Equal(1, durable.AttemptExpectedRevision);
            using var db = new SqliteConnection($"Data Source={Path.Combine(directory, "agent-journal.sqlite")};Pooling=False");
            db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT state || ':' || revision FROM target_creation_intents";
            Assert.Equal("CreationInFlight:2", cmd.ExecuteScalar());
            return Task.FromResult(new TargetCreationPermitStartReceipt(actual, binding, newlyRecorded, Issued.AddMinutes(1)));
        } };
        var r = await Coordinator(j, port).RecordStartAsync(i, b);
        Assert.Equal("StartMetadataRecorded", r.Outcome.ToString()); Assert.Equal(b, r.Binding);
        Assert.Equal(j.GetTargetCreationIntent(i.OperationId), r.Record); Assert.Equal(newlyRecorded, r.StartReceipt!.NewlyRecorded);
        Assert.Equal(1, port.Calls); Inert(r); SlotsRetained(j, i);
        var replay = await Coordinator(j, port).RecordStartAsync(i, b);
        Assert.Equal("AlreadyAttempted", replay.Outcome.ToString()); Assert.Equal(r.Record, replay.Record); Assert.Equal(1, port.Calls); Inert(replay);
    }

    [Fact]
    public async Task Default_off_precedes_cancelled_token_and_all_journal_access()
    {
        var i = Intent(); var b = Binding(i); var j = new AgentJournal(directory); j.Dispose(); var port = new Port();
        Assert.False(new TargetCreationStartCoordinatorOptions().Enabled);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await FixedFailure(() => Coordinator(j, port, false).RecordStartAsync(i, b, cts.Token)); Assert.Equal(0, port.Calls);
    }

    [Fact]
    public async Task Precancelled_call_does_not_begin_or_contact_transport()
    {
        var i = Intent(); var b = Binding(i); using var j = new AgentJournal(directory); Seed(j, i, b);
        var before = j.GetTargetCreationIntent(i.OperationId); var port = new Port(); using var cts = new CancellationTokenSource(); cts.Cancel();
        var e = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Coordinator(j, port).RecordStartAsync(i, b, cts.Token));
        Assert.Null(e.InnerException); Assert.DoesNotContain(Canary, e.ToString());
        Assert.Equal(before, j.GetTargetCreationIntent(i.OperationId)); Assert.Equal(0, port.Calls);
    }

    [Theory]
    [InlineData("missing-intent")] [InlineData("missing-binding")] [InlineData("binding")]
    [InlineData("payload")] [InlineData("invalid")] [InlineData("legacy-quarantine")] [InlineData("credential-missing")]
    public async Task Invalid_or_unbound_input_fails_before_mutation_and_transport(string kind)
    {
        var i = Intent(); var b = Binding(i); using var j = new AgentJournal(directory);
        if (kind != "missing-intent") j.SaveTargetCreationIntent(i);
        if (kind is not ("missing-intent" or "missing-binding" or "legacy-quarantine")) j.SaveTargetCreationPermitBinding(i, b);
        if (kind == "legacy-quarantine") j.QuarantineTargetCreation(i.OperationId, 1, "TargetConflict");
        var before = j.GetTargetCreationIntent(i.OperationId); var originalBinding = j.GetTargetCreationPermitBinding(i.OperationId);
        var input = kind == "payload" ? i with { FencingToken = 18 } : kind == "invalid" ? i with { SqlDatabaseName = Canary } : i;
        var binding = kind == "binding" ? b with { AttemptId = Guid.NewGuid() } : kind == "credential-missing" ? b with { CredentialId = null } : b; var port = new Port();
        await FixedFailure(() => Coordinator(j, port).RecordStartAsync(input, binding));
        Assert.Equal(before, j.GetTargetCreationIntent(i.OperationId)); Assert.Equal(originalBinding, j.GetTargetCreationPermitBinding(i.OperationId)); Assert.Equal(0, port.Calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Existing_attempt_or_reconciliation_is_never_reset_or_resent(bool quarantine)
    {
        var i = Intent(); var b = Binding(i); using var j = new AgentJournal(directory); Seed(j, i, b);
        var before = j.TryBeginTargetCreation(i.OperationId, 1, b.AttemptId).Record;
        if (quarantine) before = j.QuarantineTargetCreation(i.OperationId, 2, "TargetConflict");
        var port = new Port(); var r = await Coordinator(j, port).RecordStartAsync(i, b);
        Assert.Equal("AlreadyAttempted", r.Outcome.ToString()); Assert.Equal(before, r.Record); Assert.Equal(before, j.GetTargetCreationIntent(i.OperationId));
        Assert.Equal(0, port.Calls); Assert.Null(r.StartReceipt); Inert(r); SlotsRetained(j, i);
    }

    [Theory]
    [InlineData("error")] [InlineData("caller-cancel")] [InlineData("unrelated-cancel")]
    public async Task Any_postbegin_failure_is_durably_quarantined_without_retry(string kind)
    {
        var i = Intent(); var b = Binding(i); using var j = new AgentJournal(directory); Seed(j, i, b); using var cts = new CancellationTokenSource();
        var port = new Port { Send = (_, _, _) =>
        {
            if (kind == "caller-cancel") cts.Cancel();
            throw kind == "error" ? new InvalidOperationException(Canary) : new OperationCanceledException(Canary);
        } };
        var r = await Coordinator(j, port).RecordStartAsync(i, b, cts.Token);
        Assert.Equal("ReconciliationRequired", r.Outcome.ToString()); Assert.Equal(TargetCreationState.ReconciliationRequired, r.Record.State);
        Assert.Equal(kind == "caller-cancel" ? "Cancelled" : "OutcomeUnknown", r.Record.QuarantineReason);
        Assert.Equal(3, r.Record.Revision); Assert.Equal(b.AttemptId, r.Record.AttemptId); Assert.Equal(1, r.Record.AttemptExpectedRevision);
        Assert.Equal(r.Record, j.GetTargetCreationIntent(i.OperationId)); Assert.Equal(b, j.GetTargetCreationPermitBinding(i.OperationId));
        Assert.Equal(1, port.Calls); Assert.Null(r.StartReceipt); Inert(r); SlotsRetained(j, i);
        Assert.Equal("AlreadyAttempted", (await Coordinator(j, port).RecordStartAsync(i, b)).Outcome.ToString()); Assert.Equal(1, port.Calls);
    }

    [Theory]
    [InlineData("credential")] [InlineData("mapping")] [InlineData("intent")] [InlineData("fence")] [InlineData("time")] [InlineData("null")]
    public async Task Untrusted_transport_receipt_mismatch_quarantines_instead_of_reporting_success(string kind)
    {
        var i = Intent(); var b = Binding(i); using var j = new AgentJournal(directory); Seed(j, i, b);
        var port = new Port { Send = (_, _, _) => Task.FromResult(kind == "null" ? null! : new TargetCreationPermitStartReceipt(
            kind == "intent" ? i with { EnvironmentId = Guid.NewGuid() } : kind == "fence" ? i with { FencingToken = 18 } : i,
            kind == "credential" ? b with { CredentialId = Guid.NewGuid() } : kind == "mapping" ? b with { EndpointMappingId = Guid.NewGuid() } : b,
            true, kind == "time" ? b.StartBefore.AddTicks(1) : Issued.AddMinutes(1))) };
        var r = await Coordinator(j, port).RecordStartAsync(i, b);
        Assert.Equal("ReconciliationRequired", r.Outcome.ToString()); Assert.Equal("OutcomeUnknown", r.Record.QuarantineReason);
        Assert.Equal(r.Record, j.GetTargetCreationIntent(i.OperationId)); Assert.Equal(b.AttemptId, r.Record.AttemptId);
        Assert.Equal(1, port.Calls); Assert.Null(r.StartReceipt); Inert(r); SlotsRetained(j, i);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Durable_write_failure_never_reports_unconfirmed_progress(bool quarantineFailure)
    {
        var i = Intent(); var b = Binding(i); using var j = new AgentJournal(directory); Seed(j, i, b);
        using var db = new SqliteConnection($"Data Source={Path.Combine(directory, "agent-journal.sqlite")};Pooling=False"); db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"CREATE TRIGGER fail_transition BEFORE UPDATE ON target_creation_intents WHEN NEW.state='{(quarantineFailure ? "ReconciliationRequired" : "CreationInFlight")}' BEGIN SELECT RAISE(ABORT, '{Canary}'); END";
        cmd.ExecuteNonQuery(); var port = new Port { Send = (_, _, _) => throw new InvalidOperationException(Canary) };
        var error = await Assert.ThrowsAsync<TargetCreationStartCoordinationException>(() => Coordinator(j, port).RecordStartAsync(i, b));
        Assert.Equal(quarantineFailure ? "TargetCreationStartReconciliationUnconfirmed" : "TargetCreationStartUnavailable", error.Code);
        Assert.Null(error.InnerException); Assert.DoesNotContain(Canary, error.ToString());
        var record = j.GetTargetCreationIntent(i.OperationId)!;
        Assert.Equal(quarantineFailure ? TargetCreationState.CreationInFlight : TargetCreationState.IntentRecorded, record.State);
        Assert.Equal(quarantineFailure ? 2 : 1, record.Revision); Assert.Equal(quarantineFailure ? 1 : 0, port.Calls);
        Assert.Equal(b, j.GetTargetCreationPermitBinding(i.OperationId)); SlotsRetained(j, i);
    }

    [Fact]
    public async Task One_coordinator_serializes_concurrent_calls_into_one_transport_exchange()
    {
        var i = Intent(); var b = Binding(i); using var j = new AgentJournal(directory); Seed(j, i, b);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var port = new Port { Send = async (_, _, _) => { entered.SetResult(); await release.Task; return new(i, b, true, Issued.AddMinutes(1)); } };
        var coordinator = Coordinator(j, port); var first = coordinator.RecordStartAsync(i, b);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var second = coordinator.RecordStartAsync(i, b); release.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, port.Calls); Assert.Equal(new[] { "StartMetadataRecorded", "AlreadyAttempted" }, results.Select(r => r.Outcome.ToString()));
        foreach (var r in results) Inert(r);
    }
}