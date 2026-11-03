using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using BuildSystem.Agent;
using BuildSystem.Agent.Tools;
using BuildSystem.Contracts;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class TargetObservationExecutorTests
{
    private const string Secret = "private-executor-provider-password";
    private static TargetObservationAssignment Assignment() => new(Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 17, Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Theory]
    [InlineData(false, false, TargetObservationOutcome.BothObservedAbsent)]
    [InlineData(true, false, TargetObservationOutcome.InfobaseObservedPresent)]
    [InlineData(false, true, TargetObservationOutcome.SqlDatabaseObservedPresent)]
    [InlineData(true, true, TargetObservationOutcome.BothObservedPresent)]
    public async Task RealAdaptersProduceFourHistoricalReadOnlyReceipts(bool rac, bool sql, TargetObservationOutcome outcome)
    {
        var fixture = new Fixture { RacPresent = rac, SqlPresent = sql };
        var before = DateTimeOffset.UtcNow;
        var result = await fixture.Executor().ObserveAsync(fixture.Assignment);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(fixture.Assignment, result.Assignment);
        Assert.NotEqual(Guid.Empty, result.ReceiptId);
        Assert.NotEqual(Guid.Empty, result.RacObservationId);
        Assert.NotEqual(Guid.Empty, result.SqlObservationId);
        Assert.NotEqual(result.RacObservationId, result.SqlObservationId);
        Assert.InRange(result.ObservedAt, before, DateTimeOffset.UtcNow);
        Assert.False(result.Executable);
        Assert.True(result.ScheduledJobsMustRemainBlocked);
        Assert.Equal(new[] { "credentials", "rac", "sql", "identity", "presence" }, fixture.Events);
        Assert.True(fixture.Connection!.WasDisposed);
        Assert.Equal("--cluster=" + fixture.Assignment.CanonicalClusterId.ToString("D"), fixture.Runner.Command!.Arguments[3].Value);
        Assert.Equal("bs_test_" + fixture.Assignment.ReservationId.ToString("N"), fixture.Connection.PresenceName);
    }

    [Fact]
    public async Task RepeatedInvocationsHaveIndependentReceiptAndObservationIdentities()
    {
        var fixture = new Fixture();
        var executor = fixture.Executor();
        var first = await executor.ObserveAsync(fixture.Assignment);
        var second = await executor.ObserveAsync(fixture.Assignment);
        Assert.Equal(6, new[] { first.ReceiptId, first.RacObservationId, first.SqlObservationId,
            second.ReceiptId, second.RacObservationId, second.SqlObservationId }.Distinct().Count());
    }

    [Fact]
    public async Task EveryInvalidAssignmentPinRejectedBeforeCredentialsOrIo()
    {
        var fixture = new Fixture();
        var a = fixture.Assignment;
        TargetObservationAssignment[] invalid = [null!, a with { ObservationRequestId = Guid.Empty },
            a with { RestoreIntentId = Guid.Empty }, a with { AgentId = Guid.Empty },
            a with { AgentId = Guid.NewGuid() }, a with { OperationId = Guid.Empty },
            a with { EnvironmentId = Guid.Empty }, a with { StageJobId = Guid.Empty },
            a with { FencingToken = 0 }, a with { FencingToken = -1 }, a with { ReservationId = Guid.Empty },
            a with { TargetIncarnationId = Guid.Empty }, a with { CanonicalClusterId = Guid.Empty },
            a with { CanonicalSqlServerId = Guid.Empty }, a with { RacEndpointMappingId = Guid.Empty },
            a with { SqlEndpointMappingId = Guid.Empty }];
        var executor = fixture.Executor();
        foreach (var value in invalid) await Redacted(() => executor.ObserveAsync(value));
        Assert.Empty(fixture.Events);
    }

    [Fact]
    public void ConstructorRejectsInvalidIdentityAndMissingDependenciesWithoutIo()
    {
        var fixture = new Fixture();
        var observer = fixture.Observer();
        Assert.Throws<ArgumentException>(() => new TargetObservationExecutor(Guid.Empty, observer, () => new("u", Secret)));
        Assert.Throws<ArgumentNullException>(() => new TargetObservationExecutor(fixture.Assignment.AgentId, null!, () => new("u", Secret)));
        Assert.Throws<ArgumentNullException>(() => new TargetObservationExecutor(fixture.Assignment.AgentId, observer, null!));
        Assert.Empty(fixture.Events);
    }

    [Fact]
    public async Task CredentialProviderFailureIsRedactedBeforeAnyAdapterIo()
    {
        var fixture = new Fixture();
        await Redacted(() => fixture.Executor(() => throw new InvalidOperationException(Secret)).ObserveAsync(fixture.Assignment));
        Assert.Empty(fixture.Events);
    }

    [Fact]
    public async Task MissingCredentialsFailClosedBeforeAdapterIo()
    {
        var fixture = new Fixture();
        await Redacted(() => fixture.Executor(() => null!).ObserveAsync(fixture.Assignment));
        Assert.Empty(fixture.Events);
    }

    [Fact]
    public async Task UnrequestedProviderCancellationIsRedactedNotPropagated()
    {
        var fixture = new Fixture();
        await Redacted(() => fixture.Executor(() => throw new OperationCanceledException(Secret)).ObserveAsync(fixture.Assignment));
    }

    [Theory]
    [InlineData("credentials")]
    [InlineData("rac")]
    [InlineData("presence")]
    public async Task CallerCancellationIsSafeAndNeverReturnsPartialReceipt(string phase)
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.OnEvent = name => { if (name == phase) cancellation.Cancel(); };
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Executor().ObserveAsync(fixture.Assignment, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Secret, error.ToString());
        if (phase != "presence") Assert.DoesNotContain("sql", fixture.Events);
    }

    [Fact]
    public async Task PreCancellationStopsBeforeCredentialProvider()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Executor().ObserveAsync(fixture.Assignment, cancellation.Token));
        Assert.Empty(fixture.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdapterFailuresAreRedactedAndNeverReturnPartialReceipts(bool sqlFailure)
    {
        var fixture = new Fixture { RacFailure = !sqlFailure, SqlFailure = sqlFailure };
        await Redacted(() => fixture.Executor().ObserveAsync(fixture.Assignment));
        if (!sqlFailure) Assert.DoesNotContain("sql", fixture.Events);
        else Assert.True(fixture.Connection!.WasDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MappingMismatchFailsClosedAtItsAdapter(bool sqlMapping)
    {
        var fixture = new Fixture();
        var assignment = sqlMapping ? fixture.Assignment with { SqlEndpointMappingId = Guid.NewGuid() }
            : fixture.Assignment with { RacEndpointMappingId = Guid.NewGuid() };
        await Redacted(() => fixture.Executor().ObserveAsync(assignment));
        Assert.DoesNotContain("sql", fixture.Events);
        if (!sqlMapping) Assert.DoesNotContain("rac", fixture.Events);
    }

    private static async Task Redacted(Func<Task<ReportTargetObservation>> action)
    {
        var error = await Assert.ThrowsAsync<TargetObservationExecutionException>(action);
        Assert.Equal("Target observation execution failed.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Secret, error.ToString());
    }

    private sealed class Fixture
    {
        public TargetObservationAssignment Assignment { get; } = TargetObservationExecutorTests.Assignment();
        public List<string> Events { get; } = [];
        public Action<string>? OnEvent { get; set; }
        public bool RacPresent { get; init; }
        public bool SqlPresent { get; init; }
        public bool RacFailure { get; init; }
        public bool SqlFailure { get; init; }
        public FakeRunner Runner { get; private set; } = null!;
        public FakeConnection? Connection { get; private set; }
        public void Event(string name) { Events.Add(name); OnEvent?.Invoke(name); }
        public TargetPresenceObserver Observer()
        {
            Runner = new FakeRunner(this);
            return new(new InfobaseInventoryObserver(Runner, new()
            {
                TrustedRacToolId = "operator-rac", Endpoint = "operator-rac:1545",
                EndpointMappingId = Assignment.RacEndpointMappingId,
                WorkingDirectory = Path.GetTempPath(), Timeout = TimeSpan.FromSeconds(10)
            }), new SqlDatabaseObserver(() =>
            {
                Event("sql");
                return Connection = new FakeConnection(this);
            }, new()
            {
                EndpointMappingId = Assignment.SqlEndpointMappingId, CanonicalSqlServerId = Assignment.CanonicalSqlServerId,
                ExpectedMachineName = "operator-host", TimeoutSeconds = 10
            }), new() { TotalTimeoutSeconds = 10 });
        }
        public TargetObservationExecutor Executor(Func<InfobaseInventoryObserverCredentials>? credentials = null)
            => new(Assignment.AgentId, Observer(), credentials ?? (() => { Event("credentials"); return new("operator-user", Secret); }));
    }

    private sealed class FakeRunner(Fixture fixture) : IProcessRunner
    {
        public ProcessCommand? Command { get; private set; }
        public async Task<ProcessResult> RunAsync(ProcessCommand command, Func<ProcessOutput, Task>? onOutput = null,
            CancellationToken cancellationToken = default)
        {
            Command = command;
            fixture.Event("rac");
            if (fixture.RacFailure) throw new InvalidOperationException(Secret);
            await onOutput!(new(ProcessOutputStream.StandardOutput, fixture.RacPresent
                ? $"infobase : {Guid.NewGuid():D}\nname : bs_test_{fixture.Assignment.ReservationId:N}\n" : ""));
            return new(123, 0, ProcessCompletion.Exited, false);
        }
    }

    private sealed class FakeConnection(Fixture fixture) : DbConnection
    {
        private int commands;
        public bool WasDisposed { get; private set; }
        public object? PresenceName { get; set; }
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
        protected override DbCommand CreateDbCommand() => new FakeCommand(this, commands++ == 0);
        public DbDataReader Execute(bool first)
        {
            fixture.Event(first ? "identity" : "presence");
            if (fixture.SqlFailure) throw new InvalidOperationException(Secret);
            var table = new DataTable();
            for (var i = 0; i < (first ? 3 : 4); i++) table.Columns.Add("c" + i, typeof(object));
            if (first) table.Rows.Add("operator-host", "MSSQLSERVER", 1);
            else if (fixture.SqlPresent) table.Rows.Add($"bs_test_{fixture.Assignment.ReservationId:N}", 7, new DateTime(2025, 1, 2), "ONLINE");
            return table.CreateDataReader();
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
        {
            Assert.Equal(first ? SqlDatabaseObserver.IdentitySql : SqlDatabaseObserver.PresenceSql, CommandText);
            if (!first) owner.PresenceName = parameters[0].Value;
            return Task.FromResult(owner.Execute(first));
        }
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
