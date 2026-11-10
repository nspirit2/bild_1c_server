using BuildSystem.Agent;
using BuildSystem.Contracts;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class TargetObservationHostOptionsTests
{
    private const string Prefix = "Agent:TargetObservation:";

    [Fact]
    public void MissingSectionDefaultsToDisabled() => Assert.False(TargetObservationHostOptions.Bind(Config([])).Enabled);

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void DisabledIgnoresOtherValuesAndUnknownMembers(string? enabled)
    {
        var options = TargetObservationHostOptions.Bind(Config(new()
        {
            ["Enabled"] = enabled, ["AgentId"] = "not-a-guid", ["RacClusterId"] = "not-a-guid", ["Unknown"] = "ignored",
            ["RacExecutable"] = "\0not-a-path", ["TimeoutSeconds"] = "not-an-int"
        }));
        Assert.False(options.Enabled);
        Assert.Equal(Guid.Empty, options.AgentId);
        Assert.Equal("", options.RacExecutable);
    }

    [Theory]
    [InlineData("Enabled", "maybe")]
    [InlineData("Enabled:Child", "true")]
    public void MalformedEnablementRejectsWithRedactedError(string key, string value) =>
        Invalid(Config(new() { [key] = value }));

    [Fact]
    public void ScalarSectionRejects()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Agent:TargetObservation"] = "private-scalar" }).Build();
        Invalid(config);
    }

    [Fact]
    public void EnabledBindingUsesDefaultsAndTakesSnapshot()
    {
        using var fixture = new Fixture();
        var config = Config(fixture.Values);
        var options = TargetObservationHostOptions.Bind(config);
        config[Prefix + "AgentId"] = Guid.NewGuid().ToString();
        Assert.True(options.Enabled);
        Assert.Equal(fixture.Assignment.AgentId, options.AgentId);
        Assert.Equal(fixture.RacClusterId, options.RacClusterId);
        Assert.NotEqual(options.CanonicalClusterId, options.RacClusterId);
        Assert.Equal(fixture.Executable, options.RacExecutable);
        Assert.Equal("MSSQLSERVER", options.ExpectedSqlInstanceName);
        Assert.Equal(30, options.TimeoutSeconds);
        Assert.Equal(60, options.TotalTimeoutSeconds);
    }

    [Theory]
    [InlineData("Unknown", "private-unknown")]
    [InlineData("AgentId", "private-invalid-guid")]
    [InlineData("RacClusterId", "private-invalid-guid")]
    [InlineData("TimeoutSeconds", "private-invalid-integer")]
    [InlineData("ExpectedSqlInstanceName:Unexpected", "private-nested")]
    public void EnabledRejectsUnknownOrMalformedMembers(string key, string value)
    {
        using var fixture = new Fixture();
        fixture.Values[key] = value;
        Invalid(Config(fixture.Values));
    }

    [Theory]
    [InlineData("AgentId")]
    [InlineData("CanonicalClusterId")]
    [InlineData("RacClusterId")]
    [InlineData("CanonicalSqlServerId")]
    [InlineData("RacEndpointMappingId")]
    [InlineData("SqlEndpointMappingId")]
    public void EveryLocalPinMustBeNonempty(string key)
    {
        using var fixture = new Fixture();
        fixture.Values[key] = Guid.Empty.ToString();
        Invalid(Config(fixture.Values));
    }

    [Fact]
    public void EnabledRequiresPhysicalRacClusterId()
    {
        using var fixture = new Fixture();
        fixture.Values.Remove("RacClusterId");
        Invalid(Config(fixture.Values));
    }

    [Fact]
    public void DisabledAcceptsMissingPhysicalRacClusterId()
    {
        var options = TargetObservationHostOptions.Bind(Config(new() { ["Enabled"] = "false" }));
        Assert.False(options.Enabled);
        Assert.Equal(Guid.Empty, options.RacClusterId);
    }

    [Theory]
    [InlineData("RacExecutable", "rac.exe")]
    [InlineData("RacWorkingDirectory", ".")]
    [InlineData("RacWorkingDirectory", "\0private-invalid-path")]
    public void RejectsRelativeAndControlPaths(string key, string value)
    {
        using var fixture = new Fixture();
        fixture.Values[key] = value;
        Invalid(Config(fixture.Values));
    }

    [Fact]
    public void RequiresExistingRacExeAndExistingDirectory()
    {
        using var fixture = new Fixture();
        fixture.Values["RacExecutable"] = Path.Combine(fixture.Directory, "missing", "rac.exe");
        Invalid(Config(fixture.Values));
        var wrongName = Path.Combine(fixture.Directory, "other.exe");
        File.WriteAllText(wrongName, "");
        fixture.Values["RacExecutable"] = wrongName;
        Invalid(Config(fixture.Values));
        fixture.Values["RacExecutable"] = fixture.Executable;
        fixture.Values["RacWorkingDirectory"] = Path.Combine(fixture.Directory, "missing");
        Invalid(Config(fixture.Values));
    }

    [Theory]
    [InlineData("server")]
    [InlineData("server:0")]
    [InlineData("server:65536")]
    [InlineData("server:1:2")]
    [InlineData("server: 1545")]
    [InlineData("server:1545;secret=value")]
    public void RejectsMalformedRacEndpoints(string value)
    {
        using var fixture = new Fixture();
        fixture.Values["RacEndpoint"] = value;
        Invalid(Config(fixture.Values));
    }

    [Theory]
    [InlineData("server;Password=private-secret")]
    [InlineData("Server=server")]
    [InlineData("server name")]
    [InlineData("server\n")]
    [InlineData("server\\instance,1433")]
    [InlineData("server,")]
    [InlineData("server,65536")]
    public void RejectsConnectionStringsAndMalformedSqlEndpoints(string value)
    {
        using var fixture = new Fixture();
        fixture.Values["SqlServer"] = value;
        Invalid(Config(fixture.Values));
    }

    [Theory]
    [InlineData("server")]
    [InlineData("server\\INSTANCE")]
    [InlineData("server.example,1433")]
    public void AcceptsStructuredSqlEndpointsAndExistingOperatorPaths(string value)
    {
        using var fixture = new Fixture();
        fixture.Values["SqlServer"] = value;
        Assert.Equal(value, TargetObservationHostOptions.Bind(Config(fixture.Values)).SqlServer);
    }

    [Theory]
    [InlineData("ExpectedSqlMachineName", "")]
    [InlineData("ExpectedSqlInstanceName", "\tprivate-instance")]
    [InlineData("TimeoutSeconds", "0")]
    [InlineData("TimeoutSeconds", "61")]
    [InlineData("TotalTimeoutSeconds", "0")]
    [InlineData("TotalTimeoutSeconds", "121")]
    public void RejectsInvalidIdentityAndTimeoutBounds(string key, string value)
    {
        using var fixture = new Fixture();
        fixture.Values[key] = value;
        Invalid(Config(fixture.Values));
        fixture.Values["ExpectedSqlMachineName"] = new string('x', 129);
        Invalid(Config(fixture.Values));
    }

    [Theory]
    [InlineData("RacExecutable", 4097)]
    [InlineData("RacWorkingDirectory", 4097)]
    [InlineData("RacEndpoint", 256)]
    [InlineData("SqlServer", 256)]
    public void RejectsOversizedPathsAndEndpoints(string key, int length)
    {
        using var fixture = new Fixture();
        fixture.Values[key] = new string('x', length);
        Invalid(Config(fixture.Values));
    }

    [Fact]
    public void OptionsToStringOmitsAllLocalDetails()
    {
        using var fixture = new Fixture();
        Assert.Equal("TargetObservationHostOptions [details omitted]",
            TargetObservationHostOptions.Bind(Config(fixture.Values)).ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task EachPinMismatchRejectsBeforeInnerCredentialsOrIo(int pin)
    {
        using var fixture = new Fixture();
        var inner = new FakeExecutor();
        var executor = new TargetObservationPinnedExecutor(TargetObservationHostOptions.Bind(Config(fixture.Values)), inner);
        var a = fixture.Assignment;
        var mismatch = pin switch
        {
            0 => a with { AgentId = Guid.NewGuid() },
            1 => a with { CanonicalClusterId = Guid.NewGuid() },
            2 => a with { CanonicalSqlServerId = Guid.NewGuid() },
            3 => a with { RacEndpointMappingId = Guid.NewGuid() },
            _ => a with { SqlEndpointMappingId = Guid.NewGuid() }
        };
        await Assert.ThrowsAsync<TargetObservationExecutionException>(() => executor.ObserveAsync(mismatch));
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task ValidAssignmentPassesOriginalPayloadAndTokenExactlyOnce()
    {
        using var fixture = new Fixture();
        var inner = new FakeExecutor();
        var executor = new TargetObservationPinnedExecutor(TargetObservationHostOptions.Bind(Config(fixture.Values)), inner);
        using var cancellation = new CancellationTokenSource();
        var result = await executor.ObserveAsync(fixture.Assignment, cancellation.Token);
        Assert.Equal(1, inner.Calls);
        Assert.Same(fixture.Assignment, inner.Assignment);
        Assert.Same(fixture.Assignment, result.Assignment);
        Assert.Equal(cancellation.Token, inner.Token);
    }

    [Fact]
    public async Task DisabledAndEmptyAssignmentsRejectBeforeInner()
    {
        using var fixture = new Fixture();
        var inner = new FakeExecutor();
        var disabled = new TargetObservationPinnedExecutor(new(), inner);
        await Assert.ThrowsAsync<TargetObservationExecutionException>(() => disabled.ObserveAsync(fixture.Assignment));
        var enabled = new TargetObservationPinnedExecutor(TargetObservationHostOptions.Bind(Config(fixture.Values)), inner);
        await Assert.ThrowsAsync<TargetObservationExecutionException>(() => enabled.ObserveAsync(null!));
        await Assert.ThrowsAsync<TargetObservationExecutionException>(() => enabled.ObserveAsync(fixture.Assignment with { AgentId = Guid.Empty }));
        Assert.Equal(0, inner.Calls);
    }

    private static IConfigurationRoot Config(Dictionary<string, string?> values) => new ConfigurationBuilder()
        .AddInMemoryCollection(values.Select(pair => new KeyValuePair<string, string?>(Prefix + pair.Key, pair.Value))).Build();

    private static void Invalid(IConfiguration config)
    {
        var error = Assert.Throws<InvalidOperationException>(() => TargetObservationHostOptions.Bind(config));
        Assert.Equal("Invalid target observation host configuration.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-", error.ToString());
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "target-observation-options-" + Guid.NewGuid().ToString("N"));
        public string Executable => Path.Combine(Directory, "rac.exe");
        public TargetObservationAssignment Assignment { get; } = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        public Guid RacClusterId { get; } = Guid.NewGuid();
        public Dictionary<string, string?> Values { get; }
        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(Executable, "");
            Values = new()
            {
                ["Enabled"] = "true", ["AgentId"] = Assignment.AgentId.ToString(),
                ["CanonicalClusterId"] = Assignment.CanonicalClusterId.ToString(),
                ["RacClusterId"] = RacClusterId.ToString(),
                ["CanonicalSqlServerId"] = Assignment.CanonicalSqlServerId.ToString(),
                ["RacEndpointMappingId"] = Assignment.RacEndpointMappingId.ToString(),
                ["SqlEndpointMappingId"] = Assignment.SqlEndpointMappingId.ToString(),
                ["RacExecutable"] = Executable, ["RacWorkingDirectory"] = Directory,
                ["RacEndpoint"] = "localhost:1545", ["SqlServer"] = "localhost,1433",
                ["ExpectedSqlMachineName"] = "private-machine"
            };
        }
        public void Dispose()
        {
            var path = Path.GetFullPath(Directory);
            if (Path.GetDirectoryName(path) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()) ||
                !Path.GetFileName(path).StartsWith("target-observation-options-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected temporary fixture path.");
            System.IO.Directory.Delete(path, recursive: true);
        }
    }

    private sealed class FakeExecutor : ITargetObservationExecutor
    {
        public int Calls { get; private set; }
        public TargetObservationAssignment? Assignment { get; private set; }
        public CancellationToken Token { get; private set; }
        public Task<ReportTargetObservation> ObserveAsync(TargetObservationAssignment assignment, CancellationToken ct)
        {
            Calls++;
            Assignment = assignment;
            Token = ct;
            return Task.FromResult(new ReportTargetObservation(Guid.NewGuid(), assignment, Guid.NewGuid(), Guid.NewGuid(),
                TargetObservationOutcome.BothObservedAbsent, DateTimeOffset.UtcNow));
        }
    }
}