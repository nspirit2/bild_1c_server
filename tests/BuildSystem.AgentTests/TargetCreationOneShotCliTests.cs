using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace BuildSystem.AgentTests;

/// <summary>Only disabled or rejected invocations: never provide executable target configuration.</summary>
[Collection("Agent CLI subprocesses")]
public sealed class TargetCreationOneShotCliTests : IDisposable
{
    private const string Permit = "11111111-1111-1111-1111-111111111111";
    private const string Enabled = "Agent__TargetCreationOneShot__Enabled";
    private readonly string root = Path.Combine(Path.GetTempPath(), "one-shot-cli-" + Guid.NewGuid().ToString("N"));
    private readonly string agent = Path.Combine(AppContext.BaseDirectory, "AgentFixture", "BuildSystem.Agent.dll");

    public TargetCreationOneShotCliTests()
    {
        Directory.CreateDirectory(root);
        Assert.True(File.Exists(agent), "Agent must be copied alongside test binaries: " + agent);
        Assert.True(File.Exists(Path.ChangeExtension(agent, ".runtimeconfig.json")));
    }

    public void Dispose()
    {
        // root is the exact private directory created by this instance, never configuration input.
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public async Task KnownPermitDefaultsDisabledBeforeWorkerOrTargetInputs(string? enabled)
    {
        var result = await RunAgent(["run-target-creation-once", Permit], enabled);
        AssertReceipt(result, 3, "TargetCreationOneShotDisabled");
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public async Task ExtraArgumentRejectsBeforeMalformedConfiguration()
    {
        var result = await RunAgent(["run-target-creation-once", Permit, "Agent:TargetCreationOneShot:Enabled=true"], "not-a-boolean");
        AssertRejected(result);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("11111111111111111111111111111111")]
    public async Task MalformedPermitRejectsBeforeMalformedConfiguration(string permit)
    {
        AssertRejected(await RunAgent(["run-target-creation-once", permit], "not-a-boolean"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public async Task MalformedEnabledReturnsOnlyRedactedUnavailableReceipt()
    {
        var result = await RunAgent(["run-target-creation-once", Permit], "private-invalid-enabled-value");
        AssertReceipt(result, 5, "TargetCreationOneShotUnavailable");
        Assert.DoesNotContain("private-invalid-enabled-value", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(root, result.Stdout, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public async Task HostConfigurationFailureReturnsOnlyRedactedUnavailableReceipt()
    {
        // Exercise the constructor catch without enabling execution or providing target inputs.
        await File.WriteAllTextAsync(Path.Combine(root, "appsettings.json"), "{ private-invalid-json");
        var result = await RunAgent(["run-target-creation-once", Permit], "false");
        AssertReceipt(result, 5, "TargetCreationOneShotUnavailable");
        Assert.DoesNotContain(root, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("private-invalid-json", result.Stdout, StringComparison.Ordinal);
    }

    private static void AssertRejected(Result result)
    {
        Assert.Equal(64, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal("UnsupportedAgentCommand" + Environment.NewLine, result.Stderr);
    }

    private static void AssertReceipt(Result result, int code, string status)
    {
        Assert.Equal(code, result.ExitCode);
        Assert.Empty(result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        var receipt = document.RootElement;
        Assert.Equal(7, receipt.EnumerateObject().Count());
        Assert.Equal(status, receipt.GetProperty("status").GetString());
        Assert.False(receipt.GetProperty("executable").GetBoolean());
        Assert.False(receipt.GetProperty("freshAuthority").GetBoolean());
        Assert.True(receipt.GetProperty("scheduledJobsMustRemainBlocked").GetBoolean());
        Assert.False(receipt.GetProperty("retryAllowed").GetBoolean());
        Assert.False(receipt.GetProperty("physicalInvocationAttempted").GetBoolean());
        Assert.False(receipt.GetProperty("creationAcknowledged").GetBoolean());
        Assert.Equal(result.Stdout.TrimEnd('\r', '\n') + Environment.NewLine, result.Stdout);
    }

    private async Task<Result> RunAgent(string[] arguments, string? enabled)
    {
        var start = new ProcessStartInfo(FindDotnetHost())
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(agent);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // No inherited Agent, host-content-root, or environment-specific configuration.
        start.Environment.Clear();
        foreach (var key in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP", "HOME", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_X86" })
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        if (enabled is not null) start.Environment[Enabled] = enabled;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Agent process did not start.");
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var stdout = Capture(process.StandardOutput, timeout.Token);
        var stderr = Capture(process.StandardError, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return new(process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            // Observe both readers even after timeout/output-bound failure; never leave a child behind.
            try { await Task.WhenAll(stdout, stderr); }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException) { }
        }
    }

    private static async Task<string> Capture(StreamReader reader, CancellationToken ct)
    {
        var output = new StringBuilder();
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            if (output.Length + count > 8192) throw new InvalidOperationException("Agent output exceeded test bound.");
            output.Append(buffer, 0, count);
        }
        return output.ToString();
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
            for (var parent = new DirectoryInfo(directory!); parent is not null; parent = parent.Parent)
            {
                var candidate = Path.Combine(parent.FullName, executable);
                if (File.Exists(candidate)) return candidate;
            }
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (dotnetRoot is not null && File.Exists(Path.Combine(dotnetRoot, executable)))
            return Path.Combine(dotnetRoot, executable);
        throw new InvalidOperationException("Could not locate dotnet host for process tests");
    }

    private sealed record Result(int ExitCode, string Stdout, string Stderr);
}
