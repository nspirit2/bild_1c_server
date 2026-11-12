using System.Reflection;
using System.Text.Json;
using BuildSystem.Agent.Execution;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class InfobaseRegistrationReadbackTests
{
    private static InfobaseRegistrationReadbackScope Scope() => NewScope(Guid.NewGuid());

    private static InfobaseRegistrationReadbackScope NewScope(Guid reservation)
    {
        var name = $"bs_test_{reservation:N}";
        return new(Guid.NewGuid(), 1, Guid.NewGuid(), reservation, Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.Parse("abcdef12-3456-4789-abcd-1234567890ab"), Guid.NewGuid(), name, "sql-host\\instance,1433", name);
    }

    private static string Output(InfobaseRegistrationReadbackScope scope) =>
        $"infobase: {scope.InfobaseId:D}\nname: {scope.ExpectedInfobaseName}\n" +
        $"dbms: MSSQLServer\ndb-server: {scope.ExpectedSqlServer}\n" +
        $"db-name: {scope.ExpectedDatabaseName}\nscheduled-jobs-deny: on\n";

    private static void Failure(string code, InfobaseRegistrationReadbackScope scope,
        string output, int exitCode = 0, bool truncated = false) =>
        Assert.Equal(code, Assert.Throws<InfobaseRegistrationReadbackException>(() =>
            InfobaseRegistrationReadback.Parse(scope, exitCode, truncated, output)).Code);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ExactContractWorksWithoutGrantingAuthority(bool crlf, bool surroundingSpace)
    {
        var scope = Scope();
        var output = Output(scope);
        if (surroundingSpace) output = string.Join('\n', output.Split('\n')
            .Select(line => line.Length == 0 ? line : $" \t{line}  \t"));
        if (crlf) output = output.Replace("\n", "\r\n", StringComparison.Ordinal);
        var result = InfobaseRegistrationReadback.Parse(scope, 0, false, output);
        Assert.Equal(scope, result.Scope);
        Assert.Equal(scope.InfobaseId, result.InfobaseId);
        Assert.Equal(scope.ExpectedInfobaseName, result.InfobaseName);
        Assert.Equal(scope.ExpectedSqlServer, result.SqlServer);
        Assert.Equal(scope.ExpectedDatabaseName, result.DatabaseName);
        Assert.Equal("MSSQLServer", result.Dbms);
        Assert.Equal(InfobaseRegistrationReadback.ProfileId, result.ProfileId);
        Assert.True(result.ScheduledJobsBlocked);
        Assert.True(result.RequiresIndependentVerification);
        Assert.False(result.Executable);
        Assert.False(result.OwnershipEstablished);
        Assert.False(result.RestoreAllowed);
        Assert.False(result.RetryAllowed);
        Assert.False(result.FreshAuthority);
        InfobaseRegistrationReadback.RequireMatch(result, scope);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    [InlineData(8)] [InlineData(9)] [InlineData(10)]
    public void EveryEmptyCorrelationIdRejectsBeforeUntrustedOutput(int index)
    {
        var scope = Scope();
        scope = index switch
        {
            0 => scope with { OperationId = Guid.Empty },
            1 => scope with { TargetIncarnationId = Guid.Empty },
            2 => scope with { ReservationId = Guid.Empty },
            3 => scope with { CanonicalClusterId = Guid.Empty },
            4 => scope with { CanonicalSqlServerId = Guid.Empty },
            5 => scope with { RacEndpointMappingId = Guid.Empty },
            6 => scope with { SqlEndpointMappingId = Guid.Empty },
            7 => scope with { AttemptId = Guid.Empty },
            8 => scope with { ClaimId = Guid.Empty },
            9 => scope with { InfobaseId = Guid.Empty },
            _ => scope with { ObservationId = Guid.Empty }
        };
        Failure("InfobaseRegistrationInvalidScope", scope, "SECRET", 2, true);
    }

    [Theory]
    [InlineData("")] [InlineData(" ")] [InlineData("-server")]
    [InlineData("host name")] [InlineData("host\tname")]
    [InlineData("host\rname")] [InlineData("höst")]
    public void ServerLiteralMustBeBoundedPrintableAsciiWithoutWhitespace(string server)
    {
        var scope = Scope() with { ExpectedSqlServer = server };
        Failure("InfobaseRegistrationInvalidScope", scope, Output(scope));
    }

    [Fact]
    public void ScopeNamesAndFenceMustBeExact()
    {
        var scope = Scope();
        foreach (var bad in new[] {
            scope with { FencingToken = 0 }, scope with { FencingToken = -1 },
            scope with { ExpectedInfobaseName = scope.ExpectedInfobaseName.ToUpperInvariant() },
            scope with { ExpectedDatabaseName = scope.ExpectedDatabaseName + "_alias" },
            scope with { ExpectedSqlServer = new string('a', 256) } })
            Failure("InfobaseRegistrationInvalidScope", bad, Output(scope));
        var maxServer = scope with { ExpectedSqlServer = new string('a', 255) };
        Assert.Equal(maxServer.ExpectedSqlServer,
            InfobaseRegistrationReadback.Parse(maxServer, 0, false, Output(maxServer)).SqlServer);
    }

    [Theory]
    [InlineData(1, false)] [InlineData(-1, false)] [InlineData(0, true)]
    [InlineData(1, true)]
    public void AbnormalCaptureIsUnavailable(int exit, bool truncated)
    {
        var scope = Scope();
        Failure("InfobaseRegistrationUnavailable", scope, Output(scope), exit, truncated);
    }

    [Theory]
    [InlineData("infobase")] [InlineData("name")]
    [InlineData("dbms")] [InlineData("db-server")]
    [InlineData("db-name")] [InlineData("scheduled-jobs-deny")]
    public void MissingOrDuplicatedKeyIsInvalid(string key)
    {
        var scope = Scope();
        var lines = Output(scope).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var line = Assert.Single(lines, x => x.StartsWith(key + ":", StringComparison.Ordinal));
        Failure("InfobaseRegistrationInvalidReadback", scope,
            string.Join('\n', lines.Where(x => x != line)));
        Failure("InfobaseRegistrationInvalidReadback", scope,
            Output(scope) + line + "\n");
    }

    [Theory]
    [InlineData("unknown: secret\n")]
    [InlineData("name")]
    [InlineData("name:\n")]
    [InlineData("name: \n")]
    [InlineData("\n")]
    [InlineData("not-key : value: secret\n")]
    [InlineData("db-server: foo\tbar\n")]
    [InlineData("dbms: MSSQLServer\r")]
    [InlineData("name: bad\0secret\n")]
    [InlineData("name: bad\u001bsecret\n")]
    public void UnknownMalformedBlankOrControlDataRejects(string extra)
    {
        var scope = Scope();
        Failure("InfobaseRegistrationInvalidReadback", scope, Output(scope) + extra);
    }

    [Fact]
    public void OversizeNullAndNonCanonicalUuidReject()
    {
        var scope = Scope();
        Failure("InfobaseRegistrationInvalidReadback", scope, new string('X', 16385));
        Failure("InfobaseRegistrationInvalidReadback", scope, null!);
        Failure("InfobaseRegistrationInvalidReadback", scope,
            Output(scope).Replace(scope.InfobaseId.ToString("D"),
                scope.InfobaseId.ToString("D").ToUpperInvariant(), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("infobase")]
    [InlineData("name")]
    [InlineData("db-server")]
    [InlineData("db-name")]
    [InlineData("dbms")]
    public void EveryIdentityOrDbmsMismatchRejects(string field)
    {
        var scope = Scope();
        var old = field switch
        {
            "infobase" => scope.InfobaseId.ToString("D"),
            "name" => scope.ExpectedInfobaseName,
            "db-server" => scope.ExpectedSqlServer,
            "db-name" => scope.ExpectedDatabaseName,
            _ => "MSSQLServer"
        };
        var replacement = field == "infobase" ? Guid.NewGuid().ToString("D") : old + "x";
        Failure("InfobaseRegistrationTargetMismatch", scope,
            Output(scope).Replace(field + ": " + old, field + ": " + replacement,
                StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("off")] [InlineData("ON")] [InlineData("true")]
    public void JobsMustExplicitlyBeBlocked(string flag)
    {
        var scope = Scope();
        Failure("InfobaseRegistrationJobsNotBlocked", scope,
            Output(scope).Replace("scheduled-jobs-deny: on", "scheduled-jobs-deny: " + flag,
                StringComparison.Ordinal));
    }

    [Fact]
    public void ScopeMustMatchEntireCapturedValue()
    {
        var scope = Scope();
        var result = InfobaseRegistrationReadback.Parse(scope, 0, false, Output(scope));
        Assert.Equal("InfobaseRegistrationScopeMismatch",
            Assert.Throws<InfobaseRegistrationReadbackException>(() =>
                InfobaseRegistrationReadback.RequireMatch(result,
                    scope with { ObservationId = Guid.NewGuid() })).Code);
        Assert.Equal("InfobaseRegistrationScopeMismatch",
            Assert.Throws<InfobaseRegistrationReadbackException>(() =>
                InfobaseRegistrationReadback.RequireMatch(null!, scope)).Code);
    }

    [Fact]
    public void NoPublicResultConstructorAndMetadataNeverContainsRawOutput()
    {
        Assert.Empty(typeof(InfobaseRegistrationReadbackResult).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        var scope = Scope();
        var result = InfobaseRegistrationReadback.Parse(scope, 0, false, Output(scope));
        Assert.DoesNotContain(scope.ExpectedSqlServer, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(scope.ExpectedSqlServer, scope.ToString(), StringComparison.Ordinal);
        var exception = Assert.Throws<InfobaseRegistrationReadbackException>(() =>
            InfobaseRegistrationReadback.Parse(scope, 0, false, Output(scope) + "secret: token\n"));
        Assert.Equal("InfobaseRegistrationInvalidReadback", exception.Message);
        Assert.DoesNotContain("token", JsonSerializer.Serialize(new { exception.Code, exception.Message }),
            StringComparison.Ordinal);
    }
}
