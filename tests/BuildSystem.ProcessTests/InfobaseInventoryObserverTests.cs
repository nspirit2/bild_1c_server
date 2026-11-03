using BuildSystem.Agent.Execution;
using BuildSystem.Agent.Tools;
using Xunit;

namespace BuildSystem.ProcessTests;

public sealed class InfobaseInventoryObserverTests
{
    private const string User = "private cluster user";
    private const string Password = "private-cluster-password";
    private static readonly Guid MappingId = Guid.NewGuid();
    private static InfobaseInventoryReadbackScope Scope()
    {
        var reservation = Guid.NewGuid();
        return new(Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), MappingId, reservation,
            Guid.NewGuid(), $"bs_test_{reservation:N}");
    }
    private static InfobaseInventoryObserverOptions Options() => new()
    {
        TrustedRacToolId = "operator-rac", Endpoint = "rac.example:1545", EndpointMappingId = MappingId,
        WorkingDirectory = Path.GetTempPath(), Timeout = TimeSpan.FromSeconds(30)
    };
    private static InfobaseInventoryObserverCredentials Credentials() => new(User, Password);
    private static string Valid(InfobaseInventoryReadbackScope scope) =>
        $"infobase : {Guid.NewGuid():D}\nname : {scope.ExpectedInfobaseName}\ndescr : discarded\n";
    private static ProcessResult Success() => new(123, 0, ProcessCompletion.Exited, false);
    private static Task Emit(Func<ProcessOutput, Task> callback, string text,
        ProcessOutputStream stream = ProcessOutputStream.StandardOutput) => callback(new(stream, text));

    [Fact]
    public async Task UsesExactlySevenPinnedReadOnlyArgumentsAndSensitiveExplicitCredentials()
    {
        var scope = Scope();
        using var cancellation = new CancellationTokenSource();
        var runner = new FakeRunner(async (command, callback, token) =>
        {
            Assert.Equal("operator-rac", command.TrustedToolId);
            Assert.Equal(Path.GetTempPath(), command.WorkingDirectory);
            Assert.Equal(TimeSpan.FromSeconds(30), command.Timeout);
            Assert.Equal(cancellation.Token, token);
            Assert.Equal(7, command.Arguments.Count);
            Assert.Equal(new[] { "infobase", "summary", "list", "--cluster=" + scope.ClusterId.ToString("D"),
                "--cluster-user=" + User, "--cluster-pwd=" + Password, "rac.example:1545" },
                command.Arguments.Select(a => a.Value));
            Assert.Equal(new[] { false, false, false, false, true, true, false },
                command.Arguments.Select(a => a.Sensitive));
            Assert.Equal(new[] { User, Password }, command.KnownSecrets);
            Assert.DoesNotContain(command.Arguments, a => a.Value.StartsWith("--infobase", StringComparison.Ordinal));
            Assert.DoesNotContain(Password, command.ToString());
            Assert.DoesNotContain(User, command.ToString());
            Assert.DoesNotContain(Password, command.Arguments[5].ToString());
            await Emit(callback, Valid(scope));
            return Success();
        });
        var result = await new InfobaseInventoryObserver(runner, Options()).ObserveAsync(scope, Credentials(), cancellation.Token);
        Assert.Same(scope, result.Scope);
        Assert.Equal(InfobaseInventoryObservation.Present, result.Observation);
        Assert.Single(result.Entries);
        Assert.Equal(1, runner.Calls);
        Assert.DoesNotContain(Password, Credentials().ToString());
        Assert.DoesNotContain(User, Credentials().ToString());
        Assert.DoesNotContain(Options().Endpoint, Options().ToString());
    }

    [Fact]
    public async Task EmptySuccessfulInventoryIsAbsenceOnly()
    {
        var runner = new FakeRunner((_, _, _) => Task.FromResult(Success()));
        var result = await new InfobaseInventoryObserver(runner, Options()).ObserveAsync(Scope(), Credentials());
        Assert.Equal(InfobaseInventoryObservation.Absent, result.Observation);
        Assert.Empty(result.Entries);
        Assert.Equal(1, runner.Calls);
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
        await Unavailable(new FakeRunner(async (_, callback, _) =>
        {
            // Invalid syntax proves unsuccessful execution is rejected before parsing.
            await Emit(callback, Password);
            return new(1, exit, completion, truncated);
        }), Scope());
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("error with private-cluster-password")]
    public async Task RejectsAnyNonemptyStderr(string stderr)
    {
        await Unavailable(new FakeRunner(async (_, callback, _) =>
        {
            await Emit(callback, stderr, ProcessOutputStream.StandardError);
            return Success();
        }), Scope());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsOutputBeyondBoundEvenWithoutRunnerTruncation(bool manyChunks)
    {
        await Unavailable(new FakeRunner(async (_, callback, _) =>
        {
            if (manyChunks)
                for (var i = 0; i <= InfobaseInventoryReadback.MaxOutputCharacters; i++) await Emit(callback, " ");
            else await Emit(callback, new string(' ', InfobaseInventoryReadback.MaxOutputCharacters + 1));
            return Success();
        }), Scope());
    }

    [Fact]
    public async Task AcceptsExactlyMaximumOutputAndEmptyStderr()
    {
        var runner = new FakeRunner(async (_, callback, _) =>
        {
            await Emit(callback, new string(' ', InfobaseInventoryReadback.MaxOutputCharacters));
            await Emit(callback, "", ProcessOutputStream.StandardError);
            return Success();
        });
        Assert.Equal(InfobaseInventoryObservation.Absent,
            (await new InfobaseInventoryObserver(runner, Options()).ObserveAsync(Scope(), Credentials())).Observation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SanitizesRunnerExceptions(bool cancellation)
    {
        await Unavailable(new FakeRunner((_, _, _) => throw (cancellation
            ? new OperationCanceledException(Password) : new InvalidOperationException(Password))), Scope());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RejectsInvalidCallbacksEvenIfRunnerSuppressesFailure(int invalid)
    {
        await Unavailable(new FakeRunner(async (_, callback, _) =>
        {
            try
            {
                await callback(invalid switch
                {
                    0 => null!,
                    1 => new(ProcessOutputStream.StandardOutput, null!),
                    _ => new((ProcessOutputStream)99, Password)
                });
            }
            catch when (invalid == 3) { }
            return Success();
        }), Scope());
    }

    [Fact]
    public async Task RejectsNullRunnerResult()
    {
        await Unavailable(new FakeRunner((_, _, _) => Task.FromResult<ProcessResult>(null!)), Scope());
    }

    [Theory]
    [InlineData("private-cluster-password")]
    [InlineData("name : private-cluster-password\n")]
    [InlineData("infobase : not-a-guid\nname : target\n")]
    public async Task DefersMalformedSuccessfulOutputToParserWithoutLeaking(string text)
    {
        var runner = new FakeRunner(async (_, callback, _) => { await Emit(callback, text); return Success(); });
        var error = await Assert.ThrowsAsync<InfobaseInventoryReadbackException>(() =>
            new InfobaseInventoryObserver(runner, Options()).ObserveAsync(Scope(), Credentials()));
        Assert.Equal("InvalidInfobaseInventoryReadback", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Password, error.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RedactedOutputCannotBecomeFalseAbsenceEvenAcrossCallbacks(bool split)
    {
        await Unavailable(new FakeRunner(async (_, callback, _) =>
        {
            await Emit(callback, $"infobase : {Guid.NewGuid():D}\nname : ");
            if (split) { await Emit(callback, "[RED"); await Emit(callback, "ACTED]\n"); }
            else await Emit(callback, "[REDACTED]\n");
            return Success();
        }), Scope());
    }

    [Fact]
    public async Task CancellationBeforeOrDuringRunCannotProduceReadback()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var runner = NeverRun();
        await Assert.ThrowsAsync<InfobaseInventoryReadbackException>(() =>
            new InfobaseInventoryObserver(runner, Options()).ObserveAsync(Scope(), Credentials(), cancellation.Token));
        Assert.Equal(0, runner.Calls);
        using var during = new CancellationTokenSource();
        runner = new FakeRunner((_, _, _) => { during.Cancel(); return Task.FromResult(Success()); });
        await Unavailable(runner, Scope(), during.Token);
    }

    [Fact]
    public async Task ParallelCallbacksAndInvocationsHaveIndependentBoundedState()
    {
        var runner = new FakeRunner(async (_, callback, _) =>
        {
            await Task.WhenAll(Enumerable.Range(0, 1000).Select(_ => Task.Run(() => Emit(callback, " \n"))));
            return Success();
        });
        var observer = new InfobaseInventoryObserver(runner, Options());
        var scopes = Enumerable.Range(0, 8).Select(_ => Scope()).ToArray();
        var results = await Task.WhenAll(scopes.Select(s => observer.ObserveAsync(s, Credentials())));
        Assert.Equal(scopes, results.Select(r => r.Scope));
        Assert.All(results, r => Assert.Equal(InfobaseInventoryObservation.Absent, r.Observation));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(60001)]
    public void RejectsUnboundedTimeout(int milliseconds) => AssertInvalidOptions(timeout: TimeSpan.FromMilliseconds(milliseconds));

    [Theory]
    [InlineData("--danger")]
    [InlineData("")]
    [InlineData("host\nsecret")]
    [InlineData("host other")]
    [InlineData("host\u007f")]
    public void RejectsInvalidOperatorEndpoint(string endpoint) => AssertInvalidOptions(endpoint: endpoint);

    [Theory]
    [InlineData("")]
    [InlineData("--rac")]
    [InlineData("rac other")]
    [InlineData("rac\u007f")]
    public void RejectsInvalidToolId(string tool) => AssertInvalidOptions(tool: tool);

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData("/bad\npath")]
    public void RejectsInvalidWorkingDirectory(string directory) => AssertInvalidOptions(directory: directory);

    [Fact]
    public void RejectsEmptyConfiguredMapping() => AssertInvalidOptions(mapping: Guid.Empty);

    [Theory]
    [InlineData("user")]
    [InlineData("password")]
    [InlineData("tool")]
    [InlineData("endpoint")]
    [InlineData("directory")]
    public async Task RejectsOversizedInputsBeforeExecution(string field)
    {
        var runner = NeverRun();
        if (field is "user" or "password")
        {
            var credentials = field == "user" ? new InfobaseInventoryObserverCredentials(new string('u', 4097), Password)
                : new InfobaseInventoryObserverCredentials(User, new string('p', 4097));
            await Assert.ThrowsAsync<InfobaseInventoryReadbackException>(() =>
                new InfobaseInventoryObserver(runner, Options()).ObserveAsync(Scope(), credentials));
        }
        else AssertInvalidOptions(tool: field == "tool" ? new string('r', 129) : null,
            endpoint: field == "endpoint" ? new string('h', 256) : null,
            directory: field == "directory" ? "/" + new string('d', 4096) : null);
        Assert.Equal(0, runner.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad\tvalue")]
    [InlineData("bad\u007fvalue")]
    public async Task RejectsInvalidCredentialsBeforeExecution(string value)
    {
        var runner = NeverRun();
        var observer = new InfobaseInventoryObserver(runner, Options());
        await Assert.ThrowsAsync<InfobaseInventoryReadbackException>(() => observer.ObserveAsync(Scope(), new(value, Password)));
        await Assert.ThrowsAsync<InfobaseInventoryReadbackException>(() => observer.ObserveAsync(Scope(), new(User, value)));
        await Assert.ThrowsAsync<InfobaseInventoryReadbackException>(() => observer.ObserveAsync(Scope(), null!));
        Assert.Equal(0, runner.Calls);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("fence")]
    [InlineData("target")]
    [InlineData("cluster")]
    [InlineData("mapping")]
    [InlineData("reservation")]
    [InlineData("observation")]
    [InlineData("name")]
    [InlineData("null")]
    public async Task RejectsInvalidScopeBeforeExecution(string field)
    {
        var scope = Scope();
        scope = field switch
        {
            "operation" => scope with { OperationId = Guid.Empty },
            "fence" => scope with { FencingToken = 0 },
            "target" => scope with { TargetIncarnationId = Guid.Empty },
            "cluster" => scope with { ClusterId = Guid.Empty },
            "mapping" => scope with { EndpointMappingId = Guid.Empty },
            "reservation" => scope with { ReservationId = Guid.Empty },
            "observation" => scope with { ObservationId = Guid.Empty },
            "name" => scope with { ExpectedInfobaseName = Password },
            _ => null!
        };
        var runner = NeverRun();
        var error = await Assert.ThrowsAsync<InfobaseInventoryReadbackException>(() =>
            new InfobaseInventoryObserver(runner, Options()).ObserveAsync(scope, Credentials()));
        Assert.Equal("InvalidInfobaseInventoryScope", error.Code);
        Assert.DoesNotContain(Password, error.ToString());
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task RejectsDifferentNonemptyMappingBeforeExecution()
    {
        var runner = NeverRun();
        var error = await Assert.ThrowsAsync<InfobaseInventoryReadbackException>(() =>
            new InfobaseInventoryObserver(runner, Options()).ObserveAsync(
                Scope() with { EndpointMappingId = Guid.NewGuid() }, Credentials()));
        Assert.Equal("InfobaseInventoryScopeMismatch", error.Code);
        Assert.Equal(0, runner.Calls);
    }

    private static void AssertInvalidOptions(string? tool = null, string? endpoint = null,
        string? directory = null, TimeSpan? timeout = null, Guid? mapping = null)
    {
        var options = Options();
        var runner = NeverRun();
        Assert.Throws<ArgumentException>(() => new InfobaseInventoryObserver(runner, new()
        {
            TrustedRacToolId = tool ?? options.TrustedRacToolId, Endpoint = endpoint ?? options.Endpoint,
            EndpointMappingId = mapping ?? options.EndpointMappingId,
            WorkingDirectory = directory ?? options.WorkingDirectory, Timeout = timeout ?? options.Timeout
        }));
        Assert.Equal(0, runner.Calls);
    }
    private static FakeRunner NeverRun() => new((_, _, _) => throw new Xunit.Sdk.XunitException("must not run"));
    private static async Task Unavailable(FakeRunner runner, InfobaseInventoryReadbackScope scope,
        CancellationToken cancellationToken = default)
    {
        var error = await Assert.ThrowsAsync<InfobaseInventoryReadbackException>(() =>
            new InfobaseInventoryObserver(runner, Options()).ObserveAsync(scope, Credentials(), cancellationToken));
        Assert.Equal("InfobaseInventoryReadbackUnavailable", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Password, error.ToString());
        Assert.DoesNotContain(User, error.ToString());
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
