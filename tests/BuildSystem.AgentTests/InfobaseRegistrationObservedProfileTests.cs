using System.Reflection;
using System.Text.Json;
using BuildSystem.Agent.Execution;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class InfobaseRegistrationObservedProfileTests
{
    private const string PermissionCanary = "PERMISSION_CANARY_NOT_A_REAL_SECRET";
    private const string ConnectionCanary = "CONNECTION_CANARY_NOT_A_REAL_SECRET";
    private const string Observed = InfobaseRegistrationReadback.ObservedProfileId;
    private const string Invalid = "InfobaseRegistrationInvalidReadback";

    private static InfobaseRegistrationReadbackScope Scope()
    {
        var reservation = Guid.NewGuid();
        var name = $"bs_test_{reservation:N}";
        return new(Guid.NewGuid(), 1, Guid.NewGuid(), reservation, Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.Parse("abcdef12-3456-4789-abcd-1234567890ab"), Guid.NewGuid(), name, "sql-host\\instance,1433", name);
    }

    // RECONSTRUCTED SHAPE ONLY, not raw RAC output or byte-exact real whitespace. The authorized
    // 8.3.22.2557 sample had 21 fields and two terminal blank lines; extras were redacted on Windows
    // before transfer. No sampled secret values are saved here. The sampled bs_smoke target cannot
    // be adopted; all identity fields here use a synthetic reservation-consistent bs_test name.
    private static string[] Lines(InfobaseRegistrationReadbackScope scope) =>
    [
        $"infobase: {scope.InfobaseId:D}", $"name: {scope.ExpectedInfobaseName}",
        "dbms: MSSQLServer", $"db-server: {scope.ExpectedSqlServer}",
        $"db-name: {scope.ExpectedDatabaseName}", "scheduled-jobs-deny: on",
        "db-user:", "security-level: reconstructed", "license-distribution: reconstructed",
        "sessions-deny: on", "denied-from:", "denied-message:", "denied-parameter:", "denied-to:",
        $"permission-code: {PermissionCanary}",
        $"external-session-manager-connection-string: {ConnectionCanary}",
        "external-session-manager-required: reconstructed", "security-profile-name:",
        "safe-mode-security-profile-name:", "reserve-working-processes: reconstructed", "descr: reconstructed"
    ];

    private static string Output(InfobaseRegistrationReadbackScope scope) => string.Join('\n', Lines(scope)) + "\n\n";
    private static InfobaseRegistrationReadbackResult Parse(InfobaseRegistrationReadbackScope scope, string output) =>
        InfobaseRegistrationReadback.Parse(scope, 0, false, output, Observed);
    private static void Failure(string code, InfobaseRegistrationReadbackScope scope, string output,
        int exit = 0, bool truncated = false, string profile = Observed)
    {
        var exception = Assert.Throws<InfobaseRegistrationReadbackException>(() =>
            InfobaseRegistrationReadback.Parse(scope, exit, truncated, output, profile));
        Assert.Equal(code, exception.Code);
        Assert.Equal(code, exception.Message);
        Assert.DoesNotContain(PermissionCanary, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ConnectionCanary, exception.ToString(), StringComparison.Ordinal);
        var json = JsonSerializer.Serialize(new { exception.Code, exception.Message, exception.Data });
        Assert.DoesNotContain(PermissionCanary, json, StringComparison.Ordinal);
        Assert.DoesNotContain(ConnectionCanary, json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(false, "\n")]
    [InlineData(false, "\n\n")]
    [InlineData(false, "\n \t\n\t")]
    [InlineData(true, "\n\n")]
    [InlineData(true, "\n \t\n\t")]
    public void ReconstructedShapeIsExplicitAndNeverAuthority(bool crlf, string suffix)
    {
        var scope = Scope();
        var output = string.Join('\n', Lines(scope)) + suffix;
        if (crlf) output = output.Replace("\n", "\r\n", StringComparison.Ordinal);
        var result = Parse(scope, output);
        Assert.Equal(Observed, result.ProfileId);
        Assert.Equal(scope, result.Scope);
        Assert.Equal(scope.InfobaseId, result.InfobaseId);
        Assert.Equal(scope.ExpectedInfobaseName, result.InfobaseName);
        Assert.Equal(scope.ExpectedSqlServer, result.SqlServer);
        Assert.Equal(scope.ExpectedDatabaseName, result.DatabaseName);
        Assert.Equal("MSSQLServer", result.Dbms);
        Assert.True(result.ScheduledJobsBlocked);
        Assert.True(result.RequiresIndependentVerification);
        Assert.False(result.Executable);
        Assert.False(result.OwnershipEstablished);
        Assert.False(result.RestoreAllowed);
        Assert.False(result.RetryAllowed);
        Assert.False(result.FreshAuthority);
        InfobaseRegistrationReadback.RequireMatch(result, scope, Observed);
        InfobaseRegistrationReadback.RequireMatch(result, scope); // Legacy intentionally scope-only.
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(PermissionCanary, json, StringComparison.Ordinal);
        Assert.DoesNotContain(ConnectionCanary, json, StringComparison.Ordinal);
        Assert.DoesNotContain(PermissionCanary, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ConnectionCanary, result.ToString(), StringComparison.Ordinal);
        Assert.Empty(typeof(InfobaseRegistrationReadbackResult).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
    }

    public static IEnumerable<object[]> AllKeys() => Lines(Scope()).Select(line => new object[] { line[..line.IndexOf(':')] });

    [Theory]
    [MemberData(nameof(AllKeys))]
    public void EveryOneOf21KeysIsRequiredAndUnique(string key)
    {
        var scope = Scope();
        var lines = Lines(scope);
        var line = Assert.Single(lines, line => line.StartsWith(key + ":", StringComparison.Ordinal));
        Failure(Invalid, scope, string.Join('\n', lines.Where(candidate => candidate != line)) + "\n\n");
        Failure(Invalid, scope, string.Join('\n', lines.Append(line)) + "\n\n");
    }

    [Fact]
    public void SyntheticDefaultRemainsClosedSixWithoutDetectionOrFallback()
    {
        var scope = Scope();
        var exception = Assert.Throws<InfobaseRegistrationReadbackException>(() =>
            InfobaseRegistrationReadback.Parse(scope, 0, false, Output(scope)));
        Assert.Equal(Invalid, exception.Code);
        Failure(Invalid, scope, Output(scope), profile: InfobaseRegistrationReadback.ProfileId);
        Failure(Invalid, scope, string.Join('\n', Lines(scope).Take(6)));
        var six = string.Join('\n', Lines(scope).Take(6)) + "\n";
        Assert.Equal(InfobaseRegistrationReadback.ProfileId,
            InfobaseRegistrationReadback.Parse(scope, 0, false, six).ProfileId);
        Failure(Invalid, scope, six + "\n", profile: InfobaseRegistrationReadback.ProfileId);
        Failure(Invalid, scope, six + " \t", profile: InfobaseRegistrationReadback.ProfileId);
    }

    [Theory]
    [InlineData("unknown: hidden")]
    [InlineData("DB-USER: hidden")]
    [InlineData("malformed")]
    [InlineData("permission-code: bad\tvalue")]
    [InlineData("permission-code: bad\0value")]
    [InlineData("permission-code: bad\u001bvalue")]
    [InlineData("permission-code: bad\rvalue")]
    public void UnknownMalformedAndControlFieldsRejectWithoutLeaking(string badLine)
    {
        var scope = Scope();
        var lines = Lines(scope).Where(line => !line.StartsWith("permission-code:", StringComparison.Ordinal));
        Failure(Invalid, scope, string.Join('\n', lines.Append(badLine)) + "\n\n");
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(10)] [InlineData(20)]
    public void BlankLinesOnlyAfterAllFields(int position)
    {
        var scope = Scope();
        var lines = Lines(scope).ToList();
        lines.Insert(position, " \t");
        Failure(Invalid, scope, string.Join('\n', lines));
    }

    [Fact]
    public void ExtraValuesMayBeEmptyButAreBoundedAndDiscarded()
    {
        var scope = Scope();
        var output = Output(scope);
        Assert.Equal(Observed, Parse(scope, output.Replace(PermissionCanary, "", StringComparison.Ordinal)
            .Replace(ConnectionCanary, "", StringComparison.Ordinal)).ProfileId);
        Assert.Equal(Observed, Parse(scope, output.Replace(PermissionCanary, new string('x', 4096), StringComparison.Ordinal)).ProfileId);
        Failure(Invalid, scope, output.Replace(PermissionCanary, new string('x', 4097), StringComparison.Ordinal));
        Failure(Invalid, scope, new string('x', 16385));
        Failure(Invalid, scope, null!);
    }

    [Theory]
    [InlineData(1, false)] [InlineData(-1, false)] [InlineData(0, true)] [InlineData(1, true)]
    public void FailedOrTruncatedCaptureIsUnavailable(int exit, bool truncated)
    {
        var scope = Scope();
        Failure("InfobaseRegistrationUnavailable", scope, Output(scope), exit, truncated);
    }

    [Theory]
    [InlineData("")] [InlineData("unknown")]
    [InlineData("RAC-registration-8.3.22.2557-v1")]
    [InlineData("rac-registration-8.3.22.2557-v1 ")]
    [InlineData(null)]
    public void UnknownProfileRejectsBeforeParsingOrCaptureChecks(string? profile)
    {
        Failure("InfobaseRegistrationInvalidProfile", null!, null!, 1, true, profile!);
        Assert.Equal("InfobaseRegistrationInvalidProfile",
            Assert.Throws<InfobaseRegistrationReadbackException>(() =>
                InfobaseRegistrationReadback.RequireMatch(null!, null!, profile!)).Code);
    }

    [Theory]
    [InlineData("infobase")] [InlineData("name")] [InlineData("db-server")]
    [InlineData("db-name")] [InlineData("dbms")] [InlineData("scheduled-jobs-deny")]
    public void SixRequiredValuesKeepStrictIdentityAndJobsSemantics(string key)
    {
        var scope = Scope();
        var lines = Lines(scope);
        var index = Array.FindIndex(lines, line => line.StartsWith(key + ":", StringComparison.Ordinal));
        lines[index] = key + ": " + (key == "infobase" ? Guid.NewGuid().ToString("D") : "wrong");
        Failure(key == "scheduled-jobs-deny" ? "InfobaseRegistrationJobsNotBlocked" :
            "InfobaseRegistrationTargetMismatch", scope, string.Join('\n', lines));
        lines[index] = key + ":";
        Failure(Invalid, scope, string.Join('\n', lines));
    }

    [Fact]
    public void CanonicalUuidAndScopeValidationStillApply()
    {
        var scope = Scope();
        Failure(Invalid, scope, Output(scope).Replace(scope.InfobaseId.ToString("D"),
            scope.InfobaseId.ToString("D").ToUpperInvariant(), StringComparison.Ordinal));
        Failure("InfobaseRegistrationInvalidScope", scope with { FencingToken = 0 }, Output(scope));
        Failure("InfobaseRegistrationInvalidScope", scope with { ExpectedInfobaseName = "bs_smoke" }, Output(scope));
        Failure("InfobaseRegistrationInvalidScope", scope with { ExpectedDatabaseName = "bs_smoke" }, Output(scope));
    }

    [Fact]
    public void ProfileBoundMatchRejectsOtherProfileAndStillBindsScope()
    {
        var scope = Scope();
        var observed = Parse(scope, Output(scope));
        var synthetic = InfobaseRegistrationReadback.Parse(scope, 0, false, string.Join('\n', Lines(scope).Take(6)));
        foreach (var pair in new[] { (observed, InfobaseRegistrationReadback.ProfileId), (synthetic, Observed) })
            Assert.Equal("InfobaseRegistrationProfileMismatch", Assert.Throws<InfobaseRegistrationReadbackException>(() =>
                InfobaseRegistrationReadback.RequireMatch(pair.Item1, scope, pair.Item2)).Code);
        Assert.Equal("InfobaseRegistrationScopeMismatch", Assert.Throws<InfobaseRegistrationReadbackException>(() =>
            InfobaseRegistrationReadback.RequireMatch(observed, scope with { ObservationId = Guid.NewGuid() }, Observed)).Code);
        Assert.Equal("InfobaseRegistrationScopeMismatch", Assert.Throws<InfobaseRegistrationReadbackException>(() =>
            InfobaseRegistrationReadback.RequireMatch(null!, scope, Observed)).Code);
    }
}
