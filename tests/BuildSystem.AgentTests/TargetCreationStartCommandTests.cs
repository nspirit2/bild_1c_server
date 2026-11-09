using BuildSystem.Agent;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class TargetCreationStartCommandTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "creation-start-command-tests", Guid.NewGuid().ToString("N"));
    private const string Canary = "private-token-canary";
    private static TargetCreationIntent Intent()
    {
        var reservation = Guid.NewGuid(); var name = "bs_test_" + reservation.ToString("N");
        return new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 17, reservation, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), name, name);
    }
    private static TargetCreationPermitBinding Binding(TargetCreationIntent i) =>
        new(Guid.NewGuid(), i.OperationId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 9, 12, 5, 0, TimeSpan.Zero))
        { CredentialId = Guid.NewGuid(), EndpointMappingId = Guid.NewGuid() };
    private static void Seed(AgentJournal j, TargetCreationIntent i, TargetCreationPermitBinding b)
    { j.SaveTargetCreationIntent(i); j.SaveTargetCreationPermitBinding(i, b); }
    private sealed class Ports : ITargetCreationPreparedPermitTransport, ITargetCreationPermitStartTransport
    {
        public int Gets, Posts;
        public Func<TargetCreationIntent, TargetCreationPermitBinding, CancellationToken, Task<TargetCreationPreparedPermitReceipt>> Get =
            (i, b, _) => Task.FromResult(new TargetCreationPreparedPermitReceipt(i, b));
        public Func<TargetCreationIntent, TargetCreationPermitBinding, CancellationToken, Task<TargetCreationPermitStartReceipt>> Post =
            (i, b, _) => Task.FromResult(new TargetCreationPermitStartReceipt(i, b, true, b.IssuedAt.AddMinutes(1)));
        public Task<TargetCreationPreparedPermitReceipt> GetPreparedAsync(TargetCreationIntent i, TargetCreationPermitBinding b, CancellationToken cancellationToken = default)
        { Gets++; return Get(i, b, cancellationToken); }
        public Task<TargetCreationPermitStartReceipt> RecordStartAsync(TargetCreationIntent i, TargetCreationPermitBinding b, CancellationToken cancellationToken = default)
        { Posts++; return Post(i, b, cancellationToken); }
    }
    private static TargetCreationStartCommand Command(AgentJournal j, Ports p, bool enabled = true) =>
        new(j, p, p, new TargetCreationStartCommandOptions { Enabled = enabled });
    private static void Output(StringWriter output, string status) => Assert.Equal(
        "{\"status\":\"" + status + "\",\"executable\":false,\"scheduledJobsMustRemainBlocked\":true,\"retryAllowed\":false}" + Environment.NewLine,
        output.ToString());
    private static void Retained(AgentJournal j, TargetCreationIntent i)
    {
        Assert.Throws<InvalidOperationException>(() => j.SaveTargetCreationIntent(i with { OperationId = Guid.NewGuid(), CanonicalClusterId = Guid.NewGuid() }));
        Assert.Throws<InvalidOperationException>(() => j.SaveTargetCreationIntent(i with { OperationId = Guid.NewGuid(), CanonicalSqlServerId = Guid.NewGuid() }));
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    [Fact]
    public void Parser_accepts_only_exact_command_and_nonzero_D_guid_without_extra_arguments()
    {
        var id = Guid.Parse("abcdefab-1234-4567-89ab-abcdefabcdef");
        foreach (var text in new[] { id.ToString("D"), id.ToString("D").ToUpperInvariant() })
        {
            Assert.True(TargetCreationStartCommand.TryParse(["record-target-creation-start", text], out var parsed));
            Assert.Equal(id, parsed); Assert.True(TargetCreationStartCommand.IsStartCommand(["record-target-creation-start", text]));
        }
        string[][] invalid = [[], ["record-target-creation-start"], ["record-target-creation-start", id.ToString("D"), "extra"],
            ["unknown", id.ToString("D")], ["Record-target-creation-start", id.ToString("D")],
            ["record-target-creation-start", " " + id.ToString("D")], ["record-target-creation-start", id.ToString("D") + " "],
            ["record-target-creation-start", id.ToString("N")], ["record-target-creation-start", ""],
            ["record-target-creation-start", Guid.Empty.ToString("D")]];
        foreach (var args in invalid)
        {
            Assert.False(TargetCreationStartCommand.TryParse(args, out var parsed)); Assert.Equal(Guid.Empty, parsed);
            Assert.False(TargetCreationStartCommand.IsStartCommand(args));
        }
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Prepared_read_precedes_durable_begin_and_single_start_metadata_exchange(bool newly)
    {
        var i = Intent(); var b = Binding(i); using var j = new AgentJournal(directory); Seed(j, i, b); var p = new Ports();
        var before = j.GetTargetCreationIntent(i.OperationId);
        p.Get = (actual, binding, _) =>
        {
            Assert.Equal(i, actual); Assert.Equal(b, binding); Assert.Equal(before, j.GetTargetCreationIntent(i.OperationId));
            Assert.Equal(0, p.Posts); return Task.FromResult(new TargetCreationPreparedPermitReceipt(actual, binding));
        };
        p.Post = (actual, binding, _) =>
        {
            Assert.Equal(i, actual); Assert.Equal(b, binding); Assert.Equal(1, p.Gets);
            var record = j.GetTargetCreationIntent(i.OperationId)!;
            Assert.Equal(TargetCreationState.CreationInFlight, record.State); Assert.Equal(2, record.Revision);
            Assert.Equal(b.AttemptId, record.AttemptId); Assert.Equal(1, record.AttemptExpectedRevision);
            using var db = new SqliteConnection($"Data Source={Path.Combine(directory, "agent-journal.sqlite")};Pooling=False");
            db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT state || ':' || revision FROM target_creation_intents";
            Assert.Equal("CreationInFlight:2", cmd.ExecuteScalar());
            return Task.FromResult(new TargetCreationPermitStartReceipt(actual, binding, newly, binding.IssuedAt.AddMinutes(1)));
        };
        using var output = new StringWriter(); Assert.Equal(0, await Command(j, p).RunAsync(b.PermitId, i, b, output));
        Output(output, "StartMetadataRecorded"); Assert.Equal(1, p.Gets); Assert.Equal(1, p.Posts); Retained(j, i);
    }

    [Fact]
    public async Task Default_disabled_wins_over_malformed_cancelled_input_and_disposed_journal()
    {
        var j = new AgentJournal(directory); j.Dispose(); var p = new Ports(); using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.False(new TargetCreationStartCommandOptions().Enabled);
        var command = new TargetCreationStartCommand(j, p, p, new TargetCreationStartCommandOptions());
        using var output = new StringWriter(); Assert.Equal(3, await command.RunAsync(Guid.Empty, null!, null!, output, cts.Token));
        Output(output, "TargetCreationStartDisabled"); Assert.Equal(0, p.Gets); Assert.Equal(0, p.Posts);
    }

    [Fact]
    public async Task Precancelled_call_does_not_read_prepared_or_mutate_journal()
    {
        var i = Intent(); var b = Binding(i); using var j = new AgentJournal(directory); Seed(j, i, b); var p = new Ports();
        var before = j.GetTargetCreationIntent(i.OperationId); using var cts = new CancellationTokenSource(); cts.Cancel();
        using var output = new StringWriter(); Assert.Equal(130, await Command(j, p).RunAsync(b.PermitId, i, b, output, cts.Token));
        Output(output, "TargetCreationStartCancelled"); Assert.Equal(before, j.GetTargetCreationIntent(i.OperationId));
        Assert.Equal(0, p.Gets); Assert.Equal(0, p.Posts);
    }

    [Theory]
    [InlineData("null")] [InlineData("intent")] [InlineData("binding")] [InlineData("unknown")]
    public async Task Untrusted_or_unknown_prepared_result_never_begins_or_posts(string kind)
    {
        var i = Intent(); var b = Binding(i); using var j = new AgentJournal(directory); Seed(j, i, b); var p = new Ports();
        var before = j.GetTargetCreationIntent(i.OperationId);
        p.Get = (_, _, _) => kind == "unknown" ? throw new InvalidOperationException(Canary) : Task.FromResult(kind == "null" ? null! :
            new TargetCreationPreparedPermitReceipt(kind == "intent" ? i with { FencingToken = 18 } : i,
                kind == "binding" ? b with { CredentialId = Guid.NewGuid() } : b));
        using var output = new StringWriter(); Assert.Equal(5, await Command(j, p).RunAsync(b.PermitId, i, b, output));
        Output(output, "TargetCreationStartUnavailable"); Assert.Equal(before, j.GetTargetCreationIntent(i.OperationId));
        Assert.Equal(b, j.GetTargetCreationPermitBinding(i.OperationId)); Assert.Equal(1, p.Gets); Assert.Equal(0, p.Posts);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Existing_inflight_or_quarantine_is_not_reset_before_prepared_read(bool quarantine)
    {
        var i = Intent(); var b = Binding(i); using var j = new AgentJournal(directory); Seed(j, i, b); var p = new Ports();
        var before = j.TryBeginTargetCreation(i.OperationId, 1, b.AttemptId).Record;
        if (quarantine) before = j.QuarantineTargetCreation(i.OperationId, 2, "OutcomeUnknown");
        using var output = new StringWriter(); Assert.Equal(2, await Command(j, p).RunAsync(b.PermitId, i, b, output));
        Output(output, "TargetCreationStartAlreadyAttempted"); Assert.Equal(before, j.GetTargetCreationIntent(i.OperationId));
        Assert.Equal(0, p.Gets); Assert.Equal(0, p.Posts); Retained(j, i);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Unknown_or_cancelled_start_after_begin_quarantines_and_never_retries(bool cancel)
    {
        var i = Intent(); var b = Binding(i); using var j = new AgentJournal(directory); Seed(j, i, b); var p = new Ports();
        using var cts = new CancellationTokenSource(); p.Post = (_, _, _) =>
        { if (cancel) { cts.Cancel(); throw new OperationCanceledException(Canary); } throw new InvalidOperationException(Canary); };
        using var output = new StringWriter(); Assert.Equal(4, await Command(j, p).RunAsync(b.PermitId, i, b, output, cts.Token));
        Output(output, "TargetCreationStartReconciliationRequired"); var record = j.GetTargetCreationIntent(i.OperationId)!;
        Assert.Equal(TargetCreationState.ReconciliationRequired, record.State); Assert.Equal(3, record.Revision);
        Assert.Equal(b.AttemptId, record.AttemptId); Assert.Equal(1, record.AttemptExpectedRevision);
        Assert.Equal(cancel ? "Cancelled" : "OutcomeUnknown", record.QuarantineReason); Retained(j, i);
        using var replay = new StringWriter(); Assert.Equal(2, await Command(j, p).RunAsync(b.PermitId, i, b, replay));
        Output(replay, "TargetCreationStartAlreadyAttempted"); Assert.Equal(1, p.Gets); Assert.Equal(1, p.Posts);
    }

    [Theory]
    [InlineData("permit")] [InlineData("intent")] [InlineData("binding")] [InlineData("missing")]
    public async Task Caller_pins_must_exactly_match_actual_journal_before_any_transport(string kind)
    {
        var i = Intent(); var b = Binding(i); using var j = new AgentJournal(directory); var p = new Ports();
        if (kind != "missing") Seed(j, i, b); var before = j.GetTargetCreationIntent(i.OperationId);
        using var output = new StringWriter(); Assert.Equal(5, await Command(j, p).RunAsync(kind == "permit" ? Guid.NewGuid() : b.PermitId,
            kind == "intent" ? i with { FencingToken = 18 } : i, kind == "binding" ? b with { AttemptId = Guid.NewGuid() } : b, output));
        Output(output, "TargetCreationStartUnavailable"); Assert.Equal(before, j.GetTargetCreationIntent(i.OperationId));
        Assert.Equal(0, p.Gets); Assert.Equal(0, p.Posts);
    }

    private sealed class BrokenWriter : StringWriter
    {
        public int Calls;
        public override Task WriteLineAsync(string? value) { Calls++; throw new IOException(Canary); }
    }
    [Fact]
    public async Task Output_failure_is_not_a_second_output_or_a_second_side_effect()
    {
        var i = Intent(); var b = Binding(i); using var j = new AgentJournal(directory); Seed(j, i, b); var p = new Ports();
        using var output = new BrokenWriter(); Assert.Equal(5, await Command(j, p).RunAsync(b.PermitId, i, b, output));
        Assert.Equal(1, output.Calls); Assert.Equal(1, p.Gets); Assert.Equal(1, p.Posts);
        Assert.Equal(TargetCreationState.CreationInFlight, j.GetTargetCreationIntent(i.OperationId)!.State);
        using var replay = new StringWriter(); Assert.Equal(2, await Command(j, p).RunAsync(b.PermitId, i, b, replay));
        Assert.Equal(1, p.Gets); Assert.Equal(1, p.Posts);
    }
}
