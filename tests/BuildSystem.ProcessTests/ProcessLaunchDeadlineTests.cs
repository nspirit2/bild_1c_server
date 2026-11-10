using System.Collections.Concurrent;
using System.Text.Json;
using BuildSystem.Agent.Tools;
using Xunit;

namespace BuildSystem.ProcessTests;

public sealed class ProcessLaunchDeadlineTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "process-launch-deadline-" + Guid.NewGuid().ToString("N"));
    private readonly string fixture = Path.Combine(AppContext.BaseDirectory, "ProcessFixture", "BuildSystem.ProcessFixture.dll");

    public ProcessLaunchDeadlineTests()
    {
        Directory.CreateDirectory(root);
        Assert.True(File.Exists(fixture));
        Assert.True(File.Exists(Path.ChangeExtension(fixture, ".runtimeconfig.json")));
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* Child cleanup may still be finishing. */ }
        catch (UnauthorizedAccessException) { }
    }

    private ProcessRunner Runner(string? executable = null) => new(new ProcessRunnerOptions
    {
        TrustedTools = new Dictionary<string, string> { ["fixture"] = executable ?? DotnetHost() },
        AllowedWorkspaceRoots = [root]
    });

    private ProcessCommand Command(string mode, params string[] arguments) => new("fixture", root,
        [new(fixture), new(mode), .. arguments.Select(a => new ProcessArgument(a))], TimeSpan.FromSeconds(3));

    private static ProcessLaunchDeadline Deadline(ManualClock clock, TimeSpan? budget = null)
        => new(clock, 1_000, budget ?? TimeSpan.FromSeconds(1));

    [Fact]
    public void ConstructorRejectsNullClockWithoutDisclosingDetails()
    {
        var error = Assert.Throws<ProcessRunnerException>(() => new ProcessLaunchDeadline(null!, 1_000, TimeSpan.FromSeconds(1)));
        Assert.Equal("invalid_launch_deadline", error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(300_001)]
    public void ConstructorRejectsNonpositiveOrOverlongBudget(long milliseconds)
    {
        var error = Assert.Throws<ProcessRunnerException>(() => new ProcessLaunchDeadline(
            new ManualClock(), 1_000, TimeSpan.FromMilliseconds(milliseconds)));
        Assert.Equal("invalid_launch_deadline", error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ConstructorRejectsNonpositiveTimestampFrequency(long frequency)
    {
        var error = Assert.Throws<ProcessRunnerException>(() => new ProcessLaunchDeadline(
            new ManualClock(frequency), 1_000, TimeSpan.FromSeconds(1)));
        Assert.Equal("invalid_launch_deadline", error.Code);
    }

    [Fact]
    public void BoundaryIsExclusiveAndBudgetNeverRenews()
    {
        var clock = new ManualClock();
        Deadline(clock, TimeSpan.FromTicks(1)).DemandNotExpired();
        Deadline(clock, TimeSpan.FromMinutes(5)).DemandNotExpired();
        var deadline = Deadline(clock);
        clock.Timestamp = 1_000 + TimeSpan.TicksPerSecond - 1;
        deadline.DemandNotExpired();
        clock.Timestamp++;
        Assert.Equal("launch_deadline_expired", Assert.Throws<ProcessRunnerException>(deadline.DemandNotExpired).Code);
        clock.Timestamp += TimeSpan.TicksPerSecond;
        Assert.Equal("launch_deadline_expired", Assert.Throws<ProcessRunnerException>(deadline.DemandNotExpired).Code);
    }

    [Fact]
    public void NegativeElapsedTimeFailsClosed()
    {
        var clock = new ManualClock { Timestamp = 999 };
        Assert.Equal("launch_deadline_expired", Assert.Throws<ProcessRunnerException>(Deadline(clock).DemandNotExpired).Code);
    }

    [Fact]
    public void ClockFailureIsSanitized()
    {
        const string secret = "private-clock-diagnostic-92471";
        var clock = new ManualClock { Failure = new InvalidOperationException(secret) };
        var error = Assert.Throws<ProcessRunnerException>(Deadline(clock).DemandNotExpired);
        Assert.Equal("launch_deadline_expired", error.Code);
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticAndJsonCannotDiscloseOrRehydrateDeadlineIncludingNull()
    {
        var deadline = Deadline(new ManualClock());
        Assert.DoesNotContain("1000", deadline.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("1000", (Command("echo") with { LaunchDeadline = deadline }).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("LaunchDeadline", JsonSerializer.Serialize(Command("echo") with { LaunchDeadline = null }), StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(Command("echo") with { LaunchDeadline = deadline }));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(deadline));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize<ProcessLaunchDeadline?>(null));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProcessLaunchDeadline>("null"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProcessLaunchDeadline>("{}"));
    }

    [Fact]
    public async Task ExpiredDeadlinePreventsManagedFixtureFromWritingMarker()
    {
        var marker = Path.Combine(root, "tree-child.pid");
        var clock = new ManualClock { Timestamp = 1_000 + TimeSpan.TicksPerSecond };
        var command = Command("tree", marker) with { Timeout = TimeSpan.FromSeconds(1), LaunchDeadline = Deadline(clock) };
        var error = await Assert.ThrowsAsync<ProcessRunnerException>(() => Runner().RunAsync(command).WaitAsync(TimeSpan.FromSeconds(8)));
        Assert.Equal("launch_deadline_expired", error.Code);
        Assert.False(File.Exists(marker), "Expired launch must not start the fixture or its child");
    }

    [Fact]
    public async Task ExpiredDeadlineBeatsStartFailureForMissingTrustedExecutable()
    {
        var missing = Path.Combine(root, "missing-dotnet-executable");
        var clock = new ManualClock { Timestamp = 1_000 + TimeSpan.TicksPerSecond };
        var error = await Assert.ThrowsAsync<ProcessRunnerException>(() => Runner(missing).RunAsync(
            Command("echo") with { LaunchDeadline = Deadline(clock) }));
        Assert.Equal("launch_deadline_expired", error.Code);
    }

    [Fact]
    public async Task OptionalNullAndLiveDeadlineBothLaunchNormally()
    {
        var output = new ConcurrentBag<ProcessOutput>();
        var runner = Runner();
        var without = await runner.RunAsync(Command("echo", "optional-null") with { LaunchDeadline = null },
            line => { output.Add(line); return Task.CompletedTask; });
        Assert.Equal(ProcessCompletion.Exited, without.Completion);
        Assert.Equal(0, without.ExitCode);
        Assert.Contains(output, line => line.Text.Contains("stdout:optional-null", StringComparison.Ordinal));
        output.Clear();
        var with = await runner.RunAsync(Command("echo", "live") with { LaunchDeadline = Deadline(new ManualClock()) },
            line => { output.Add(line); return Task.CompletedTask; });
        Assert.Equal(ProcessCompletion.Exited, with.Completion);
        Assert.Equal(0, with.ExitCode);
        Assert.Contains(output, line => line.Text.Contains("stdout:live", StringComparison.Ordinal));
    }

    private sealed class ManualClock(long frequency = TimeSpan.TicksPerSecond) : TimeProvider
    {
        public long Timestamp { get; set; } = 1_000;
        public Exception? Failure { get; set; }
        public override long TimestampFrequency => frequency;
        public override long GetTimestamp() => Failure is { } failure ? throw failure : Timestamp;
    }

    private static string DotnetHost()
    {
        var explicitHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (explicitHost is not null && File.Exists(explicitHost)) return Path.GetFullPath(explicitHost);
        var current = Environment.ProcessPath;
        if (current is not null && Path.GetFileNameWithoutExtension(current).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return current;
        var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        foreach (var directory in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(typeof(object).Assembly.Location) })
            for (var parent = new DirectoryInfo(directory!); parent is not null; parent = parent.Parent)
            {
                var candidate = Path.Combine(parent.FullName, name);
                if (File.Exists(candidate)) return candidate;
            }
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (dotnetRoot is not null && File.Exists(Path.Combine(dotnetRoot, name)))
            return Path.Combine(dotnetRoot, name);
        throw new InvalidOperationException("Could not locate dotnet host for process tests");
    }
}
