using BuildSystem.Agent.Tools;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BuildSystem.IntegrationTests;

// Locks only: no target database, RAC registration, CREATE/restore or metadata migrations.
public sealed class SqlTargetNamespaceGuardTests
{
    private static async Task<(string Connection, string Machine, string Instance)> Configuration()
    {
        if (!SqlServerTestDatabase.TryGetMasterConnectionString(out var input, out var reason))
            throw new InvalidOperationException(reason);
        var builder = new SqlConnectionStringBuilder(input) { Pooling = false, ConnectRetryCount = 0 };
        await using var connection = new SqlConnection(builder.ConnectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = SqlDatabaseObserver.IdentitySql;
        await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(2));
        return (builder.ConnectionString, reader.GetString(0), reader.GetString(1));
    }

    private static SqlTargetNamespaceGuard Guard((string Connection, string Machine, string Instance) config,
        Guid server, Guid mapping, Action<SqlConnection>? capture = null) => new(() => {
            var connection = new SqlConnection(config.Connection); capture?.Invoke(connection); return connection;
        }, new SqlTargetNamespaceGuardOptions { CanonicalSqlServerId = server, EndpointMappingId = mapping,
            ExpectedMachineName = config.Machine, ExpectedInstanceName = config.Instance, TimeoutSeconds = 10 });

    private static SqlTargetNamespaceScope Scope(Guid server, Guid mapping, Guid reservation) =>
        new(Guid.NewGuid(), Guid.NewGuid(), reservation, server, mapping);

    [SqlServerFact]
    public async Task Same_physical_target_excludes_alias_mappings_and_releases_on_dispose()
    {
        var config = await Configuration(); var reservation = Guid.NewGuid();
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var mapA = Guid.NewGuid(); var mapB = Guid.NewGuid();
        var left = Guard(config, a, mapA); var alias = Guard(config, b, mapB);
        var held = await left.AcquireAsync(Scope(a, mapA, reservation));
        try {
            Assert.False(held.Executable); Assert.True(held.ScheduledJobsMustRemainBlocked);
            await held.CheckHeldAsync();
            await Assert.ThrowsAsync<SqlTargetNamespaceException>(() => alias.AcquireAsync(Scope(b, mapB, reservation)));
            await held.CheckHeldAsync();
        } finally { await held.DisposeAsync(); }
        await using var next = await alias.AcquireAsync(Scope(b, mapB, reservation)); await next.CheckHeldAsync();
    }

    [SqlServerFact]
    public async Task Different_reservations_do_not_share_the_session_lock()
    {
        var config = await Configuration(); var server = Guid.NewGuid(); var map = Guid.NewGuid();
        var guard = Guard(config, server, map);
        await using var first = await guard.AcquireAsync(Scope(server, map, Guid.NewGuid()));
        await using var second = await guard.AcquireAsync(Scope(server, map, Guid.NewGuid()));
        await first.CheckHeldAsync(); await second.CheckHeldAsync();
    }

    [SqlServerFact]
    public async Task Closed_session_permanently_invalidates_lease_and_releases_exclusion()
    {
        var config = await Configuration(); var server = Guid.NewGuid(); var map = Guid.NewGuid(); var reservation = Guid.NewGuid();
        SqlConnection? owned = null; var guard = Guard(config, server, map, connection => owned = connection);
        var lease = await guard.AcquireAsync(Scope(server, map, reservation));
        await owned!.CloseAsync();
        await Assert.ThrowsAsync<SqlTargetNamespaceException>(() => lease.CheckHeldAsync());
        await Assert.ThrowsAsync<SqlTargetNamespaceException>(() => lease.CheckHeldAsync());
        await lease.DisposeAsync(); await lease.DisposeAsync();
        await using var next = await guard.AcquireAsync(Scope(server, map, reservation)); await next.CheckHeldAsync();
    }

    [SqlServerFact]
    public async Task Wrong_expected_server_identity_fails_without_holding_target_lock()
    {
        var config = await Configuration(); var server = Guid.NewGuid(); var map = Guid.NewGuid(); var reservation = Guid.NewGuid();
        var bad = Guard((config.Connection, "not-the-server-" + Guid.NewGuid().ToString("N"), config.Instance), server, map);
        await Assert.ThrowsAsync<SqlTargetNamespaceException>(() => bad.AcquireAsync(Scope(server, map, reservation)));
        await using var held = await Guard(config, server, map).AcquireAsync(Scope(server, map, reservation));
        await held.CheckHeldAsync();
    }
}
