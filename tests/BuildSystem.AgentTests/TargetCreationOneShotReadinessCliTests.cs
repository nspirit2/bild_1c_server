using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BuildSystem.Agent;
using Xunit;

namespace BuildSystem.AgentTests;

/// <summary>Only the offline readiness route is invoked, even for enabled configuration.</summary>
public sealed class TargetCreationOneShotReadinessCliTests : IDisposable
{
    private const string Command = "check-target-creation-readiness";
    private const string Prefix = "Agent__TargetCreationOneShot__";
    private readonly string root = Path.Combine(Path.GetTempPath(), "readiness-cli-" + Guid.NewGuid().ToString("N"));
    private readonly string agent = Path.Combine(AppContext.BaseDirectory, "AgentFixture", "BuildSystem.Agent.dll");

    public TargetCreationOneShotReadinessCliTests()
    {
        Directory.CreateDirectory(root);
        Assert.True(File.Exists(agent), "Agent fixture must be copied alongside test binaries.");
        Assert.True(File.Exists(Path.ChangeExtension(agent, ".runtimeconfig.json")));
    }

    public void Dispose()
    {
        var target = Path.GetFullPath(root);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), Path.GetDirectoryName(target));
        Assert.StartsWith("readiness-cli-", Path.GetFileName(target));
        Assert.True(Guid.TryParseExact(Path.GetFileName(target)["readiness-cli-".Length..], "N", out _));
        try { Directory.Delete(target, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void CommandRequiresExactlyOneCaseSensitiveArgument()
    {
        Assert.True(TargetCreationOneShotReadinessCommand.IsReadinessCommand([Command]));
        foreach (var args in new[] { Array.Empty<string>(), new[] { Command, "extra" }, new[] { Command.ToUpperInvariant() }, new[] { "run-target-creation-once" } })
            Assert.False(TargetCreationOneShotReadinessCommand.IsReadinessCommand(args));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public async Task DefaultDisabledProducesOnlyNonReadyReportWithoutCreatingDataDirectory(string? enabled)
    {
        var values = new Dictionary<string, string> { ["Agent__DataDirectory"] = Path.Combine(root, "must-not-exist") };
        if (enabled is not null) values[Prefix + "Enabled"] = enabled;
        var report = AssertReport(await RunAgent([Command], values), 3, "DisabledNotAssessed");
        Assert.False(report.GetProperty("executionConfiguredEnabled").GetBoolean());
        Assert.False(report.GetProperty("staticConfigurationValid").GetBoolean());
        Assert.False(report.GetProperty("existingLocalArtifactsPresent").GetBoolean());
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public async Task ExtraArgumentRejectsBeforeMalformedHostConfiguration()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "appsettings.json"), "{ private-invalid-json");
        var result = await RunAgent([Command, "extra"], new() { [Prefix + "Enabled"] = "private-invalid-boolean" });
        Assert.Equal(64, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal("UnsupportedAgentCommand" + Environment.NewLine, result.Stderr);
    }

    [Fact]
    public async Task InvalidEnabledProducesRedactedInvalidReport()
    {
        var report = AssertReport(await RunAgent([Command], new() { [Prefix + "Enabled"] = "private-invalid-boolean" }), 4, "InvalidConfiguration");
        Assert.False(report.GetProperty("executionConfiguredEnabled").GetBoolean());
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public async Task MalformedAppsettingsProducesFixedInvalidReportWithoutDiagnostics()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "appsettings.json"), "{ private-invalid-json");
        var report = AssertReport(await RunAgent([Command], new() { [Prefix + "Enabled"] = "true" }), 4, "InvalidConfiguration");
        Assert.False(report.GetProperty("executionConfiguredEnabled").GetBoolean());
        Assert.False(report.GetProperty("staticConfigurationValid").GetBoolean());
        Assert.False(report.GetProperty("existingLocalArtifactsPresent").GetBoolean());
    }

    [Fact]
    public async Task EnabledStaticChecksNeverRunHostOrReadOrModifyMalformedArtifacts()
    {
        var executable = Path.Combine(root, "rac.exe");
        var journal = Path.Combine(root, "agent-journal.sqlite");
        var token = Path.Combine(root, "agent-token.dpapi");
        byte[] journalBytes = Encoding.UTF8.GetBytes("malformedSQLitecanary");
        byte[] tokenBytes = Encoding.UTF8.GetBytes("invalidDPAPIcanary");
        await File.WriteAllBytesAsync(executable, []); // Invalid executable format: must never launch.
        await File.WriteAllBytesAsync(journal, journalBytes);
        await File.WriteAllBytesAsync(token, tokenBytes);
        var values = new Dictionary<string, string>
        {
            [Prefix + "Enabled"] = "true", [Prefix + "ExclusiveNamespaceConfirmed"] = "true",
            [Prefix + "RacExecutable"] = executable, [Prefix + "RacWorkingDirectory"] = root,
            [Prefix + "RacEndpoint"] = "private-server.invalid:1545", [Prefix + "SqlServer"] = "private-server.invalid,1433",
            [Prefix + "ExpectedSqlMachineName"] = "private-server", ["Agent__ApiOrigin"] = "https://private-origin.invalid/",
            ["Agent__DataDirectory"] = root
        };
        foreach (var key in new[] { "OperationId", "AgentId", "CredentialId", "EndpointMappingId", "RacObservationMappingId",
            "SqlObservationMappingId", "CanonicalClusterId", "RacClusterId", "CanonicalSqlServerId" })
            values[Prefix + key] = Guid.NewGuid().ToString();
        var before = Directory.GetFileSystemEntries(root).Order().ToArray();
        var result = await RunAgent([Command], values);
        var report = AssertReport(result, 2, "StaticChecksPassedRequiresOperationalVerification");
        Assert.True(report.GetProperty("executionConfiguredEnabled").GetBoolean());
        Assert.True(report.GetProperty("staticConfigurationValid").GetBoolean());
        Assert.True(report.GetProperty("existingLocalArtifactsPresent").GetBoolean());
        Assert.Equal(before, Directory.GetFileSystemEntries(root).Order().ToArray());
        Assert.Equal(journalBytes, await File.ReadAllBytesAsync(journal));
        Assert.Equal(tokenBytes, await File.ReadAllBytesAsync(token));
        Assert.Empty(await File.ReadAllBytesAsync(executable));
        foreach (var value in values.Values.Where(value => value != "true"))
            Assert.DoesNotContain(value, result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutputFailureIsFiveAndDoesNotRetry()
    {
        using var output = new FailingWriter();
        Assert.Equal(5, await TargetCreationOneShotReadinessCommand.RunAsync(null!, output));
        Assert.Equal(1, output.Attempts);
        using var failureOutput = new StringWriter();
        Assert.Equal(4, await TargetCreationOneShotReadinessCommand.WriteConfigurationFailureAsync(failureOutput));
        AssertReport(new(4, failureOutput.ToString(), ""), 4, "InvalidConfiguration");
    }

    private JsonElement AssertReport(Result result, int exitCode, string status)
    {
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Empty(result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        var report = document.RootElement;
        Assert.Equal(11, report.EnumerateObject().Count());
        Assert.Equal(status, report.GetProperty("status").GetString());
        Assert.Equal(OperatingSystem.IsWindows(), report.GetProperty("platformSupported").GetBoolean());
        foreach (var key in new[] { "executable", "freshAuthority", "readyForExecution", "retryAllowed" })
            Assert.False(report.GetProperty(key).GetBoolean());
        foreach (var key in new[] { "scheduledJobsMustRemainBlocked", "operationalVerificationRequired" })
            Assert.True(report.GetProperty(key).GetBoolean());
        Assert.All(report.EnumerateObject().Where(property => property.Name != "status"), property =>
            Assert.True(property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False));
        Assert.Equal(result.Stdout.TrimEnd('\r', '\n') + Environment.NewLine, result.Stdout);
        Assert.DoesNotContain(root, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("private-", result.Stdout, StringComparison.Ordinal);
        return report.Clone();
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
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

    private sealed class FailingWriter : StringWriter
    {
        public int Attempts { get; private set; }
        public override Task WriteLineAsync(string? value) { Attempts++; throw new IOException("private-output-failure"); }
    }
    private sealed record Result(int ExitCode, string Stdout, string Stderr);
}
