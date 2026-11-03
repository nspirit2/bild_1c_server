using System.Reflection;
using BuildSystem.Agent.Execution;
using BuildSystem.Agent.Tools;
using Xunit;

namespace BuildSystem.ProcessTests;

public sealed class TargetPresenceAssessmentTests
{
    private static TargetPresenceAssessmentScope Scope() => new(Guid.NewGuid(), 7, Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static string Name(TargetPresenceAssessmentScope scope) => $"bs_test_{scope.ReservationId:N}";
    private static InfobaseInventoryReadbackScope RacScope(TargetPresenceAssessmentScope scope) => new(
        scope.OperationId, scope.FencingToken, scope.TargetIncarnationId, scope.CanonicalClusterId,
        scope.RacEndpointMappingId, scope.ReservationId, scope.RacObservationId, Name(scope));
    private static SqlDatabaseObservationScope SqlScope(TargetPresenceAssessmentScope scope) => new(
        scope.OperationId, scope.FencingToken, scope.TargetIncarnationId, scope.CanonicalSqlServerId,
        scope.SqlEndpointMappingId, scope.ReservationId, scope.SqlObservationId, Name(scope));
    private static InfobaseInventoryReadbackResult Rac(TargetPresenceAssessmentScope scope, bool present = false) =>
        InfobaseInventoryReadback.Parse(RacScope(scope), 0, false,
            present ? $"infobase: {Guid.NewGuid():D}\nname: {Name(scope)}\n" : "");

    // Test-only reflection exercises correlation failures that real observers reject upstream.
    // Do not widen production constructors or claim these fixtures authenticate provider output.
    private static T Construct<T>(params object?[] arguments) => (T)Activator.CreateInstance(typeof(T),
        BindingFlags.Instance | BindingFlags.NonPublic, null, arguments, null)!;
    private static SqlDatabaseObservationResult Sql(SqlDatabaseObservationScope scope, bool present = false,
        string? actualName = null) => Construct<SqlDatabaseObservationResult>(scope,
            present ? new SqlDatabaseObservedIdentity(actualName ?? scope.ExpectedDatabaseName, 9,
                new DateTime(2025, 1, 2), "OFFLINE") : null);
    private static InfobaseInventoryReadbackResult RacWithScope(InfobaseInventoryReadbackScope scope) =>
        Construct<InfobaseInventoryReadbackResult>(scope, Array.Empty<InfobaseInventoryEntry>());
    private static void Reject(string code, Action action)
    {
        var error = Assert.Throws<TargetPresenceAssessmentException>(action);
        Assert.Equal(code, error.Code);
        Assert.Equal(code, error.Message);
        Assert.Equal($"TargetPresenceAssessmentException: {code}", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(false, false, TargetPresenceObservation.BothObservedAbsent)]
    [InlineData(true, false, TargetPresenceObservation.InfobaseObservedPresent)]
    [InlineData(false, true, TargetPresenceObservation.SqlDatabaseObservedPresent)]
    [InlineData(true, true, TargetPresenceObservation.BothObservedPresent)]
    public void CompleteOccupancyMatrixNeverGrantsAuthority(bool racPresent, bool sqlPresent,
        TargetPresenceObservation observation)
    {
        var expected = Scope();
        var result = TargetPresenceAssessment.Assess(expected, Rac(expected, racPresent), Sql(SqlScope(expected), sqlPresent));
        Assert.Same(expected, result.Scope);
        Assert.Equal(observation, result.Observation);
        Assert.False(result.Executable);
        Assert.True(result.ScheduledJobsMustRemainBlocked);
    }

    [Fact]
    public void EveryExpectedPinMustMatchIndividually()
    {
        var expected = Scope();
        var rac = Rac(expected);
        var sql = Sql(SqlScope(expected));
        TargetPresenceAssessmentScope[] mismatches =
        [
            expected with { OperationId = Guid.NewGuid() }, expected with { FencingToken = 8 },
            expected with { TargetIncarnationId = Guid.NewGuid() }, expected with { ReservationId = Guid.NewGuid() },
            expected with { CanonicalClusterId = Guid.NewGuid() }, expected with { CanonicalSqlServerId = Guid.NewGuid() },
            expected with { RacEndpointMappingId = Guid.NewGuid() }, expected with { SqlEndpointMappingId = Guid.NewGuid() },
            expected with { RacObservationId = Guid.NewGuid() }, expected with { SqlObservationId = Guid.NewGuid() }
        ];
        foreach (var mismatch in mismatches)
            Reject("TargetPresenceScopeMismatch", () => TargetPresenceAssessment.Assess(mismatch, rac, sql));
    }

    [Fact]
    public void EveryRacScopePinAndExactGeneratedNameMustMatchIndividually()
    {
        var expected = Scope();
        var scope = RacScope(expected);
        InfobaseInventoryReadbackScope[] mismatches =
        [
            scope with { OperationId = Guid.NewGuid() }, scope with { FencingToken = 8 },
            scope with { TargetIncarnationId = Guid.NewGuid() }, scope with { ReservationId = Guid.NewGuid() },
            scope with { ClusterId = Guid.NewGuid() }, scope with { EndpointMappingId = Guid.NewGuid() },
            scope with { ObservationId = Guid.NewGuid() }, scope with { ObservationId = expected.SqlObservationId },
            scope with { ExpectedInfobaseName = "private-custom-target" },
            scope with { ExpectedInfobaseName = scope.ExpectedInfobaseName.ToUpperInvariant() },
            scope with { ExpectedInfobaseName = scope.ExpectedInfobaseName + " " }
        ];
        foreach (var mismatch in mismatches)
            Reject("TargetPresenceScopeMismatch", () => TargetPresenceAssessment.Assess(expected,
                RacWithScope(mismatch), Sql(SqlScope(expected))));
    }

    [Fact]
    public void EverySqlScopePinAndExactGeneratedNameMustMatchIndividually()
    {
        var expected = Scope();
        var scope = SqlScope(expected);
        SqlDatabaseObservationScope[] mismatches =
        [
            scope with { OperationId = Guid.NewGuid() }, scope with { FencingToken = 8 },
            scope with { TargetIncarnationId = Guid.NewGuid() }, scope with { ReservationId = Guid.NewGuid() },
            scope with { SqlServerId = Guid.NewGuid() }, scope with { EndpointMappingId = Guid.NewGuid() },
            scope with { ObservationId = Guid.NewGuid() }, scope with { ObservationId = expected.RacObservationId },
            scope with { ExpectedDatabaseName = "private-custom-target" },
            scope with { ExpectedDatabaseName = scope.ExpectedDatabaseName.ToUpperInvariant() },
            scope with { ExpectedDatabaseName = scope.ExpectedDatabaseName + " " }
        ];
        foreach (var mismatch in mismatches)
            Reject("TargetPresenceScopeMismatch", () => TargetPresenceAssessment.Assess(expected, Rac(expected), Sql(mismatch)));
    }

    [Fact]
    public void EveryExpectedGuidMustBeNonemptyFencePositiveAndObservationIdsDistinct()
    {
        var expected = Scope();
        TargetPresenceAssessmentScope[] invalid =
        [
            expected with { OperationId = Guid.Empty }, expected with { FencingToken = 0 },
            expected with { FencingToken = -1 }, expected with { TargetIncarnationId = Guid.Empty },
            expected with { ReservationId = Guid.Empty }, expected with { CanonicalClusterId = Guid.Empty },
            expected with { CanonicalSqlServerId = Guid.Empty }, expected with { RacEndpointMappingId = Guid.Empty },
            expected with { SqlEndpointMappingId = Guid.Empty }, expected with { RacObservationId = Guid.Empty },
            expected with { SqlObservationId = Guid.Empty }, expected with { SqlObservationId = expected.RacObservationId }
        ];
        foreach (var scope in invalid)
            Reject("InvalidTargetPresenceScope", () => TargetPresenceAssessment.Assess(scope, Rac(expected), Sql(SqlScope(expected))));
        Reject("InvalidTargetPresenceScope", () => TargetPresenceAssessment.Assess(null!, null!, null!));
    }

    [Fact]
    public void MissingObservationsNeverProduceSuccessfulDefault()
    {
        var expected = Scope();
        Reject("TargetPresenceObservationUnavailable", () => TargetPresenceAssessment.Assess(expected, null!, Sql(SqlScope(expected))));
        Reject("TargetPresenceObservationUnavailable", () => TargetPresenceAssessment.Assess(expected, Rac(expected), null!));
        Reject("TargetPresenceObservationUnavailable", () => TargetPresenceAssessment.Assess(expected, null!, null!));
    }

    [Fact]
    public void EndpointMappingIdentifiersMayCoincideAcrossIndependentDomains()
    {
        var initial = Scope();
        var expected = initial with { SqlEndpointMappingId = initial.RacEndpointMappingId };
        Assert.Equal(TargetPresenceObservation.BothObservedAbsent,
            TargetPresenceAssessment.Assess(expected, Rac(expected), Sql(SqlScope(expected))).Observation);
    }

    [Fact]
    public void SimulatedSameScopeReplayPassesBecauseCorrelationIsNotFreshnessOrAuthentication()
    {
        var expected = Scope();
        var oldRac = Rac(expected);
        var oldSql = Sql(SqlScope(expected));
        var first = TargetPresenceAssessment.Assess(expected, oldRac, oldSql);
        // No clock, nonce consumption or external state exists here; reuse cannot be detected.
        var replay = TargetPresenceAssessment.Assess(expected, oldRac, oldSql);
        Assert.Equal(first.Observation, replay.Observation);
        Assert.False(replay.Executable);
        Assert.True(replay.ScheduledJobsMustRemainBlocked);
    }

    [Fact]
    public void RacCaseAliasIsRejectedUpstreamNotComposedIntoPhantomAbsence()
    {
        var expected = Scope();
        var error = Assert.Throws<InfobaseInventoryReadbackException>(() => InfobaseInventoryReadback.Parse(
            RacScope(expected), 0, false, $"infobase: {Guid.NewGuid():D}\nname: {Name(expected).ToUpperInvariant()}\n"));
        Assert.Equal("AmbiguousInfobaseInventoryTarget", error.Code);
    }

    [Fact]
    public void SqlCollationMatchedAliasRemainsPresentWithoutExportingFingerprint()
    {
        var expected = Scope();
        var result = TargetPresenceAssessment.Assess(expected, Rac(expected),
            Sql(SqlScope(expected), true, "PRIVATE-collation-alias"));
        Assert.Equal(TargetPresenceObservation.SqlDatabaseObservedPresent, result.Observation);
        Assert.DoesNotContain("PRIVATE", result.ToString());
    }

    [Fact]
    public void ExactMinimalResultAllowlistIsSealedGetterOnlyAndRedacted()
    {
        var expected = Scope();
        var result = TargetPresenceAssessment.Assess(expected, Rac(expected), Sql(SqlScope(expected)));
        var type = typeof(TargetPresenceAssessmentResult);
        Assert.True(type.IsSealed);
        Assert.Empty(type.GetConstructors());
        Assert.Equal(new[] { "Executable", "Observation", "ScheduledJobsMustRemainBlocked", "Scope" },
            type.GetProperties().Select(p => p.Name).OrderBy(n => n).ToArray());
        Assert.All(type.GetProperties(), property => Assert.Null(property.SetMethod));
        Assert.Equal(new[] { "ToString", "get_Executable", "get_Observation", "get_ScheduledJobsMustRemainBlocked", "get_Scope" }
                .OrderBy(n => n),
            type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(m => m.Name).OrderBy(n => n));
        Assert.Equal(new[] { "BothObservedAbsent", "InfobaseObservedPresent", "SqlDatabaseObservedPresent", "BothObservedPresent" },
            Enum.GetNames<TargetPresenceObservation>());
        foreach (var text in new[] { result.ToString(), expected.ToString() })
        {
            Assert.DoesNotContain(Name(expected), text);
            foreach (var pin in typeof(TargetPresenceAssessmentScope).GetProperties().Select(p => p.GetValue(expected)))
                Assert.DoesNotContain(pin!.ToString()!, text);
        }
    }
}
