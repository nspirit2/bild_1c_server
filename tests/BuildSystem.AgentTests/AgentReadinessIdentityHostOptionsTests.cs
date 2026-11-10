using BuildSystem.Agent;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class AgentReadinessIdentityHostOptionsTests
{
    private const string Prefix = "Agent:ReadinessIdentityProbe:";

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void DisabledIgnoresUnknownNestedAndBadConfigurationWithoutCreatingDirectory(string? enabled)
    {
        using var fixture = new Fixture();
        var missing = Path.Combine(fixture.Directory, "missing");
        var options = AgentReadinessIdentityHostOptions.Bind(Config(new()
        {
            [Prefix + "Enabled"] = enabled, [Prefix + "Unknown:Nested"] = "private-secret",
            [Prefix + "ExpectedAgentId"] = "not-a-guid", [Prefix + "HttpTimeoutSeconds"] = "bad",
            ["Agent:ApiOrigin"] = "http://private-user:private-secret@host/path",
            ["Agent:DataDirectory"] = missing
        }));
        Assert.False(options.Enabled);
        Assert.Equal(Guid.Empty, options.ExpectedAgentId);
        Assert.Equal(30, options.HttpTimeoutSeconds);
        Assert.Equal("", options.DataDirectory);
        Assert.False(System.IO.Directory.Exists(missing));
        Assert.False(AgentReadinessIdentityHostOptions.Bind(Config(new()
            { ["Agent:DataDirectory"] = "\0private-path", ["Agent:ApiOrigin"] = "\n" })).Enabled);
    }

    [Fact]
    public void NullConfigurationRejectsWithFixedError() => Invalid(null!);

    [Theory]
    [InlineData("Enabled", "maybe")]
    [InlineData("Enabled", "null")]
    [InlineData("Enabled:Nested", "false")]
    public void MalformedEnablementRejectsBeforeDisabledShortCircuit(string key, string value) =>
        Invalid(Config(new() { [Prefix + key] = value }));

    [Fact]
    public void ScalarSectionRejectsEvenWhenDisabled() => Invalid(Config(new()
        { [Prefix.TrimEnd(':')] = "private-scalar", [Prefix + "Enabled"] = "false" }));

    [Theory]
    [InlineData("Unknown")]
    [InlineData("Unknown:Nested")]
    [InlineData("ExpectedAgentId:Nested")]
    [InlineData("HttpTimeoutSeconds:Nested")]
    [InlineData("ApiOrigin")]
    [InlineData("DataDirectory")]
    public void EnabledRejectsUnknownNestedAndSectionOverrides(string key)
    {
        using var fixture = new Fixture();
        fixture.Values[Prefix + key] = "private-override";
        Invalid(Config(fixture.Values));
    }

    [Theory]
    [InlineData("https://example.invalid", 1)]
    [InlineData("https://example.invalid:8443/", 60)]
    [InlineData("https://[::1]:8443/", 30)]
    public void ValidRootsPreserveIndependentExpectedIdAndTimeout(string origin, int timeout)
    {
        using var fixture = new Fixture();
        fixture.Values["Agent:ApiOrigin"] = origin;
        fixture.Values["Agent:AgentId"] = Guid.NewGuid().ToString();
        fixture.Values[Prefix + "HttpTimeoutSeconds"] = timeout.ToString();
        var config = Config(fixture.Values);
        var options = AgentReadinessIdentityHostOptions.Bind(config);
        config[Prefix + "ExpectedAgentId"] = Guid.NewGuid().ToString();
        Assert.True(options.Enabled);
        Assert.Equal(fixture.ExpectedId, options.ExpectedAgentId);
        Assert.Equal(timeout, options.HttpTimeoutSeconds);
        Assert.Equal(new Uri(origin), options.ApiOrigin);
        Assert.Equal(fixture.Directory, options.DataDirectory);
        Assert.Equal("AgentReadinessIdentityHostOptions [details omitted]", options.ToString());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("61")]
    [InlineData("private-not-an-int")]
    public void InvalidTimeoutRejects(string value)
    {
        using var fixture = new Fixture();
        fixture.Values[Prefix + "HttpTimeoutSeconds"] = value;
        Invalid(Config(fixture.Values));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("private-invalid-guid")]
    public void ExpectedIdMustBeIndependentlyConfiguredAndNonempty(string? value)
    {
        using var fixture = new Fixture();
        fixture.Values["Agent:AgentId"] = Guid.NewGuid().ToString();
        fixture.Values[Prefix + "ExpectedAgentId"] = value;
        Invalid(Config(fixture.Values));
    }

    [Theory]
    [InlineData("http://example.invalid/")]
    [InlineData("/relative")]
    [InlineData("https://example.invalid/path")]
    [InlineData("https://example.invalid/?private-secret")]
    [InlineData("https://example.invalid/#private-secret")]
    [InlineData("https://private-user:private-secret@example.invalid/")]
    [InlineData("https://example.invalid/\n")]
    [InlineData("")]
    public void OriginMustBeControlFreeHttpsRoot(string origin)
    {
        using var fixture = new Fixture();
        fixture.Values["Agent:ApiOrigin"] = origin;
        Invalid(Config(fixture.Values));
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("\0private-path")]
    [InlineData("")]
    public void DataDirectoryMustBeAbsoluteAndControlFree(string path)
    {
        using var fixture = new Fixture();
        fixture.Values["Agent:DataDirectory"] = path;
        Invalid(Config(fixture.Values));
        fixture.Values["Agent:DataDirectory"] = new string('x', 4097);
        Invalid(Config(fixture.Values));
    }

    [Fact]
    public void MissingDirectoryRejectsWithoutCreatingIt()
    {
        using var fixture = new Fixture();
        var missing = Path.Combine(fixture.Directory, "missing");
        fixture.Values["Agent:DataDirectory"] = missing;
        Invalid(Config(fixture.Values));
        Assert.False(System.IO.Directory.Exists(missing));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(16385)]
    public void MissingOrOutOfBoundsArtifactRejects(int? length)
    {
        using var fixture = new Fixture(length);
        Invalid(Config(fixture.Values));
        Assert.Equal(length.HasValue ? 1 : 0, System.IO.Directory.GetFileSystemEntries(fixture.Directory).Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16384)]
    public void MalformedTokenContentIsAcceptedUnchangedWithoutJournalWalOrLock(int length)
    {
        using var fixture = new Fixture(length);
        var options = AgentReadinessIdentityHostOptions.Bind(Config(fixture.Values));
        Assert.True(options.Enabled);
        Assert.Equal(30, options.HttpTimeoutSeconds);
        Assert.Equal(fixture.Bytes, File.ReadAllBytes(fixture.TokenPath));
        Assert.Equal(new[] { fixture.TokenPath }, System.IO.Directory.GetFileSystemEntries(fixture.Directory));
    }

    private static IConfigurationRoot Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static void Invalid(IConfiguration config)
    {
        var error = Assert.Throws<InvalidOperationException>(() => AgentReadinessIdentityHostOptions.Bind(config));
        Assert.Equal("InvalidAgentReadinessIdentityHostConfiguration", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-", error.ToString());
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "readiness-options-" + Guid.NewGuid().ToString("N")));
        public string TokenPath => Path.Combine(Directory, "agent-token.dpapi");
        public Guid ExpectedId { get; } = Guid.NewGuid();
        public byte[] Bytes { get; }
        public Dictionary<string, string?> Values { get; }
        public Fixture(int? length = 1)
        {
            System.IO.Directory.CreateDirectory(Directory);
            Bytes = Enumerable.Repeat((byte)0xff, length ?? 0).ToArray();
            if (length.HasValue) File.WriteAllBytes(TokenPath, Bytes);
            Values = new() { [Prefix + "Enabled"] = "true", [Prefix + "ExpectedAgentId"] = ExpectedId.ToString(),
                ["Agent:ApiOrigin"] = "https://example.invalid/", ["Agent:DataDirectory"] = Directory };
        }
        public void Dispose()
        {
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(Directory));
            Assert.StartsWith("readiness-options-", Path.GetFileName(Directory));
            System.IO.Directory.Delete(Directory, recursive: true);
            Assert.False(System.IO.Directory.Exists(Directory));
        }
    }
}
