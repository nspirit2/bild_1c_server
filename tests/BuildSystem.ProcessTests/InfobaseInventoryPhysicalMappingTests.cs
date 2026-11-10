using BuildSystem.Agent.Execution;
using BuildSystem.Agent.Tools;
using Xunit;

namespace BuildSystem.ProcessTests;

public sealed class InfobaseInventoryPhysicalMappingTests
{
    private static readonly Guid MappingId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid CanonicalId = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid PhysicalId = Guid.Parse("30000000-0000-0000-0000-000000000003");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitMappingUsesPhysicalArgumentAndRetainsCanonicalCorrelation(bool present)
    {
        var scope = Scope(CanonicalId);
        var runner = new CapturingRunner(present
            ? $"infobase : {Guid.NewGuid():D}\nname : {scope.ExpectedInfobaseName}\n"
            : "");
        var observer = new InfobaseInventoryObserver(runner, Options(CanonicalId, PhysicalId));

        var result = await observer.ObserveAsync(scope, Credentials());

        Assert.NotEqual(CanonicalId, PhysicalId);
        Assert.Equal(1, runner.Calls);
        Assert.NotNull(runner.Command);
        Assert.Equal("--cluster=" + PhysicalId.ToString("D"), runner.Command.Arguments[3].Value);
        Assert.Same(scope, result.Scope);
        Assert.Equal(CanonicalId, result.Scope.ClusterId);
        Assert.Equal(MappingId, result.Scope.EndpointMappingId);
        Assert.Equal(present ? InfobaseInventoryObservation.Present : InfobaseInventoryObservation.Absent,
            result.Observation);
        Assert.Equal(present ? 1 : 0, result.Entries.Count);
        InfobaseInventoryReadback.RequireMatch(result, scope);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitMappingRejectsWrongCanonicalBeforeRunner(bool usePhysicalIdAsCanonical)
    {
        var runner = new CapturingRunner("");
        var scope = Scope(usePhysicalIdAsCanonical ? PhysicalId : Guid.NewGuid());
        var observer = new InfobaseInventoryObserver(runner, Options(CanonicalId, PhysicalId));

        var error = await Assert.ThrowsAsync<InfobaseInventoryReadbackException>(() =>
            observer.ObserveAsync(scope, Credentials()));

        Assert.Equal("InfobaseInventoryScopeMismatch", error.Code);
        Assert.Equal(0, runner.Calls);
        Assert.Null(runner.Command);
    }

    [Fact]
    public async Task ExplicitMappingStillRejectsEndpointMappingMismatchBeforeRunner()
    {
        var runner = new CapturingRunner("");
        var observer = new InfobaseInventoryObserver(runner, Options(CanonicalId, PhysicalId));
        var scope = Scope(CanonicalId) with { EndpointMappingId = Guid.NewGuid() };

        var error = await Assert.ThrowsAsync<InfobaseInventoryReadbackException>(() =>
            observer.ObserveAsync(scope, Credentials()));

        Assert.Equal("InfobaseInventoryScopeMismatch", error.Code);
        Assert.Equal(0, runner.Calls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void PartialOrEmptyMappingIsRejectedByConstructor(int invalidCase)
    {
        var (canonical, physical) = invalidCase switch
        {
            1 => ((Guid?)CanonicalId, (Guid?)null),
            2 => ((Guid?)null, (Guid?)PhysicalId),
            3 => ((Guid?)Guid.Empty, (Guid?)PhysicalId),
            4 => ((Guid?)CanonicalId, (Guid?)Guid.Empty),
            5 => ((Guid?)Guid.Empty, (Guid?)Guid.Empty),
            6 => ((Guid?)Guid.Empty, (Guid?)null),
            _ => ((Guid?)null, (Guid?)Guid.Empty)
        };
        var runner = new CapturingRunner("");

        var error = Assert.Throws<ArgumentException>(() =>
            new InfobaseInventoryObserver(runner, Options(canonical, physical)));

        Assert.Equal("options", error.ParamName);
        Assert.StartsWith("invalid_observer_options", error.Message);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task DefaultNullMappingPreservesLegacyScopeClusterArgument()
    {
        var options = Options();
        Assert.Null(options.CanonicalClusterId);
        Assert.Null(options.RacClusterId);
        var runner = new CapturingRunner("");
        var observer = new InfobaseInventoryObserver(runner, options);
        var scopes = new[] { Scope(CanonicalId), Scope(PhysicalId) };

        foreach (var scope in scopes)
        {
            var result = await observer.ObserveAsync(scope, Credentials());
            Assert.NotNull(runner.Command);
            Assert.Equal("--cluster=" + scope.ClusterId.ToString("D"), runner.Command.Arguments[3].Value);
            Assert.Same(scope, result.Scope);
            Assert.Equal(InfobaseInventoryObservation.Absent, result.Observation);
        }
        Assert.Equal(2, runner.Calls);
    }

    [Fact]
    public async Task ExplicitEqualClusterIdsAreAlsoValid()
    {
        var runner = new CapturingRunner("");
        var observer = new InfobaseInventoryObserver(runner, Options(CanonicalId, CanonicalId));
        var result = await observer.ObserveAsync(Scope(CanonicalId), Credentials());

        Assert.NotNull(runner.Command);
        Assert.Equal("--cluster=" + CanonicalId.ToString("D"), runner.Command.Arguments[3].Value);
        Assert.Equal(CanonicalId, result.Scope.ClusterId);
    }

    private static InfobaseInventoryReadbackScope Scope(Guid clusterId)
    {
        var reservation = Guid.NewGuid();
        return new(Guid.NewGuid(), 1, Guid.NewGuid(), clusterId, MappingId, reservation,
            Guid.NewGuid(), $"bs_test_{reservation:N}");
    }

    private static InfobaseInventoryObserverOptions Options(Guid? canonical = null, Guid? physical = null) => new()
    {
        TrustedRacToolId = "operator-rac",
        Endpoint = "rac.example:1545",
        EndpointMappingId = MappingId,
        CanonicalClusterId = canonical,
        RacClusterId = physical,
        WorkingDirectory = Path.GetTempPath(),
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static InfobaseInventoryObserverCredentials Credentials() => new("cluster-user", "cluster-password");

    private sealed class CapturingRunner(string stdout) : IProcessRunner
    {
        public int Calls { get; private set; }
        public ProcessCommand? Command { get; private set; }

        public async Task<ProcessResult> RunAsync(ProcessCommand command, Func<ProcessOutput, Task>? onOutput = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Command = command;
            await onOutput!(new(ProcessOutputStream.StandardOutput, stdout));
            return new(123, 0, ProcessCompletion.Exited, false);
        }
    }
}
