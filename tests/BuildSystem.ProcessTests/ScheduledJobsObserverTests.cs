using BuildSystem.Agent.Execution;
using BuildSystem.Agent.Tools;
using Xunit;

namespace BuildSystem.ProcessTests;

public sealed class ScheduledJobsObserverTests
{
    private const string User = "private cluster user";
    private const string Password = "private-cluster-password";
    private static ScheduledJobsReadbackScope Scope() => new(Guid.NewGuid(), 1, Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ScheduledJobsCheckpoint.BeforeRestore);
    private static ScheduledJobsObserverOptions Options() => new()
    {
        TrustedRacToolId = "operator-rac", Endpoint = "rac.example:1545",
        WorkingDirectory = Path.GetTempPath(), Timeout = TimeSpan.FromSeconds(30)
    };
    private static ScheduledJobsObserverCredentials Credentials() => new(User, Password);
    private static string Valid(ScheduledJobsReadbackScope scope) =>
        $"infobase : {scope.InfobaseId:D}\nscheduled-jobs-deny : on\n";
    private static ProcessResult Success() => new(123, 0, ProcessCompletion.Exited, false);
    private static Task Emit(Func<ProcessOutput, Task> callback, string text,
        ProcessOutputStream stream = ProcessOutputStream.StandardOutput) => callback(new(stream, text));

    [Fact]
    public async Task UsesOnlyPinnedInfoCommandAndExplicitSensitiveCredentials()
    {
        var scope = Scope();
        using var cancellation = new CancellationTokenSource();
        var runner = new FakeRunner(async (command, callback, token) =>
        {
            Assert.Equal("operator-rac", command.TrustedToolId);
            Assert.Equal(Path.GetTempPath(), command.WorkingDirectory);
            Assert.Equal(TimeSpan.FromSeconds(30), command.Timeout);
            Assert.Equal(cancellation.Token, token);
            Assert.Equal(new[] { "infobase", "info", "--cluster=" + scope.ClusterId.ToString("D"),
                "--infobase=" + scope.InfobaseId.ToString("D"), "--cluster-user=" + User,
                "--cluster-pwd=" + Password, "rac.example:1545" }, command.Arguments.Select(a => a.Value));
            Assert.Equal(new[] { false, false, false, false, true, true, false },
                command.Arguments.Select(a => a.Sensitive));
            Assert.Contains(Password, command.KnownSecrets!);
            Assert.Contains(User, command.KnownSecrets!);
            Assert.DoesNotContain(Password, command.ToString());
            Assert.DoesNotContain(Password, command.Arguments[5].ToString());
            await Emit(callback, Valid(scope));
            return Success();
        });
        var result = await new ScheduledJobsObserver(runner, Options()).ObserveAsync(scope, Credentials(), cancellation.Token);
        Assert.Same(scope, result.Scope);
        Assert.Equal(1, runner.Calls);
        Assert.DoesNotContain(Password, Credentials().ToString());
        Assert.DoesNotContain(User, Credentials().ToString());
    }

    [Theory]
    [InlineData(ProcessCompletion.TimedOut, 0, false)]
    [InlineData(ProcessCompletion.Cancelled, 0, false)]
    [InlineData((ProcessCompletion)99, 0, false)]
    [InlineData(ProcessCompletion.Exited, 1, false)]
    [InlineData(ProcessCompletion.Exited, null, false)]
    [InlineData(ProcessCompletion.Exited, 0, true)]
    public async Task RejectsIncompleteOrUnsuccessfulProcess(ProcessCompletion completion, int? exit, bool truncated)
    {
        var scope = Scope();
        await Unavailable(new FakeRunner(async (_, callback, _) =>
        {
            await Emit(callback, Valid(scope));
            return new(1, exit, completion, truncated);
        }), scope);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("error with private-cluster-password")]
    public async Task RejectsAnyNonemptyStderr(string stderr)
    {
        var scope = Scope();
        await Unavailable(new FakeRunner(async (_, callback, _) =>
        {
            await Emit(callback, Valid(scope));
            await Emit(callback, stderr, ProcessOutputStream.StandardError);
            return Success();
        }), scope);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsOutputBeyondParserBoundEvenWhenRunnerDoesNotReportTruncation(bool manyChunks)
    {
        var scope = Scope();
        await Unavailable(new FakeRunner(async (_, callback, _) =>
        {
            await Emit(callback, Valid(scope));
            if (manyChunks)
                for (var i = 0; i < ScheduledJobsReadback.MaxOutputCharacters; i++) await Emit(callback, " ");
            else await Emit(callback, new string(' ', ScheduledJobsReadback.MaxOutputCharacters + 1));
            return Success();
        }), scope);
    }

    [Fact]
    public async Task AcceptsExactlyMaximumOutputAndEmptyStderr()
    {
        var scope = Scope();
        var text = Valid(scope);
        var runner = new FakeRunner(async (_, callback, _) =>
        {
            await Emit(callback, text);
            await Emit(callback, new string(' ', ScheduledJobsReadback.MaxOutputCharacters - text.Length));
            await Emit(callback, "", ProcessOutputStream.StandardError);
            return Success();
        });
        Assert.Equal(scope, (await new ScheduledJobsObserver(runner, Options()).ObserveAsync(scope, Credentials())).Scope);
    }

    [Theory]
    [InlineData("start_failed")]
    [InlineData("output_failed")]
    [InlineData("callback_failed")]
    [InlineData("cancellation")]
    public async Task SanitizesRunnerAndOutputExceptions(string failure)
    {
        var runner = new FakeRunner((_, _, _) => throw (failure == "cancellation"
            ? new OperationCanceledException(Password)
            : new InvalidOperationException(failure + Password)));
        await Unavailable(runner, Scope());
    }

    [Fact]
    public async Task SanitizesInvalidOutputCallbackFailure()
    {
        await Unavailable(new FakeRunner(async (_, callback, _) =>
        {
            await callback(new((ProcessOutputStream)99, Password));
            return Success();
        }), Scope());
    }

    [Theory]
    [InlineData("")]
    [InlineData("scheduled-jobs-deny : off\n")]
    [InlineData("scheduled-jobs-deny : on\nscheduled-jobs-deny : on\n")]
    [InlineData("private-cluster-password")]
    public async Task DefersToParserAndDoesNotLeakMalformedOutput(string suffix)
    {
        var scope = Scope();
        var runner = new FakeRunner(async (_, callback, _) =>
        {
            await Emit(callback, $"infobase : {scope.InfobaseId:D}\n" + suffix);
            return Success();
        });
        var error = await Assert.ThrowsAsync<ScheduledJobsReadbackException>(() =>
            new ScheduledJobsObserver(runner, Options()).ObserveAsync(scope, Credentials()));
        Assert.DoesNotContain(Password, error.ToString());
    }

    [Fact]
    public async Task CancellationBeforeOrDuringRunCannotProduceReadback()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var runner = new FakeRunner((_, _, _) => throw new Xunit.Sdk.XunitException("must not run"));
        await Assert.ThrowsAsync<ScheduledJobsReadbackException>(() =>
            new ScheduledJobsObserver(runner, Options()).ObserveAsync(Scope(), Credentials(), cancellation.Token));
        Assert.Equal(0, runner.Calls);
        using var during = new CancellationTokenSource();
        var scope = Scope();
        runner = new FakeRunner(async (_, callback, _) =>
        {
            await Emit(callback, Valid(scope));
            during.Cancel();
            return Success();
        });
        await Assert.ThrowsAsync<ScheduledJobsReadbackException>(() =>
            new ScheduledJobsObserver(runner, Options()).ObserveAsync(scope, Credentials(), during.Token));
    }

    [Fact]
    public async Task ParallelCallbacksAndInvocationsHaveIndependentBoundedState()
    {
        var runner = new FakeRunner(async (command, callback, _) =>
        {
            var id = command.Arguments[3].Value["--infobase=".Length..];
            await Emit(callback, $"infobase : {id}\nscheduled-jobs-deny : on\n");
            await Task.WhenAll(Enumerable.Range(0, 1000).Select(_ => Task.Run(() => Emit(callback, " \n"))));
            return Success();
        });
        var observer = new ScheduledJobsObserver(runner, Options());
        var scopes = Enumerable.Range(0, 8).Select(_ => Scope()).ToArray();
        var observations = await Task.WhenAll(scopes.Select(s => observer.ObserveAsync(s, Credentials())));
        Assert.Equal(scopes, observations.Select(o => o.Scope));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(60001)]
    public void RejectsUnboundedTimeout(int milliseconds)
    {
        var options = Options();
        Assert.Throws<ArgumentException>(() => new ScheduledJobsObserver(new FakeRunner((_, _, _) => Task.FromResult(Success())),
            new() { TrustedRacToolId = options.TrustedRacToolId, Endpoint = options.Endpoint,
                WorkingDirectory = options.WorkingDirectory, Timeout = TimeSpan.FromMilliseconds(milliseconds) }));
    }

    [Theory]
    [InlineData("--danger")]
    [InlineData("")]
    [InlineData("host\nsecret")]
    [InlineData("host other")]
    public void RejectsInvalidOperatorEndpoint(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => new ScheduledJobsObserver(new FakeRunner((_, _, _) => Task.FromResult(Success())),
            new() { TrustedRacToolId = "rac", Endpoint = endpoint, WorkingDirectory = Path.GetTempPath(),
                Timeout = TimeSpan.FromMinutes(1) }));
    }

    [Fact]
    public async Task InvalidScopeOrCredentialsDoNotExecute()
    {
        var runner = new FakeRunner((_, _, _) => throw new Xunit.Sdk.XunitException("must not run"));
        var observer = new ScheduledJobsObserver(runner, Options());
        await Assert.ThrowsAsync<ScheduledJobsReadbackException>(() => observer.ObserveAsync(Scope() with { ClusterId = Guid.Empty }, Credentials()));
        await Assert.ThrowsAsync<ScheduledJobsReadbackException>(() => observer.ObserveAsync(Scope(), new(User, "bad\0password")));
        Assert.Equal(0, runner.Calls);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("password")]
    [InlineData("tool")]
    [InlineData("endpoint")]
    [InlineData("directory")]
    public async Task RejectsOversizedInputsBeforeExecution(string field)
    {
        var runner = new FakeRunner((_, _, _) => throw new Xunit.Sdk.XunitException("must not run"));
        var options = Options();
        if (field is "user" or "password")
        {
            var credentials = field == "user" ? new ScheduledJobsObserverCredentials(new string('u', 4097), Password)
                : new ScheduledJobsObserverCredentials(User, new string('p', 4097));
            await Assert.ThrowsAsync<ScheduledJobsReadbackException>(() =>
                new ScheduledJobsObserver(runner, options).ObserveAsync(Scope(), credentials));
        }
        else
            Assert.Throws<ArgumentException>(() => new ScheduledJobsObserver(runner, new()
            {
                TrustedRacToolId = field == "tool" ? new string('r', 129) : options.TrustedRacToolId,
                Endpoint = field == "endpoint" ? new string('h', 256) : options.Endpoint,
                WorkingDirectory = field == "directory" ? "/" + new string('d', 4096) : options.WorkingDirectory,
                Timeout = options.Timeout
            }));
        Assert.Equal(0, runner.Calls);
    }

    [Theory]
    [InlineData("bad\tvalue")]
    [InlineData("bad\u007fvalue")]
    public async Task RejectsControlCharactersInEitherCredential(string value)
    {
        var runner = new FakeRunner((_, _, _) => throw new Xunit.Sdk.XunitException("must not run"));
        var observer = new ScheduledJobsObserver(runner, Options());
        await Assert.ThrowsAsync<ScheduledJobsReadbackException>(() => observer.ObserveAsync(Scope(), new(value, Password)));
        await Assert.ThrowsAsync<ScheduledJobsReadbackException>(() => observer.ObserveAsync(Scope(), new(User, value)));
        Assert.Equal(0, runner.Calls);
    }

    private static async Task Unavailable(FakeRunner runner, ScheduledJobsReadbackScope scope)
    {
        var error = await Assert.ThrowsAsync<ScheduledJobsReadbackException>(() =>
            new ScheduledJobsObserver(runner, Options()).ObserveAsync(scope, Credentials()));
        Assert.Equal("ScheduledJobsReadbackUnavailable", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Password, error.ToString());
    }

    private sealed class FakeRunner(Func<ProcessCommand, Func<ProcessOutput, Task>, CancellationToken, Task<ProcessResult>> run) : IProcessRunner
    {
        private int calls;
        public int Calls => calls;
        public Task<ProcessResult> RunAsync(ProcessCommand command, Func<ProcessOutput, Task>? onOutput = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            return run(command, onOutput!, cancellationToken);
        }
    }
}
