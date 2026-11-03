using BuildSystem.Agent.Tools;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BuildSystem.IntegrationTests;

// The only database created/dropped here is the fixture-owned bs_ci_metadata database.
// Observation targets are random bs_test names which are never created, restored or dropped.
public sealed class SqlDatabaseObserverTests
{
    [SqlServerFact]
    public async Task Absent_with_complete_visibility_preserves_scope_and_owned_database()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var master = MasterConnectionString();
        var options = await ReadTrustedOptionsAsync(master);
        var scope = NewScope(options);
        var before = await ReadOwnedDatabaseAsync(master, sql.DatabaseName);
        var observer = new SqlDatabaseObserver(() => new SqlConnection(master), options);

        var result = await observer.ObserveAsync(scope, CancellationToken.None);

        Assert.Equal(scope, result.Scope);
        Assert.Equal(SqlDatabaseObservation.Absent, result.Observation);
        Assert.Null(result.Database);
        Assert.Equal(before, await ReadOwnedDatabaseAsync(master, sql.DatabaseName));
    }

    [SqlServerFact]
    public async Task Server_identity_mismatch_fails_closed_without_changing_owned_database()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var master = MasterConnectionString();
        var trusted = await ReadTrustedOptionsAsync(master);
        var wrongIdentity = new SqlDatabaseObserverOptions
        {
            EndpointMappingId = trusted.EndpointMappingId,
            CanonicalSqlServerId = trusted.CanonicalSqlServerId,
            ExpectedMachineName = "wrong-machine-" + Guid.NewGuid().ToString("N"),
            ExpectedInstanceName = trusted.ExpectedInstanceName,
            TimeoutSeconds = 10
        };
        var before = await ReadOwnedDatabaseAsync(master, sql.DatabaseName);
        var observer = new SqlDatabaseObserver(() => new SqlConnection(master), wrongIdentity);

        var error = await Assert.ThrowsAsync<SqlDatabaseObservationException>(() =>
            observer.ObserveAsync(NewScope(wrongIdentity), CancellationToken.None));

        Assert.Equal("SqlDatabaseObservationUnavailable", error.Message);
        Assert.Equal(before, await ReadOwnedDatabaseAsync(master, sql.DatabaseName));
    }

    [SqlServerFact]
    public async Task Injected_non_master_connection_fails_closed_without_changing_owned_database()
    {
        await using var sql = await SqlServerTestDatabase.CreateAsync();
        var master = MasterConnectionString();
        var options = await ReadTrustedOptionsAsync(master);
        var before = await ReadOwnedDatabaseAsync(master, sql.DatabaseName);
        // Deliberately inject the fixture's real non-master connection, not a fake DbConnection.
        var observer = new SqlDatabaseObserver(() => new SqlConnection(sql.ConnectionString), options);

        var error = await Assert.ThrowsAsync<SqlDatabaseObservationException>(() =>
            observer.ObserveAsync(NewScope(options), CancellationToken.None));

        Assert.Equal("SqlDatabaseObservationUnavailable", error.Message);
        Assert.Equal(before, await ReadOwnedDatabaseAsync(master, sql.DatabaseName));
    }

    [SqlServerFact]
    public async Task Cancellation_before_observation_does_not_invoke_connection_factory()
    {
        // A pre-cancelled invocation must not perform IO, including creating a fixture database.
        var options = new SqlDatabaseObserverOptions
        {
            EndpointMappingId = Guid.NewGuid(),
            CanonicalSqlServerId = Guid.NewGuid(),
            ExpectedMachineName = "unused-machine",
            ExpectedInstanceName = "MSSQLSERVER",
            TimeoutSeconds = 10
        };
        var factoryCalls = 0;
        var observer = new SqlDatabaseObserver(() =>
        {
            factoryCalls++;
            throw new InvalidOperationException("A cancelled observation must not request a connection.");
        }, options);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = await Assert.ThrowsAsync<SqlDatabaseObservationException>(() =>
            observer.ObserveAsync(NewScope(options), cancellation.Token));

        Assert.Equal("SqlDatabaseObservationUnavailable", error.Message);
        Assert.Equal(0, factoryCalls);
    }

    private static SqlDatabaseObservationScope NewScope(SqlDatabaseObserverOptions options)
    {
        var reservationId = Guid.NewGuid();
        return new(Guid.NewGuid(), 17, Guid.NewGuid(), options.CanonicalSqlServerId,
            options.EndpointMappingId, reservationId, Guid.NewGuid(), $"bs_test_{reservationId:N}");
    }

    private static string MasterConnectionString()
    {
        Assert.True(SqlServerTestDatabase.TryGetMasterConnectionString(out var master, out var reason), reason);
        return master;
    }

    private static async Task<SqlDatabaseObserverOptions> ReadTrustedOptionsAsync(string master)
    {
        await using var connection = new SqlConnection(master);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 10;
        // Test ground truth comes directly from the operator-configured fixture endpoint.
        // No login/user creation or permission changes are needed to prove complete visibility.
        command.CommandText = """
            SELECT CONVERT(nvarchar(128), SERVERPROPERTY('MachineName')),
                   COALESCE(CONVERT(nvarchar(128), SERVERPROPERTY('InstanceName')), N'MSSQLSERVER'),
                   IS_SRVROLEMEMBER(N'sysadmin');
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.False(reader.IsDBNull(0));
        Assert.False(reader.IsDBNull(2));
        Assert.True(reader.GetInt32(2) == 1,
            "These read-only observer SQL tests require a sysadmin fixture login for complete database visibility.");
        return new SqlDatabaseObserverOptions
        {
            EndpointMappingId = Guid.NewGuid(),
            CanonicalSqlServerId = Guid.NewGuid(),
            ExpectedMachineName = reader.GetString(0),
            ExpectedInstanceName = reader.GetString(1),
            TimeoutSeconds = 10
        };
    }

    private static async Task<(string Name, int DatabaseId, DateTime CreateDate, string State)>
        ReadOwnedDatabaseAsync(string master, string databaseName)
    {
        await using var connection = new SqlConnection(master);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 10;
        command.CommandText = """
            SELECT name, database_id, create_date, state_desc
            FROM sys.databases WHERE name = @databaseName;
            """;
        command.Parameters.Add(new SqlParameter("@databaseName", System.Data.SqlDbType.NVarChar, 128)
        {
            Value = databaseName
        });
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var database = (reader.GetString(0), reader.GetInt32(1), reader.GetDateTime(2), reader.GetString(3));
        Assert.False(await reader.ReadAsync());
        return database;
    }
}
