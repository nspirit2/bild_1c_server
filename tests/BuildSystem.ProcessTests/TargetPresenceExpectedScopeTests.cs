using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using BuildSystem.Agent.Tools;
using Xunit;
namespace BuildSystem.ProcessTests;
public sealed class TargetPresenceExpectedScopeTests
{
    private static readonly InfobaseInventoryObserverCredentials Credentials = new("operator", "private-password");
    private sealed class Fixture
    {
        public TargetPresenceAssessmentScope Expected { get; } = new(Guid.NewGuid(), 17, Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        public Guid PhysicalCluster { get; } = Guid.NewGuid();
        public Runner Runner { get; } = new();
        public int SqlFactoryCalls { get; private set; }
        public List<string> Reads { get; } = [];
        public List<string> DatabaseNames { get; } = [];
        public TargetPresenceObserver Observer()
        {
            var rac = new InfobaseInventoryObserver(Runner, new()
            {
                TrustedRacToolId = "operator-rac", Endpoint = "operator-rac:1545",
                EndpointMappingId = Expected.RacEndpointMappingId, CanonicalClusterId = Expected.CanonicalClusterId,
                RacClusterId = PhysicalCluster, WorkingDirectory = Path.GetTempPath(), Timeout = TimeSpan.FromSeconds(10)
            });
            var sql = new SqlDatabaseObserver(() =>
            {
                SqlFactoryCalls++;
                Assert.Equal(SqlFactoryCalls, Runner.Commands.Count); // RAC must finish before SQL starts.
                return new Connection(Reads, DatabaseNames);
            }, new()
            {
                EndpointMappingId = Expected.SqlEndpointMappingId, CanonicalSqlServerId = Expected.CanonicalSqlServerId,
                ExpectedMachineName = "operator-host", TimeoutSeconds = 10
            });
            return new(rac, sql, new() { TotalTimeoutSeconds = 120 });
        }
    }
    [Fact]
    public async Task EveryInvalidExpectedPinIsRejectedBeforeEitherAdapterDoesIo()
    {
        var f = new Fixture();
        var s = f.Expected;
        var observer = f.Observer();
        TargetPresenceAssessmentScope[] invalid = [null!, s with { OperationId = Guid.Empty },
            s with { FencingToken = 0 }, s with { FencingToken = -1 }, s with { TargetIncarnationId = Guid.Empty },
            s with { ReservationId = Guid.Empty }, s with { CanonicalClusterId = Guid.Empty },
            s with { CanonicalSqlServerId = Guid.Empty }, s with { RacEndpointMappingId = Guid.Empty },
            s with { SqlEndpointMappingId = Guid.Empty }, s with { RacObservationId = Guid.Empty },
            s with { SqlObservationId = Guid.Empty }, s with { SqlObservationId = s.RacObservationId }];
        foreach (var expected in invalid)
        {
            var error = await Assert.ThrowsAsync<TargetPresenceObservationException>(
                () => observer.ObserveExpectedScopeAsync(expected, null!));
            Assert.Equal("InvalidTargetPresenceRequest", error.Code);
            Assert.Null(error.InnerException);
        }
        Assert.Empty(f.Runner.Commands);
        Assert.Equal(0, f.SqlFactoryCalls);
    }
    [Fact]
    public async Task CallerObservationIdsAndCanonicalPinsArePreservedByRealObservers()
    {
        var f = new Fixture();
        var result = await f.Observer().ObserveExpectedScopeAsync(f.Expected, Credentials);
        Assert.Equal(f.Expected, result.Scope);
        Assert.NotEqual(result.Scope.RacObservationId, result.Scope.SqlObservationId);
        Assert.Equal("--cluster=" + f.PhysicalCluster.ToString("D"), Assert.Single(f.Runner.Commands).Arguments[3].Value);
        Assert.Equal(new[] { SqlDatabaseObserver.IdentitySql, SqlDatabaseObserver.PresenceSql }, f.Reads);
        Assert.Equal($"bs_test_{f.Expected.ReservationId:N}", Assert.Single(f.DatabaseNames));
        Assert.Equal(TargetPresenceObservation.BothObservedAbsent, result.Observation);
        Assert.False(result.Executable);
        Assert.True(result.ScheduledJobsMustRemainBlocked);
    }
    [Fact]
    public async Task WrongCanonicalClusterStopsBeforeRacProcessAndSqlFactory()
    {
        var f = new Fixture();
        await Unavailable(() => f.Observer().ObserveExpectedScopeAsync(
            f.Expected with { CanonicalClusterId = Guid.NewGuid() }, Credentials));
        Assert.Empty(f.Runner.Commands);
        Assert.Equal(0, f.SqlFactoryCalls);
    }
    [Fact]
    public async Task WrongSqlMappingReadsRacButNeverInvokesSqlFactory()
    {
        var f = new Fixture();
        await Unavailable(() => f.Observer().ObserveExpectedScopeAsync(
            f.Expected with { SqlEndpointMappingId = Guid.NewGuid() }, Credentials));
        Assert.Single(f.Runner.Commands);
        Assert.Equal(0, f.SqlFactoryCalls);
    }
    [Fact]
    public async Task CorrelationIdsDoNotCacheAbsenceOrGrantContinuingAuthority()
    {
        var f = new Fixture();
        var observer = f.Observer();
        var absent = await observer.ObserveExpectedScopeAsync(f.Expected, Credentials);
        f.Runner.Output = $"infobase : {Guid.NewGuid():D}\nname : bs_test_{f.Expected.ReservationId:N}\n";
        var present = await observer.ObserveExpectedScopeAsync(f.Expected, Credentials);
        Assert.Equal(TargetPresenceObservation.BothObservedAbsent, absent.Observation);
        Assert.Equal(TargetPresenceObservation.InfobaseObservedPresent, present.Observation);
        Assert.Equal(f.Expected, present.Scope);
        Assert.Equal(2, f.Runner.Commands.Count);
        Assert.Equal(2, f.SqlFactoryCalls);
        Assert.False(present.Executable);
        Assert.True(present.ScheduledJobsMustRemainBlocked);
    }
    private static async Task Unavailable(Func<Task<TargetPresenceAssessmentResult>> action)
    {
        var error = await Assert.ThrowsAsync<TargetPresenceObservationException>(action);
        Assert.Equal("TargetPresenceObservationUnavailable", error.Code);
        Assert.Equal(error.Code, error.Message);
        Assert.Null(error.InnerException);
    }
    private sealed class Runner : IProcessRunner
    {
        public List<ProcessCommand> Commands { get; } = [];
        public string Output { get; set; } = "";
        public async Task<ProcessResult> RunAsync(ProcessCommand command, Func<ProcessOutput, Task>? onOutput = null,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            await onOutput!(new(ProcessOutputStream.StandardOutput, Output));
            return new(123, 0, ProcessCompletion.Exited, false);
        }
    }
    private sealed class Connection(List<string> reads, List<string> names) : DbConnection
    {
        [AllowNull] public override string ConnectionString { get; set; } = ""; public override string Database => "master";
        public override string DataSource => "operator-host"; public override string ServerVersion => "1";
        public override ConnectionState State => ConnectionState.Open; public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() { } public override void Open() => throw new NotSupportedException();
        public override Task OpenAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new Command(this, reads, names);
    }
    private sealed class Command(Connection owner, List<string> reads, List<string> names) : DbCommand
    {
        [AllowNull] public override string CommandText { get; set; } = ""; public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; } public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; } protected override DbConnection? DbConnection { get; set; } = owner;
        protected override DbTransaction? DbTransaction { get; set; } protected override DbParameterCollection DbParameterCollection { get; } = new Parameters();
        public override void Cancel() { } public override int ExecuteNonQuery() => throw new Xunit.Sdk.XunitException("mutation attempted");
        public override object ExecuteScalar() => throw new NotSupportedException(); public override void Prepare() => throw new NotSupportedException();
        protected override DbParameter CreateDbParameter() => new Parameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        {
            reads.Add(CommandText);
            var identity = CommandText == SqlDatabaseObserver.IdentitySql;
            var table = new DataTable();
            for (var i = 0; i < (identity ? 3 : 4); i++) table.Columns.Add("c" + i, typeof(object));
            if (identity) table.Rows.Add("operator-host", "MSSQLSERVER", 1);
            else names.Add((string)Parameters[0].Value!);
            return Task.FromResult<DbDataReader>(table.CreateDataReader());
        }
    }
    private sealed class Parameter : DbParameter
    {
        public override DbType DbType { get; set; } public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
        public override bool IsNullable { get; set; } [AllowNull] public override string ParameterName { get; set; } = "";
        [AllowNull] public override string SourceColumn { get; set; } = ""; public override object? Value { get; set; }
        public override bool SourceColumnNullMapping { get; set; } public override int Size { get; set; }
        public override void ResetDbType() { }
    }
    private sealed class Parameters : DbParameterCollection
    {
        private readonly List<DbParameter> values = [];
        public override int Count => values.Count; public override object SyncRoot => this;
        public override int Add(object value) { values.Add((DbParameter)value); return values.Count - 1; }
        public override void AddRange(Array items) { foreach (var item in items) Add(item!); } public override void Clear() => values.Clear();
        public override bool Contains(object value) => values.Contains((DbParameter)value); public override bool Contains(string value) => IndexOf(value) >= 0;
        public override void CopyTo(Array array, int index) => ((ICollection)values).CopyTo(array, index); public override IEnumerator GetEnumerator() => values.GetEnumerator();
        public override int IndexOf(object value) => values.IndexOf((DbParameter)value);
        public override int IndexOf(string name) => values.FindIndex(p => p.ParameterName == name);
        public override void Insert(int index, object value) => values.Insert(index, (DbParameter)value);
        public override void Remove(object value) => values.Remove((DbParameter)value); public override void RemoveAt(int index) => values.RemoveAt(index);
        public override void RemoveAt(string name) => RemoveAt(IndexOf(name)); protected override DbParameter GetParameter(int index) => values[index];
        protected override DbParameter GetParameter(string name) => values[IndexOf(name)];
        protected override void SetParameter(int index, DbParameter value) => values[index] = value;
        protected override void SetParameter(string name, DbParameter value) => values[IndexOf(name)] = value;
    }
}
