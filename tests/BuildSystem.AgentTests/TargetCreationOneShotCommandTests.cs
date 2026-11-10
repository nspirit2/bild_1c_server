using System.Text;
using System.Text.Json;
using BuildSystem.Agent;
using BuildSystem.Agent.Tools;
using Xunit;

namespace BuildSystem.AgentTests;

// Concrete coordinator, local journal and rejecting fake ports only: no process or network.
public sealed class TargetCreationOneShotCommandTests
{
    private const string Canary = "private-command-canary";
    private sealed class Fixture : IDisposable, ITargetCreationPreparedPermitTransport, ITargetCreationPermitStartTransport,
        ITargetCreationCredentialTransport, ITargetCreationNamespacePort, ITargetCreationAbsencePort,
        ITargetCreationExecutionClaimTransport, ITargetCreationSingleInvocation
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "one-shot-command-tests", Guid.NewGuid().ToString("N"));
        public readonly AgentJournal Journal;
        public readonly TargetCreationIntent Intent;
        public readonly TargetCreationPermitBinding Binding;
        public readonly List<string> Calls = [];
        public Func<TargetCreationPreparedPermitReceipt>? Prepared;
        public Action? BeforeCredentialFailure;
        public Fixture()
        {
            var r = Guid.NewGuid(); var name = "bs_test_" + r.ToString("N");
            Intent = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 17, r, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), name, name);
            var issued = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
            Binding = new(Guid.NewGuid(), Intent.OperationId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), issued, issued.AddMinutes(5))
                { CredentialId = Guid.NewGuid(), EndpointMappingId = Guid.NewGuid() };
            Journal = new(directory); Journal.SaveTargetCreationIntent(Intent); Journal.SaveTargetCreationPermitBinding(Intent, Binding);
        }
        public TargetCreationOneShotCommand Command() => new(Journal, this,
            new TargetCreationOneShotCoordinator(Journal, new(Journal, this, this, new() { Enabled = true }), this, this, this, this,
                new() { Enabled = true, TrustedAgentId = Binding.AgentId, CanonicalClusterId = Intent.CanonicalClusterId,
                    CanonicalSqlServerId = Intent.CanonicalSqlServerId, EndpointMappingId = Binding.EndpointMappingId!.Value,
                    RacObservationMappingId = Guid.NewGuid(), SqlObservationMappingId = Guid.NewGuid() }), new() { Enabled = true });
        public Task<TargetCreationPreparedPermitReceipt> GetPreparedAsync(TargetCreationIntent i, TargetCreationPermitBinding b, CancellationToken cancellationToken = default)
        {
            Calls.Add("get"); Assert.Equal(Intent, i); Assert.Equal(Binding, b);
            var local = Journal.GetTargetCreationIntent(i.OperationId)!;
            Assert.Equal(1, local.Revision); Assert.Equal(TargetCreationState.IntentRecorded, local.State);
            Assert.Equal(i, local.Intent); Assert.Equal(b, Journal.GetTargetCreationPermitBinding(i.OperationId));
            return Task.FromResult(Prepared is null ? new TargetCreationPreparedPermitReceipt(i, b) : Prepared());
        }
        public Task<TargetCreationPermitStartReceipt> RecordStartAsync(TargetCreationIntent i, TargetCreationPermitBinding b, CancellationToken cancellationToken = default)
        {
            Calls.Add("start"); Assert.Equal(Intent, i); Assert.Equal(Binding, b);
            Assert.Equal(2, Journal.GetTargetCreationIntent(i.OperationId)!.Revision);
            return Task.FromResult(new TargetCreationPermitStartReceipt(i, b, true, b.IssuedAt.AddSeconds(1)));
        }
        public Task<TargetCreationDeliveredCredentials> ResolveAsync(TargetCreationCredentialRequest request, CancellationToken cancellationToken = default)
        { Calls.Add("credentials"); BeforeCredentialFailure?.Invoke(); throw new InvalidOperationException(Canary); }
        public Task<ITargetCreationNamespaceHold> AcquireAsync(SqlTargetNamespaceScope scope, CancellationToken cancellationToken = default)
        { Calls.Add("namespace"); throw new InvalidOperationException(Canary); }
        public Task<TargetPresenceAssessmentResult> ObserveAsync(TargetPresenceAssessmentScope scope, TargetCreationDeliveredSecretValues secrets, CancellationToken cancellationToken = default)
        { Calls.Add("observe"); throw new InvalidOperationException(Canary); }
        public Task<TargetCreationExecutionClaimGrant> ClaimAsync(TargetCreationIntent i, TargetCreationPermitBinding b, CancellationToken cancellationToken = default)
        { Calls.Add("claim"); throw new InvalidOperationException(Canary); }
        public Task<RacInfobaseCreationOutcome> InvokeAsync(RacInfobaseCreationScope scope, TargetCreationDeliveredSecretValues secrets, ProcessLaunchDeadline deadline, CancellationToken cancellationToken = default)
        { Calls.Add("invoke"); throw new InvalidOperationException(Canary); }
        public void Dispose() { Journal.Dispose(); Directory.Delete(directory, true); }
    }
    private sealed class RejectingWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public int Attempts;
        public override Task WriteLineAsync(string? value) { Attempts++; throw new IOException(Canary); }
    }
    private static void Output(StringWriter writer, string status)
    {
        var text = writer.ToString(); Assert.EndsWith(Environment.NewLine, text); Assert.DoesNotContain(Canary, text);
        using var json = JsonDocument.Parse(text); var root = json.RootElement;
        Assert.Equal(new[] { "creationAcknowledged", "executable", "freshAuthority", "physicalInvocationAttempted", "retryAllowed", "scheduledJobsMustRemainBlocked", "status" },
            root.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(status, root.GetProperty("status").GetString());
        foreach (var name in new[] { "executable", "freshAuthority", "retryAllowed", "physicalInvocationAttempted", "creationAcknowledged" })
            Assert.False(root.GetProperty(name).GetBoolean());
        Assert.True(root.GetProperty("scheduledJobsMustRemainBlocked").GetBoolean());
    }

    [Fact]
    public void Parser_requires_exact_command_nonzero_D_guid_and_two_arguments()
    {
        var id = Guid.Parse("abcdefab-1234-4567-89ab-abcdefabcdef");
        foreach (var text in new[] { id.ToString("D"), id.ToString("D").ToUpperInvariant() })
        {
            string[] args = ["run-target-creation-once", text];
            Assert.True(TargetCreationOneShotCommand.TryParse(args, out var parsed)); Assert.Equal(id, parsed);
            Assert.True(TargetCreationOneShotCommand.SupportedArguments(args)); Assert.True(TargetCreationOneShotCommand.IsOneShotCommand(args));
        }
        string[]?[] invalid = [null, [], ["run-target-creation-once"], ["run-target-creation-once", id.ToString("D"), "extra"],
            ["record-target-creation-start", id.ToString("D")], ["Run-target-creation-once", id.ToString("D")],
            ["run-target-creation-once", " " + id.ToString("D")], ["run-target-creation-once", id.ToString("D") + " "],
            ["run-target-creation-once", id.ToString("N")], ["run-target-creation-once", id.ToString("B")],
            ["run-target-creation-once", ""], ["run-target-creation-once", Guid.Empty.ToString("D")],
            [null!, id.ToString("D")], ["run-target-creation-once", null!]];
        foreach (var args in invalid)
        {
            Assert.False(TargetCreationOneShotCommand.TryParse(args!, out var parsed)); Assert.Equal(Guid.Empty, parsed);
            Assert.False(TargetCreationOneShotCommand.SupportedArguments(args!)); Assert.False(TargetCreationOneShotCommand.IsOneShotCommand(args!));
        }
    }

    [Fact]
    public async Task Default_off_wins_over_null_dependencies_bad_arguments_and_cancellation()
    {
        Assert.False(new TargetCreationOneShotCommandOptions().Enabled);
        using var cts = new CancellationTokenSource(); cts.Cancel(); using var output = new StringWriter();
        var command = new TargetCreationOneShotCommand(null, null, null, new());
        Assert.Equal(3, await command.RunAsync(Guid.Empty, null!, null!, output, cts.Token));
        Output(output, "TargetCreationOneShotDisabled");
    }

    [Theory]
    [InlineData("permit")] [InlineData("intent")] [InlineData("binding")] [InlineData("missing")]
    public async Task Exact_persisted_pins_are_required_before_prepared_read(string kind)
    {
        using var f = new Fixture(); using var output = new StringWriter(); var before = f.Journal.GetTargetCreationIntent(f.Intent.OperationId);
        var i = kind == "intent" ? f.Intent with { FencingToken = 18 } : kind == "missing" ? f.Intent with { OperationId = Guid.NewGuid() } : f.Intent;
        var b = kind == "binding" ? f.Binding with { CredentialId = Guid.NewGuid() } : kind == "missing" ? f.Binding with { OperationId = i.OperationId } : f.Binding;
        Assert.Equal(5, await f.Command().RunAsync(kind == "permit" ? Guid.NewGuid() : b.PermitId, i, b, output));
        Output(output, "TargetCreationOneShotUnavailable"); Assert.Empty(f.Calls); Assert.Equal(before, f.Journal.GetTargetCreationIntent(f.Intent.OperationId));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Existing_attempt_never_gets_prepared_or_enters_coordinator(bool quarantine)
    {
        using var f = new Fixture(); var before = f.Journal.TryBeginTargetCreation(f.Intent.OperationId, 1, f.Binding.AttemptId).Record;
        if (quarantine) before = f.Journal.QuarantineTargetCreation(f.Intent.OperationId, 2, "OutcomeUnknown");
        using var output = new StringWriter(); Assert.Equal(2, await f.Command().RunAsync(f.Binding.PermitId, f.Intent, f.Binding, output));
        Output(output, "TargetCreationOneShotAlreadyAttempted"); Assert.Empty(f.Calls); Assert.Equal(before, f.Journal.GetTargetCreationIntent(f.Intent.OperationId));
    }

    [Theory]
    [InlineData("null")] [InlineData("intent")] [InlineData("binding")] [InlineData("exception")]
    public async Task Invalid_prepared_metadata_never_begins_or_resolves_credentials(string kind)
    {
        using var f = new Fixture(); var before = f.Journal.GetTargetCreationIntent(f.Intent.OperationId);
        f.Prepared = () => kind == "exception" ? throw new InvalidOperationException(Canary) : kind == "null" ? null! :
            new(kind == "intent" ? f.Intent with { FencingToken = 18 } : f.Intent,
                kind == "binding" ? f.Binding with { CredentialId = Guid.NewGuid() } : f.Binding);
        using var output = new StringWriter(); Assert.Equal(5, await f.Command().RunAsync(f.Binding.PermitId, f.Intent, f.Binding, output));
        Output(output, "TargetCreationOneShotUnavailable"); Assert.Equal(new[] { "get" }, f.Calls);
        Assert.Equal(before, f.Journal.GetTargetCreationIntent(f.Intent.OperationId)); Assert.Equal(f.Binding, f.Journal.GetTargetCreationPermitBinding(f.Intent.OperationId));
    }

    [Fact]
    public async Task Prebegin_cancellation_has_no_io_or_mutation()
    {
        using var f = new Fixture(); using var cts = new CancellationTokenSource(); cts.Cancel(); using var output = new StringWriter();
        Assert.Equal(130, await f.Command().RunAsync(f.Binding.PermitId, f.Intent, f.Binding, output, cts.Token));
        Output(output, "TargetCreationOneShotCancelled"); Assert.Empty(f.Calls); Assert.Equal(1, f.Journal.GetTargetCreationIntent(f.Intent.OperationId)!.Revision);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Preparation_failure_is_reconciliation_only_and_replay_never_repeats_io(bool unconfirmed)
    {
        using var f = new Fixture();
        if (unconfirmed) f.BeforeCredentialFailure = () => f.Journal.QuarantineTargetCreation(f.Intent.OperationId, 2, "OutcomeUnknown");
        using var output = new StringWriter(); Assert.Equal(unconfirmed ? 6 : 4, await f.Command().RunAsync(f.Binding.PermitId, f.Intent, f.Binding, output));
        Output(output, unconfirmed ? "TargetCreationOneShotReconciliationUnconfirmed" : "TargetCreationOneShotReconciliationRequired");
        Assert.Equal(new[] { "get", "start", "credentials" }, f.Calls);
        var record = f.Journal.GetTargetCreationIntent(f.Intent.OperationId)!;
        Assert.Equal(3, record.Revision); Assert.Equal(TargetCreationState.ReconciliationRequired, record.State);
        Assert.Equal(f.Binding.AttemptId, record.AttemptId); Assert.Equal(f.Binding, f.Journal.GetTargetCreationPermitBinding(f.Intent.OperationId));
        using var replay = new StringWriter(); Assert.Equal(2, await f.Command().RunAsync(f.Binding.PermitId, f.Intent, f.Binding, replay));
        Output(replay, "TargetCreationOneShotAlreadyAttempted"); Assert.Equal(new[] { "get", "start", "credentials" }, f.Calls);
        Assert.Equal(record, f.Journal.GetTargetCreationIntent(f.Intent.OperationId));
    }

    [Fact]
    public async Task Output_failure_after_quarantine_is_single_attempt_and_never_retries_operations()
    {
        using var f = new Fixture(); using var output = new RejectingWriter();
        Assert.Equal(5, await f.Command().RunAsync(f.Binding.PermitId, f.Intent, f.Binding, output)); Assert.Equal(1, output.Attempts);
        Assert.Equal(new[] { "get", "start", "credentials" }, f.Calls);
        Assert.Equal(3, f.Journal.GetTargetCreationIntent(f.Intent.OperationId)!.Revision);
        Assert.Equal(TargetCreationState.ReconciliationRequired, f.Journal.GetTargetCreationIntent(f.Intent.OperationId)!.State);
    }
}
