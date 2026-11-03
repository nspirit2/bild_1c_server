using BuildSystem.Agent;
using BuildSystem.Contracts;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class TargetObservationHostFactoryTests
{
    [Theory]
    [InlineData("sql-host", 1)]
    [InlineData("sql-host\\INSTANCE", 30)]
    [InlineData("sql-host.example,1433", 60)]
    public void SqlConnectionHasOnlyFixedSecurityAndTransportSettings(string server, int timeout)
    {
        var connectionString = TargetObservationHostFactory.BuildSqlConnectionString(new()
        {
            SqlServer = server,
            TimeoutSeconds = timeout
        });
        var builder = new SqlConnectionStringBuilder(connectionString);
        Assert.Equal(server, builder.DataSource);
        Assert.True(builder.IntegratedSecurity);
        Assert.Equal("master", builder.InitialCatalog);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, builder.Encrypt);
        Assert.False(builder.TrustServerCertificate);
        Assert.Equal(timeout, builder.ConnectTimeout);
        Assert.False(builder.Pooling);
        Assert.Equal("BuildSystem.TargetObservation", builder.ApplicationName);
        Assert.Empty(builder.UserID);
        Assert.Empty(builder.Password);
        Assert.False(builder.PersistSecurityInfo);
        Assert.False(builder.MultipleActiveResultSets);
        Assert.Empty(builder.AttachDBFilename);
        var explicitlySet = builder.Keys.Cast<string>().Where(builder.ShouldSerialize).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "Application Name", "Connect Timeout", "Data Source", "Encrypt", "Initial Catalog",
            "Integrated Security", "Pooling", "Trust Server Certificate" }.OrderBy(x => x), explicitlySet);
    }

    [Theory]
    [InlineData("sql-host;Password=private-secret", 30)]
    [InlineData("Data Source=private-server", 30)]
    [InlineData("private-server\n", 30)]
    [InlineData("", 30)]
    [InlineData("sql-host", 0)]
    [InlineData("sql-host", 61)]
    public void SqlConnectionRejectsMalformedPublicOptionsWithRedactedError(string server, int timeout)
    {
        Redacted(() => TargetObservationHostFactory.BuildSqlConnectionString(new()
        {
            SqlServer = server,
            TimeoutSeconds = timeout
        }));
    }

    [Fact]
    public void DisabledRejectsWithoutInspectingPathsOrCredentials()
    {
        Redacted(() => TargetObservationHostFactory.CreateExecutor(new()
        {
            Enabled = false,
            RacExecutable = "\0private-executable",
            RacWorkingDirectory = "\0private-directory"
        }, "\0private-data-directory"));
    }

    [Fact]
    public void MalformedPublicOptionsDoNotExposeConstructorDiagnostics()
    {
        using var fixture = new Fixture();
        Redacted(() => TargetObservationHostFactory.CreateExecutor(fixture.Options("relative-private-path"), fixture.Directory));
        Redacted(() => TargetObservationHostFactory.CreateExecutor(null!, fixture.Directory));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EveryPinRejectsAtOutermostBoundaryBeforeCredentialOrProcessIo(int pin)
    {
        using var fixture = new Fixture();
        // An empty, non-executable rac.exe is enough for construction; no subprocess is launched.
        // The malformed data directory must not be inspected or passed to DPAPI during construction.
        var executor = TargetObservationHostFactory.CreateExecutor(fixture.Options(), "\0private-data-directory");
        Assert.IsType<TargetObservationPinnedExecutor>(executor);
        var a = fixture.Assignment;
        var mismatch = pin switch
        {
            0 => a with { AgentId = Guid.NewGuid() },
            1 => a with { CanonicalClusterId = Guid.NewGuid() },
            2 => a with { CanonicalSqlServerId = Guid.NewGuid() },
            3 => a with { RacEndpointMappingId = Guid.NewGuid() },
            _ => a with { SqlEndpointMappingId = Guid.NewGuid() }
        };
        // A synchronous rejection proves that the async inner executor (and its credential loader)
        // was never entered, not merely that a later credential/process failure was redacted.
        var error = Assert.Throws<TargetObservationExecutionException>(() =>
        {
            _ = executor.ObserveAsync(mismatch, CancellationToken.None);
        });
        Assert.Equal("Target observation execution failed.", error.Message);
        Assert.Null(error.InnerException);
        Assert.Empty(File.ReadAllBytes(fixture.Executable));
        Assert.Single(System.IO.Directory.GetFiles(fixture.Directory));
    }

    private static void Redacted(Action action)
    {
        var error = Assert.Throws<InvalidOperationException>(action);
        Assert.Equal("Invalid target observation host configuration.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-", error.ToString());
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "target-observation-factory-" + Guid.NewGuid().ToString("N"));
        public string Executable => Path.Combine(Directory, "rac.exe");
        public TargetObservationAssignment Assignment { get; } = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(Executable, "");
        }

        public TargetObservationHostOptions Options(string? workingDirectory = null) => new()
        {
            Enabled = true,
            AgentId = Assignment.AgentId,
            CanonicalClusterId = Assignment.CanonicalClusterId,
            CanonicalSqlServerId = Assignment.CanonicalSqlServerId,
            RacEndpointMappingId = Assignment.RacEndpointMappingId,
            SqlEndpointMappingId = Assignment.SqlEndpointMappingId,
            RacExecutable = Executable,
            RacWorkingDirectory = workingDirectory ?? Directory,
            RacEndpoint = "rac-host:1545",
            SqlServer = "sql-host\\INSTANCE",
            ExpectedSqlMachineName = "sql-host",
            ExpectedSqlInstanceName = "INSTANCE"
        };

        public void Dispose()
        {
            // Directory is an absolute, uniquely generated fixture-owned path.
            if (Path.IsPathFullyQualified(Directory) && Path.GetFileName(Directory).StartsWith("target-observation-factory-", StringComparison.Ordinal))
                System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
