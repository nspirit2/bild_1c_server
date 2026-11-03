using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using BuildSystem.Agent.Tools;
using Xunit;

namespace BuildSystem.ProcessTests;

public sealed class SqlDatabaseObserverTests
{
    private const string Secret = "private-provider-connection-secret";
    private static readonly Guid Mapping = Guid.NewGuid();
    private static readonly Guid Server = Guid.NewGuid();
    private static SqlDatabaseObservationScope Scope()
    {
        var reservation = Guid.NewGuid();
        return new(Guid.NewGuid(), 1, Guid.NewGuid(), Server, Mapping, reservation, Guid.NewGuid(), $"bs_test_{reservation:N}");
    }
    private static SqlDatabaseObserverOptions Options(int timeout = 10) => new()
    {
        EndpointMappingId = Mapping, CanonicalSqlServerId = Server,
        ExpectedMachineName = "operator-host", TimeoutSeconds = timeout
    };
    private static DataTable Table(params object?[][] rows)
    {
        var result = new DataTable();
        var width = rows.Length == 0 ? 4 : rows[0].Length;
        for (var i = 0; i < width; i++) result.Columns.Add("c" + i, typeof(object));
        foreach (var row in rows) result.Rows.Add(row.Select(v => v ?? DBNull.Value).ToArray());
        return result;
    }
    private static DataTable Identity(object? role = null) => Table(["operator-host", "MSSQLSERVER", role ?? 1]);
    private static object?[] Row(string name = "observed", string state = "ONLINE") => [name, 7, new DateTime(2025, 1, 2), state];
    private static SqlDatabaseObserver Observer(FakeConnection connection, int timeout = 10) => new(() => connection, Options(timeout));
    private static async Task Unavailable(Func<Task<SqlDatabaseObservationResult>> action)
    {
        var error = await Assert.ThrowsAsync<SqlDatabaseObservationException>(action);
        Assert.Equal("SqlDatabaseObservationUnavailable", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Secret, error.ToString());
    }

    [Fact]
    public async Task FixedReadOnlyQueriesAndOnePreciselyTypedParameter()
    {
        var scope = Scope();
        var connection = new FakeConnection(Identity(), Table(Row(scope.ExpectedDatabaseName)));
        var result = await Observer(connection).ObserveAsync(scope);
        Assert.Same(scope, result.Scope);
        Assert.Equal(SqlDatabaseObservation.Present, result.Observation);
        Assert.Equal(scope.ExpectedDatabaseName, result.Database!.Name);
        Assert.Equal(7, result.Database.DatabaseId);
        Assert.Equal(new DateTime(2025, 1, 2), result.Database.CreateDate);
        Assert.Equal("ONLINE", result.Database.State);
        Assert.Equal(2, connection.Commands.Count);
        Assert.Equal("SELECT CONVERT(nvarchar(128),SERVERPROPERTY('MachineName')) MachineName, COALESCE(CONVERT(nvarchar(128),SERVERPROPERTY('InstanceName')),N'MSSQLSERVER') InstanceName, CONVERT(int,IS_SRVROLEMEMBER(N'sysadmin')) HasCompleteVisibility", connection.Commands[0].CommandText);
        Assert.Equal("SELECT name,database_id,create_date,state_desc FROM sys.databases WHERE name = @databaseName", connection.Commands[1].CommandText);
        Assert.Empty(connection.Commands[0].Parameters);
        var parameter = Assert.IsAssignableFrom<DbParameter>(Assert.Single(connection.Commands[1].Parameters.Cast<object>()));
        Assert.Equal("@databaseName", parameter.ParameterName);
        Assert.Equal(DbType.String, parameter.DbType);
        Assert.Equal(128, parameter.Size);
        Assert.Equal(scope.ExpectedDatabaseName, parameter.Value);
        Assert.All(connection.Commands, command =>
        {
            Assert.Equal(10, command.CommandTimeout);
            Assert.Equal(CommandType.Text, command.CommandType);
            Assert.True(command.WasDisposed);
        });
        Assert.True(connection.WasDisposed);
        Assert.True(connection.OpenToken.CanBeCanceled);
    }

    [Fact]
    public async Task SuccessfulEmptyLookupMeansAbsent()
    {
        var result = await Observer(new(Identity(), Table())).ObserveAsync(Scope());
        Assert.Equal(SqlDatabaseObservation.Absent, result.Observation);
        Assert.Null(result.Database);
    }

    [Theory]
    [InlineData("OFFLINE")]
    [InlineData("RESTORING")]
    [InlineData("RECOVERY_PENDING")]
    [InlineData("OFFLINE_SECONDARY")]
    public async Task AnySqlCollationMatchedRowIsPresentRetainingActualName(string state)
    {
        // SQL collation may match case, accents or other aliases; no CLR name equality is assumed.
        const string actual = "BS_TÉST_sql_collation_alias";
        var result = await Observer(new(Identity(), Table(Row(actual, state)))).ObserveAsync(Scope());
        Assert.Equal(SqlDatabaseObservation.Present, result.Observation);
        Assert.Equal(actual, result.Database!.Name);
        Assert.Equal(state, result.Database.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    [InlineData(null)]
    [InlineData("1")]
    [InlineData(true)]
    public async Task CompleteVisibilityRequiresExactIntegerSysadminOne(object? role)
    {
        var connection = new FakeConnection(Table(["operator-host", "MSSQLSERVER", role]), Table());
        await Unavailable(() => Observer(connection).ObserveAsync(Scope()));
        Assert.Single(connection.Commands);
    }

    [Theory]
    [InlineData("wrong-host", "MSSQLSERVER")]
    [InlineData("operator-host", "wrong-instance")]
    [InlineData("operator-host", null)]
    public async Task ServerIdentityMismatchBlocksLookup(string host, string? instance)
    {
        var connection = new FakeConnection(Table([host, instance, 1]), Table());
        await Unavailable(() => Observer(connection).ObserveAsync(Scope()));
        Assert.Single(connection.Commands);
    }

    [Fact]
    public async Task ServerIdentityAndMasterAreCaseInsensitiveAndRecheckedEachInvocation()
    {
        var calls = 0;
        var first = new FakeConnection(Table(["OPERATOR-HOST", "mssqlserver", 1]), Table()) { DatabaseName = "MASTER" };
        var second = new FakeConnection(Table(["wrong", "MSSQLSERVER", 1]), Table());
        var observer = new SqlDatabaseObserver(() => ++calls == 1 ? first : second, Options());
        Assert.Equal(SqlDatabaseObservation.Absent, (await observer.ObserveAsync(Scope())).Observation);
        await Unavailable(() => observer.ObserveAsync(Scope()));
        Assert.Equal(2, calls);
        Assert.Single(second.Commands);
    }

    [Fact]
    public async Task NonMasterConnectionFailsBeforeAnyCommand()
    {
        var connection = new FakeConnection(Identity(), Table()) { DatabaseName = "application" };
        await Unavailable(() => Observer(connection).ObserveAsync(Scope()));
        Assert.Empty(connection.Commands);
    }

    [Fact]
    public async Task InvalidScopeAndMappingRejectedBeforeFactory()
    {
        var calls = 0;
        var observer = new SqlDatabaseObserver(() => { calls++; throw new Exception(Secret); }, Options());
        var scope = Scope();
        var invalid = new[]
        {
            scope with { OperationId = Guid.Empty }, scope with { FencingToken = 0 },
            scope with { TargetIncarnationId = Guid.Empty }, scope with { SqlServerId = Guid.Empty },
            scope with { EndpointMappingId = Guid.Empty }, scope with { ReservationId = Guid.Empty },
            scope with { ObservationId = Guid.Empty }, scope with { ExpectedDatabaseName = "other" },
            scope with { ExpectedDatabaseName = scope.ExpectedDatabaseName.ToUpperInvariant() },
            scope with { SqlServerId = Guid.NewGuid() }, scope with { EndpointMappingId = Guid.NewGuid() }
        };
        foreach (var bad in invalid)
            await Assert.ThrowsAsync<SqlDatabaseObservationException>(() => observer.ObserveAsync(bad));
        await Assert.ThrowsAsync<SqlDatabaseObservationException>(() => observer.ObserveAsync(null!));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public void InvalidTimeoutRejectedBeforeFactory(int timeout)
    {
        Assert.Throws<ArgumentException>(() => new SqlDatabaseObserver(() => throw new Exception(Secret), Options(timeout)));
    }

    [Fact]
    public void InvalidOperatorIdentityAndMappingRejected()
    {
        foreach (var name in new[] { "", " host", "host ", "host\n", new string('x', 129) })
            Assert.Throws<ArgumentException>(() => new SqlDatabaseObserver(() => throw new Exception(Secret), new()
            {
                EndpointMappingId = Mapping, CanonicalSqlServerId = Server,
                ExpectedMachineName = name, TimeoutSeconds = 1
            }));
        Assert.Throws<ArgumentException>(() => new SqlDatabaseObserver(() => throw new Exception(Secret), new()
        {
            EndpointMappingId = Guid.Empty, CanonicalSqlServerId = Server,
            ExpectedMachineName = "host", TimeoutSeconds = 60
        }));
    }

    [Fact]
    public async Task MalformedDatabaseRowsNeverProduceAbsence()
    {
        object?[][] invalid =
        [
            [null, 7, DateTime.Today, "ONLINE"], ["name", null, DateTime.Today, "ONLINE"],
            ["name", 0, DateTime.Today, "ONLINE"], ["name", -1, DateTime.Today, "ONLINE"],
            ["name", 7L, DateTime.Today, "ONLINE"], ["name", 7, null, "ONLINE"],
            ["name", 7, default(DateTime), "ONLINE"], ["name", 7, "2025-01-01", "ONLINE"],
            ["name", 7, DateTime.Today, null], ["name", 7, DateTime.Today, "UNKNOWN"],
            ["name", 7, DateTime.Today, "online"], [" name", 7, DateTime.Today, "ONLINE"],
            ["name ", 7, DateTime.Today, "ONLINE"], ["na\nme", 7, DateTime.Today, "ONLINE"],
            [new string('x', 129), 7, DateTime.Today, "ONLINE"]
        ];
        foreach (var row in invalid)
            await Unavailable(() => Observer(new(Identity(), Table(row))).ObserveAsync(Scope()));
    }

    [Fact]
    public async Task EmptyMalformedSchemaCannotBecomeAbsence()
    {
        var wrongShape = new DataTable();
        wrongShape.Columns.Add("unexpected", typeof(string));
        await Unavailable(() => Observer(new(Identity(), wrongShape)).ObserveAsync(Scope()));
    }

    [Fact]
    public async Task DuplicateRowsAndMissingIdentityRejected()
    {
        foreach (var identity in new[] { Table(), Table(["operator-host", "MSSQLSERVER", 1], ["operator-host", "MSSQLSERVER", 1]) })
            await Unavailable(() => Observer(new(identity, Table())).ObserveAsync(Scope()));
        await Unavailable(() => Observer(new(Identity(), Table(Row(), Row()))).ObserveAsync(Scope()));
    }

    [Fact]
    public async Task AdditionalResultSetsCannotBecomePresenceOrAbsence()
    {
        foreach (var first in new[] { true, false })
        {
            var connection = new FakeConnection(Identity(), Table()) { ExtraResultsAtIdentity = first, ExtraResultsAtPresence = !first };
            await Unavailable(() => Observer(connection).ObserveAsync(Scope()));
        }
    }

    [Fact]
    public async Task FactoryOpenExecuteAndReaderErrorsSanitized()
    {
        await Unavailable(() => new SqlDatabaseObserver(() => throw new Exception(Secret), Options()).ObserveAsync(Scope()));
        await Unavailable(() => new SqlDatabaseObserver(() => null!, Options()).ObserveAsync(Scope()));
        foreach (var phase in new[] { "open", "identity", "presence", "reader", "dispose" })
        {
            var connection = new FakeConnection(Identity(), Table()) { Failure = phase };
            await Unavailable(() => Observer(connection).ObserveAsync(Scope()));
        }
    }

    [Fact]
    public async Task PreCancelledDoesNotInvokeFactory()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Unavailable(() => new SqlDatabaseObserver(() => throw new Xunit.Sdk.XunitException("factory invoked"), Options())
            .ObserveAsync(Scope(), cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterQueryRejectedEvenWhenProviderIgnoresToken(bool present)
    {
        using var cancellation = new CancellationTokenSource();
        var connection = new FakeConnection(Identity(), present ? Table(Row()) : Table())
        {
            OnPresence = () => cancellation.Cancel()
        };
        await Unavailable(() => Observer(connection).ObserveAsync(Scope(), cancellation.Token));
    }

    [Fact]
    public async Task ConfiguredTimeoutRejectedEvenWhenProviderIgnoresToken()
    {
        var connection = new FakeConnection(Identity(), Table()) { DelayPresence = true };
        await Unavailable(() => Observer(connection, 1).ObserveAsync(Scope()));
    }

    [Fact]
    public async Task DiagnosticRepresentationsAreRedacted()
    {
        var scope = Scope();
        var result = await Observer(new(Identity(), Table(Row(Secret)))).ObserveAsync(scope);
        foreach (var text in new[] { Options().ToString(), scope.ToString(), result.ToString(), result.Database!.ToString() })
        {
            Assert.DoesNotContain(Secret, text);
            Assert.DoesNotContain(scope.ExpectedDatabaseName, text);
            Assert.DoesNotContain("operator-host", text);
        }
    }

    private sealed class FakeConnection(DataTable identity, DataTable presence) : DbConnection
    {
        public string DatabaseName { get; init; } = "master";
        public string? Failure { get; init; }
        public Action? OnPresence { get; init; }
        public bool DelayPresence { get; init; }
        public bool ExtraResultsAtIdentity { get; init; }
        public bool ExtraResultsAtPresence { get; init; }
        public List<FakeCommand> Commands { get; } = [];
        public CancellationToken OpenToken { get; private set; }
        public bool WasDisposed { get; private set; }
        [AllowNull] public override string ConnectionString { get; set; } = Secret;
        public override string Database => DatabaseName;
        public override string DataSource => Secret;
        public override string ServerVersion => "1";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() { }
        public override void Open() => throw new NotSupportedException();
        public override Task OpenAsync(CancellationToken cancellationToken)
        {
            OpenToken = cancellationToken;
            if (Failure == "open") throw new InvalidOperationException(Secret);
            return Task.CompletedTask;
        }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand()
        {
            var command = new FakeCommand(this, Commands.Count == 0);
            Commands.Add(command);
            return command;
        }
        public override ValueTask DisposeAsync()
        {
            WasDisposed = true;
            if (Failure == "dispose") throw new InvalidOperationException(Secret);
            return ValueTask.CompletedTask;
        }
        public async Task<DbDataReader> Execute(bool first, CancellationToken token)
        {
            Assert.True(token.CanBeCanceled);
            if (Failure == (first ? "identity" : "presence")) throw new InvalidOperationException(Secret);
            if (!first)
            {
                OnPresence?.Invoke();
                if (DelayPresence) await Task.Delay(1200); // Deliberately ignores cancellation.
            }
            var table = first ? identity : presence;
            var reader = (first ? ExtraResultsAtIdentity : ExtraResultsAtPresence)
                ? new DataTableReader([table, Table(Row())])
                : table.CreateDataReader();
            if (Failure == "reader") reader.Close();
            return reader;
        }
    }

    private sealed class FakeCommand(FakeConnection owner, bool first) : DbCommand
    {
        private readonly FakeParameters parameters = new();
        public bool WasDisposed { get; private set; }
        [AllowNull] public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = owner;
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => parameters;
        public override void Cancel() { }
        public override int ExecuteNonQuery() => throw new Xunit.Sdk.XunitException("mutation attempted");
        public override object ExecuteScalar() => throw new NotSupportedException();
        public override void Prepare() => throw new NotSupportedException();
        protected override DbParameter CreateDbParameter() => new FakeParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
            => owner.Execute(first, cancellationToken);
        public override ValueTask DisposeAsync() { WasDisposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class FakeParameter : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
        public override bool IsNullable { get; set; }
        [AllowNull] public override string ParameterName { get; set; } = "";
        [AllowNull] public override string SourceColumn { get; set; } = "";
        public override object? Value { get; set; }
        public override bool SourceColumnNullMapping { get; set; }
        public override int Size { get; set; }
        public override void ResetDbType() { }
    }

    private sealed class FakeParameters : DbParameterCollection
    {
        private readonly List<DbParameter> values = [];
        public override int Count => values.Count;
        public override object SyncRoot => this;
        public override int Add(object value) { values.Add((DbParameter)value); return values.Count - 1; }
        public override void AddRange(Array items) { foreach (var item in items) Add(item!); }
        public override void Clear() => values.Clear();
        public override bool Contains(object value) => values.Contains((DbParameter)value);
        public override bool Contains(string value) => IndexOf(value) >= 0;
        public override void CopyTo(Array array, int index) => ((ICollection)values).CopyTo(array, index);
        public override IEnumerator GetEnumerator() => values.GetEnumerator();
        public override int IndexOf(object value) => values.IndexOf((DbParameter)value);
        public override int IndexOf(string parameterName) => values.FindIndex(p => p.ParameterName == parameterName);
        public override void Insert(int index, object value) => values.Insert(index, (DbParameter)value);
        public override void Remove(object value) => values.Remove((DbParameter)value);
        public override void RemoveAt(int index) => values.RemoveAt(index);
        public override void RemoveAt(string parameterName) => RemoveAt(IndexOf(parameterName));
        protected override DbParameter GetParameter(int index) => values[index];
        protected override DbParameter GetParameter(string parameterName) => values[IndexOf(parameterName)];
        protected override void SetParameter(int index, DbParameter value) => values[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) => values[IndexOf(parameterName)] = value;
    }
}
