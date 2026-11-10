using System.Text;
using System.Text.Json;
using BuildSystem.Agent;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class TargetCreationOneShotReadinessTests
{
    private const string Prefix = "Agent:TargetCreationOneShot:";

    [Fact]
    public void NullConfigurationIsAnInertInvalidReport() => Check(null!, "InvalidConfiguration");

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void DisabledIgnoresBadFieldsAndDoesNotCreateMissingDirectory(string? enabled)
    {
        using var fixture = new Fixture();
        var missing = Path.Combine(fixture.Root, "must-not-be-created");
        var config = Config(new()
        {
            [Prefix + "Enabled"] = enabled, [Prefix + "Unknown:Nested"] = "private-unknown",
            [Prefix + "RacExecutable"] = "\0private-path", [Prefix + "AgentId"] = "bad-guid",
            ["Agent:ApiOrigin"] = "http://private-origin/path", ["Agent:DataDirectory"] = missing
        });
        var before = fixture.Snapshot();
        Check(config, "DisabledNotAssessed");
        var report = TargetCreationOneShotReadiness.Evaluate(config);
        Assert.False(report.ExecutionConfiguredEnabled);
        Assert.False(report.StaticConfigurationValid);
        Assert.False(report.ExistingLocalArtifactsPresent);
        Assert.False(Directory.Exists(missing));
        Assert.Equal(before, fixture.Snapshot());
    }

    [Theory]
    [InlineData("Enabled", "malformed-private-enabled")]
    [InlineData("Unknown", "private-unknown")]
    public void MalformedEnablementOrEnabledUnknownConfigurationIsInvalid(string key, string value)
    {
        using var fixture = new Fixture();
        fixture.Values[Prefix + key] = value;
        Check(Config(fixture.Values), "InvalidConfiguration");
    }

    [Fact]
    public void EnabledPresenceOnlyCheckAcceptsMalformedArtifactsWithoutOpeningOrChangingThem()
    {
        using var fixture = new Fixture();
        var before = fixture.Snapshot();
        var config = Config(fixture.Values);
        Check(config, "StaticChecksPassedRequiresOperationalVerification");
        var report = TargetCreationOneShotReadiness.Evaluate(config);
        Assert.True(report.ExecutionConfiguredEnabled);
        Assert.True(report.StaticConfigurationValid);
        Assert.True(report.ExistingLocalArtifactsPresent);
        Assert.Equal(OperatingSystem.IsWindows(), report.PlatformSupported);
        Assert.False(report.ReadyForExecution); // Enabled plus static checks is never execution authority
        Assert.Equal(before, fixture.Snapshot());
        Assert.Equal(Fixture.JournalCanary, File.ReadAllBytes(fixture.Journal));
        Assert.Equal(Fixture.TokenCanary, File.ReadAllBytes(fixture.Token));
        Assert.Empty(File.ReadAllBytes(fixture.Executable));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "agent-journal.lock")));
        Assert.False(File.Exists(fixture.Journal + "-wal"));
        Assert.False(File.Exists(fixture.Journal + "-shm"));
    }

    [Theory]
    [InlineData("http://private-origin.example/")]
    [InlineData("https://private-origin.example/private-path")]
    [InlineData("https://private-origin.example/?private-query=secret")]
    [InlineData("https://private-user:private-secret@private-origin.example/")]
    [InlineData("not-a-private-uri")]
    public void InvalidOriginsReturnFixedRedactedInvalidReports(string origin)
    {
        using var fixture = new Fixture();
        fixture.Values["Agent:ApiOrigin"] = origin;
        Check(Config(fixture.Values), "InvalidConfiguration");
    }

    [Theory]
    [InlineData("agent-journal.sqlite")]
    [InlineData("agent-token.dpapi")]
    public void MissingArtifactIsInvalidWithoutClaimingReadiness(string missing)
    {
        using var fixture = new Fixture(missing);
        var before = fixture.Snapshot();
        var config = Config(fixture.Values);
        Check(config, "InvalidConfiguration");
        Assert.False(TargetCreationOneShotReadiness.Evaluate(config).ExistingLocalArtifactsPresent);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Theory]
    [InlineData("Agent:TargetCreationOneShot:ExclusiveNamespaceConfirmed", "false")]
    [InlineData("Agent:TargetCreationOneShot:OperationId", "private-invalid-guid")]
    [InlineData("Agent:DataDirectory", "relative-private-directory")]
    public void InvalidPhysicalConfigurationIsReportedNotThrown(string key, string value)
    {
        using var fixture = new Fixture();
        fixture.Values[key] = value;
        Check(Config(fixture.Values), "InvalidConfiguration");
    }

    [Fact]
    public void ReportIsSealedGetOnlyAndDoesNotSerializePrivateConfiguration()
    {
        using var fixture = new Fixture();
        var report = TargetCreationOneShotReadiness.Evaluate(Config(fixture.Values));
        Assert.True(report.GetType().IsSealed);
        Assert.All(report.GetType().GetProperties(), property => Assert.Null(property.SetMethod));
        var serialized = JsonSerializer.Serialize(report) + report;
        foreach (var value in fixture.Values.Values.Where(value => !string.IsNullOrEmpty(value)))
            if (value != "true") Assert.DoesNotContain(value!, serialized);
        Assert.DoesNotContain("malformedSQLitecanary", serialized);
        Assert.DoesNotContain("invalidDPAPIcanary", serialized);
    }

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static void Check(IConfiguration configuration, string status)
    {
        var report = TargetCreationOneShotReadiness.Evaluate(configuration);
        Assert.Equal(status, report.Status);
        Assert.False(report.Executable);
        Assert.False(report.FreshAuthority);
        Assert.False(report.RetryAllowed);
        Assert.True(report.ScheduledJobsMustRemainBlocked);
        Assert.False(report.ReadyForExecution);
        Assert.True(report.OperationalVerificationRequired);
        Assert.Equal("TargetCreationOneShotReadinessReport [details omitted]", report.ToString());
        var text = JsonSerializer.Serialize(report) + report;
        Assert.DoesNotContain("private-", text);
    }

    private sealed class Fixture : IDisposable
    {
        public static readonly byte[] JournalCanary = Encoding.UTF8.GetBytes("malformedSQLitecanary");
        public static readonly byte[] TokenCanary = Encoding.UTF8.GetBytes("invalidDPAPIcanary");
        public string Root { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "readiness-" + Guid.NewGuid().ToString("N")));
        public string Executable => Path.Combine(Root, "rac.exe");
        public string Journal => Path.Combine(Root, "agent-journal.sqlite");
        public string Token => Path.Combine(Root, "agent-token.dpapi");
        public Dictionary<string, string?> Values { get; } = new();

        public Fixture(string? missing = null)
        {
            Directory.CreateDirectory(Root);
            File.WriteAllBytes(Executable, []);
            if (missing != "agent-journal.sqlite") File.WriteAllBytes(Journal, JournalCanary);
            if (missing != "agent-token.dpapi") File.WriteAllBytes(Token, TokenCanary);
            foreach (var key in new[] { "OperationId", "AgentId", "CredentialId", "EndpointMappingId",
                "RacObservationMappingId", "SqlObservationMappingId", "CanonicalClusterId", "RacClusterId", "CanonicalSqlServerId" })
                Values[Prefix + key] = Guid.NewGuid().ToString();
            Values[Prefix + "Enabled"] = "true";
            Values[Prefix + "ExclusiveNamespaceConfirmed"] = "true";
            Values[Prefix + "RacExecutable"] = Executable;
            Values[Prefix + "RacWorkingDirectory"] = Root;
            Values[Prefix + "RacEndpoint"] = "private-server.example:1545";
            Values[Prefix + "SqlServer"] = "private-server.example,1433";
            Values[Prefix + "ExpectedSqlMachineName"] = "private-server";
            Values["Agent:ApiOrigin"] = "https://private-origin.example/";
            Values["Agent:DataDirectory"] = Root;
        }

        public string[] Snapshot() => Directory.GetFileSystemEntries(Root, "*", SearchOption.AllDirectories).Order().ToArray();

        public void Dispose()
        {
            var normalized = Path.GetFullPath(Root);
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            var name = Path.GetFileName(normalized);
            if (Path.GetDirectoryName(normalized) != parent || !name.StartsWith("readiness-", StringComparison.Ordinal) ||
                !Guid.TryParseExact(name["readiness-".Length..], "N", out _))
                throw new InvalidOperationException("Unsafe test cleanup path");
            Directory.Delete(normalized, recursive: true);
        }
    }
}
