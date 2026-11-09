using System.Text.Json;
using BuildSystem.Agent;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class TargetCreationStartHostCommandTests
{
    private const string Section = "Agent:TargetCreationStartMetadata:";
    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    private static Dictionary<string, string?> Valid() => new()
    {
        [Section + "Enabled"] = "true", [Section + "OperationId"] = Guid.NewGuid().ToString(),
        [Section + "AgentId"] = Guid.NewGuid().ToString(), [Section + "CanonicalClusterId"] = Guid.NewGuid().ToString(),
        [Section + "CanonicalSqlServerId"] = Guid.NewGuid().ToString(), [Section + "CredentialId"] = Guid.NewGuid().ToString(),
        [Section + "EndpointMappingId"] = Guid.NewGuid().ToString()
    };
    [Fact]
    public async Task Disabled_ignores_other_pins_and_storage_and_emits_one_inert_json()
    {
        var config = Config(new() { [Section + "Enabled"] = "false", [Section + "Unknown:Nested"] = "secret-canary", ["Agent:DataDirectory"] = "invalid" });
        using var output = new StringWriter();
        Assert.Equal(3, await TargetCreationStartHostCommand.RunAsync(Guid.NewGuid(), config, output));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("TargetCreationStartDisabled", json.RootElement.GetProperty("status").GetString());
        Assert.False(json.RootElement.GetProperty("executable").GetBoolean());
        Assert.True(json.RootElement.GetProperty("scheduledJobsMustRemainBlocked").GetBoolean());
        Assert.DoesNotContain("secret-canary", output.ToString());
    }
    [Theory]
    [InlineData("Unknown", "secret-canary")]
    [InlineData("AgentId", "secret-canary")]
    [InlineData("OperationId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("TimeoutSeconds", "0")]
    [InlineData("TimeoutSeconds", "61")]
    [InlineData("AgentId:Nested", "secret-canary")]
    public void Enabled_configuration_is_closed_scalar_and_redacted(string key, string value)
    {
        var values = Valid(); values[Section + key] = value;
        var error = Assert.Throws<InvalidOperationException>(() => TargetCreationStartHostOptions.Bind(Config(values)));
        Assert.Equal("InvalidTargetCreationStartHostConfiguration", error.Message); Assert.Null(error.InnerException);
    }
    [Fact]
    public void Exact_trusted_selection_is_bound_without_any_io()
    {
        var values = Valid(); var options = TargetCreationStartHostOptions.Bind(Config(values));
        Assert.True(options.Enabled); Assert.Equal(30, options.TimeoutSeconds);
        Assert.Equal(Guid.Parse(values[Section + "OperationId"]!), options.OperationId);
        Assert.DoesNotContain(options.OperationId.ToString(), options.ToString());
    }
    [Fact]
    public async Task Precancel_is_sanitized_before_configuration_or_storage()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel(); using var output = new StringWriter();
        Assert.Equal(130, await TargetCreationStartHostCommand.RunAsync(Guid.NewGuid(), null!, output, cts.Token));
        Assert.Contains("TargetCreationStartCancelled", output.ToString());
        Assert.DoesNotContain("Exception", output.ToString());
    }
    [Fact]
    public async Task Missing_existing_journal_never_creates_storage_or_loads_credentials()
    {
        var directory = Path.Combine(Path.GetTempPath(), "creation-host-no-create-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var values = Valid(); values["Agent:DataDirectory"] = directory; values["Agent:ApiOrigin"] = "https://invalid.example/";
            using var output = new StringWriter();
            Assert.Equal(5, await TargetCreationStartHostCommand.RunAsync(Guid.NewGuid(), Config(values), output));
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory)); Assert.Contains("TargetCreationStartUnavailable", output.ToString());
        }
        finally
        {
            var absolute = Path.GetFullPath(directory);
            Assert.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar, absolute);
            Directory.Delete(absolute);
        }
    }
    private sealed class FailingWriter : StringWriter
    {
        public int Calls;
        public override Task WriteLineAsync(string? value) { Calls++; throw new IOException("secret-canary"); }
    }
    [Fact]
    public async Task Output_failure_has_no_second_write()
    {
        using var output = new FailingWriter();
        Assert.Equal(5, await TargetCreationStartHostCommand.RunAsync(Guid.NewGuid(), Config(new()), output));
        Assert.Equal(1, output.Calls);
    }
}