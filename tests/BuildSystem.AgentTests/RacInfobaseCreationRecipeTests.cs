using BuildSystem.Agent.Tools;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class RacInfobaseCreationRecipeTests
{
    private static readonly Guid Cluster = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RacCluster = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Server = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Mapping = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly string Workspace = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rac-recipe-tests"));

    private static RacInfobaseCreationRecipeOptions Options() => new()
    {
        TrustedRacToolId = "trusted-rac-create",
        Endpoint = "trusted-ras.example:1545",
        EndpointMappingId = Mapping,
        CanonicalClusterId = Cluster,
        RacClusterId = RacCluster,
        CanonicalSqlServerId = Server,
        SqlServer = "trusted-sql.example\\TEST",
        WorkingDirectory = Workspace,
        Timeout = TimeSpan.FromSeconds(30),
        Locale = "en_US",
        Enabled = true,
        ExclusiveNamespaceConfirmed = true
    };

    private static RacInfobaseCreationScope Scope() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Cluster, Server, Mapping);

    private static RacInfobaseCreationCredentials Credentials() => new(
        "private-cluster-user", "private-cluster-password", "private-sql-user", "private-sql-password");

    private static void SetOption(RacInfobaseCreationRecipeOptions options, string property, object? value) =>
        typeof(RacInfobaseCreationRecipeOptions).GetProperty(property)!.SetValue(options, value);

    private static RacInfobaseCreationRecipeException Reject(string code, Action action)
    {
        var error = Assert.Throws<RacInfobaseCreationRecipeException>(action);
        Assert.Equal(code, error.Code);
        Assert.Null(error.InnerException);
        Assert.Equal(code, error.Message);
        return error;
    }

    [Fact]
    public void SafetyFlagsDefaultToFalse()
    {
        var options = Options();
        var defaults = new RacInfobaseCreationRecipeOptions
            {
                TrustedRacToolId = options.TrustedRacToolId, Endpoint = options.Endpoint,
                EndpointMappingId = Mapping, CanonicalClusterId = Cluster, RacClusterId = RacCluster,
                CanonicalSqlServerId = Server, SqlServer = options.SqlServer,
                WorkingDirectory = Workspace, Timeout = options.Timeout, Locale = options.Locale
            };
        Assert.False(defaults.Enabled);
        Assert.False(defaults.ExclusiveNamespaceConfirmed);
        SetOption(options, nameof(options.Enabled), false);
        SetOption(options, nameof(options.ExclusiveNamespaceConfirmed), false);
        var builder = new RacInfobaseCreationRecipe(options);
        Reject("RacInfobaseCreationDisabled", () => builder.Prepare(Scope(), Credentials()));
    }

    [Fact]
    public void BothOperatorOptInsAreRequired()
    {
        foreach (var (enabled, exclusive, code) in new[]
        {
            (false, false, "RacInfobaseCreationDisabled"),
            (false, true, "RacInfobaseCreationDisabled"),
            (true, false, "RacInfobaseCreationExclusiveNamespaceRequired")
        })
        {
            var options = Options();
            SetOption(options, nameof(options.Enabled), enabled);
            SetOption(options, nameof(options.ExclusiveNamespaceConfirmed), exclusive);
            Reject(code, () => new RacInfobaseCreationRecipe(options).Prepare(Scope(), Credentials()));
        }
    }

    [Fact]
    public void PreparedRecipeIsNotExecutionAuthorityAndKeepsScheduledJobsBlocked()
    {
        var scope = Scope();
        var recipe = new RacInfobaseCreationRecipe(Options()).Prepare(scope, Credentials());
        Assert.Equal(scope, recipe.Scope);
        Assert.False(recipe.Executable);
        Assert.True(recipe.ScheduledJobsMustRemainBlocked);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void EveryScopeIdentifierMustBeNonempty(int index)
    {
        var scope = Scope();
        var invalid = index switch
        {
            0 => scope with { OperationId = Guid.Empty },
            1 => scope with { PermitId = Guid.Empty },
            2 => scope with { AttemptId = Guid.Empty },
            3 => scope with { ReservationId = Guid.Empty },
            4 => scope with { TargetIncarnationId = Guid.Empty },
            5 => scope with { CanonicalClusterId = Guid.Empty },
            6 => scope with { CanonicalSqlServerId = Guid.Empty },
            _ => scope with { EndpointMappingId = Guid.Empty }
        };
        Reject("InvalidRacInfobaseCreationScope", () => new RacInfobaseCreationRecipe(Options()).Prepare(invalid, Credentials()));
    }

    [Fact]
    public void NullScopeIsRejected() => Reject("InvalidRacInfobaseCreationScope",
        () => new RacInfobaseCreationRecipe(Options()).Prepare(null!, Credentials()));

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void AllCanonicalMappingPinsMustMatch(int pin)
    {
        var scope = Scope();
        var invalid = pin switch
        {
            0 => scope with { CanonicalClusterId = Guid.NewGuid() },
            1 => scope with { CanonicalSqlServerId = Guid.NewGuid() },
            _ => scope with { EndpointMappingId = Guid.NewGuid() }
        };
        Reject("RacInfobaseCreationScopeMismatch", () => new RacInfobaseCreationRecipe(Options()).Prepare(invalid, Credentials()));
    }

    public static IEnumerable<object[]> InvalidOptions()
    {
        foreach (var property in new[] { "EndpointMappingId", "CanonicalClusterId", "RacClusterId", "CanonicalSqlServerId" })
            yield return [property, Guid.Empty];
        foreach (var property in new[] { "TrustedRacToolId", "Endpoint", "SqlServer" })
            foreach (var value in new[] { "", " ", " leading", "trailing ", "bad\nvalue", "--switch" })
                yield return [property, value];
        foreach (var value in new[] { "", "relative/workspace", Workspace + "\n" })
            yield return ["WorkingDirectory", value];
        yield return ["Timeout", TimeSpan.Zero];
        yield return ["Timeout", TimeSpan.FromTicks(-1)];
        yield return ["Timeout", TimeSpan.FromMinutes(5) + TimeSpan.FromTicks(1)];
        yield return ["Timeout", TimeSpan.MaxValue];
        yield return ["TrustedRacToolId", new string('x', 129)];
        yield return ["Endpoint", new string('x', 256)];
        yield return ["SqlServer", new string('x', 256)];
        yield return ["SqlServer", "server;DROP DATABASE app"];
        yield return ["WorkingDirectory", Workspace + new string('x', 4097)];
    }

    [Fact]
    public void InvalidOperatorPinsAreRejectedAtConstruction()
    {
        Reject("InvalidRacInfobaseCreationOptions", () => new RacInfobaseCreationRecipe(null!));
        foreach (var item in InvalidOptions())
        {
            var options = Options();
            SetOption(options, (string)item[0], item[1]);
            Reject("InvalidRacInfobaseCreationOptions", () => new RacInfobaseCreationRecipe(options));
        }
    }

    [Fact]
    public void LocaleMustBeAnExplicitOperatorLocaleInTheRequiredPattern()
    {
        foreach (var locale in new[] { "", "en-US", "en_us", "EN_US", "en_US ", " en_US", "en_US\n", "en_US --scheduled-jobs-deny=off" })
        {
            var options = Options();
            SetOption(options, nameof(options.Locale), locale);
            Reject("InvalidRacInfobaseCreationOptions", () => new RacInfobaseCreationRecipe(options));
        }
    }

    [Theory]
    [InlineData("en_US")] [InlineData("ru_RU")]
    public void OperatorLocaleIsPassedExactlyWithoutInference(string locale)
    {
        var options = Options();
        SetOption(options, nameof(options.Locale), locale);
        var command = new RacInfobaseCreationRecipe(options).Prepare(Scope(), Credentials()).Command;
        Assert.Contains(command.Arguments, a => a.Value == "--locale=" + locale);
    }

    [Fact]
    public void GeneratedNameComesOnlyFromReservationAndIsSharedByInfobaseAndDatabase()
    {
        var scope = Scope();
        var builder = new RacInfobaseCreationRecipe(Options());
        var first = builder.Prepare(scope, Credentials());
        var second = builder.Prepare(scope with
        {
            OperationId = Guid.NewGuid(), PermitId = Guid.NewGuid(), AttemptId = Guid.NewGuid(),
            TargetIncarnationId = Guid.NewGuid()
        }, Credentials());
        var expected = "bs_test_" + scope.ReservationId.ToString("N");
        Assert.Equal(expected, first.GeneratedName);
        Assert.Equal(expected, second.GeneratedName);
        Assert.Equal(first.Command.Arguments.Select(a => a.Value), second.Command.Arguments.Select(a => a.Value));
        Assert.Contains(first.Command.Arguments, a => a.Value == "--name=" + expected);
        Assert.Contains(first.Command.Arguments, a => a.Value == "--db-name=" + expected);
    }

    [Fact]
    public void DifferentReservationsProduceDifferentNames()
    {
        var scope = Scope();
        var builder = new RacInfobaseCreationRecipe(Options());
        var first = builder.Prepare(scope, Credentials());
        var second = builder.Prepare(scope with { ReservationId = Guid.NewGuid() }, Credentials());
        Assert.NotEqual(first.GeneratedName, second.GeneratedName);
    }

    [Fact]
    public void ExactArgumentVectorUsesActualRacClusterAndTrustedSqlServerWithEndpointLast()
    {
        var options = Options();
        var scope = Scope();
        var credentials = Credentials();
        var recipe = new RacInfobaseCreationRecipe(options).Prepare(scope, credentials);
        var command = recipe.Command;
        Assert.Equal(options.TrustedRacToolId, command.TrustedToolId);
        Assert.Equal(Workspace, command.WorkingDirectory);
        Assert.Equal(options.Timeout, command.Timeout);
        Assert.Equal(new[]
        {
            "infobase", "create", "--cluster=" + RacCluster.ToString("D"),
            "--cluster-user=" + credentials.ClusterUser, "--cluster-pwd=" + credentials.ClusterPassword,
            "--create-database", "--name=" + recipe.GeneratedName, "--dbms=MSSQLServer",
            "--db-server=" + options.SqlServer, "--db-name=" + recipe.GeneratedName,
            "--db-user=" + credentials.SqlUser, "--db-pwd=" + credentials.SqlPassword,
            "--locale=en_US", "--scheduled-jobs-deny=on", options.Endpoint
        }, command.Arguments.Select(a => a.Value));
        Assert.DoesNotContain(command.Arguments, a => a.Value == "--cluster=" + Cluster.ToString("D"));
    }

    [Fact]
    public void AllFourCredentialArgumentsAreSensitiveAndAllFourValuesAreKnownSecrets()
    {
        var credentials = Credentials();
        var command = new RacInfobaseCreationRecipe(Options()).Prepare(Scope(), credentials).Command;
        Assert.Equal(new[] { 3, 4, 10, 11 }, command.Arguments.Select((a, i) => (a, i))
            .Where(item => item.a.Sensitive).Select(item => item.i));
        Assert.Equal(new[] { credentials.ClusterUser, credentials.ClusterPassword, credentials.SqlUser, credentials.SqlPassword }, command.KnownSecrets);
    }

    [Fact]
    public void EveryCredentialMustBeNonemptyBoundedAndControlFree()
    {
        var credentials = Credentials();
        for (var field = 0; field < 4; field++)
        foreach (var value in new[] { null, "", "bad\0credential", "bad\ncredential", "bad\rcredential", new string('x', 4097) })
        {
            var invalid = field switch
            {
                0 => credentials with { ClusterUser = value! },
                1 => credentials with { ClusterPassword = value! },
                2 => credentials with { SqlUser = value! },
                _ => credentials with { SqlPassword = value! }
            };
            var error = Reject("InvalidRacInfobaseCreationCredentials",
                () => new RacInfobaseCreationRecipe(Options()).Prepare(Scope(), invalid));
            foreach (var secret in new[] { credentials.ClusterUser, credentials.ClusterPassword, credentials.SqlUser, credentials.SqlPassword })
                Assert.DoesNotContain(secret, error.ToString());
        }
    }

    [Fact]
    public void NullCredentialsAreRejected() => Reject("InvalidRacInfobaseCreationCredentials",
        () => new RacInfobaseCreationRecipe(Options()).Prepare(Scope(), null!));

    [Fact]
    public void ConstructorSnapshotsOptionsInsteadOfRetainingMutableConfiguration()
    {
        var options = Options();
        var scope = Scope();
        var builder = new RacInfobaseCreationRecipe(options);
        var before = builder.Prepare(scope, Credentials());
        foreach (var invalid in InvalidOptions()) SetOption(options, (string)invalid[0], invalid[1]);
        SetOption(options, nameof(options.Locale), "ru_RU");
        SetOption(options, nameof(options.Enabled), false);
        SetOption(options, nameof(options.ExclusiveNamespaceConfirmed), false);
        var after = builder.Prepare(scope, Credentials());
        Assert.Equal(before.GeneratedName, after.GeneratedName);
        Assert.Equal(before.Command.TrustedToolId, after.Command.TrustedToolId);
        Assert.Equal(before.Command.WorkingDirectory, after.Command.WorkingDirectory);
        Assert.Equal(before.Command.Timeout, after.Command.Timeout);
        Assert.Equal(before.Command.Arguments, after.Command.Arguments);
        Assert.Equal(before.Command.KnownSecrets, after.Command.KnownSecrets);
    }

    [Fact]
    public void PreparingAnotherRecipeDoesNotMutatePreviouslyPreparedCredentialsOrArguments()
    {
        var builder = new RacInfobaseCreationRecipe(Options());
        var first = builder.Prepare(Scope(), Credentials());
        var arguments = first.Command.Arguments.ToArray();
        var secrets = first.Command.KnownSecrets!.ToArray();
        var second = builder.Prepare(Scope(), new("other-cluster-user", "other-cluster-password", "other-sql-user", "other-sql-password"));
        Assert.Equal(arguments, first.Command.Arguments);
        Assert.Equal(secrets, first.Command.KnownSecrets);
        Assert.NotSame(first.Command.Arguments, second.Command.Arguments);
        Assert.NotSame(first.Command.KnownSecrets, second.Command.KnownSecrets);
    }

    [Fact]
    public void ArgumentAndKnownSecretCollectionsCannotBeMutated()
    {
        var command = new RacInfobaseCreationRecipe(Options()).Prepare(Scope(), Credentials()).Command;
        AssertReadOnly(command.Arguments, new ProcessArgument("--scheduled-jobs-deny=off"));
        AssertReadOnly(command.KnownSecrets!, "replacement-secret");
    }

    private static void AssertReadOnly<T>(IReadOnlyList<T> values, T replacement)
    {
        Assert.False(values is T[]);
        if (values is ICollection<T> collection)
        {
            Assert.True(collection.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => collection.Add(replacement));
            Assert.Throws<NotSupportedException>(() => collection.Clear());
        }
        if (values is IList<T> list)
            Assert.Throws<NotSupportedException>(() => list[0] = replacement);
    }

    [Fact]
    public void DiagnosticsNeverRevealAnyOfTheFourCredentialValues()
    {
        var credentials = Credentials();
        var builder = new RacInfobaseCreationRecipe(Options());
        var recipe = builder.Prepare(Scope(), credentials);
        var diagnostics = string.Join("\n", credentials.ToString(), builder.ToString(), recipe.ToString(),
            recipe.Command.ToString(), string.Join(" ", recipe.Command.Arguments));
        foreach (var secret in new[] { credentials.ClusterUser, credentials.ClusterPassword, credentials.SqlUser, credentials.SqlPassword })
            Assert.DoesNotContain(secret, diagnostics);
    }
}
