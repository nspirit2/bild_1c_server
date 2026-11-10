using System.Text.Json;
using BuildSystem.Agent;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class TargetCreationOneShotHostCommandTests
{
    private const string Section = "Agent:TargetCreationOneShot:";
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static void AssertResult(StringWriter output, string status)
    {
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.Equal(7, root.EnumerateObject().Count());
        Assert.Equal(status, root.GetProperty("status").GetString());
        foreach (var name in new[] { "executable", "freshAuthority", "retryAllowed", "physicalInvocationAttempted", "creationAcknowledged" })
            Assert.False(root.GetProperty(name).GetBoolean());
        Assert.True(root.GetProperty("scheduledJobsMustRemainBlocked").GetBoolean());
        Assert.DoesNotContain("secret-canary", output.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public async Task Default_off_wins_over_cancellation_unknown_nested_pins_and_storage(string? enabled)
    {
        var values = new Dictionary<string, string?>
        {
            [Section + "Enabled"] = enabled, [Section + "Unknown:Nested"] = "secret-canary",
            [Section + "AgentId"] = "secret-canary", ["Agent:DataDirectory"] = "secret-canary"
        };
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var output = new StringWriter();
        Assert.Equal(3, await TargetCreationOneShotHostCommand.RunAsync(Guid.Empty, Config(values), output, cancellation.Token));
        AssertResult(output, "TargetCreationOneShotDisabled");
    }

    [Theory]
    [InlineData("Enabled", "secret-canary")]
    [InlineData("Enabled:Nested", "secret-canary")]
    public async Task Invalid_enabled_shape_is_sanitized_even_when_cancelled(string key, string value)
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var output = new StringWriter();
        Assert.Equal(5, await TargetCreationOneShotHostCommand.RunAsync(Guid.NewGuid(),
            Config(new() { [Section + key] = value }), output, cancellation.Token));
        AssertResult(output, "TargetCreationOneShotUnavailable");
    }

    [Fact]
    public async Task Enabled_precancel_happens_after_bind_before_dependencies()
    {
        using var fixture = new LocalOptions();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var output = new StringWriter();
        Assert.Equal(130, await TargetCreationOneShotHostCommand.RunAsync(Guid.NewGuid(), Config(fixture.Values), output, cancellation.Token));
        AssertResult(output, "TargetCreationOneShotCancelled");
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "agent-journal.sqlite")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Zero_permit_or_missing_existing_journal_never_creates_storage_or_loads_tokens(bool zeroPermit)
    {
        using var fixture = new LocalOptions();
        using var output = new StringWriter();
        Assert.Equal(5, await TargetCreationOneShotHostCommand.RunAsync(zeroPermit ? Guid.Empty : Guid.NewGuid(), Config(fixture.Values), output));
        AssertResult(output, "TargetCreationOneShotUnavailable");
        Assert.Single(Directory.EnumerateFileSystemEntries(fixture.DirectoryPath)); // Only inert rac.exe.
    }

    [Fact]
    public async Task Nonwindows_gate_rejects_before_touching_even_an_existing_invalid_journal()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new LocalOptions();
        var journalPath = Path.Combine(fixture.DirectoryPath, "agent-journal.sqlite");
        File.WriteAllText(journalPath, "secret-canary");
        using var output = new StringWriter();
        Assert.Equal(5, await TargetCreationOneShotHostCommand.RunAsync(Guid.NewGuid(), Config(fixture.Values), output));
        AssertResult(output, "TargetCreationOneShotUnavailable");
        Assert.Equal("secret-canary", File.ReadAllText(journalPath));
        Assert.Equal(2, Directory.EnumerateFileSystemEntries(fixture.DirectoryPath).Count());
    }

    [Fact]
    public async Task Output_failure_is_one_attempt_without_fallback()
    {
        using var output = new FailingWriter();
        Assert.Equal(5, await TargetCreationOneShotHostCommand.RunAsync(Guid.Empty, Config(new()), output));
        Assert.Equal(1, output.Calls);
    }

    private sealed class FailingWriter : StringWriter
    {
        public int Calls;
        public override Task WriteLineAsync(string? value) { Calls++; throw new IOException("secret-canary"); }
    }

    private sealed class LocalOptions : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "one-shot-host-tests-" + Guid.NewGuid().ToString("N"));
        public Dictionary<string, string?> Values { get; } = new();
        public LocalOptions()
        {
            Directory.CreateDirectory(DirectoryPath);
            var executable = Path.Combine(DirectoryPath, "rac.exe");
            File.WriteAllBytes(executable, []); // Never executable: every test stops before composition.
            Values[Section + "Enabled"] = "true";
            Values[Section + "ExclusiveNamespaceConfirmed"] = "true";
            foreach (var pin in new[] { "OperationId", "AgentId", "CanonicalClusterId", "RacClusterId", "CanonicalSqlServerId",
                         "CredentialId", "EndpointMappingId", "RacObservationMappingId", "SqlObservationMappingId" })
                Values[Section + pin] = Guid.NewGuid().ToString();
            Values[Section + "RacExecutable"] = executable;
            Values[Section + "RacWorkingDirectory"] = DirectoryPath;
            Values[Section + "RacEndpoint"] = "localhost:1545";
            Values[Section + "SqlServer"] = "localhost";
            Values[Section + "ExpectedSqlMachineName"] = "localhost";
            Values[Section + "ExpectedSqlInstanceName"] = "MSSQLSERVER";
            Values["Agent:DataDirectory"] = DirectoryPath;
            Values["Agent:ApiOrigin"] = "https://invalid.example/";
        }
        public void Dispose()
        {
            var absolute = Path.GetFullPath(DirectoryPath);
            Assert.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar, absolute);
            Assert.StartsWith("one-shot-host-tests-", Path.GetFileName(absolute));
            Directory.Delete(absolute, recursive: true);
        }
    }
}
