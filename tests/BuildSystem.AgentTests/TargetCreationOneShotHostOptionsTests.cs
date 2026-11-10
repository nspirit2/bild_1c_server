using BuildSystem.Agent;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class TargetCreationOneShotHostOptionsTests
{
    private const string Prefix = "Agent:TargetCreationOneShot:";

    [Fact]
    public void NullConfigurationReturnsFixedRedactedError() => Invalid(null!);

    [Fact]
    public void MissingOrDisabledIgnoresUnknownNestedAndInvalidValues()
    {
        Assert.False(TargetCreationOneShotHostOptions.Bind(Config(new())).Enabled);
        var disabled = TargetCreationOneShotHostOptions.Bind(Config(new()
        {
            ["Enabled"] = "false", ["Unexpected:Nested"] = "private-secret",
            ["AgentId"] = "not-a-guid", ["RacExecutable"] = "\0private-path",
            ["SqlTimeoutSeconds"] = "not-an-integer"
        }));
        Assert.False(disabled.Enabled);
        Assert.Equal(Guid.Empty, disabled.AgentId);
        Assert.Equal("", disabled.RacExecutable);
        Assert.False(disabled.ToAdapterOptions().Enabled);
    }

    [Theory]
    [InlineData("Enabled", "maybe")]
    [InlineData("Enabled:Nested", "true")]
    public void InvalidEnablementIsAlwaysRejected(string member, string value) =>
        Invalid(Config(new() { [member] = value }));

    [Fact]
    public void ScalarSectionIsRejected()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Agent:TargetCreationOneShot"] = "private-section" }).Build();
        Invalid(config);
    }

    [Fact]
    public void EnabledBindsDefaultsSnapshotsAndProjectsEveryPin()
    {
        using var fixture = new Fixture();
        var config = Config(fixture.Values);
        var options = TargetCreationOneShotHostOptions.Bind(config);
        config[Prefix + "OperationId"] = Guid.NewGuid().ToString();
        Assert.True(options.Enabled);
        Assert.True(options.ExclusiveNamespaceConfirmed);
        Assert.Equal(fixture.OperationId, options.OperationId);
        Assert.Equal(fixture.Executable, options.RacExecutable);
        Assert.Equal("MSSQLSERVER", options.ExpectedSqlInstanceName);
        Assert.Equal(30, options.HttpTimeoutSeconds);
        Assert.Equal(30, options.RacTimeoutSeconds);
        Assert.Equal(30, options.SqlTimeoutSeconds);
        Assert.Equal(60, options.TotalObservationTimeoutSeconds);
        Assert.Equal(120, options.CreationTimeoutSeconds);
        var adapter = options.ToAdapterOptions();
        Assert.Equal(TargetCreationOneShotHostOptions.TrustedToolId, adapter.TrustedRacToolId);
        Assert.Equal(Guid.Parse(fixture.Values["AgentId"]!), adapter.TrustedAgentId);
        Assert.Equal(Guid.Parse(fixture.Values["EndpointMappingId"]!), adapter.EndpointMappingId);
        Assert.Equal(Guid.Parse(fixture.Values["RacObservationMappingId"]!), adapter.RacObservationMappingId);
        Assert.Equal(Guid.Parse(fixture.Values["SqlObservationMappingId"]!), adapter.SqlObservationMappingId);
        Assert.Equal(options.RacWorkingDirectory, adapter.WorkingDirectory);
        Assert.Equal(options.RacEndpoint, adapter.RacEndpoint);
        Assert.Equal(options.SqlServer, adapter.SqlServer);
        var observation = options.ToObservationOptions();
        Assert.Equal(options.RacObservationMappingId, observation.RacEndpointMappingId);
        Assert.Equal(options.SqlObservationMappingId, observation.SqlEndpointMappingId);
        Assert.Equal(options.RacExecutable, observation.RacExecutable);
        Assert.Equal(options.SqlTimeoutSeconds, observation.TimeoutSeconds);
        Assert.Equal("TargetCreationOneShotHostOptions [details omitted]", options.ToString());
    }

    [Theory]
    [InlineData("OperationId")]
    [InlineData("AgentId")]
    [InlineData("CredentialId")]
    [InlineData("EndpointMappingId")]
    [InlineData("RacObservationMappingId")]
    [InlineData("SqlObservationMappingId")]
    [InlineData("CanonicalClusterId")]
    [InlineData("RacClusterId")]
    [InlineData("CanonicalSqlServerId")]
    public void EveryGuidPinMustBeNonzero(string member)
    {
        using var fixture = new Fixture();
        fixture.Values[member] = Guid.Empty.ToString();
        Invalid(Config(fixture.Values));
    }

    [Theory]
    [InlineData("ExclusiveNamespaceConfirmed", "false")]
    [InlineData("Unknown", "private-unknown")]
    [InlineData("SqlServer:Nested", "private-nested")]
    [InlineData("AgentId", "private-invalid-guid")]
    [InlineData("HttpTimeoutSeconds", "private-invalid-int")]
    [InlineData("RacEndpoint", "server:0")]
    [InlineData("SqlServer", "server;Password=secret")]
    [InlineData("ExpectedSqlMachineName", "")]
    public void RejectsMalformedPolicyWithoutLeakingDiagnostics(string member, string value)
    {
        using var fixture = new Fixture();
        fixture.Values[member] = value;
        Invalid(Config(fixture.Values));
    }

    [Theory]
    [InlineData("HttpTimeoutSeconds", "0")]
    [InlineData("HttpTimeoutSeconds", "61")]
    [InlineData("RacTimeoutSeconds", "0")]
    [InlineData("RacTimeoutSeconds", "61")]
    [InlineData("SqlTimeoutSeconds", "0")]
    [InlineData("SqlTimeoutSeconds", "61")]
    [InlineData("TotalObservationTimeoutSeconds", "0")]
    [InlineData("TotalObservationTimeoutSeconds", "121")]
    [InlineData("CreationTimeoutSeconds", "0")]
    [InlineData("CreationTimeoutSeconds", "301")]
    public void TimeoutBoundsAreEnforced(string member, string value)
    {
        using var fixture = new Fixture();
        fixture.Values[member] = value;
        Invalid(Config(fixture.Values));
    }

    [Theory]
    [InlineData("RacExecutable", "rac.exe")]
    [InlineData("RacWorkingDirectory", ".")]
    [InlineData("RacExecutable", "\0private-path")]
    [InlineData("RacWorkingDirectory", "\0private-path")]
    public void PathsMustBeAbsoluteAndControlFree(string member, string value)
    {
        using var fixture = new Fixture();
        fixture.Values[member] = value;
        Invalid(Config(fixture.Values));
    }

    [Fact]
    public void ExecutableAndDirectoryMustExistWithFixedExecutableName()
    {
        using var fixture = new Fixture();
        fixture.Values["RacExecutable"] = Path.Combine(fixture.Directory, "missing", "rac.exe");
        Invalid(Config(fixture.Values));
        var wrong = Path.Combine(fixture.Directory, "other.exe");
        File.WriteAllText(wrong, "");
        fixture.Values["RacExecutable"] = wrong;
        Invalid(Config(fixture.Values));
        fixture.Values["RacExecutable"] = fixture.Executable;
        fixture.Values["RacWorkingDirectory"] = Path.Combine(fixture.Directory, "missing");
        Invalid(Config(fixture.Values));
    }

    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder()
        .AddInMemoryCollection(values.ToDictionary(pair => Prefix + pair.Key, pair => pair.Value)).Build();

    private static void Invalid(IConfiguration config)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => TargetCreationOneShotHostOptions.Bind(config));
        Assert.Equal("InvalidTargetCreationOneShotHostConfiguration", exception.Message);
        Assert.Null(exception.InnerException);
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "target-creation-host-" + Guid.NewGuid().ToString("N"));
        public string Executable { get; }
        public Guid OperationId { get; } = Guid.NewGuid();
        public Dictionary<string, string?> Values { get; }

        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Executable = Path.Combine(Directory, "rac.exe");
            File.WriteAllText(Executable, "");
            Values = new()
            {
                ["Enabled"] = "true", ["ExclusiveNamespaceConfirmed"] = "true",
                ["OperationId"] = OperationId.ToString(), ["AgentId"] = Guid.NewGuid().ToString(),
                ["CredentialId"] = Guid.NewGuid().ToString(), ["EndpointMappingId"] = Guid.NewGuid().ToString(),
                ["RacObservationMappingId"] = Guid.NewGuid().ToString(),
                ["SqlObservationMappingId"] = Guid.NewGuid().ToString(),
                ["CanonicalClusterId"] = Guid.NewGuid().ToString(), ["RacClusterId"] = Guid.NewGuid().ToString(),
                ["CanonicalSqlServerId"] = Guid.NewGuid().ToString(),
                ["RacExecutable"] = Executable, ["RacWorkingDirectory"] = Directory,
                ["RacEndpoint"] = "server.example:1545", ["SqlServer"] = "server.example,1433",
                ["ExpectedSqlMachineName"] = "server"
            };
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
