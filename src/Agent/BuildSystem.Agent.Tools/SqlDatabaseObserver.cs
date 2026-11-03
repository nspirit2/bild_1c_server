using System.Data;
using System.Data.Common;
using System.Diagnostics;

namespace BuildSystem.Agent.Tools;

/// <summary>Operator-owned mapping and expected server identity; the connection factory is never supplied by an invocation.</summary>
public sealed class SqlDatabaseObserverOptions
{
    public required Guid EndpointMappingId { get; init; }
    public required Guid CanonicalSqlServerId { get; init; }
    public required string ExpectedMachineName { get; init; }
    public string ExpectedInstanceName { get; init; } = "MSSQLSERVER";
    public required int TimeoutSeconds { get; init; }
    public override string ToString() => "SqlDatabaseObserverOptions [details omitted]";
}

/// <summary>Correlation only: neither this scope nor an observed database fingerprint proves ownership.</summary>
public sealed record SqlDatabaseObservationScope(Guid OperationId, long FencingToken,
    Guid TargetIncarnationId, Guid SqlServerId, Guid EndpointMappingId, Guid ReservationId,
    Guid ObservationId, string ExpectedDatabaseName)
{
    public override string ToString() => "SqlDatabaseObservationScope [details omitted]";
}

public enum SqlDatabaseObservation { Present, Absent }

/// <summary>A point-in-time observed fingerprint, not proof of ownership, incarnation or continuing presence.</summary>
public sealed record SqlDatabaseObservedIdentity(string Name, int DatabaseId, DateTime CreateDate, string State)
{
    public override string ToString() => "SqlDatabaseObservedIdentity [details omitted]";
}

/// <summary>Two independent reads, not a transaction or TOCTOU guarantee.</summary>
public sealed class SqlDatabaseObservationResult
{
    public SqlDatabaseObservationScope Scope { get; }
    public SqlDatabaseObservation Observation { get; }
    public SqlDatabaseObservedIdentity? Database { get; }
    internal SqlDatabaseObservationResult(SqlDatabaseObservationScope scope, SqlDatabaseObservedIdentity? database)
    {
        Scope = scope;
        Database = database;
        Observation = database is null ? SqlDatabaseObservation.Absent : SqlDatabaseObservation.Present;
    }
    public override string ToString() => "SqlDatabaseObservationResult [details omitted]";
}

/// <summary>Fixed, redacted failure; provider messages and connection details are never propagated.</summary>
public sealed class SqlDatabaseObservationException : Exception
{
    public string Code { get; }
    internal SqlDatabaseObservationException(string code) : base(code) => Code = code;
    public override string ToString() => $"SqlDatabaseObservationException: {Code}";
}

/// <summary>
/// Privileged read-only SQL Server presence probe. The operator must configure a sysadmin connection
/// to see offline/hidden databases; this probe does not grant permissions or perform SQL mutations.
/// The canonical SQL server GUID is an operator assertion, checked against the scope, not discovered
/// from SQL Server. Machine/instance identity is checked anew on every invocation before lookup.
/// </summary>
public sealed class SqlDatabaseObserver
{
    public const string IdentitySql = "SELECT CONVERT(nvarchar(128),SERVERPROPERTY('MachineName')) MachineName, COALESCE(CONVERT(nvarchar(128),SERVERPROPERTY('InstanceName')),N'MSSQLSERVER') InstanceName, CONVERT(int,IS_SRVROLEMEMBER(N'sysadmin')) HasCompleteVisibility";
    public const string PresenceSql = "SELECT name,database_id,create_date,state_desc FROM sys.databases WHERE name = @databaseName";
    private static readonly HashSet<string> States = new(StringComparer.Ordinal)
    {
        "ONLINE", "OFFLINE", "RESTORING", "RECOVERING", "RECOVERY_PENDING", "SUSPECT",
        "EMERGENCY", "COPYING", "OFFLINE_SECONDARY"
    };
    private readonly Func<DbConnection> connectionFactory;
    private readonly Guid endpointMappingId;
    private readonly Guid canonicalSqlServerId;
    private readonly string machineName;
    private readonly string instanceName;
    private readonly int timeoutSeconds;

    public SqlDatabaseObserver(Func<DbConnection> connectionFactory, SqlDatabaseObserverOptions options)
    {
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        ArgumentNullException.ThrowIfNull(options);
        if (options.EndpointMappingId == Guid.Empty || options.CanonicalSqlServerId == Guid.Empty ||
            !ValidIdentifier(options.ExpectedMachineName) || !ValidIdentifier(options.ExpectedInstanceName) ||
            options.TimeoutSeconds is < 1 or > 60)
            throw new ArgumentException("invalid_sql_database_observer_options", nameof(options));
        endpointMappingId = options.EndpointMappingId;
        canonicalSqlServerId = options.CanonicalSqlServerId;
        machineName = options.ExpectedMachineName;
        instanceName = options.ExpectedInstanceName;
        timeoutSeconds = options.TimeoutSeconds;
    }

    public async Task<SqlDatabaseObservationResult> ObserveAsync(SqlDatabaseObservationScope scope,
        CancellationToken cancellationToken = default)
    {
        // All caller-controlled data is validated before invoking the trusted factory.
        if (scope is null || scope.FencingToken <= 0 ||
            scope.OperationId == Guid.Empty || scope.TargetIncarnationId == Guid.Empty ||
            scope.SqlServerId == Guid.Empty || scope.EndpointMappingId == Guid.Empty ||
            scope.ReservationId == Guid.Empty || scope.ObservationId == Guid.Empty ||
            !string.Equals(scope.ExpectedDatabaseName, $"bs_test_{scope.ReservationId:N}", StringComparison.Ordinal))
            throw new SqlDatabaseObservationException("InvalidSqlDatabaseScope");
        if (scope.SqlServerId != canonicalSqlServerId || scope.EndpointMappingId != endpointMappingId)
            throw new SqlDatabaseObservationException("SqlDatabaseScopeMismatch");
        if (cancellationToken.IsCancellationRequested)
            throw Unavailable();

        var startedAt = Stopwatch.GetTimestamp();
        var deadline = TimeSpan.FromSeconds(timeoutSeconds);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(deadline);
        void CheckDeadline()
        {
            CheckCancellation(linked.Token);
            // Timer callbacks can be delayed under load; expiration does not depend on dispatch.
            if (Stopwatch.GetElapsedTime(startedAt) >= deadline) throw Unavailable();
        }
        try
        {
            await using var connection = connectionFactory() ?? throw Unavailable();
            await connection.OpenAsync(linked.Token).ConfigureAwait(false);
            CheckDeadline();
            if (!string.Equals(connection.Database, "master", StringComparison.OrdinalIgnoreCase))
                throw Unavailable();

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = IdentitySql;
                command.CommandType = CommandType.Text;
                command.CommandTimeout = timeoutSeconds;
                await using var reader = await command.ExecuteReaderAsync(linked.Token).ConfigureAwait(false);
                if (!await reader.ReadAsync(linked.Token).ConfigureAwait(false)) throw Unavailable();
                CheckDeadline();
                if (reader.FieldCount != 3 || reader.GetValue(0) is not string machine ||
                    reader.GetValue(1) is not string instance || reader.GetValue(2) is not int sysadmin ||
                    !ValidIdentifier(machine) || !ValidIdentifier(instance) || sysadmin != 1 ||
                    !string.Equals(machine, machineName, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(instance, instanceName, StringComparison.OrdinalIgnoreCase) ||
                    await reader.ReadAsync(linked.Token).ConfigureAwait(false) ||
                    await reader.NextResultAsync(linked.Token).ConfigureAwait(false)) throw Unavailable();
                CheckDeadline();
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = PresenceSql;
                command.CommandType = CommandType.Text;
                command.CommandTimeout = timeoutSeconds;
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@databaseName";
                parameter.DbType = DbType.String;
                parameter.Size = 128;
                parameter.Value = scope.ExpectedDatabaseName;
                command.Parameters.Add(parameter);
                await using var reader = await command.ExecuteReaderAsync(linked.Token).ConfigureAwait(false);
                if (reader.FieldCount != 4) throw Unavailable();
                if (!await reader.ReadAsync(linked.Token).ConfigureAwait(false))
                {
                    if (await reader.NextResultAsync(linked.Token).ConfigureAwait(false)) throw Unavailable();
                    CheckDeadline();
                    return new SqlDatabaseObservationResult(scope, null);
                }
                CheckDeadline();
                if (reader.FieldCount != 4 || reader.GetValue(0) is not string name ||
                    reader.GetValue(1) is not int id || reader.GetValue(2) is not DateTime created ||
                    reader.GetValue(3) is not string state || !ValidIdentifier(name) || id <= 0 ||
                    created == default || !States.Contains(state) ||
                    await reader.ReadAsync(linked.Token).ConfigureAwait(false) ||
                    await reader.NextResultAsync(linked.Token).ConfigureAwait(false)) throw Unavailable();
                CheckDeadline();
                return new SqlDatabaseObservationResult(scope, new SqlDatabaseObservedIdentity(name, id, created, state));
            }
        }
        catch { throw Unavailable(); }
        finally
        {
            // Async disposal is also provider code and may ignore cancellation. Do not let a
            // cancellation arriving during cleanup turn into a successful observation.
            CheckDeadline();
        }
    }

    private static bool ValidIdentifier(string? value) => value is { Length: > 0 and <= 128 } &&
        !string.IsNullOrWhiteSpace(value) && !char.IsWhiteSpace(value[0]) &&
        !char.IsWhiteSpace(value[^1]) && !value.Any(char.IsControl);
    private static void CheckCancellation(CancellationToken token)
    {
        if (token.IsCancellationRequested) throw Unavailable();
    }
    private static SqlDatabaseObservationException Unavailable() => new("SqlDatabaseObservationUnavailable");
}
