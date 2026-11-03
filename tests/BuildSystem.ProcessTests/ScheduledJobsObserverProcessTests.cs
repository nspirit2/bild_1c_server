using System.Collections.Concurrent;
using BuildSystem.Agent.Execution;
using BuildSystem.Agent.Tools;
using Xunit;

namespace BuildSystem.ProcessTests;

public sealed class ScheduledJobsObserverProcessTests : IDisposable
{
    private const string ToolId = "fixture-rac";
    private const string User = "private cluster user λ";
    private const string Password = "private-cluster-password-🌍";
    private const int LineLimit = 3;
    private readonly string root = Path.Combine(Path.GetTempPath(), "scheduled-jobs-process-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string fixture = Path.Combine(AppContext.BaseDirectory, "ProcessFixture", "BuildSystem.ProcessFixture.dll");

    public ScheduledJobsObserverProcessTests()
    {
        Directory.CreateDirectory(root);
        Assert.True(File.Exists(fixture), "Fixture must be copied alongside test binaries: " + fixture);
        Assert.True(File.Exists(Path.ChangeExtension(fixture, ".runtimeconfig.json")));
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* A terminated fixture may still be getting reaped. */ }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task RealFragmentedOutputProducesReadbackForExactObserverCommand()
    {
        var scope = Scope();
        var bridge = Bridge();
        var readback = await Observer(bridge, "fixture-ok").ObserveAsync(scope, Credentials());

        Assert.Same(scope, readback.Scope);
        AssertSuccessfulExit(bridge);
        var command = Assert.IsType<ProcessCommand>(bridge.Command);
        Assert.Equal(ToolId, command.TrustedToolId);
        Assert.Equal(root, command.WorkingDirectory);
        Assert.Equal(TimeSpan.FromSeconds(10), command.Timeout);
        Assert.Equal(new[] { "infobase", "info", "--cluster=" + scope.ClusterId.ToString("D"),
            "--infobase=" + scope.InfobaseId.ToString("D"), "--cluster-user=" + User,
            "--cluster-pwd=" + Password, "fixture-ok" }, command.Arguments.Select(a => a.Value));
        Assert.Equal(new[] { false, false, false, false, true, true, false }, command.Arguments.Select(a => a.Sensitive));
        Assert.Equal(new[] { User, Password }, command.KnownSecrets);
        Assert.DoesNotContain(bridge.Output, o => o.Stream == ProcessOutputStream.StandardError);
        Assert.Equal($"infobase : {scope.InfobaseId:D}\nscheduled-jobs-deny : on\n", Stdout(bridge));
        Assert.True(bridge.Output.Count > 10, "Response must reach the observer in real runner fragments.");
        Assert.All(bridge.Output, output => Assert.InRange(output.Text.Length, 1, LineLimit));
    }

    [Fact]
    public async Task RealStderrFailsClosedAndCredentialsAreRedactedAcrossFragments()
    {
        var bridge = Bridge();
        await Reject(Observer(bridge, "fixture-stderr"), Scope(), Credentials(), "ScheduledJobsReadbackUnavailable");

        AssertSuccessfulExit(bridge);
        var stderr = string.Concat(bridge.Output.Where(o => o.Stream == ProcessOutputStream.StandardError).Select(o => o.Text));
        Assert.Contains("private diagnostic:", stderr);
        Assert.Contains("[REDACTED]", stderr);
        Assert.DoesNotContain(User, stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, stderr, StringComparison.Ordinal);
        Assert.All(bridge.Output, output => Assert.InRange(output.Text.Length, 1, LineLimit));
    }

    [Fact]
    public async Task RealTimeoutRejectsEvenACompleteBlockedResponse()
    {
        var scope = Scope();
        var bridge = Bridge();
        await Reject(Observer(bridge, "fixture-hang", TimeSpan.FromSeconds(2)), scope, Credentials(),
            "ScheduledJobsReadbackUnavailable");

        Assert.NotNull(bridge.Result);
        Assert.Equal(ProcessCompletion.TimedOut, bridge.Result.Completion);
        Assert.True(bridge.Result.ProcessId > 0);
        Assert.Equal($"infobase : {scope.InfobaseId:D}\nscheduled-jobs-deny : on\n", Stdout(bridge));
    }

    [Fact]
    public async Task RealOffResponseCannotProduceBlockedReadback()
    {
        var bridge = Bridge();
        await Reject(Observer(bridge, "fixture-off"), Scope(), Credentials(), "ScheduledJobsNotBlocked");
        AssertSuccessfulExit(bridge);
        Assert.Contains("scheduled-jobs-deny : off\n", Stdout(bridge));
    }

    [Fact]
    public async Task RealRunnerOutputTruncationFailsClosed()
    {
        var bridge = Bridge(outputLimit: 32);
        await Reject(Observer(bridge, "fixture-ok"), Scope(), Credentials(), "ScheduledJobsReadbackUnavailable");

        Assert.NotNull(bridge.Result);
        Assert.Equal(ProcessCompletion.Exited, bridge.Result.Completion);
        Assert.Equal(0, bridge.Result.ExitCode);
        Assert.True(bridge.Result.OutputTruncated);
        Assert.Equal(32, bridge.Output.Sum(o => o.Text.Length));
    }

    [Theory]
    [InlineData(false, "infobase", "InvalidScheduledJobsReadback")]
    [InlineData(true, "scheduled-jobs-deny", "InvalidScheduledJobsReadback")]
    [InlineData(false, "on", "ScheduledJobsNotBlocked")]
    [InlineData(true, "on", "ScheduledJobsNotBlocked")]
    [InlineData(false, "id", "ScheduledJobsTargetMismatch")]
    [InlineData(true, "id", "ScheduledJobsTargetMismatch")]
    public async Task CredentialsCollidingWithResponseFieldsFailClosed(bool password, string collision, string code)
    {
        var scope = Scope();
        var secret = collision == "id" ? scope.InfobaseId.ToString("D") : collision;
        var credentials = password ? new ScheduledJobsObserverCredentials(User, secret)
            : new ScheduledJobsObserverCredentials(secret, Password);
        var bridge = Bridge();
        await Reject(Observer(bridge, "fixture-ok"), scope, credentials, code);

        AssertSuccessfulExit(bridge);
        Assert.Contains("[REDACTED]", Stdout(bridge));
        Assert.DoesNotContain(secret, Stdout(bridge), StringComparison.Ordinal);
    }

    private FixtureBridge Bridge(int outputLimit = 65536) => new(fixture, new ProcessRunner(new ProcessRunnerOptions
    {
        TrustedTools = new Dictionary<string, string> { [ToolId] = FindDotnetHost() },
        AllowedWorkspaceRoots = [root],
        InheritedEnvironmentVariables = [],
        MaxLineCharacters = LineLimit,
        MaxOutputCharacters = outputLimit
    }));

    private ScheduledJobsObserver Observer(IProcessRunner runner, string endpoint, TimeSpan? timeout = null) => new(runner, new()
    {
        TrustedRacToolId = ToolId,
        Endpoint = endpoint,
        WorkingDirectory = root,
        Timeout = timeout ?? TimeSpan.FromSeconds(10)
    });

    private static ScheduledJobsReadbackScope Scope() => new(Guid.NewGuid(), 1, Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ScheduledJobsCheckpoint.BeforeRestore);

    private static ScheduledJobsObserverCredentials Credentials() => new(User, Password);
    private static string Stdout(FixtureBridge bridge) => string.Concat(bridge.Output
        .Where(o => o.Stream == ProcessOutputStream.StandardOutput).Select(o => o.Text));

    private static void AssertSuccessfulExit(FixtureBridge bridge)
    {
        Assert.NotNull(bridge.Result);
        Assert.Equal(ProcessCompletion.Exited, bridge.Result.Completion);
        Assert.Equal(0, bridge.Result.ExitCode);
        Assert.True(bridge.Result.ProcessId > 0);
        Assert.False(bridge.Result.OutputTruncated);
    }

    private static async Task Reject(ScheduledJobsObserver observer, ScheduledJobsReadbackScope scope,
        ScheduledJobsObserverCredentials credentials, string code)
    {
        var error = await Assert.ThrowsAsync<ScheduledJobsReadbackException>(() =>
            observer.ObserveAsync(scope, credentials).WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal(code, error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(User, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Password, error.ToString(), StringComparison.Ordinal);
        // Short collision secrets can occur in the fixed diagnostic code itself;
        // requiring the exact code and no inner exception excludes reflected output.
        Assert.Equal($"BuildSystem.Agent.Execution.ScheduledJobsReadbackException: {code}",
            error.ToString().Split('\n')[0].TrimEnd('\r'));
    }

    // No fake process outcome or synthetic RAC arguments: the only command change
    // is the fixture DLL prepended for the explicitly trusted dotnet host.
    private sealed class FixtureBridge(string fixture, ProcessRunner runner) : IProcessRunner
    {
        public ProcessCommand? Command { get; private set; }
        public ProcessResult? Result { get; private set; }
        public ConcurrentQueue<ProcessOutput> Output { get; } = new();

        public async Task<ProcessResult> RunAsync(ProcessCommand command, Func<ProcessOutput, Task>? onOutput = null,
            CancellationToken cancellationToken = default)
        {
            Command = command;
            Result = await runner.RunAsync(command with { Arguments = [new(fixture), .. command.Arguments] }, async output =>
            {
                Output.Enqueue(output);
                if (onOutput is not null) await onOutput(output);
            }, cancellationToken);
            return Result;
        }
    }

    private static string FindDotnetHost()
    {
        var explicitHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (explicitHost is not null && File.Exists(explicitHost)) return Path.GetFullPath(explicitHost);
        var processPath = Environment.ProcessPath;
        if (processPath is not null && Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return processPath;
        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        foreach (var directory in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(typeof(object).Assembly.Location) })
        {
            for (var parent = new DirectoryInfo(directory!); parent is not null; parent = parent.Parent)
            {
                var candidate = Path.Combine(parent.FullName, executable);
                if (File.Exists(candidate)) return candidate;
            }
        }
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (dotnetRoot is not null && File.Exists(Path.Combine(dotnetRoot, executable)))
            return Path.Combine(dotnetRoot, executable);
        throw new InvalidOperationException("Could not locate dotnet host for process tests");
    }
}
