using System.Text.Json;
using BuildSystem.Agent;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class AgentReadinessIdentityHostCommandTests
{
    private const string Prefix = "Agent:ReadinessIdentityProbe:";
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public async Task DisabledWinsOverCancellationAndMalformedOtherConfiguration(string? enabled)
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var output = new StringWriter();
        var result = await AgentReadinessIdentityHostCommand.RunAsync(Config(new()
        {
            [Prefix + "Enabled"] = enabled, [Prefix + "Unknown:Nested"] = "private-secret",
            ["Agent:DataDirectory"] = "\0private-path", ["Agent:ApiOrigin"] = "invalid-private-origin"
        }), output, cancellation.Token);
        Check(output.ToString(), result, 3, "AgentReadinessIdentityDisabled");
    }

    [Fact]
    public async Task NullConfigurationProducesFixedUnavailable()
    {
        using var output = new StringWriter();
        CheckAfter(await AgentReadinessIdentityHostCommand.RunAsync(null!, output), output);
    }

    [Theory]
    [InlineData("private-invalid-enabled")]
    [InlineData("true")]
    public async Task InvalidConfigurationCannotReachCredentialOrNetwork(string enabled)
    {
        using var output = new StringWriter();
        CheckAfter(await AgentReadinessIdentityHostCommand.RunAsync(Config(new()
            { [Prefix + "Enabled"] = enabled }), output), output);
    }

    [Fact]
    public async Task ValidConfigurationPreCancellationPreventsDpapiAndNetworkOnEveryPlatform()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "identity-host-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var token = Path.Combine(root, "agent-token.dpapi");
        File.WriteAllBytes(token, [0xff]); // Invalid DPAPI intentionally: never decrypted by this test.
        try
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            using var output = new StringWriter();
            var result = await AgentReadinessIdentityHostCommand.RunAsync(Config(new()
            {
                [Prefix + "Enabled"] = "true", [Prefix + "ExpectedAgentId"] = Guid.NewGuid().ToString("D"),
                ["Agent:ApiOrigin"] = "https://never-contact.invalid/", ["Agent:DataDirectory"] = root
            }), output, cancellation.Token);
            Check(output.ToString(), result, 130, "AgentReadinessIdentityCancelled");
            Assert.Equal(new byte[] { 0xff }, File.ReadAllBytes(token));
            Assert.Equal(new[] { token }, Directory.GetFileSystemEntries(root));
        }
        finally
        {
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), Path.GetDirectoryName(root));
            Assert.StartsWith("identity-host-", Path.GetFileName(root));
            Assert.True(Guid.TryParseExact(Path.GetFileName(root)["identity-host-".Length..], "N", out _));
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConfigurationFailureIsFixedAndOutputFailureIsNotRetried()
    {
        using var output = new StringWriter();
        CheckAfter(await AgentReadinessIdentityHostCommand.WriteConfigurationFailureAsync(output), output);
        using var broken = new BrokenWriter();
        Assert.Equal(5, await AgentReadinessIdentityHostCommand.RunAsync(Config(new()), broken));
        Assert.Equal(1, broken.Attempts);
    }

    private static void CheckAfter(int result, StringWriter output) =>
        Check(output.ToString(), result, 5, "AgentReadinessIdentityUnavailable");

    private static void Check(string output, int result, int expected, string status)
    {
        Assert.Equal(expected, result);
        using var json = JsonDocument.Parse(output);
        var value = json.RootElement;
        Assert.Equal(8, value.EnumerateObject().Count());
        Assert.Equal(status, value.GetProperty("status").GetString());
        foreach (var name in new[] { "apiIdentityObserved", "executable", "freshAuthority", "readyForExecution", "retryAllowed" })
            Assert.False(value.GetProperty(name).GetBoolean());
        Assert.True(value.GetProperty("operationalVerificationRequired").GetBoolean());
        Assert.True(value.GetProperty("scheduledJobsMustRemainBlocked").GetBoolean());
        Assert.DoesNotContain("private", output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(output.TrimEnd('\r', '\n') + Environment.NewLine, output);
    }

    private sealed class BrokenWriter : StringWriter
    {
        public int Attempts { get; private set; }
        public override Task WriteLineAsync(string? value)
        { Attempts++; throw new IOException("private-output-error"); }
    }
}