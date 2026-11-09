using System.Text.Json;
using BuildSystem.Agent.Tools;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class RacInfobaseCreationInvocationTests
{
    private static readonly Guid Cluster = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RacCluster = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Server = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Mapping = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Infobase = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly string Ack = "infobase : " + Infobase.ToString("D");
    private static readonly string Workspace = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rac-invocation-tests"));
    private static readonly string[] Secrets =
        ["private-cluster-user", "private-cluster-password", "private-sql-user", "private-sql-password"];

    private static RacInfobaseCreationCommandRecipe Recipe() => new RacInfobaseCreationRecipe(new()
    {
        TrustedRacToolId = "trusted-rac-invocation", Endpoint = "private-ras.example:1545",
        EndpointMappingId = Mapping, CanonicalClusterId = Cluster, RacClusterId = RacCluster,
        CanonicalSqlServerId = Server, SqlServer = "private-sql.example\\TEST",
        WorkingDirectory = Workspace, Timeout = TimeSpan.FromSeconds(30), Locale = "ru_RU",
        Enabled = true, ExclusiveNamespaceConfirmed = true
    }).Prepare(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Cluster, Server, Mapping), new(Secrets[0], Secrets[1], Secrets[2], Secrets[3]));

    private static ProcessResult Success() => new(123, 0, ProcessCompletion.Exited, false);

    private static RacInfobaseCreationInvocation Enabled(FakeRunner runner) =>
        new(runner, new() { Enabled = true });

    private static async Task Emit(Func<ProcessOutput, Task>? callback,
        string text, ProcessOutputStream stream = ProcessOutputStream.StandardOutput)
    {
        Assert.NotNull(callback);
        await callback(new(stream, text));
    }

    private static void AssertInert(RacInfobaseCreationOutcome outcome)
    {
        Assert.False(outcome.Executable);
        Assert.True(outcome.ScheduledJobsMustRemainBlocked);
        Assert.True(outcome.RequiresIndependentVerification);
        Assert.False(outcome.RetryAllowed);
    }

    private static void AssertUnknown(RacInfobaseCreationOutcome outcome)
    {
        Assert.Equal(RacInfobaseCreationOutcomeKind.ReconciliationRequired, outcome.Kind);
        Assert.Equal("OutcomeUnknown", outcome.ReasonCode);
        Assert.Null(outcome.ReportedInfobaseId);
        AssertInert(outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullConstructorDependencyHasOnlyFixedValidationDiagnostics(bool nullRunner)
    {
        var error = Assert.Throws<RacInfobaseCreationInvocationException>(() =>
            new RacInfobaseCreationInvocation(nullRunner ? null! : new FakeRunner(),
                nullRunner ? new() : null!));
        Assert.Equal("InvalidRacInfobaseCreationInvocationOptions", error.Code);
        Assert.Equal(error.Code, error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task InvocationIsDisabledByDefaultAndDoesNotCallRunner()
    {
        var runner = new FakeRunner();
        var options = new RacInfobaseCreationInvocationOptions();
        Assert.False(options.Enabled);
        var error = await Assert.ThrowsAsync<RacInfobaseCreationInvocationException>(() =>
            new RacInfobaseCreationInvocation(runner, options).RunAsync(Recipe()));
        Assert.Equal("RacInfobaseCreationInvocationDisabled", error.Code);
        Assert.Equal(error.Code, error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task DisabledGatePrecedesRecipeValidation()
    {
        var runner = new FakeRunner();
        var error = await Assert.ThrowsAsync<RacInfobaseCreationInvocationException>(() =>
            new RacInfobaseCreationInvocation(runner, new()).RunAsync(null!));
        Assert.Equal("RacInfobaseCreationInvocationDisabled", error.Code);
        Assert.Null(error.InnerException);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task EnabledNullRecipeIsRejectedBeforeCallingRunner()
    {
        var runner = new FakeRunner();
        var error = await Assert.ThrowsAsync<RacInfobaseCreationInvocationException>(() =>
            Enabled(runner).RunAsync(null!));
        Assert.Equal("InvalidRacInfobaseCreationRecipe", error.Code);
        Assert.Equal(error.Code, error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task PrecancelledTokenThrowsWithoutCallingRunner()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var runner = new FakeRunner();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Enabled(runner).RunAsync(Recipe(), cancellation.Token));
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task ExactPreparedCommandAndCancellationTokenAreForwardedOnce()
    {
        var recipe = Recipe();
        using var cancellation = new CancellationTokenSource();
        var runner = new FakeRunner(async (command, callback, token) =>
        {
            Assert.Same(recipe.Command, command);
            Assert.Equal(cancellation.Token, token);
            Assert.Contains(command.Arguments, a => a.Value == "--create-database");
            Assert.Contains(command.Arguments, a => a.Value == "--scheduled-jobs-deny=on");
            Assert.Equal(4, command.Arguments.Count(a => a.Sensitive));
            Assert.Equal(Secrets, command.KnownSecrets);
            await Emit(callback, Ack + "\n");
            return Success();
        });
        var outcome = await Enabled(runner).RunAsync(recipe, cancellation.Token);
        Assert.Equal(1, runner.Calls);
        Assert.Same(recipe.Scope, outcome.Scope);
        Assert.Equal(RacInfobaseCreationOutcomeKind.AcknowledgementObserved, outcome.Kind);
        Assert.Equal("CreationAcknowledgementObserved", outcome.ReasonCode);
        Assert.Equal(Infobase, outcome.ReportedInfobaseId);
        AssertInert(outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task SingleAcknowledgementAcceptsActualLineTerminators(string terminator)
    {
        var runner = new FakeRunner(async (_, callback, _) =>
        {
            await Emit(callback, Ack + terminator);
            return Success();
        });
        var outcome = await Enabled(runner).RunAsync(Recipe());
        Assert.Equal(RacInfobaseCreationOutcomeKind.AcknowledgementObserved, outcome.Kind);
        AssertInert(outcome);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task RawChunksWithoutNewlinesAreReassembledWithoutInventedSeparators()
    {
        var runner = new FakeRunner(async (_, callback, _) =>
        {
            await Emit(callback, Ack[..7]);
            await Emit(callback, Ack[7..20]);
            await Emit(callback, Ack[20..]);
            return Success();
        });
        var outcome = await Enabled(runner).RunAsync(Recipe());
        Assert.Equal(RacInfobaseCreationOutcomeKind.AcknowledgementObserved, outcome.Kind);
        Assert.Equal(Infobase, outcome.ReportedInfobaseId);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task TwoAcknowledgementLinesCannotBecomeOneAcknowledgement()
    {
        var runner = new FakeRunner(async (_, callback, _) =>
        {
            await Emit(callback, Ack + "\n");
            await Emit(callback, Ack + "\n");
            return Success();
        });
        AssertUnknown(await Enabled(runner).RunAsync(Recipe()));
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task NoOutputCallbacksIsUnknownEvenForSuccessfulExit()
    {
        var runner = new FakeRunner();
        AssertUnknown(await Enabled(runner).RunAsync(Recipe()));
        Assert.Equal(1, runner.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\n")]
    public async Task EmptyOrWhitespaceOnlyOutputIsUnknown(string text)
    {
        var runner = new FakeRunner(async (_, callback, _) =>
        {
            await Emit(callback, text);
            return Success();
        });
        AssertUnknown(await Enabled(runner).RunAsync(Recipe()));
    }

    [Theory]
    [InlineData("warning")]
    [InlineData(" ")]
    [InlineData("\n")]
    public async Task AnyStderrTextMakesAcknowledgementUnknown(string stderr)
    {
        var runner = new FakeRunner(async (_, callback, _) =>
        {
            await Emit(callback, Ack);
            await Emit(callback, stderr, ProcessOutputStream.StandardError);
            return Success();
        });
        AssertUnknown(await Enabled(runner).RunAsync(Recipe()));
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task EmptyStderrChunkDoesNotInventStderrContent()
    {
        var runner = new FakeRunner(async (_, callback, _) =>
        {
            await Emit(callback, Ack);
            await Emit(callback, "", ProcessOutputStream.StandardError);
            return Success();
        });
        var outcome = await Enabled(runner).RunAsync(Recipe());
        Assert.Equal(RacInfobaseCreationOutcomeKind.AcknowledgementObserved, outcome.Kind);
    }

    [Theory]
    [InlineData(4096, true)]
    [InlineData(4097, false)]
    public async Task RawCaptureBudgetHasExact4096CharacterBoundary(int length, bool acknowledged)
    {
        var runner = new FakeRunner(async (_, callback, _) =>
        {
            await Emit(callback, Ack);
            await Emit(callback, new string(' ', length - Ack.Length));
            return Success();
        });
        var outcome = await Enabled(runner).RunAsync(Recipe());
        if (acknowledged)
            Assert.Equal(RacInfobaseCreationOutcomeKind.AcknowledgementObserved, outcome.Kind);
        else
            AssertUnknown(outcome);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task OverflowRemainsUnknownAfterSubsequentValidAcknowledgement()
    {
        var runner = new FakeRunner(async (_, callback, _) =>
        {
            await Emit(callback, new string(' ', 4097));
            await Emit(callback, Ack);
            return Success();
        });
        AssertUnknown(await Enabled(runner).RunAsync(Recipe()));
    }

    [Fact]
    public async Task CombinedStreamOverflowIsUnknownWithoutRetry()
    {
        var runner = new FakeRunner(async (_, callback, _) =>
        {
            await Emit(callback, new string(' ', 2048));
            await Emit(callback, new string(' ', 2049), ProcessOutputStream.StandardError);
            await Emit(callback, Ack);
            return Success();
        });
        AssertUnknown(await Enabled(runner).RunAsync(Recipe()));
        Assert.Equal(1, runner.Calls);
    }

    [Theory]
    [InlineData("[REDACTED]")]
    [InlineData("[redacted]")]
    [InlineData("unexpected diagnostic")]
    [InlineData("\0")]
    public async Task RedactionOrUnrecognizedOutputCannotAcknowledgeCreation(string text)
    {
        var runner = new FakeRunner(async (_, callback, _) =>
        {
            await Emit(callback, Ack + "\n" + text);
            return Success();
        });
        AssertUnknown(await Enabled(runner).RunAsync(Recipe()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task MalformedCallbackRecordCannotAcknowledgeCreation(int malformed)
    {
        var runner = new FakeRunner(async (_, callback, _) =>
        {
            Assert.NotNull(callback);
            await Emit(callback, Ack);
            await callback(malformed switch
            {
                0 => null!,
                1 => new(ProcessOutputStream.StandardOutput, null!),
                _ => new((ProcessOutputStream)12345, "")
            });
            return Success();
        });
        AssertUnknown(await Enabled(runner).RunAsync(Recipe()));
        Assert.Equal(1, runner.Calls);
    }

    public static IEnumerable<object[]> UncertainResults()
    {
        yield return [new ProcessResult(123, 1, ProcessCompletion.Exited, false)];
        yield return [new ProcessResult(123, null, ProcessCompletion.Exited, false)];
        yield return [new ProcessResult(null, 0, ProcessCompletion.Exited, false)];
        yield return [new ProcessResult(0, 0, ProcessCompletion.Exited, false)];
        yield return [new ProcessResult(-1, 0, ProcessCompletion.Exited, false)];
        yield return [new ProcessResult(123, 0, ProcessCompletion.TimedOut, false)];
        yield return [new ProcessResult(123, 0, ProcessCompletion.Cancelled, false)];
        yield return [new ProcessResult(123, 0, (ProcessCompletion)12345, false)];
        yield return [new ProcessResult(123, 0, ProcessCompletion.Exited, true)];
    }

    [Theory]
    [MemberData(nameof(UncertainResults))]
    public async Task UncertainProcessResultIsUnknownDespiteAcknowledgement(ProcessResult result)
    {
        var runner = new FakeRunner(async (_, callback, _) =>
        {
            await Emit(callback, Ack);
            return result;
        });
        AssertUnknown(await Enabled(runner).RunAsync(Recipe()));
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task NullProcessResultIsUnknown()
    {
        var runner = new FakeRunner(async (_, callback, _) =>
        {
            await Emit(callback, Ack);
            return null!;
        });
        AssertUnknown(await Enabled(runner).RunAsync(Recipe()));
        Assert.Equal(1, runner.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunnerExceptionBeforeOrAfterAcknowledgementIsSanitizedUnknown(bool emitFirst)
    {
        var recipe = Recipe();
        var raw = "raw-private-diagnostic " + string.Join(" ", Secrets) + " " + recipe.GeneratedName;
        var runner = new FakeRunner(async (_, callback, _) =>
        {
            if (emitFirst) await Emit(callback, Ack);
            throw new InvalidOperationException(raw);
        });
        var outcome = await Enabled(runner).RunAsync(recipe);
        AssertUnknown(outcome);
        var diagnostics = JsonSerializer.Serialize(outcome) + outcome;
        foreach (var secret in Secrets.Append(raw).Append(recipe.GeneratedName).Append(Workspace)
                     .Append("private-ras.example:1545").Append("private-sql.example\\TEST"))
            Assert.DoesNotContain(secret, diagnostics, StringComparison.Ordinal);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task SynchronousRunnerThrowIsAlsoUnknown()
    {
        var runner = new FakeRunner((_, _, _) => throw new InvalidOperationException("private-runner-error"));
        AssertUnknown(await Enabled(runner).RunAsync(Recipe()));
        Assert.Equal(1, runner.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationExceptionAfterInvocationIsUnknownNotRawCancellation(bool emitFirst)
    {
        var runner = new FakeRunner(async (_, callback, _) =>
        {
            if (emitFirst) await Emit(callback, Ack);
            throw new OperationCanceledException("private-cancellation-detail");
        });
        AssertUnknown(await Enabled(runner).RunAsync(Recipe()));
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task TokenCancelledByRunnerCannotTurnSuccessfulExitIntoFalseAcknowledgement()
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new FakeRunner(async (_, callback, _) =>
        {
            await Emit(callback, Ack);
            cancellation.Cancel();
            return Success();
        });
        AssertUnknown(await Enabled(runner).RunAsync(Recipe(), cancellation.Token));
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task ConcurrentCallbacksPreserveAcknowledgementAndBoundedCapture()
    {
        var runner = new FakeRunner(async (_, callback, _) =>
        {
            await Emit(callback, Ack + "\n");
            await Task.WhenAll(Enumerable.Range(0, 128).Select(_ => Task.Run(() => Emit(callback, "\n"))));
            return Success();
        });
        var outcome = await Enabled(runner).RunAsync(Recipe());
        Assert.Equal(RacInfobaseCreationOutcomeKind.AcknowledgementObserved, outcome.Kind);
        Assert.Equal(Infobase, outcome.ReportedInfobaseId);
        AssertInert(outcome);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task ConcurrentCallbacksCrossingBudgetRemainUnknown()
    {
        var runner = new FakeRunner(async (_, callback, _) =>
        {
            await Emit(callback, Ack + "\n");
            await Task.WhenAll(Enumerable.Range(0, 64).Select(_ =>
                Task.Run(() => Emit(callback, new string(' ', 128)))));
            return Success();
        });
        AssertUnknown(await Enabled(runner).RunAsync(Recipe()));
        Assert.Equal(1, runner.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateCallbacksCannotChangeReturnedOutcome(bool acknowledgeBeforeReturn)
    {
        Func<ProcessOutput, Task>? captured = null;
        var runner = new FakeRunner(async (_, callback, _) =>
        {
            captured = callback;
            if (acknowledgeBeforeReturn) await Emit(callback, Ack);
            return Success();
        });
        var outcome = await Enabled(runner).RunAsync(Recipe());
        var before = JsonSerializer.Serialize(outcome);
        await Emit(captured, acknowledgeBeforeReturn ? "late stderr" : Ack,
            acknowledgeBeforeReturn ? ProcessOutputStream.StandardError : ProcessOutputStream.StandardOutput);
        Assert.Equal(before, JsonSerializer.Serialize(outcome));
        if (acknowledgeBeforeReturn)
            Assert.Equal(RacInfobaseCreationOutcomeKind.AcknowledgementObserved, outcome.Kind);
        else
            AssertUnknown(outcome);
        AssertInert(outcome);
        Assert.Equal(1, runner.Calls);
    }

    // All invocation tests use this in-memory seam; no ProcessRunner or child process is created.
    private sealed class FakeRunner(
        Func<ProcessCommand, Func<ProcessOutput, Task>?, CancellationToken, Task<ProcessResult>>? run = null)
        : IProcessRunner
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);

        public Task<ProcessResult> RunAsync(ProcessCommand command, Func<ProcessOutput, Task>? onOutput = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            return run is null ? Task.FromResult(Success()) : run(command, onOutput, cancellationToken);
        }
    }
}
