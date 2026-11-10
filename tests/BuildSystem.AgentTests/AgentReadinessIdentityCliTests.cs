using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BuildSystem.Agent;
using Xunit;

namespace BuildSystem.AgentTests;

/// <summary>Only disabled or invalid CLI configurations are launched; never a valid enabled probe.</summary>
[Collection("Agent CLI subprocesses")]
public sealed class AgentReadinessIdentityCliTests : IDisposable
{
    private const string Command = "probe-agent-api-identity";
    private const string Enabled = "Agent__ReadinessIdentityProbe__Enabled";
    private readonly string root = Path.Combine(Path.GetTempPath(), "identity-cli-" + Guid.NewGuid().ToString("N"));
    private readonly string agent = Path.Combine(AppContext.BaseDirectory, "AgentFixture", "BuildSystem.Agent.dll");

    public AgentReadinessIdentityCliTests()
    {
        Directory.CreateDirectory(root);
        Assert.True(File.Exists(agent), "Agent fixture must be copied alongside test binaries.");
        Assert.True(File.Exists(Path.ChangeExtension(agent, ".runtimeconfig.json")));
    }

    public void Dispose()
    {
        var target = Path.GetFullPath(root);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), Path.GetDirectoryName(target));
        Assert.StartsWith("identity-cli-", Path.GetFileName(target));
        Assert.True(Guid.TryParseExact(Path.GetFileName(target)["identity-cli-".Length..], "N", out _));
        try { Directory.Delete(target, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void CommandRequiresExactlyOneCaseSensitiveArgument()
    {
        Assert.True(AgentReadinessIdentityHostCommand.IsProbeCommand([Command]));
        foreach (var args in new[] { Array.Empty<string>(), new[] { Command, "extra" }, new[] { Command.ToUpperInvariant() }, new[] { "probe-agent-api-identitiy" } })
            Assert.False(AgentReadinessIdentityHostCommand.IsProbeCommand(args));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public async Task DisabledProbeDoesNotRequireDataDirectoryOrInvokeWorker(string? enabled)
    {
        var values = new Dictionary<string, string> { ["Agent__DataDirectory"] = Path.Combine(root, "must-not-exist") };
        if (enabled is not null) values[Enabled] = enabled;
        AssertReport(await RunAgent([Command], values), 3, "AgentReadinessIdentityDisabled");
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public async Task InvalidEnabledFailsClosedWithoutConfigurationDiagnostics()
    {
        AssertReport(await RunAgent([Command], new() { [Enabled] = "private-invalid-boolean" }),
            5, "AgentReadinessIdentityUnavailable");
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public async Task EnabledWithoutExpectedGuidFailsBeforePlatformOrDependencies()
    {
        // No expected GUID, credentials, data directory or origin: cannot reach token IO or network on any OS.
        AssertReport(await RunAgent([Command], new() { [Enabled] = "true" }),
            5, "AgentReadinessIdentityUnavailable");
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public async Task MalformedAppsettingsProducesFixedUnavailableReport()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "appsettings.json"), "{ private-invalid-json");
        AssertReport(await RunAgent([Command], new() { [Enabled] = "true" }),
            5, "AgentReadinessIdentityUnavailable");
    }

    [Theory]
    [InlineData("probe-agent-api-identity", "extra")]
    [InlineData("probe-agent-api-identitiy", null)]
    public async Task UnsupportedArgumentRejectedBeforeMalformedHostConfiguration(string command, string? extra)
    {
        await File.WriteAllTextAsync(Path.Combine(root, "appsettings.json"), "{ private-invalid-json");
        var args = extra is null ? new[] { command } : new[] { command, extra };
        var result = await RunAgent(args, new() { [Enabled] = "private-invalid-boolean" });
        Assert.Equal(64, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal("UnsupportedAgentCommand" + Environment.NewLine, result.Stderr);
    }

    private void AssertReport(Result result, int exitCode, string status)
    {
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Empty(result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        var report = document.RootElement;
        Assert.Equal(8, report.EnumerateObject().Count());
        Assert.Equal(status, report.GetProperty("status").GetString());
        foreach (var key in new[] { "apiIdentityObserved", "executable", "freshAuthority", "readyForExecution", "retryAllowed" })
            Assert.False(report.GetProperty(key).GetBoolean());
        foreach (var key in new[] { "scheduledJobsMustRemainBlocked", "operationalVerificationRequired" })
            Assert.True(report.GetProperty(key).GetBoolean());
        Assert.All(report.EnumerateObject().Where(property => property.Name != "status"), property =>
            Assert.True(property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False));
        Assert.Equal(result.Stdout.TrimEnd('\r', '\n') + Environment.NewLine, result.Stdout);
        Assert.DoesNotContain(root, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("private-", result.Stdout, StringComparison.Ordinal);
    }

    private async Task<Result> RunAgent(string[] arguments, Dictionary<string, string> values)
    {
        var start = new ProcessStartInfo(FindDotnetHost())
        {
            WorkingDirectory = root, UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add(agent);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var key in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP", "HOME", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_X86" })
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        foreach (var pair in values) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Agent process did not start.");
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var stdout = Capture(process.StandardOutput, timeout.Token);
        var stderr = Capture(process.StandardError, timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); return new(process.ExitCode, await stdout, await stderr); }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
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
        if (processPath is not null && Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return processPath;
        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        foreach (var directory in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(typeof(object).Assembly.Location) })
            for (var parent = new DirectoryInfo(directory!); parent is not null; parent = parent.Parent)
                if (File.Exists(Path.Combine(parent.FullName, executable))) return Path.Combine(parent.FullName, executable);
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (dotnetRoot is not null && File.Exists(Path.Combine(dotnetRoot, executable))) return Path.Combine(dotnetRoot, executable);
        throw new InvalidOperationException("Could not locate dotnet host for process tests");
    }

    private sealed record Result(int ExitCode, string Stdout, string Stderr);
}
