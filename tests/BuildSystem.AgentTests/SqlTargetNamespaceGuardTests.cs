using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using BuildSystem.Agent.Tools;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class SqlTargetNamespaceGuardTests
{
    private const string Secret = "private-provider-connection-secret";
    private static readonly Guid Mapping = Guid.NewGuid();
    private static readonly Guid Server = Guid.NewGuid();
    private static SqlTargetNamespaceScope Scope() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Server, Mapping);
    private static SqlTargetNamespaceGuardOptions Options(int timeout = 10) => new()
    {
        EndpointMappingId = Mapping, CanonicalSqlServerId = Server,
        ExpectedMachineName = "operator-host", TimeoutSeconds = timeout
    };
    private static DataTable Table(int width, params object?[][] rows)
    {
        var table = new DataTable();
        for (var i = 0; i < width; i++) table.Columns.Add("c" + i, typeof(object));
        foreach (var row in rows) table.Rows.Add(row.Select(v => v ?? DBNull.Value).ToArray());
        return table;
    }
    private static DataTable Identity() => Identity(1);
    private static DataTable Identity(object? role) =>
        Table(4, ["operator-host", "MSSQLSERVER", role, 51]);
    private static FakeConnection Connection(params DataTable[] results) => new([Identity(), .. results]);
    private static FakeConnection Successful() => Connection(Table(2, [0, 51]), Table(2, [51, "Exclusive"]));
    private static SqlTargetNamespaceGuard Guard(FakeConnection connection) => new(() => connection, Options());
    private static async Task Unavailable(Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<SqlTargetNamespaceException>(action);
        Assert.Equal("SqlTargetNamespaceUnavailable", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Secret, error.ToString());
    }

    [Fact]
    public async Task AcquiresExclusiveReservationResourceWithoutGrantingExecutionAuthority()
    {
        var scope = Scope();
        var connection = Successful();
        await using (var lease = await Guard(connection).AcquireAsync(scope))
        {
            Assert.Same(scope, lease.Scope);
            Assert.False(lease.Executable);
            Assert.True(lease.ScheduledJobsMustRemainBlocked);
            Assert.Equal(3, connection.Commands.Count);
            Assert.Equal(SqlTargetNamespaceGuard.IdentitySql, connection.Commands[0].CommandText);
            Assert.Equal(SqlTargetNamespaceGuard.AcquireSql, connection.Commands[1].CommandText);
            Assert.Equal(SqlTargetNamespaceGuard.CheckSql, connection.Commands[2].CommandText);
            Assert.Empty(connection.Commands[0].Parameters);
            var resource = Assert.Single(connection.Commands[1].Parameters.Cast<DbParameter>());
            Assert.Equal("@resource", resource.ParameterName);
            Assert.Equal(DbType.String, resource.DbType);
            Assert.Contains("bs_test_" + scope.ReservationId.ToString("N"), Assert.IsType<string>(resource.Value));
            Assert.DoesNotContain(scope.OperationId.ToString("N"), Assert.IsType<string>(resource.Value));
            Assert.DoesNotContain(scope.AttemptId.ToString("N"), Assert.IsType<string>(resource.Value));
            Assert.DoesNotContain(scope.CanonicalSqlServerId.ToString("N"), Assert.IsType<string>(resource.Value));
            Assert.Equal(resource.Value, Assert.Single(connection.Commands[2].Parameters.Cast<DbParameter>()).Value);
            await lease.CheckHeldAsync();
            Assert.Equal(4, connection.Commands.Count);
        }
        Assert.True(connection.WasDisposed);
        Assert.Equal(5, connection.Commands.Count);
        Assert.Equal(SqlTargetNamespaceGuard.ReleaseSql, connection.Commands[4].CommandText);
        var releaseSession = Assert.Single(connection.Commands[4].Parameters.Cast<DbParameter>(), p => p.ParameterName == "@spid");
        Assert.Equal(DbType.Int32, releaseSession.DbType);
        Assert.Equal(51, releaseSession.Value);
        Assert.Contains("IF CONVERT(int,@@SPID) = @spid", connection.Commands[4].CommandText);
        Assert.All(connection.Commands, c => { Assert.Equal(CommandType.Text, c.CommandType); Assert.Equal(10, c.CommandTimeout); Assert.True(c.WasDisposed); });
        Assert.True(connection.OpenToken.CanBeCanceled);
    }

    [Fact]
    public async Task SameReservationSharesLockResourceAcrossOperationAndAttempt()
    {
        var first = Scope();
        var second = first with { OperationId = Guid.NewGuid(), AttemptId = Guid.NewGuid() };
        var a = Successful(); var b = Successful();
        await using (await Guard(a).AcquireAsync(first)) { }
        await using (await Guard(b).AcquireAsync(second)) { }
        Assert.Equal(Assert.Single(a.Commands[1].Parameters.Cast<DbParameter>()).Value,
            Assert.Single(b.Commands[1].Parameters.Cast<DbParameter>()).Value);
    }

    [Fact]
    public async Task ScopeValidationAndMappingMismatchHappenBeforeFactory()
    {
        var calls = 0;
        var guard = new SqlTargetNamespaceGuard(() => { calls++; throw new Exception(Secret); }, Options());
        var scope = Scope();
        foreach (var bad in new[] { scope with { OperationId = Guid.Empty }, scope with { AttemptId = Guid.Empty },
            scope with { ReservationId = Guid.Empty }, scope with { CanonicalSqlServerId = Guid.Empty },
            scope with { EndpointMappingId = Guid.Empty } })
        {
            var error = await Assert.ThrowsAsync<SqlTargetNamespaceException>(() => guard.AcquireAsync(bad));
            Assert.Equal("InvalidSqlTargetNamespaceScope", error.Code);
        }
        Assert.Equal("InvalidSqlTargetNamespaceScope", (await Assert.ThrowsAsync<SqlTargetNamespaceException>(() => guard.AcquireAsync(null!))).Code);
        foreach (var bad in new[] { scope with { CanonicalSqlServerId = Guid.NewGuid() }, scope with { EndpointMappingId = Guid.NewGuid() } })
            Assert.Equal("SqlTargetNamespaceScopeMismatch", (await Assert.ThrowsAsync<SqlTargetNamespaceException>(() => guard.AcquireAsync(bad))).Code);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(0)] [InlineData(61)]
    public void InvalidTimeoutRejected(int timeout) =>
        Assert.Throws<ArgumentException>(() => new SqlTargetNamespaceGuard(() => throw new Exception(Secret), Options(timeout)));

    [Fact]
    public void InvalidOperatorMappingAndMachineAreRejected()
    {
        foreach (var name in new[] { "", " host", "host ", "host\n", new string('x', 129) })
            Assert.Throws<ArgumentException>(() => new SqlTargetNamespaceGuard(() => throw new Exception(Secret), new()
            { EndpointMappingId = Mapping, CanonicalSqlServerId = Server, ExpectedMachineName = name, TimeoutSeconds = 1 }));
        foreach (var mapping in new[] { Guid.Empty, Mapping })
            Assert.Throws<ArgumentException>(() => new SqlTargetNamespaceGuard(() => throw new Exception(Secret), new()
            { EndpointMappingId = mapping, CanonicalSqlServerId = mapping == Mapping ? Guid.Empty : Server,
                ExpectedMachineName = "host", TimeoutSeconds = 60 }));
    }

    [Theory]
    [InlineData("Pooling=true;ConnectRetryCount=0")]
    [InlineData("ConnectRetryCount=0")]
    [InlineData("Pooling=false")]
    [InlineData("Pooling=false;ConnectRetryCount=1")]
    [InlineData("Pooling=false;ConnectRetryCount=bogus")]
    [InlineData("")]
    public async Task UnsafeConnectionSettingsFailBeforeOpen(string settings)
    {
        var connection = Successful(); connection.ConnectionString = settings;
        await Unavailable(async () => await Guard(connection).AcquireAsync(Scope()));
        Assert.Equal(0, connection.OpenCount);
        Assert.Empty(connection.Commands);
        Assert.True(connection.WasDisposed);
    }

    [Fact]
    public async Task SqlClientCanonicalSpacedRetryKeywordIsAcceptedWithoutAllowingAmbiguousAliases()
    {
        var connection = Successful(); connection.ConnectionString = "Pooling=False;Connect Retry Count=0";
        await using var held = await Guard(connection).AcquireAsync(Scope());
        await held.CheckHeldAsync();
        foreach (var settings in new[] {
            "Pooling=false;ConnectRetryCount=0;Connect Retry Count=1",
            "Pooling=false;ConnectRetryCount=1;Connect Retry Count=0",
            "Pooling=false;Connect_Retry_Count=0" })
        {
            var invalid = Successful(); invalid.ConnectionString = settings;
            await Unavailable(async () => await Guard(invalid).AcquireAsync(Scope()));
            Assert.Equal(0, invalid.OpenCount);
        }
    }

    [Fact]
    public async Task PreviouslyOpenedConnectionRejectedBeforeOpeningOrQuerying()
    {
        var connection = Successful(); connection.IsOpen = true;
        await Unavailable(async () => await Guard(connection).AcquireAsync(Scope()));
        Assert.Equal(0, connection.OpenCount);
        Assert.Empty(connection.Commands);
    }

    [Fact]
    public async Task WrongDatabaseFailsBeforeIdentityQuery()
    {
        var connection = Successful(); connection.DatabaseName = "app";
        await Unavailable(async () => await Guard(connection).AcquireAsync(Scope()));
        Assert.Empty(connection.Commands);
    }

    [Theory]
    [InlineData(0)] [InlineData(2)] [InlineData(-1)] [InlineData(null)] [InlineData("1")]
    public async Task SysadminMustBeIntegerOne(object? role)
    {
        var connection = new FakeConnection([Identity(role)]);
        await Unavailable(async () => await Guard(connection).AcquireAsync(Scope()));
        Assert.Single(connection.Commands);
    }

    [Fact]
    public async Task IdentityMismatchAndMalformedSessionNeverAcquire()
    {
        foreach (var identity in new[] { Table(4, ["other", "MSSQLSERVER", 1, 51]),
            Table(4, ["operator-host", "other", 1, 51]), Table(4, ["operator-host", "MSSQLSERVER", 1, 0]),
            Table(4, ["operator-host", "MSSQLSERVER", 1, "51"]), Table(4),
            Table(4, ["operator-host", "MSSQLSERVER", 1, 51], ["operator-host", "MSSQLSERVER", 1, 51]) })
        {
            var connection = new FakeConnection([identity]);
            await Unavailable(async () => await Guard(connection).AcquireAsync(Scope()));
            Assert.Single(connection.Commands);
        }
    }

    [Theory]
    [InlineData(-1)] [InlineData(-2)] [InlineData(-3)] [InlineData(-999)] [InlineData(2)]
    public async Task AcquireContentionOrInvalidStatusFailsClosed(int status)
    {
        var connection = Connection(Table(2, [status, 51]));
        await Unavailable(async () => await Guard(connection).AcquireAsync(Scope()));
        Assert.True(connection.WasDisposed);
        Assert.Equal(2, connection.Commands.Count);
    }

    [Fact]
    public async Task SuccessStatusOneIsAcceptedOnlyWithHeldSession()
    {
        var connection = Connection(Table(2, [1, 51]), Table(2, [51, "Exclusive"]));
        await using var lease = await Guard(connection).AcquireAsync(Scope());
        Assert.False(lease.Executable);
    }

    [Fact]
    public async Task AcquireSessionMustMatchIdentitySession()
    {
        var connection = Connection(Table(2, [0, 52]), Table(2, [52, "Exclusive"]));
        await Unavailable(async () => await Guard(connection).AcquireAsync(Scope()));
        Assert.True(connection.WasDisposed);
    }

    [Fact]
    public async Task MalformedAcquireAndCheckRowsNeverReturnLease()
    {
        var cases = new (DataTable acquire, DataTable check)[]
        {
            (Table(2), Table(2, [51, "Exclusive"])), (Table(1, [0]), Table(2, [51, "Exclusive"])),
            (Table(2, ["0", 51]), Table(2, [51, "Exclusive"])),
            (Table(2, [0, 51], [0, 51]), Table(2, [51, "Exclusive"])),
            (Table(2, [0, 51]), Table(2)), (Table(2, [0, 51]), Table(2, [51, "Shared"])),
            (Table(2, [0, 51]), Table(2, [52, "Exclusive"])),
            (Table(2, [0, 51]), Table(2, [51, "exclusive"])),
            (Table(2, [0, 51]), Table(2, [51, "Exclusive"], [51, "Exclusive"]))
        };
        foreach (var (acquire, check) in cases)
        {
            var connection = Connection(acquire, check);
            await Unavailable(async () => await Guard(connection).AcquireAsync(Scope()));
            Assert.True(connection.WasDisposed);
        }
    }

    [Fact]
    public async Task ExtraResultSetsAreNeverAccepted()
    {
        foreach (var position in new[] { 0, 1, 2 })
        {
            var connection = Successful(); connection.ExtraResultAt = position;
            await Unavailable(async () => await Guard(connection).AcquireAsync(Scope()));
            Assert.True(connection.WasDisposed);
        }
    }

    [Fact]
    public async Task FailedCheckIsStickyWithoutFurtherProviderIo()
    {
        var connection = Connection(Table(2, [0, 51]), Table(2, [51, "Exclusive"]), Table(2, [52, "Exclusive"]));
        var lease = await Guard(connection).AcquireAsync(Scope());
        await Unavailable(() => lease.CheckHeldAsync());
        var count = connection.Commands.Count;
        await Unavailable(() => lease.CheckHeldAsync());
        Assert.Equal(count, connection.Commands.Count);
        await lease.DisposeAsync();
        await Unavailable(() => lease.CheckHeldAsync());
    }

    [Fact]
    public async Task DisposeIsIdempotentAndRevokesLeaseEvenIfReleaseFails()
    {
        var connection = Successful(); connection.FailureAt = 3;
        var lease = await Guard(connection).AcquireAsync(Scope());
        await lease.DisposeAsync();
        var count = connection.Commands.Count;
        await lease.DisposeAsync();
        Assert.Equal(count, connection.Commands.Count);
        Assert.True(connection.WasDisposed);
        await Unavailable(() => lease.CheckHeldAsync());
    }

    [Fact]
    public async Task ProviderErrorsAndCancellationAreRedacted()
    {
        await Unavailable(async () => await new SqlTargetNamespaceGuard(() => throw new Exception(Secret), Options()).AcquireAsync(Scope()));
        await Unavailable(async () => await new SqlTargetNamespaceGuard(() => null!, Options()).AcquireAsync(Scope()));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var calls = 0;
        await Unavailable(async () => await new SqlTargetNamespaceGuard(() => { calls++; throw new Exception(Secret); }, Options()).AcquireAsync(Scope(), cancellation.Token));
        Assert.Equal(0, calls);
        foreach (var failure in new[] { -1, 0, 1, 2 })
        {
            var connection = Successful(); connection.FailureAt = failure;
            await Unavailable(async () => await Guard(connection).AcquireAsync(Scope()));
            Assert.True(connection.WasDisposed);
        }
    }

    [Fact]
    public async Task CancelledWaiterMakesConcurrentActiveCheckFailSticky()
    {
        var connection = Successful();
        await using var lease = await Guard(connection).AcquireAsync(Scope());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.BeforeExecute = async (index, _) =>
        {
            if (index != 3) return;
            entered.SetResult();
            await resume.Task;
        };
        var active = lease.CheckHeldAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var waiting = lease.CheckHeldAsync(cancellation.Token);
        cancellation.Cancel();
        try { await Unavailable(() => waiting); }
        finally { resume.TrySetResult(); }
        await Unavailable(() => active);
        var count = connection.Commands.Count;
        await Unavailable(() => lease.CheckHeldAsync());
        Assert.Equal(count, connection.Commands.Count);
    }

    [Fact]
    public async Task CancellationDuringProviderCommandCleanupFailsCheckSticky()
    {
        var connection = Successful();
        await using var lease = await Guard(connection).AcquireAsync(Scope());
        using var cancellation = new CancellationTokenSource();
        connection.OnCommandDispose = index => { if (index == 3) cancellation.Cancel(); };
        await Unavailable(() => lease.CheckHeldAsync(cancellation.Token));
        var count = connection.Commands.Count;
        await Unavailable(() => lease.CheckHeldAsync());
        Assert.Equal(count, connection.Commands.Count);
    }

    [Fact]
    public async Task DiagnosticsNeverExposeResourceOrOperatorDetails()
    {
        var scope = Scope();
        var connection = Successful();
        await using var lease = await Guard(connection).AcquireAsync(scope);
        foreach (var text in new[] { Options().ToString(), scope.ToString(), lease.ToString()! })
        {
            Assert.DoesNotContain("operator-host", text);
            Assert.DoesNotContain("bs_test_", text);
            Assert.DoesNotContain(scope.ReservationId.ToString("N"), text);
            Assert.DoesNotContain(Secret, text);
        }
    }

    private sealed class FakeConnection(DataTable[] results) : DbConnection
    {
        public string DatabaseName { get; set; } = "master";
        public bool IsOpen { get; set; }
        public int OpenCount { get; private set; }
        public int FailureAt { get; set; } = int.MinValue;
        public int ExtraResultAt { get; set; } = -1;
        public List<FakeCommand> Commands { get; } = [];
        public CancellationToken OpenToken { get; private set; }
        public bool WasDisposed { get; private set; }
        [AllowNull] public override string ConnectionString { get; set; } = "Pooling=false;ConnectRetryCount=0;Password=" + Secret;
        public override string Database => DatabaseName;
        public override string DataSource => Secret;
        public override string ServerVersion => "1";
        public override ConnectionState State => IsOpen ? ConnectionState.Open : ConnectionState.Closed;
        public override void ChangeDatabase(string name) => throw new NotSupportedException();
        public override void Close() => IsOpen = false;
        public override void Open() => throw new NotSupportedException();
        public override Task OpenAsync(CancellationToken token)
        {
            OpenCount++; OpenToken = token;
            if (FailureAt == -1) throw new InvalidOperationException(Secret);
            IsOpen = true;
            return Task.CompletedTask;
        }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand()
        {
            var command = new FakeCommand(this, Commands.Count); Commands.Add(command); return command;
        }
        public override ValueTask DisposeAsync() { WasDisposed = true; IsOpen = false; return ValueTask.CompletedTask; }
        public Func<int, CancellationToken, Task>? BeforeExecute { get; set; }
        public Action<int>? OnCommandDispose { get; set; }
        public async Task<DbDataReader> Execute(int index, string sql, CancellationToken token)
        {
            Assert.True(token.CanBeCanceled);
            if (BeforeExecute is not null) await BeforeExecute(index, token);
            if (FailureAt == index) throw new InvalidOperationException(Secret);
            var table = index < results.Length ? results[index] :
                sql.Contains("sp_releaseapplock", StringComparison.Ordinal) ? Table(2, [51, 0]) : Table(2, [51, "Exclusive"]);
            return ExtraResultAt == index ? new DataTableReader([table, Table(1, [1])]) : table.CreateDataReader();
        }
    }

    private sealed class FakeCommand(FakeConnection owner, int index) : DbCommand
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
        public override int ExecuteNonQuery() => throw new Xunit.Sdk.XunitException("unexpected non-reader mutation");
        public override object ExecuteScalar() => throw new NotSupportedException();
        public override void Prepare() => throw new NotSupportedException();
        protected override DbParameter CreateDbParameter() => new FakeParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken token) => owner.Execute(index, CommandText, token);
        public override ValueTask DisposeAsync() { WasDisposed = true; owner.OnCommandDispose?.Invoke(index); return ValueTask.CompletedTask; }
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
        public override int IndexOf(string name) => values.FindIndex(p => p.ParameterName == name);
        public override void Insert(int index, object value) => values.Insert(index, (DbParameter)value);
        public override void Remove(object value) => values.Remove((DbParameter)value);
        public override void RemoveAt(int index) => values.RemoveAt(index);
        public override void RemoveAt(string name) => RemoveAt(IndexOf(name));
        protected override DbParameter GetParameter(int index) => values[index];
        protected override DbParameter GetParameter(string name) => values[IndexOf(name)];
        protected override void SetParameter(int index, DbParameter value) => values[index] = value;
        protected override void SetParameter(string name, DbParameter value) => values[IndexOf(name)] = value;
    }
}
