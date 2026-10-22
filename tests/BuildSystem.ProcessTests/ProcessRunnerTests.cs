using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using BuildSystem.Agent.Tools;
using Xunit;

namespace BuildSystem.ProcessTests;

public sealed class ProcessRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "process-runner-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _host = FindDotnetHost();
    private readonly string _fixture = Path.Combine(AppContext.BaseDirectory, "ProcessFixture", "BuildSystem.ProcessFixture.dll");

    public ProcessRunnerTests()
    {
        Directory.CreateDirectory(_root);
        Assert.True(File.Exists(_fixture), "Fixture must be copied, including its runtimeconfig, alongside test binaries: " + _fixture);
        Assert.True(File.Exists(Path.ChangeExtension(_fixture, ".runtimeconfig.json")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* cleanup is best-effort when a terminated child is still being reaped */ }
        catch (UnauthorizedAccessException) { }
    }

    private ProcessRunner Runner(int lineLimit = 4096, int outputLimit = 65536, IReadOnlyList<string>? inherited = null,
        IReadOnlyDictionary<string, string>? tools = null)
        => new(new ProcessRunnerOptions
        {
            TrustedTools = tools ?? new Dictionary<string, string> { ["fixture"] = _host },
            AllowedWorkspaceRoots = [_root],
            InheritedEnvironmentVariables = inherited ?? [],
            MaxLineCharacters = lineLimit,
            MaxOutputCharacters = outputLimit
        });

    private ProcessCommand Command(string mode, params string[] extra)
        => new("fixture", _root, [new(_fixture), new(mode), .. extra.Select(x => new ProcessArgument(x))], TimeSpan.FromSeconds(20));

    [Fact]
    public async Task StreamsUnicodeAndSpacedArgumentsWithRealExitCode()
    {
        var output = new ConcurrentBag<ProcessOutput>();
        var result = await Runner().RunAsync(Command("echo", "two words", "漢字🌍"), item => { output.Add(item); return Task.CompletedTask; });
        Assert.Equal(ProcessCompletion.Exited, result.Completion);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.ProcessId > 0);
        Assert.False(result.OutputTruncated);
        Assert.Contains(output, x => x.Stream == ProcessOutputStream.StandardOutput && x.Text.Contains("stdout:two words|漢字🌍", StringComparison.Ordinal));
        Assert.Contains(output, x => x.Stream == ProcessOutputStream.StandardError && x.Text.Contains("stderr:λ🌍", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NonZeroExitCodeIsReturnedNotThrown()
    {
        var result = await Runner().RunAsync(Command("exit", "23"));
        Assert.Equal(ProcessCompletion.Exited, result.Completion);
        Assert.Equal(23, result.ExitCode);
    }

    [Fact]
    public async Task TimeoutTerminatesProcess()
    {
        var command = Command("hang") with { Timeout = TimeSpan.FromMilliseconds(200) };
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await Runner().RunAsync(command, cancellationToken: watchdog.Token);
        Assert.Equal(ProcessCompletion.TimedOut, result.Completion);
        Assert.True(result.ProcessId > 0);
        await AssertEventuallyGone(result.ProcessId!.Value);
    }

    [Fact]
    public async Task CancellationTerminatesDescendantTree()
    {
        var pidFile = Path.Combine(_root, "child.pid");
        using var cancellation = new CancellationTokenSource();
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var task = Runner().RunAsync(Command("tree", pidFile), cancellationToken: cancellation.Token);
        var childPid = await AwaitChildPid(pidFile, task, watchdog.Token);
        cancellation.Cancel();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(12));
        Assert.Equal(ProcessCompletion.Cancelled, result.Completion);
        Assert.True(result.ProcessId > 0);
        await AssertEventuallyGone(result.ProcessId!.Value);
        await AssertEventuallyGone(childPid);
    }

    [Fact]
    public async Task CallbackExceptionIsSanitizedAndTerminatesProcess()
    {
        const string sentinel = "super-secret-callback-error";
        var exception = await Assert.ThrowsAsync<ProcessRunnerException>(() => Runner().RunAsync(
            Command("echo", "hello"), _ => throw new InvalidOperationException(sentinel)));
        Assert.Equal("callback_failed", exception.Code);
        Assert.DoesNotContain(sentinel, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallbackFailureTerminatesDescendantTree()
    {
        var pidFile = Path.Combine(_root, "callback-child.pid");
        var run = Runner().RunAsync(Command("tree", pidFile), item =>
            item.Text.Contains("tree-ready", StringComparison.Ordinal)
                ? throw new InvalidOperationException("private-callback-secret") : Task.CompletedTask);
        var error = await Assert.ThrowsAsync<ProcessRunnerException>(() => run.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal("callback_failed", error.Code);
        Assert.DoesNotContain("private-callback-secret", error.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(pidFile));
        await AssertEventuallyGone(int.Parse(File.ReadAllText(pidFile), CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task TimeoutBoundsSynchronouslyBlockingCallback()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            var run = Runner().RunAsync(Command("echo", "blocked") with { Timeout = TimeSpan.FromSeconds(1) }, _ =>
            {
                entered.Set();
                release.Wait();
                return Task.CompletedTask;
            });
            var result = await run.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.True(entered.IsSet);
            Assert.Equal(ProcessCompletion.TimedOut, result.Completion);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task TimeoutBoundsNoncooperativeAsyncCallback()
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var result = await Runner().RunAsync(Command("echo", "blocked") with { Timeout = TimeSpan.FromMilliseconds(500) },
                _ => blocked.Task).WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(ProcessCompletion.TimedOut, result.Completion);
        }
        finally { blocked.TrySetResult(); }
    }

    [Fact]
    public async Task OutputOrderingIsPreservedWithinEachStream()
    {
        var output = new ConcurrentQueue<ProcessOutput>();
        var result = await Runner().RunAsync(Command("ordered"), item => { output.Enqueue(item); return Task.CompletedTask; });
        Assert.Equal(ProcessCompletion.Exited, result.Completion);
        foreach (var (stream, prefix) in new[] { (ProcessOutputStream.StandardOutput, "out:"), (ProcessOutputStream.StandardError, "err:") })
        {
            var lines = string.Concat(output.Where(x => x.Stream == stream).Select(x => x.Text))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.TrimEnd('\r')).ToArray();
            Assert.Equal(Enumerable.Range(0, 20).Select(x => prefix + x), lines);
        }
    }

    [Fact]
    public async Task SymlinkWorkspaceIsRejected()
    {
        var target = Path.Combine(_root, "real");
        var link = Path.Combine(_root, "linked");
        Directory.CreateDirectory(target);
        try { Directory.CreateSymbolicLink(link, target); }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { return; }
        var error = await Assert.ThrowsAsync<ProcessRunnerException>(() => Runner().RunAsync(Command("echo") with { WorkingDirectory = link }));
        Assert.Equal("invalid_command", error.Code);
    }

    [Fact]
    public async Task FloodingBothStreamsStaysBoundedWithoutDeadlock()
    {
        var output = new ConcurrentBag<ProcessOutput>();
        var runner = Runner(lineLimit: 128, outputLimit: 2048);
        var result = await runner.RunAsync(Command("flood"), item => { output.Add(item); return Task.CompletedTask; })
            .WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(ProcessCompletion.Exited, result.Completion);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.OutputTruncated);
        Assert.All(output, item => Assert.True(item.Text.Length <= 128, "Output line exceeded configured limit"));
        Assert.True(output.Sum(x => x.Text.Length) <= 2048, "Output exceeded configured total limit");
    }

    [Theory]
    [InlineData("unique-secret-αβ-970314")]
    [InlineData("first-secret-line\nsecond-secret-line")]
    public async Task SecretIsRedactedAcrossWritesLinesAndStreams(string secret)
    {
        var output = new ConcurrentBag<ProcessOutput>();
        var command = Command("secret", secret) with { KnownSecrets = [secret] };
        var result = await Runner().RunAsync(command, item => { output.Add(item); return Task.CompletedTask; });
        Assert.Equal(ProcessCompletion.Exited, result.Completion);
        Assert.DoesNotContain(secret, string.Concat(output.Select(x => x.Text)), StringComparison.Ordinal);
        Assert.Contains(output, item => item.Stream == ProcessOutputStream.StandardOutput && item.Text.Contains("before-", StringComparison.Ordinal));
        Assert.Contains(output, item => item.Stream == ProcessOutputStream.StandardError && item.Text.Contains("error-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SensitiveArgumentIsAlsoRedactedFromOutput()
    {
        const string secret = "sensitive-argument-4532";
        var output = new ConcurrentBag<ProcessOutput>();
        var command = Command("secret", secret);
        command = command with { Arguments = [new(_fixture), new("secret"), new(secret, Sensitive: true)] };
        await Runner().RunAsync(command, item => { output.Add(item); return Task.CompletedTask; });
        Assert.DoesNotContain(secret, string.Join("\n", output.Select(x => x.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InheritedEnvironmentIsAllowlistedNotImplicit()
    {
        const string key = "TZ";
        var previous = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, "Etc/UTC");
        try
        {
            var output = new ConcurrentBag<ProcessOutput>();
            await Runner().RunAsync(Command("cwd-env", key), item => { output.Add(item); return Task.CompletedTask; });
            Assert.Contains(output, x => x.Text.Contains("env:<unset>", StringComparison.Ordinal));
            Assert.Contains(output, x => x.Text.Contains("cwd:" + _root, StringComparison.Ordinal));
            output.Clear();
            await Runner(inherited: [key]).RunAsync(Command("cwd-env", key), item => { output.Add(item); return Task.CompletedTask; });
            Assert.Contains(output, x => x.Text.Contains("env:Etc/UTC", StringComparison.Ordinal));
        }
        finally { Environment.SetEnvironmentVariable(key, previous); }
    }

    [Fact]
    public async Task InvalidToolWorkspaceAndTimeoutAreRejectedBeforeLaunch()
    {
        var runner = Runner();
        var outside = Path.GetTempPath();
        var rejected = new[]
        {
            Command("echo") with { TrustedToolId = "not-registered" },
            Command("echo") with { WorkingDirectory = outside },
            Command("echo") with { WorkingDirectory = Path.Combine(_root, "..") },
            Command("echo") with { WorkingDirectory = "." },
            Command("echo") with { Timeout = TimeSpan.Zero },
            Command("echo") with { Timeout = TimeSpan.FromSeconds(-1) },
            Command("echo") with { Timeout = TimeSpan.FromHours(25) }
        };
        foreach (var command in rejected)
        {
            var exception = await Assert.ThrowsAsync<ProcessRunnerException>(() => runner.RunAsync(command));
            Assert.Equal("invalid_command", exception.Code);
        }
    }

    [Fact]
    public async Task NonexistentExecutableHasSanitizedStartFailure()
    {
        var missing = Path.Combine(_root, "nonexistent-secret-executable");
        var error = await Assert.ThrowsAsync<ProcessRunnerException>(() =>
            Runner(tools: new Dictionary<string, string> { ["missing"] = missing })
                .RunAsync(Command("echo") with { TrustedToolId = "missing" }));
        Assert.Equal("start_failed", error.Code);
        Assert.DoesNotContain(missing, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ShellExecutableCannotBeTrusted()
    {
        var shell = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")
            : "/bin/sh";
        Assert.Throws<ArgumentException>(() => Runner(tools: new Dictionary<string, string> { ["shell"] = shell }));
    }

    [Theory]
    [InlineData("PATH")]
    [InlineData("DOTNET_STARTUP_HOOKS")]
    [InlineData("LD_PRELOAD")]
    [InlineData("COMPlus_EnableDiagnostics")]
    public void UnsafeInheritedEnvironmentPolicyIsRejected(string variable)
        => Assert.Throws<ArgumentException>(() => Runner(inherited: [variable]));

    private static async Task<int> AwaitChildPid(string path, Task<ProcessResult> task, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (File.Exists(path) && int.TryParse(File.ReadAllText(path), CultureInfo.InvariantCulture, out var pid))
                return pid;
            if (task.IsCompleted)
                throw new Xunit.Sdk.XunitException("Parent completed before creating child pid file: " + task.Exception);
            await Task.Delay(20, ct);
        }
        throw new OperationCanceledException(ct);
    }

    private static async Task AssertEventuallyGone(int pid)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                    return;
                if (OperatingSystem.IsLinux())
                {
                    var stat = $"/proc/{pid}/stat";
                    if (File.Exists(stat) && File.ReadAllText(stat).Split(' ')[2] == "Z")
                        return;
                }
            }
            catch (ArgumentException) { return; }
            await Task.Delay(30, timeout.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        throw new Xunit.Sdk.XunitException("Process survived termination: " + pid);
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
