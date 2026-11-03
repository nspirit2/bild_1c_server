using System.Collections.Concurrent;
using BuildSystem.Agent.Execution;
using BuildSystem.Agent.Tools;
using Xunit;

namespace BuildSystem.ProcessTests;

public sealed class InfobaseInventoryObserverProcessTests : IDisposable
{
    private const string ToolId = "fixture-inventory-rac";
    private const string User = "private inventory user λ";
    private const string Password = "private-inventory-password-🌍";
    private const string ExpectedName = "bs_test_11111111111111111111111111111111";
    private const string InfobaseId = "22222222-2222-2222-2222-222222222222";
    private const int LineLimit = 3;
    private static readonly Guid EndpointMappingId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private readonly string root = Path.Combine(Path.GetTempPath(), "inventory-process-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string fixture = Path.Combine(AppContext.BaseDirectory, "ProcessFixture", "BuildSystem.ProcessFixture.dll");

    public InfobaseInventoryObserverProcessTests()
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
    public async Task RealFragmentedInventoryProducesPresentForExactReadOnlyCommand()
    {
        var scope = Scope();
        var bridge = Bridge();
        var readback = await Observer(bridge, "inventory-ok").ObserveAsync(scope, Credentials());

        Assert.Same(scope, readback.Scope);
        Assert.Equal(InfobaseInventoryObservation.Present, readback.Observation);
        var entry = Assert.Single(readback.Entries);
        Assert.Equal(Guid.Parse(InfobaseId), entry.InfobaseId);
        Assert.Equal(ExpectedName, entry.Name);
        AssertSuccessfulExit(bridge);
        var command = Assert.IsType<ProcessCommand>(bridge.Command);
        Assert.Equal(ToolId, command.TrustedToolId);
        Assert.Equal(root, command.WorkingDirectory);
        Assert.Equal(TimeSpan.FromSeconds(10), command.Timeout);
        Assert.Equal(new[] { "infobase", "summary", "list", "--cluster=" + scope.ClusterId.ToString("D"),
            "--cluster-user=" + User, "--cluster-pwd=" + Password, "inventory-ok" },
            command.Arguments.Select(a => a.Value));
        Assert.Equal(new[] { false, false, false, false, true, true, false }, command.Arguments.Select(a => a.Sensitive));
        Assert.Equal(new[] { User, Password }, command.KnownSecrets);
        Assert.DoesNotContain(bridge.Output, o => o.Stream == ProcessOutputStream.StandardError);
        Assert.Equal(CompleteResponse(), Stdout(bridge));
        Assert.True(bridge.Output.Count > 20, "Actual runner callbacks must fragment field names and values.");
        Assert.All(bridge.Output, output => Assert.InRange(output.Text.Length, 1, LineLimit));
        Assert.DoesNotContain("irrelevant", readback.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("irrelevant", entry.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealEmptySuccessfulInventoryProducesAbsentOnly()
    {
        var scope = Scope();
        var bridge = Bridge();
        var readback = await Observer(bridge, "inventory-empty").ObserveAsync(scope, Credentials());

        AssertSuccessfulExit(bridge);
        Assert.Same(scope, readback.Scope);
        Assert.Equal(InfobaseInventoryObservation.Absent, readback.Observation);
        Assert.Empty(readback.Entries);
        Assert.Empty(bridge.Output);
    }

    [Fact]
    public async Task FixtureRejectsWrongPositionalCommandWithoutSyntheticBridgeArguments()
    {
        var bridge = Bridge();
        var command = new ProcessCommand(ToolId, root,
            [new("infobase"), new("summary"), new("--cluster=" + Scope().ClusterId.ToString("D")),
                new("list"), new("--cluster-user=" + User, Sensitive: true),
                new("--cluster-pwd=" + Password, Sensitive: true), new("inventory-ok")],
            TimeSpan.FromSeconds(10), [User, Password]);
        var result = await bridge.RunAsync(command);

        Assert.Same(command, bridge.Command);
        Assert.Equal(ProcessCompletion.Exited, result.Completion);
        Assert.Equal(64, result.ExitCode);
        Assert.Empty(bridge.Output);
    }

    [Fact]
    public async Task RealTimeoutRejectsEvenCompleteValidInventory()
    {
        var bridge = Bridge();
        await Reject(Observer(bridge, "inventory-hang", TimeSpan.FromSeconds(2)), Scope(), Credentials(),
            "InfobaseInventoryReadbackUnavailable");

        Assert.NotNull(bridge.Result);
        Assert.Equal(ProcessCompletion.TimedOut, bridge.Result.Completion);
        Assert.True(bridge.Result.ProcessId > 0);
        Assert.Equal(CompleteResponse(), Stdout(bridge));
    }

    [Fact]
    public async Task RealStderrFailsClosedAndCredentialsAreMaskedAcrossFragments()
    {
        var bridge = Bridge();
        await Reject(Observer(bridge, "inventory-stderr"), Scope(), Credentials(), "InfobaseInventoryReadbackUnavailable");

        AssertSuccessfulExit(bridge);
        var stderr = string.Concat(bridge.Output.Where(o => o.Stream == ProcessOutputStream.StandardError).Select(o => o.Text));
        Assert.Contains("private inventory diagnostic:", stderr);
        Assert.Contains("[REDACTED]", stderr);
        Assert.DoesNotContain(User, stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, stderr, StringComparison.Ordinal);
        Assert.All(bridge.Output, output => Assert.InRange(output.Text.Length, 1, LineLimit));
    }

    [Fact]
    public async Task RealRunnerOutputLimitAndTruncationCannotProduceAbsence()
    {
        var bridge = Bridge(outputLimit: 32);
        await Reject(Observer(bridge, "inventory-ok"), Scope(), Credentials(), "InfobaseInventoryReadbackUnavailable");

        Assert.NotNull(bridge.Result);
        Assert.Equal(ProcessCompletion.Exited, bridge.Result.Completion);
        Assert.Equal(0, bridge.Result.ExitCode);
        Assert.True(bridge.Result.OutputTruncated);
        Assert.Equal(32, bridge.Output.Sum(o => o.Text.Length));
    }

    [Fact]
    public async Task RealMalformedAndCaseAliasInventoriesFailClosed()
    {
        foreach (var (endpoint, code) in new[]
        {
            ("inventory-malformed", "InvalidInfobaseInventoryReadback"),
            ("inventory-casealias", "AmbiguousInfobaseInventoryTarget")
        })
        {
            var bridge = Bridge();
            await Reject(Observer(bridge, endpoint), Scope(), Credentials(), code);
            AssertSuccessfulExit(bridge);
        }
    }

    [Theory]
    [InlineData(false, ExpectedName)]
    [InlineData(true, InfobaseId)]
    [InlineData(false, "infobase")]
    public async Task CredentialCollisionsWithNameGuidOrFieldCannotTurnPresentIntoAbsent(bool password, string secret)
    {
        var credentials = password ? new InfobaseInventoryObserverCredentials(User, secret)
            : new InfobaseInventoryObserverCredentials(secret, Password);
        var bridge = Bridge();
        var error = await Assert.ThrowsAsync<InfobaseInventoryReadbackException>(() =>
            Observer(bridge, "inventory-ok").ObserveAsync(Scope(), credentials).WaitAsync(TimeSpan.FromSeconds(15)));

        Assert.Equal("InfobaseInventoryReadbackUnavailable", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(User, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Password, error.ToString(), StringComparison.Ordinal);
        Assert.Equal($"BuildSystem.Agent.Execution.InfobaseInventoryReadbackException: {error.Code}",
            error.ToString().Split('\n')[0].TrimEnd('\r'));
        // Either reject before launch or reject the redacted readback; never report Absent.
        if (bridge.Result is not null)
        {
            AssertSuccessfulExit(bridge);
            Assert.Contains("[REDACTED]", Stdout(bridge));
            Assert.DoesNotContain(secret, Stdout(bridge), StringComparison.Ordinal);
        }
    }

    private FixtureBridge Bridge(int outputLimit = 65536) => new(fixture, new ProcessRunner(new ProcessRunnerOptions
    {
        TrustedTools = new Dictionary<string, string> { [ToolId] = FindDotnetHost() },
        AllowedWorkspaceRoots = [root],
        InheritedEnvironmentVariables = [],
        MaxLineCharacters = LineLimit,
        MaxOutputCharacters = outputLimit
    }));

    private InfobaseInventoryObserver Observer(IProcessRunner runner, string endpoint, TimeSpan? timeout = null) => new(runner, new()
    {
        TrustedRacToolId = ToolId,
        Endpoint = endpoint,
        EndpointMappingId = EndpointMappingId,
        WorkingDirectory = root,
        Timeout = timeout ?? TimeSpan.FromSeconds(10)
    });

    private static InfobaseInventoryReadbackScope Scope() => new(Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(),
        EndpointMappingId, Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.NewGuid(), ExpectedName);
    private static InfobaseInventoryObserverCredentials Credentials() => new(User, Password);
    private static string CompleteResponse() => $"infobase : {InfobaseId}\nname : {ExpectedName}\ndescr : irrelevant λ🌍 description\n";
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

    private static async Task Reject(InfobaseInventoryObserver observer, InfobaseInventoryReadbackScope scope,
        InfobaseInventoryObserverCredentials credentials, string code)
    {
        var error = await Assert.ThrowsAsync<InfobaseInventoryReadbackException>(() =>
            observer.ObserveAsync(scope, credentials).WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal(code, error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(User, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Password, error.ToString(), StringComparison.Ordinal);
        Assert.Equal($"BuildSystem.Agent.Execution.InfobaseInventoryReadbackException: {code}",
            error.ToString().Split('\n')[0].TrimEnd('\r'));
    }

    // Only prepend the fixture DLL for the explicitly trusted dotnet host. Do not
    // change arguments, manufacture results, or interpret output on the adapter's behalf.
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
