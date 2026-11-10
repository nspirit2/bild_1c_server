using BuildSystem.Agent;
using BuildSystem.Agent.Tools;
using Xunit;

namespace BuildSystem.AgentTests;

// Trusted composition with fake runners only; enablement and a deadline are not authority.
public sealed class TargetCreationDeadlineInvocationAdapterTests
{
    private static readonly Guid Cluster = Guid.NewGuid(), Server = Guid.NewGuid(), Mapping = Guid.NewGuid();
    private const string Canary = "deadline-secret-canary";
    private static readonly TargetCreationDeliveredSecretValues Secrets = new(Canary, Canary, Canary, Canary);
    private sealed class Clock : TimeProvider
    {
        public long Ticks;
        public bool ThrowOnRead;
        public int Reads;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { Reads++; return ThrowOnRead ? throw new ObjectDisposedException(Canary) : Ticks; }
    }
    private sealed class Runner(Func<ProcessCommand, Func<ProcessOutput, Task>?, CancellationToken, Task<ProcessResult>>? run = null) : IProcessRunner
    {
        public int Calls;
        public ProcessCommand? Command;
        public Task<ProcessResult> RunAsync(ProcessCommand command, Func<ProcessOutput, Task>? onOutput = null, CancellationToken cancellationToken = default)
        { Calls++; Command = command; return run is null ? Task.FromResult(new ProcessResult(123, 0, ProcessCompletion.Exited, false)) : run(command, onOutput, cancellationToken); }
    }
    private static RacInfobaseCreationScope Scope() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Cluster, Server, Mapping);
    private static RacInfobaseCreationRecipe Recipe() => new(new()
    {
        TrustedRacToolId = "trusted-rac-deadline", Endpoint = "trusted-ras.example:1545", EndpointMappingId = Mapping,
        CanonicalClusterId = Cluster, RacClusterId = Guid.NewGuid(), CanonicalSqlServerId = Server,
        SqlServer = "trusted-sql.example\\TEST", WorkingDirectory = Path.GetFullPath(Path.GetTempPath()),
        Timeout = TimeSpan.FromSeconds(30), Locale = "ru_RU", Enabled = true, ExclusiveNamespaceConfirmed = true
    });
    private static ProcessLaunchDeadline Guard(Clock clock) => new(clock, 0, TimeSpan.FromSeconds(10));
    private static TargetCreationDeadlineInvocationAdapter Adapter(Runner runner, bool enabled = true) =>
        new(Recipe(), new RacInfobaseCreationInvocation(runner, new() { Enabled = true }), new() { Enabled = enabled });
    private static void Fixed(TargetCreationDeadlineInvocationException error)
    {
        Assert.Equal("TargetCreationDeadlineInvocationUnavailable", error.Message);
        Assert.Null(error.InnerException); Assert.DoesNotContain(Canary, error.ToString());
    }
    private static void Unknown(RacInfobaseCreationOutcome outcome)
    {
        Assert.Equal(RacInfobaseCreationOutcomeKind.ReconciliationRequired, outcome.Kind);
        Assert.False(outcome.Executable); Assert.False(outcome.RetryAllowed); Assert.True(outcome.ScheduledJobsMustRemainBlocked);
        Assert.Null(outcome.ReportedInfobaseId); Assert.DoesNotContain(Canary, outcome.ToString());
    }

    [Fact]
    public async Task Default_off_precedes_null_arguments_and_precancelled_token()
    {
        var runner = new Runner(); var options = new TargetCreationDeadlineInvocationAdapterOptions(); Assert.False(options.Enabled);
        var adapter = new TargetCreationDeadlineInvocationAdapter(Recipe(), new(runner, new() { Enabled = true }), options);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Fixed(await Assert.ThrowsAsync<TargetCreationDeadlineInvocationException>(() => adapter.InvokeAsync(null!, null!, null!, cancellation.Token)));
        var clock = new Clock(); var deadline = Guard(clock); clock.ThrowOnRead = true;
        Fixed(await Assert.ThrowsAsync<TargetCreationDeadlineInvocationException>(() => adapter.InvokeAsync(Scope(), Secrets, deadline, cancellation.Token)));
        Assert.Equal(0, runner.Calls); Assert.Equal(0, clock.Reads);
    }

    [Fact]
    public async Task Enabled_composition_preserves_guard_and_factory_command_once()
    {
        var clock = new Clock { Ticks = TimeSpan.FromSeconds(9).Ticks }; var deadline = Guard(clock); var scope = Scope();
        using var cancellation = new CancellationTokenSource();
        var runner = new Runner(async (command, output, ct) =>
        {
            Assert.Same(deadline, command.LaunchDeadline); command.LaunchDeadline!.DemandNotExpired(); Assert.Equal(cancellation.Token, ct);
            var args = command.Arguments.Select(a => (a.Value, a.Sensitive)).ToArray();
            Assert.Contains(("--locale=ru_RU", false), args); Assert.Contains(("--scheduled-jobs-deny=on", false), args);
            Assert.Contains(("--create-database", false), args); Assert.Equal(4, args.Count(a => a.Sensitive));
            Assert.All(args.Where(a => a.Sensitive), a => Assert.Contains(Canary, a.Value));
            await output!(new(ProcessOutputStream.StandardOutput, $"infobase: {Guid.NewGuid():D}\n"));
            return new(123, 0, ProcessCompletion.Exited, false);
        });
        var outcome = await Adapter(runner).InvokeAsync(scope, Secrets, deadline, cancellation.Token);
        Assert.Equal(1, runner.Calls); Assert.Same(scope, outcome.Scope);
        Assert.Equal(RacInfobaseCreationOutcomeKind.AcknowledgementObserved, outcome.Kind);
        Assert.False(outcome.Executable); Assert.False(outcome.RetryAllowed); Assert.True(outcome.ScheduledJobsMustRemainBlocked);
    }

    [Fact]
    public async Task Setup_delay_does_not_renew_original_guard_at_fake_physical_boundary()
    {
        var clock = new Clock { Ticks = TimeSpan.FromSeconds(9).Ticks }; var deadline = Guard(clock); var physicalStarts = 0;
        var runner = new Runner((command, _, _) =>
        {
            Assert.Same(deadline, command.LaunchDeadline); clock.Ticks = TimeSpan.FromSeconds(10).Ticks;
            command.LaunchDeadline!.DemandNotExpired(); physicalStarts++;
            return Task.FromResult(new ProcessResult(123, 0, ProcessCompletion.Exited, false));
        });
        Unknown(await Adapter(runner).InvokeAsync(Scope(), Secrets, deadline, default));
        Assert.Equal(1, runner.Calls); Assert.Equal(0, physicalStarts); Assert.NotNull(runner.Command);
        Assert.Same(deadline, runner.Command.LaunchDeadline);
        Assert.Throws<ProcessRunnerException>(() => deadline.DemandNotExpired());
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Null_or_already_expired_guard_has_fixed_error_without_runner(bool expired)
    {
        var runner = new Runner(); var clock = new Clock { Ticks = TimeSpan.FromSeconds(10).Ticks };
        Fixed(await Assert.ThrowsAsync<TargetCreationDeadlineInvocationException>(() =>
            Adapter(runner).InvokeAsync(Scope(), Secrets, expired ? Guard(clock) : null!, default)));
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task Real_factory_scope_failure_has_fixed_error_without_argument_or_secret_echo()
    {
        var runner = new Runner(); var scope = Scope() with { CanonicalClusterId = Guid.NewGuid() };
        Fixed(await Assert.ThrowsAsync<TargetCreationDeadlineInvocationException>(() =>
            Adapter(runner).InvokeAsync(scope, Secrets, Guard(new Clock()), default)));
        Assert.Equal(0, runner.Calls); Assert.Null(runner.Command);
    }

    [Fact]
    public async Task Runner_exception_is_unknown_and_does_not_leak_plaintext()
    {
        var runner = new Runner((_, _, _) => throw new InvalidOperationException(Canary));
        Unknown(await Adapter(runner).InvokeAsync(Scope(), Secrets, Guard(new Clock()), default));
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task Existing_RunAsync_path_still_has_no_deadline()
    {
        var runner = new Runner(); var scope = Scope();
        var recipe = Recipe().Prepare(scope, new(Canary, Canary, Canary, Canary));
        Unknown(await new RacInfobaseCreationInvocation(runner, new() { Enabled = true }).RunAsync(recipe));
        Assert.Equal(1, runner.Calls); Assert.NotNull(runner.Command); Assert.Null(runner.Command.LaunchDeadline);
    }
}
