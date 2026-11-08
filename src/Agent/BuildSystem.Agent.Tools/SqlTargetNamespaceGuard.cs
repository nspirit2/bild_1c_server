using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;

namespace BuildSystem.Agent.Tools;

/// <summary>Operator-owned SQL endpoint mapping. The trusted factory must return a fresh, unpooled,
/// non-reconnecting SQL Server connection to master; callers never supply connection settings.</summary>
public sealed class SqlTargetNamespaceGuardOptions
{
    public required Guid EndpointMappingId { get; init; }
    public required Guid CanonicalSqlServerId { get; init; }
    public required string ExpectedMachineName { get; init; }
    public string ExpectedInstanceName { get; init; } = "MSSQLSERVER";
    public required int TimeoutSeconds { get; init; }
    public override string ToString() => "SqlTargetNamespaceGuardOptions [details omitted]";
}

/// <summary>Correlation and namespace identification only; no fencing or execution authority.</summary>
public sealed record SqlTargetNamespaceScope(Guid OperationId, Guid AttemptId, Guid ReservationId,
    Guid CanonicalSqlServerId, Guid EndpointMappingId)
{
    public override string ToString() => "SqlTargetNamespaceScope [details omitted]";
}

/// <summary>Fixed, redacted failure; provider and connection details are never propagated.</summary>
public sealed class SqlTargetNamespaceException : Exception
{
    public string Code { get; }
    internal SqlTargetNamespaceException(string code) : base(code) => Code = code;
    public override string ToString() => $"SqlTargetNamespaceException: {Code}";
}

/// <summary>
/// Cooperative exclusive SQL session lock on the reservation-derived bs_test_* name. Every service
/// agent that can create that physical target must acquire this same lock on master first and retain
/// its lease throughout the operation. This guard grants no SQL execution or create authority.
/// Timeouts use cancellation plus a monotonic deadline, not preemption: provider calls and owned
/// connection cleanup are awaited even if the provider ignores cancellation.
/// </summary>
public sealed class SqlTargetNamespaceGuard
{
    public const string IdentitySql = SqlDatabaseObserver.IdentitySql + ", CONVERT(int,@@SPID) SessionId";
    public const string CheckSql = HeldSql;
    public const string AcquireSql = "DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource=@resource, @LockMode=N'Exclusive', @LockOwner=N'Session', @LockTimeout=0, @DbPrincipal=N'public'; SELECT @result, CONVERT(int,@@SPID)";
    public const string HeldSql = "SELECT CONVERT(int,@@SPID), APPLOCK_MODE(N'public',@resource,N'Session')";
    public const string ReleaseSql = "DECLARE @result int = -999; IF CONVERT(int,@@SPID) = @spid AND APPLOCK_MODE(N'public',@resource,N'Session') = N'Exclusive' EXEC @result = sys.sp_releaseapplock @Resource=@resource, @LockOwner=N'Session', @DbPrincipal=N'public'; SELECT CONVERT(int,@@SPID), @result";

    private readonly Func<DbConnection> factory;
    private readonly Guid endpointMappingId;
    private readonly Guid canonicalSqlServerId;
    private readonly string machineName;
    private readonly string instanceName;
    private readonly int timeoutSeconds;

    public SqlTargetNamespaceGuard(Func<DbConnection> trustedFactory, SqlTargetNamespaceGuardOptions options)
    {
        factory = trustedFactory ?? throw new ArgumentNullException(nameof(trustedFactory));
        ArgumentNullException.ThrowIfNull(options);
        if (options.EndpointMappingId == Guid.Empty || options.CanonicalSqlServerId == Guid.Empty ||
            !ValidIdentifier(options.ExpectedMachineName) || !ValidIdentifier(options.ExpectedInstanceName) ||
            options.TimeoutSeconds is < 1 or > 60)
            throw new ArgumentException("invalid_sql_target_namespace_guard_options", nameof(options));
        endpointMappingId = options.EndpointMappingId;
        canonicalSqlServerId = options.CanonicalSqlServerId;
        machineName = options.ExpectedMachineName;
        instanceName = options.ExpectedInstanceName;
        timeoutSeconds = options.TimeoutSeconds;
    }

    public async Task<SqlTargetNamespaceLease> AcquireAsync(SqlTargetNamespaceScope scope,
        CancellationToken cancellationToken = default)
    {
        // Validate all untrusted scope fields before invoking the trusted factory.
        if (scope is null || scope.OperationId == Guid.Empty || scope.AttemptId == Guid.Empty ||
            scope.ReservationId == Guid.Empty || scope.CanonicalSqlServerId == Guid.Empty ||
            scope.EndpointMappingId == Guid.Empty)
            throw new SqlTargetNamespaceException("InvalidSqlTargetNamespaceScope");
        if (scope.CanonicalSqlServerId != canonicalSqlServerId || scope.EndpointMappingId != endpointMappingId)
            throw new SqlTargetNamespaceException("SqlTargetNamespaceScopeMismatch");
        var resource = "BuildSystem:target-creation:bs_test_" + scope.ReservationId.ToString("N");
        var deadline = new SqlTargetNamespaceDeadline(timeoutSeconds, cancellationToken);
        DbConnection? connection = null;
        SqlTargetNamespaceLease? lease = null;
        try
        {
            deadline.Check();
            connection = factory() ?? throw Unavailable();
            if (connection.State != ConnectionState.Closed || !SafeConnectionSettings(connection.ConnectionString))
                throw Unavailable();
            await connection.OpenAsync(deadline.Token).ConfigureAwait(false);
            deadline.Check();
            RequireMaster(connection);
            int identitySpid;
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = IdentitySql;
                command.CommandType = CommandType.Text;
                command.CommandTimeout = deadline.CommandTimeout;
                await using var reader = await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false);
                if (!await reader.ReadAsync(deadline.Token).ConfigureAwait(false)) throw Unavailable();
                deadline.Check();
                if (reader.FieldCount != 4 || reader.GetValue(0) is not string machine ||
                    reader.GetValue(1) is not string instance || reader.GetValue(2) is not int admin ||
                    reader.GetValue(3) is not int sessionId || sessionId <= 0 ||
                    !ValidIdentifier(machine) || !ValidIdentifier(instance) || admin != 1 ||
                    !string.Equals(machine, machineName, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(instance, instanceName, StringComparison.OrdinalIgnoreCase) ||
                    await reader.ReadAsync(deadline.Token).ConfigureAwait(false) ||
                    await reader.NextResultAsync(deadline.Token).ConfigureAwait(false)) throw Unavailable();
                deadline.Check();
                identitySpid = sessionId;
            }
            deadline.Check();
            RequireMaster(connection);
            int spid;
            await using (var command = NewResourceCommand(connection, AcquireSql, resource, deadline.CommandTimeout))
            {
                var (result, session) = await ReadTwoIntegersAsync(command, deadline).ConfigureAwait(false);
                if (result is not (0 or 1) || session <= 0 || session != identitySpid) throw Unavailable();
                spid = session;
            }
            lease = new SqlTargetNamespaceLease(scope, connection, resource, spid, timeoutSeconds);
            await lease.CheckHeldAsync(deadline).ConfigureAwait(false);
            deadline.Check();
            connection = null; // Lease owns the connection only after the post-acquisition check.
            return lease;
        }
        catch
        {
            if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
            else if (connection is not null)
            {
                // An acquire may have succeeded before a failed read/cancellation. Unpooled connections
                // must physically close even when the provider refuses an explicit release.
                try { await connection.DisposeAsync().ConfigureAwait(false); } catch { /* fail closed */ }
            }
            throw Unavailable();
        }
        finally { deadline.Dispose(); }
    }

    internal static DbCommand NewResourceCommand(DbConnection connection, string sql, string resource, int timeout)
    {
        var command = connection.CreateCommand();
        try
        {
            command.CommandType = CommandType.Text;
            command.CommandText = sql;
            command.CommandTimeout = timeout;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@resource";
            parameter.DbType = DbType.String;
            parameter.Size = 255;
            parameter.Value = resource;
            command.Parameters.Add(parameter);
            return command;
        }
        catch
        {
            try { command.Dispose(); } catch { /* Preserve the original failure for outer redaction. */ }
            throw;
        }
    }

    internal static async Task<(int, int)> ReadTwoIntegersAsync(DbCommand command, SqlTargetNamespaceDeadline deadline)
    {
        await using var reader = await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false);
        if (!await reader.ReadAsync(deadline.Token).ConfigureAwait(false)) throw Unavailable();
        deadline.Check();
        if (reader.FieldCount != 2 || reader.GetValue(0) is not int first ||
            reader.GetValue(1) is not int second || await reader.ReadAsync(deadline.Token).ConfigureAwait(false) ||
            await reader.NextResultAsync(deadline.Token).ConfigureAwait(false)) throw Unavailable();
        deadline.Check();
        return (first, second);
    }

    internal static void RequireMaster(DbConnection connection)
    {
        if (connection.State != ConnectionState.Open ||
            !string.Equals(connection.Database, "master", StringComparison.OrdinalIgnoreCase)) throw Unavailable();
    }

    // DbConnection is intentionally provider-independent for test fakes. The operator's trusted
    // factory must honor these explicit settings and supply a genuine SQL Server provider. Reject
    // absent, duplicated or ambiguous key aliases; never accept a provider's pooling default.
    private static bool SafeConnectionSettings(string connectionString)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        bool? pooling = null;
        int? retry = null;
        foreach (string key in builder.Keys)
        {
            var normalized = new string(key.Where(c => c != ' ' && c != '_').ToArray());
            if ((string.Equals(normalized, "Pooling", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(key, "Pooling", StringComparison.OrdinalIgnoreCase)) ||
                (string.Equals(normalized, "ConnectRetryCount", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(key, "ConnectRetryCount", StringComparison.OrdinalIgnoreCase))) return false;
            if (string.Equals(key, "Pooling", StringComparison.OrdinalIgnoreCase))
            {
                if (pooling.HasValue || !bool.TryParse(Convert.ToString(builder[key], CultureInfo.InvariantCulture), out var value)) return false;
                pooling = value;
            }
            else if (string.Equals(key, "ConnectRetryCount", StringComparison.OrdinalIgnoreCase))
            {
                if (retry.HasValue || !int.TryParse(Convert.ToString(builder[key], CultureInfo.InvariantCulture),
                        NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return false;
                retry = value;
            }
        }
        return pooling == false && retry == 0;
    }

    private static bool ValidIdentifier(string? value) => value is { Length: > 0 and <= 128 } &&
        !string.IsNullOrWhiteSpace(value) && !char.IsWhiteSpace(value[0]) &&
        !char.IsWhiteSpace(value[^1]) && !value.Any(char.IsControl);
    internal static SqlTargetNamespaceException Unavailable() => new("SqlTargetNamespaceUnavailable");
}

/// <summary>A session-bound cooperative lock, never an execution credential or fencing token.</summary>
public sealed class SqlTargetNamespaceLease : IAsyncDisposable
{
    private readonly DbConnection connection;
    private readonly string resource;
    private readonly int spid;
    private readonly int timeoutSeconds;
    private readonly SemaphoreSlim gate = new(1, 1);
    private int closed;
    private int failed;
    public SqlTargetNamespaceScope Scope { get; }
    public bool Executable => false;
    public bool JobsBlocked => true;
    public bool ScheduledJobsMustRemainBlocked => true;

    internal SqlTargetNamespaceLease(SqlTargetNamespaceScope scope, DbConnection connection,
        string resource, int spid, int timeoutSeconds)
    {
        Scope = scope;
        this.connection = connection;
        this.resource = resource;
        this.spid = spid;
        this.timeoutSeconds = timeoutSeconds;
    }

    public async Task CheckHeldAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = new SqlTargetNamespaceDeadline(timeoutSeconds, cancellationToken);
        await CheckHeldAsync(deadline).ConfigureAwait(false);
    }

    internal async Task CheckHeldAsync(SqlTargetNamespaceDeadline deadline)
    {
        try
        {
            if (Volatile.Read(ref closed) != 0 || Volatile.Read(ref failed) != 0) throw SqlTargetNamespaceGuard.Unavailable();
            deadline.Check();
            await gate.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                deadline.Check();
                if (Volatile.Read(ref closed) != 0 || Volatile.Read(ref failed) != 0) throw SqlTargetNamespaceGuard.Unavailable();
                SqlTargetNamespaceGuard.RequireMaster(connection);
                await using (var command = SqlTargetNamespaceGuard.NewResourceCommand(connection,
                    SqlTargetNamespaceGuard.HeldSql, resource, deadline.CommandTimeout))
                {
                    await using var reader = await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false);
                    if (!await reader.ReadAsync(deadline.Token).ConfigureAwait(false)) throw SqlTargetNamespaceGuard.Unavailable();
                    deadline.Check();
                    if (reader.FieldCount != 2 || reader.GetValue(0) is not int currentSpid || currentSpid <= 0 || currentSpid != spid ||
                        reader.GetValue(1) is not string mode || !string.Equals(mode, "Exclusive", StringComparison.Ordinal) ||
                        await reader.ReadAsync(deadline.Token).ConfigureAwait(false) ||
                        await reader.NextResultAsync(deadline.Token).ConfigureAwait(false)) throw SqlTargetNamespaceGuard.Unavailable();
                    deadline.Check();
                }
                // Provider disposal is awaited too; cancellation during reader/command cleanup cannot
                // turn into a successful check. Publish failure before another waiter enters.
                deadline.Check();
                if (Volatile.Read(ref closed) != 0 || Volatile.Read(ref failed) != 0)
                    throw SqlTargetNamespaceGuard.Unavailable();
            }
            catch
            {
                Interlocked.Exchange(ref failed, 1);
                throw;
            }
            finally { gate.Release(); }
        }
        catch
        {
            Interlocked.Exchange(ref failed, 1); // Never recover a failed lease, even if a later check would succeed.
            throw SqlTargetNamespaceGuard.Unavailable();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // No public access to this connection. Release only from the original session, and
            // test the SPID again within the SAME SQL batch to prevent reconnect between reads.
            try
            {
                if (connection.State == ConnectionState.Open &&
                    string.Equals(connection.Database, "master", StringComparison.OrdinalIgnoreCase))
                {
                    using var deadline = new SqlTargetNamespaceDeadline(timeoutSeconds, CancellationToken.None);
                    await using var command = SqlTargetNamespaceGuard.NewResourceCommand(connection,
                        SqlTargetNamespaceGuard.ReleaseSql, resource, deadline.CommandTimeout);
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = "@spid";
                    parameter.DbType = DbType.Int32;
                    parameter.Value = spid;
                    command.Parameters.Add(parameter);
                    var (currentSpid, code) = await SqlTargetNamespaceGuard.ReadTwoIntegersAsync(command, deadline).ConfigureAwait(false);
                    if (currentSpid != spid || code < 0) Interlocked.Exchange(ref failed, 1);
                }
            }
            catch { Interlocked.Exchange(ref failed, 1); }
            finally
            {
                try { await connection.DisposeAsync().ConfigureAwait(false); }
                catch { Interlocked.Exchange(ref failed, 1); }
            }
        }
        finally { gate.Release(); }
    }
}

internal sealed class SqlTargetNamespaceDeadline : IDisposable
{
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly TimeSpan limit;
    private readonly CancellationTokenSource linked;
    internal CancellationToken Token => linked.Token;
    internal int CommandTimeout => Math.Max(1, (int)Math.Ceiling((limit - Stopwatch.GetElapsedTime(started)).TotalSeconds));
    internal SqlTargetNamespaceDeadline(int seconds, CancellationToken token)
    {
        limit = TimeSpan.FromSeconds(seconds);
        linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        linked.CancelAfter(limit);
    }
    internal void Check()
    {
        if (linked.IsCancellationRequested || Stopwatch.GetElapsedTime(started) >= limit)
            throw SqlTargetNamespaceGuard.Unavailable();
    }
    public void Dispose() => linked.Dispose();
}
