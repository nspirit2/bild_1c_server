using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using BuildSystem.Agent.Tools;
using Xunit;

namespace BuildSystem.ProcessTests;

public sealed class TargetPresenceObserverTests
{
    private const string Secret = "private-target-provider-password";
    private static readonly InfobaseInventoryObserverCredentials Credentials = new("private-cluster-user", Secret);
    private static TargetPresenceRequest Request() => new(Guid.NewGuid(), 17, Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static ProcessResult Success() => new(123, 0, ProcessCompletion.Exited, false);
    private static DataTable Table(params object[][] rows)
    {
        var table = new DataTable();
        for (var i = 0; i < (rows.Length == 0 ? 4 : rows[0].Length); i++)
            table.Columns.Add("c" + i, typeof(object));
        foreach (var row in rows) table.Rows.Add(row);
        return table;
    }

    private sealed class Fixture
    {
        public TargetPresenceRequest Request { get; } = TargetPresenceObserverTests.Request();
        public int SqlFactoryCalls { get; private set; }
        public List<FakeConnection> Connections { get; } = [];
        public FakeRunner Runner { get; } = new();
        public bool SqlPresent { get; set; }
        public int Visibility { get; set; } = 1;
        public Action? OnSqlPresence { get; set; }
        public bool SqlFailure { get; set; }
        public TargetPresenceObserver Observer(int timeout = 10)
        {
            var rac = new InfobaseInventoryObserver(Runner, new()
            {
                TrustedRacToolId = "operator-rac", Endpoint = "operator-rac:1545",
                EndpointMappingId = Request.RacEndpointMappingId,
                WorkingDirectory = Path.GetTempPath(), Timeout = TimeSpan.FromSeconds(30)
            });
            var sql = new SqlDatabaseObserver(() =>
            {
                SqlFactoryCalls++;
                var connection = new FakeConnection(Table(["operator-host", "MSSQLSERVER", Visibility]),
                    SqlPresent ? Table([$"bs_test_{Request.ReservationId:N}", 7, new DateTime(2025, 1, 2), "ONLINE"]) : Table())
                { OnPresence = OnSqlPresence, Failure = SqlFailure };
                Connections.Add(connection);
                return connection;
            }, new()
            {
                EndpointMappingId = Request.SqlEndpointMappingId,
                CanonicalSqlServerId = Request.CanonicalSqlServerId,
                ExpectedMachineName = "operator-host", TimeoutSeconds = 10
            });
            return new(rac, sql, new() { TotalTimeoutSeconds = timeout });
        }
    }

    private static async Task Unavailable(Func<Task<BuildSystem.Agent.Tools.TargetPresenceAssessmentResult>> action)
    {
        var error = await Assert.ThrowsAsync<TargetPresenceObservationException>(action);
        Assert.Equal("TargetPresenceObservationUnavailable", error.Code);
        Assert.Equal(error.Code, error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Secret, error.ToString());
        Assert.DoesNotContain(Credentials.ClusterUser, error.ToString());
    }

    [Theory]
    [InlineData(false, false, TargetPresenceObservation.BothObservedAbsent)]
    [InlineData(true, false, TargetPresenceObservation.InfobaseObservedPresent)]
    [InlineData(false, true, TargetPresenceObservation.SqlDatabaseObservedPresent)]
    [InlineData(true, true, TargetPresenceObservation.BothObservedPresent)]
    public async Task ActualAdaptersProduceAllFourReadOnlyOutcomes(bool racPresent, bool sqlPresent,
        TargetPresenceObservation observation)
    {
        var fixture = new Fixture { SqlPresent = sqlPresent };
        if (racPresent)
            fixture.Runner.Output = $"infobase : {Guid.NewGuid():D}\nname : bs_test_{fixture.Request.ReservationId:N}\n";
        var result = await fixture.Observer().ObserveAsync(fixture.Request, Credentials);
        Assert.Equal(observation, result.Observation);
        Assert.False(result.Executable);
        Assert.True(result.ScheduledJobsMustRemainBlocked);
        Assert.Equal(1, fixture.Runner.Calls);
        Assert.Equal(1, fixture.SqlFactoryCalls);
        Assert.True(Assert.Single(fixture.Connections).WasDisposed);
    }

    [Fact]
    public async Task PinsFanOutAndEachInvocationHasFreshDistinctObservationIds()
    {
        var fixture = new Fixture();
        var observer = fixture.Observer();
        var first = await observer.ObserveAsync(fixture.Request, Credentials);
        var second = await observer.ObserveAsync(fixture.Request, Credentials);
        foreach (var result in new[] { first, second })
        {
            var scope = result.Scope;
            Assert.Equal(new TargetPresenceAssessmentScope(fixture.Request.OperationId, fixture.Request.FencingToken,
                fixture.Request.TargetIncarnationId, fixture.Request.ReservationId, fixture.Request.CanonicalClusterId,
                fixture.Request.CanonicalSqlServerId, fixture.Request.RacEndpointMappingId,
                fixture.Request.SqlEndpointMappingId, scope.RacObservationId, scope.SqlObservationId), scope);
            Assert.NotEqual(Guid.Empty, scope.RacObservationId);
            Assert.NotEqual(Guid.Empty, scope.SqlObservationId);
        }
        Assert.Equal(4, new[] { first.Scope.RacObservationId, first.Scope.SqlObservationId,
            second.Scope.RacObservationId, second.Scope.SqlObservationId }.Distinct().Count());
        Assert.Equal(2, fixture.Runner.Calls);
        Assert.Equal(2, fixture.SqlFactoryCalls);
        Assert.All(fixture.Runner.Commands, command =>
            Assert.Equal("--cluster=" + fixture.Request.CanonicalClusterId.ToString("D"), command.Arguments[3].Value));
        Assert.All(fixture.Connections, connection =>
        {
            Assert.Equal(SqlDatabaseObserver.IdentitySql, connection.Commands[0].CommandText);
            Assert.Equal(SqlDatabaseObserver.PresenceSql, connection.Commands[1].CommandText);
            Assert.Equal($"bs_test_{fixture.Request.ReservationId:N}", connection.Commands[1].Parameters[0].Value);
        });
    }

    [Fact]
    public async Task EveryInvalidRequestPinRejectedBeforeAnyAdapterIo()
    {
        var fixture = new Fixture();
        var request = fixture.Request;
        var observer = fixture.Observer();
        TargetPresenceRequest[] invalid = [null!, request with { OperationId = Guid.Empty },
            request with { FencingToken = 0 }, request with { FencingToken = -1 },
            request with { TargetIncarnationId = Guid.Empty }, request with { ReservationId = Guid.Empty },
            request with { CanonicalClusterId = Guid.Empty }, request with { CanonicalSqlServerId = Guid.Empty },
            request with { RacEndpointMappingId = Guid.Empty }, request with { SqlEndpointMappingId = Guid.Empty }];
        foreach (var value in invalid)
        {
            var error = await Assert.ThrowsAsync<TargetPresenceObservationException>(() => observer.ObserveAsync(value, null!));
            Assert.Equal("InvalidTargetPresenceRequest", error.Code);
            Assert.Null(error.InnerException);
        }
        Assert.Equal(0, fixture.Runner.Calls);
        Assert.Equal(0, fixture.SqlFactoryCalls);
    }

    [Fact]
    public async Task RacMappingMismatchStopsBeforeSqlFactoryOrProcess()
    {
        var fixture = new Fixture();
        await Unavailable(() => fixture.Observer().ObserveAsync(
            fixture.Request with { RacEndpointMappingId = Guid.NewGuid() }, Credentials));
        Assert.Equal(0, fixture.Runner.Calls);
        Assert.Equal(0, fixture.SqlFactoryCalls);
    }

    [Fact]
    public async Task SqlMappingMismatchAfterRacReturnsNoPartialAssessment()
    {
        var fixture = new Fixture();
        await Unavailable(() => fixture.Observer().ObserveAsync(
            fixture.Request with { SqlEndpointMappingId = Guid.NewGuid() }, Credentials));
        Assert.Equal(1, fixture.Runner.Calls);
        Assert.Equal(0, fixture.SqlFactoryCalls);
    }

    [Theory]
    [InlineData("stderr")]
    [InlineData("nonzero")]
    [InlineData("malformed")]
    [InlineData("exception")]
    public async Task EveryRacFailureStopsSqlAndRedactsErrors(string failure)
    {
        var fixture = new Fixture();
        fixture.Runner.Failure = failure;
        await Unavailable(() => fixture.Observer().ObserveAsync(fixture.Request, Credentials));
        Assert.Equal(1, fixture.Runner.Calls);
        Assert.Equal(0, fixture.SqlFactoryCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HiddenMetadataOrProviderFailureDiscardsCompletedRac(bool providerFailure)
    {
        var fixture = new Fixture { Visibility = 0, SqlFailure = providerFailure };
        await Unavailable(() => fixture.Observer().ObserveAsync(fixture.Request, Credentials));
        Assert.Equal(1, fixture.Runner.Calls);
        Assert.Equal(1, fixture.SqlFactoryCalls);
        Assert.True(Assert.Single(fixture.Connections).WasDisposed);
    }

    [Fact]
    public async Task PreCancellationPerformsNoIo()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Unavailable(() => fixture.Observer().ObserveAsync(fixture.Request, Credentials, cancellation.Token));
        Assert.Equal(0, fixture.Runner.Calls);
        Assert.Equal(0, fixture.SqlFactoryCalls);
    }

    [Fact]
    public async Task CancellationDuringRacRejectsIgnoredTokenAndStopsSql()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Runner.OnRun = cancellation.Cancel;
        await Unavailable(() => fixture.Observer().ObserveAsync(fixture.Request, Credentials, cancellation.Token));
        Assert.Equal(1, fixture.Runner.Calls);
        Assert.Equal(0, fixture.SqlFactoryCalls);
    }

    [Fact]
    public async Task CancellationDuringSqlRejectsIgnoredTokenAndDisposesConnection()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new Fixture { OnSqlPresence = cancellation.Cancel };
        await Unavailable(() => fixture.Observer().ObserveAsync(fixture.Request, Credentials, cancellation.Token));
        Assert.Equal(1, fixture.Runner.Calls);
        Assert.Equal(1, fixture.SqlFactoryCalls);
        Assert.True(Assert.Single(fixture.Connections).WasDisposed);
    }

    [Fact]
    public async Task OverallTimeoutRejectsDelayedRacSuccessEvenWhenRunnerIgnoresCancellation()
    {
        var fixture = new Fixture();
        fixture.Runner.Delay = true;
        await Unavailable(() => fixture.Observer(1).ObserveAsync(fixture.Request, Credentials));
        Assert.Equal(1, fixture.Runner.Calls);
        Assert.Equal(0, fixture.SqlFactoryCalls);
        Assert.True(fixture.Runner.Completed);
    }

    [Fact]
    public async Task InvalidCredentialsAreValidatedByRacBeforeAnyIoAndDiagnosticsAreRedacted()
    {
        var fixture = new Fixture();
        var observer = fixture.Observer();
        foreach (var credentials in new InfobaseInventoryObserverCredentials[]
            { null!, new("", Secret), new(Credentials.ClusterUser, "bad\n" + Secret) })
            await Unavailable(() => observer.ObserveAsync(fixture.Request, credentials));
        Assert.Equal(0, fixture.Runner.Calls);
        Assert.Equal(0, fixture.SqlFactoryCalls);
        foreach (var text in new[] { fixture.Request.ToString(), Credentials.ToString(),
            new TargetPresenceObserverOptions { TotalTimeoutSeconds = 1 }.ToString() })
        {
            Assert.DoesNotContain(Secret, text);
            Assert.DoesNotContain(Credentials.ClusterUser, text);
            Assert.DoesNotContain(fixture.Request.OperationId.ToString(), text);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(121)]
    public void InvalidOverallTimeoutRejectedWithoutIo(int timeout)
    {
        var fixture = new Fixture();
        Assert.Throws<ArgumentException>(() => fixture.Observer(timeout));
        Assert.Equal(0, fixture.Runner.Calls);
        Assert.Equal(0, fixture.SqlFactoryCalls);
    }

    private sealed class FakeRunner : IProcessRunner
    {
        public int Calls { get; private set; }
        public List<ProcessCommand> Commands { get; } = [];
        public string Output { get; set; } = "";
        public string? Failure { get; set; }
        public Action? OnRun { get; set; }
        public bool Delay { get; set; }
        public bool Completed { get; private set; }
        public async Task<ProcessResult> RunAsync(ProcessCommand command, Func<ProcessOutput, Task>? onOutput = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Commands.Add(command);
            Assert.True(cancellationToken.CanBeCanceled);
            OnRun?.Invoke();
            if (Delay) await Task.Delay(1200); // Deliberately ignores cancellation and command.Timeout.
            if (Failure == "exception") throw new InvalidOperationException(Secret);
            await onOutput!(new(Failure == "stderr" ? ProcessOutputStream.StandardError : ProcessOutputStream.StandardOutput,
                Failure is "stderr" or "malformed" ? Secret : Output));
            Completed = true;
            return Failure == "nonzero" ? new(123, 1, ProcessCompletion.Exited, false) : Success();
        }
    }

    private sealed class FakeConnection(DataTable identity, DataTable presence) : DbConnection
    {
        public Action? OnPresence { get; init; }
        public bool Failure { get; init; }
        public bool WasDisposed { get; private set; }
        public List<FakeCommand> Commands { get; } = [];
        [AllowNull] public override string ConnectionString { get; set; } = Secret;
        public override string Database => "master";
        public override string DataSource => Secret;
        public override string ServerVersion => "1";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() { }
        public override void Open() => throw new NotSupportedException();
        public override Task OpenAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand()
        {
            var command = new FakeCommand(this, Commands.Count == 0);
            Commands.Add(command);
            return command;
        }
        public DbDataReader Execute(bool first)
        {
            if (Failure) throw new InvalidOperationException(Secret);
            if (!first) OnPresence?.Invoke(); // Deliberately returns success despite cancellation.
            return (first ? identity : presence).CreateDataReader();
        }
        public override ValueTask DisposeAsync() { WasDisposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class FakeCommand(FakeConnection owner, bool first) : DbCommand
    {
        private readonly FakeParameters parameters = new();
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
            => Task.FromResult(owner.Execute(first));
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
