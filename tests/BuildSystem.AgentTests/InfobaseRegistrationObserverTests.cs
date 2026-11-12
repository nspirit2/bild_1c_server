using BuildSystem.Agent.Execution;
using BuildSystem.Agent.Tools;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class InfobaseRegistrationObserverTests
{
    private const string User = "private cluster user";
    private const string Password = "private-cluster-password";
    private static readonly Guid Physical = Guid.NewGuid();
    private static InfobaseRegistrationObserverOptions Options(string invalid = "") => new()
    {
        Enabled = true, TrustedRacToolId = invalid == "tool" ? "bad\n" : "operator-rac",
        Endpoint = invalid == "endpoint" ? "--danger" : "rac.example:1545",
        WorkingDirectory = invalid == "directory" ? "relative" : Path.GetTempPath(),
        Timeout = invalid == "timeout-short" ? TimeSpan.FromMilliseconds(999) :
            invalid == "timeout-long" ? TimeSpan.FromSeconds(61) : TimeSpan.FromSeconds(30),
        CanonicalClusterId = Guid.Parse("30f8dbaf-86d6-4fee-ae4a-4298dbf80eb2"),
        CanonicalSqlServerId = Guid.Parse("2784381f-022d-4ea6-9fe8-7786b916c3de"),
        RacEndpointMappingId = Guid.Parse("ac17d9af-7db0-4ef4-8137-ec2512915e89"),
        SqlEndpointMappingId = Guid.Parse("17968f43-d166-4821-80c8-00fa323fcbd6"),
        PhysicalClusterId = invalid == "physical" ? Guid.Empty : Physical,
        ExpectedSqlServer = invalid == "server" ? "sql host" : "sql01\\INSTANCE",
        OutputProfileId = InfobaseRegistrationReadback.ProfileId
    };
    private static InfobaseRegistrationReadbackScope Scope()
    {
        var settings = Options();
        var reservation = Guid.NewGuid();
        var name = $"bs_test_{reservation:N}";
        return new(Guid.NewGuid(), 1, Guid.NewGuid(), reservation, settings.CanonicalClusterId,
            settings.CanonicalSqlServerId, settings.RacEndpointMappingId, settings.SqlEndpointMappingId,
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), name,
            settings.ExpectedSqlServer!, name);
    }
    private static InfobaseRegistrationObserverCredentials Credentials() => new(User, Password);
    private static ProcessResult Success() => new(231, 0, ProcessCompletion.Exited, false);
    private static string Valid(InfobaseRegistrationReadbackScope s) =>
        $"infobase : {s.InfobaseId:D}\nname : {s.ExpectedInfobaseName}\ndbms : MSSQLServer\n" +
        $"db-server : {s.ExpectedSqlServer}\ndb-name : {s.ExpectedDatabaseName}\nscheduled-jobs-deny : on\n";
    private static Task Emit(Func<ProcessOutput, Task> callback, string text,
        ProcessOutputStream stream = ProcessOutputStream.StandardOutput) => callback(new(stream, text));
    private static async Task Unavailable(FakeRunner runner, InfobaseRegistrationReadbackScope? scope = null,
        CancellationToken cancellationToken = default, TimeProvider? clock = null)
    {
        var error = await Assert.ThrowsAsync<InfobaseRegistrationObserverException>(() =>
            new InfobaseRegistrationObserver(runner, Options(), clock).ObserveAsync(scope ?? Scope(), Credentials(), cancellationToken));
        Assert.Equal("InfobaseRegistrationObserverUnavailable", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Password, error.ToString());
        Assert.DoesNotContain(User, error.ToString());
    }

    [Theory]
    [InlineData("tool")]
    [InlineData("endpoint")]
    [InlineData("directory")]
    [InlineData("timeout-short")]
    [InlineData("timeout-long")]
    [InlineData("physical")]
    [InlineData("server")]
    public void InvalidEnabledOptionsFailWithFixedCode(string field)
    {
        var runner = new FakeRunner((_, _, _) => throw new Exception("must not run"));
        var error = Assert.Throws<InfobaseRegistrationObserverException>(() =>
            new InfobaseRegistrationObserver(runner, Options(field)));
        Assert.Equal("InfobaseRegistrationObserverInvalidOptions", error.Code);
        Assert.Null(error.InnerException);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task DisabledIgnoresInvalidSettingsAndNeverTouchesRunner()
    {
        var runner = new FakeRunner((_, _, _) => throw new Exception("must not run"));
        var observer = new InfobaseRegistrationObserver(runner, new() { Enabled = false,
            TrustedRacToolId = "\n", Endpoint = "-bad", Timeout = TimeSpan.FromDays(90) });
        var error = await Assert.ThrowsAsync<InfobaseRegistrationObserverException>(() =>
            observer.ObserveAsync(Scope(), Credentials()));
        Assert.Equal("InfobaseRegistrationObserverDisabled", error.Code);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task PinsPhysicalClusterReadOnlyCommandAndRedactsCredentials()
    {
        var scope = Scope();
        var runner = new FakeRunner(async (command, callback, token) =>
        {
            Assert.Equal("operator-rac", command.TrustedToolId);
            Assert.Equal(Path.GetTempPath(), command.WorkingDirectory);
            Assert.Equal(TimeSpan.FromSeconds(30), command.Timeout);
            Assert.True(token.CanBeCanceled);
            Assert.Equal(new[] { "infobase", "info", "--cluster=" + Physical.ToString("D"),
                "--infobase=" + scope.InfobaseId.ToString("D"), "--cluster-user=" + User,
                "--cluster-pwd=" + Password, "rac.example:1545" }, command.Arguments.Select(a => a.Value));
            Assert.NotEqual(scope.CanonicalClusterId, Physical);
            Assert.Equal(new[] { false, false, false, false, true, true, false },
                command.Arguments.Select(a => a.Sensitive));
            Assert.Equal(new[] { User, Password }, command.KnownSecrets);
            Assert.DoesNotContain(Password, command.ToString());
            Assert.DoesNotContain(User, command.Arguments[4].ToString());
            await Emit(callback, Valid(scope)[..12]);
            await Emit(callback, Valid(scope)[12..]);
            await Emit(callback, "", ProcessOutputStream.StandardError);
            return Success();
        });
        var result = await new InfobaseRegistrationObserver(runner, Options()).ObserveAsync(scope, Credentials());
        Assert.Same(scope, result.Scope);
        Assert.Equal(InfobaseRegistrationReadback.ProfileId, result.ProfileId);
        Assert.False(result.Executable);
        Assert.False(result.RestoreAllowed);
        Assert.False(result.FreshAuthority);
        Assert.True(result.RequiresIndependentVerification);
        Assert.Equal(1, runner.Calls);
        Assert.DoesNotContain(User, Credentials().ToString());
        Assert.DoesNotContain(Password, Credentials().ToString());
    }

    [Theory]
    [InlineData("cluster")]
    [InlineData("sql")]
    [InlineData("rac-map")]
    [InlineData("sql-map")]
    [InlineData("literal")]
    [InlineData("fence")]
    [InlineData("name")]
    [InlineData("operation")]
    public async Task ScopeDriftOrInvalidScopeFailsBeforeIo(string field)
    {
        var scope = Scope();
        scope = field switch
        {
            "cluster" => scope with { CanonicalClusterId = Guid.NewGuid() },
            "sql" => scope with { CanonicalSqlServerId = Guid.NewGuid() },
            "rac-map" => scope with { RacEndpointMappingId = Guid.NewGuid() },
            "sql-map" => scope with { SqlEndpointMappingId = Guid.NewGuid() },
            "literal" => scope with { ExpectedSqlServer = "SQL01\\INSTANCE" },
            "fence" => scope with { FencingToken = 0 },
            "name" => scope with { ExpectedInfobaseName = "wrong" },
            _ => scope with { OperationId = Guid.Empty }
        };
        var runner = new FakeRunner((_, _, _) => throw new Exception("must not run"));
        var error = await Assert.ThrowsAsync<InfobaseRegistrationObserverException>(() =>
            new InfobaseRegistrationObserver(runner, Options()).ObserveAsync(scope, Credentials()));
        Assert.Equal("InfobaseRegistrationObserverScopeMismatch", error.Code);
        Assert.Equal(0, runner.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad\nname")]
    public async Task RejectsBadCredentialsWithoutIo(string user)
    {
        var runner = new FakeRunner((_, _, _) => throw new Exception("must not run"));
        var error = await Assert.ThrowsAsync<InfobaseRegistrationObserverException>(() =>
            new InfobaseRegistrationObserver(runner, Options()).ObserveAsync(Scope(), new(user, Password)));
        Assert.Equal("InfobaseRegistrationObserverCredentialsInvalid", error.Code);
        Assert.Equal(0, runner.Calls);
    }

    [Theory]
    [InlineData(ProcessCompletion.TimedOut, 0, false, 123)]
    [InlineData(ProcessCompletion.Cancelled, 0, false, 123)]
    [InlineData(ProcessCompletion.Exited, 1, false, 123)]
    [InlineData(ProcessCompletion.Exited, null, false, 123)]
    [InlineData(ProcessCompletion.Exited, 0, true, 123)]
    [InlineData(ProcessCompletion.Exited, 0, false, null)]
    [InlineData(ProcessCompletion.Exited, 0, false, 0)]
    public async Task RejectsIncompleteUnsuccessfulAndPidlessProcess(ProcessCompletion completion, int? exit,
        bool truncated, int? pid)
    {
        var scope = Scope();
        await Unavailable(new FakeRunner(async (_, callback, _) =>
        {
            await Emit(callback, Valid(scope));
            return new(pid, exit, completion, truncated);
        }), scope);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("private-cluster-password")]
    public async Task AnyStderrRejectsWithoutLeaking(string text)
    {
        var scope = Scope();
        await Unavailable(new FakeRunner(async (_, callback, _) =>
        {
            await Emit(callback, Valid(scope));
            await Emit(callback, text, ProcessOutputStream.StandardError);
            return Success();
        }), scope);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsOverflowIncludingSwallowedCallbackFailure(bool swallow)
    {
        var scope = Scope();
        await Unavailable(new FakeRunner(async (_, callback, _) =>
        {
            await Emit(callback, Valid(scope));
            try { await Emit(callback, new string('x', InfobaseRegistrationReadback.MaxOutputCharacters + 1)); }
            catch when (swallow) { }
            return Success();
        }), scope);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullOrUnknownCallbackFailsEvenIfRunnerSwallows(bool nullOutput)
    {
        var scope = Scope();
        await Unavailable(new FakeRunner(async (_, callback, _) =>
        {
            await Emit(callback, Valid(scope));
            try
            {
                if (nullOutput) await callback(null!);
                else await callback(new((ProcessOutputStream)200, Password));
            }
            catch { /* adversarial runner swallows callback exception */ }
            return Success();
        }), scope);
    }

    [Fact]
    public async Task RunnerErrorAndCancellationCannotReturnEvidence()
    {
        await Unavailable(new FakeRunner((_, _, _) => throw new InvalidOperationException(Password)));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var never = new FakeRunner((_, _, _) => throw new Exception("must not run"));
        await Unavailable(never, cancellationToken: cancelled.Token);
        Assert.Equal(0, never.Calls);
        using var during = new CancellationTokenSource();
        var scope = Scope();
        await Unavailable(new FakeRunner(async (_, callback, _) =>
        {
            await Emit(callback, Valid(scope));
            during.Cancel();
            return Success();
        }), scope, during.Token);
    }

    [Fact]
    public async Task DeadlineIncludesRunnerAndRejectsLateSuccess()
    {
        var clock = new FakeTimeProvider();
        var scope = Scope();
        await Unavailable(new FakeRunner(async (_, callback, _) =>
        {
            await Emit(callback, Valid(scope));
            clock.Advance(TimeSpan.FromSeconds(30));
            return Success();
        }), scope, clock: clock);
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("throw-start")]
    [InlineData("throw-after-run")]
    [InlineData("expired-after-parse")]
    public async Task RejectsClockFailuresNegativeElapsedAndExpirationAfterParse(string mode)
    {
        var scope = Scope();
        await Unavailable(new FakeRunner(async (_, callback, _) =>
        {
            await Emit(callback, Valid(scope));
            return Success();
        }), scope, clock: new ScriptedTimeProvider(mode));
    }

    [Fact]
    public async Task LateCallbackIsRejectedWithoutClaimingItCanRetractReturnedResult()
    {
        var scope = Scope();
        Func<ProcessOutput, Task>? saved = null;
        var runner = new FakeRunner(async (_, callback, _) =>
        {
            saved = callback;
            await Emit(callback, Valid(scope));
            return Success();
        });
        var result = await new InfobaseRegistrationObserver(runner, Options()).ObserveAsync(scope, Credentials());
        Assert.Same(scope, result.Scope);
        Assert.Throws<InvalidOperationException>(() => { _ = saved!(new(ProcessOutputStream.StandardOutput, Password)); });
    }

    [Theory]
    [InlineData("bad-profile")]
    [InlineData("")]
    public void InvalidEnabledProfileHasFixedError(string profile)
    {
        var options = Options();
        var ex = Assert.Throws<InfobaseRegistrationObserverException>(() =>
            new InfobaseRegistrationObserver(new FakeRunner((_, _, _) => Task.FromResult(Success())), new()
            {
                Enabled = true, TrustedRacToolId = options.TrustedRacToolId, Endpoint = options.Endpoint,
                WorkingDirectory = options.WorkingDirectory, Timeout = options.Timeout,
                CanonicalClusterId = options.CanonicalClusterId, CanonicalSqlServerId = options.CanonicalSqlServerId,
                RacEndpointMappingId = options.RacEndpointMappingId, SqlEndpointMappingId = options.SqlEndpointMappingId,
                PhysicalClusterId = options.PhysicalClusterId, ExpectedSqlServer = options.ExpectedSqlServer,
                OutputProfileId = profile
            }));
        Assert.Equal("InfobaseRegistrationObserverInvalidOptions", ex.Code);
        Assert.Null(ex.InnerException);
    }

    private sealed class FakeRunner(Func<ProcessCommand, Func<ProcessOutput, Task>, CancellationToken,
        Task<ProcessResult>> implementation) : IProcessRunner
    {
        public int Calls { get; private set; }
        public Task<ProcessResult> RunAsync(ProcessCommand command, Func<ProcessOutput, Task>? onOutput = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return implementation(command, onOutput!, cancellationToken);
        }
    }

    private sealed class ScriptedTimeProvider(string mode) : TimeProvider
    {
        private int calls;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp()
        {
            var call = Interlocked.Increment(ref calls);
            if (mode == "throw-start" || (mode == "throw-after-run" && call >= 3))
                throw new InvalidOperationException(Password);
            if (mode == "negative" && call >= 3) return -1;
            if (mode == "expired-after-parse" && call >= 4) return TimeSpan.FromSeconds(30).Ticks;
            return 0;
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public void Advance(TimeSpan delta) => Interlocked.Add(ref ticks, delta.Ticks);
    }
}
