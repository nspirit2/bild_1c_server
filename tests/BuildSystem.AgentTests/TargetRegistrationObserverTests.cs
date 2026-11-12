using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using BuildSystem.Agent.Execution;
using BuildSystem.Agent.Tools;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class TargetRegistrationObserverTests
{
    private const string Secret = "private-registration-provider-password";
    private static readonly InfobaseRegistrationObserverCredentials Credentials = new("private-user", Secret);
    private sealed class Fixture
    {
        public TargetRegistrationAssessmentScope Expected { get; }
        public FakeRunner Runner { get; } = new();
        public List<string> Order { get; } = [];
        public int SqlCalls { get; private set; }
        public FakeConnection? Connection { get; private set; }
        public string? SqlState { get; set; } = "ONLINE";
        public bool WrongName { get; set; }
        public bool SqlFailure { get; set; }
        public Action? OnSql { get; set; }
        public Action? OnDispose { get; set; }
        public Fixture()
        {
            var reservation = Guid.NewGuid();
            var name = $"bs_test_{reservation:N}";
            Expected = new(new(Guid.NewGuid(), 17, Guid.NewGuid(), reservation, Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                name, "sql01\\INSTANCE", name), Guid.NewGuid(), InfobaseRegistrationReadback.ObservedProfileId);
            Runner.Output = Output(Expected.RegistrationScope);
            Runner.OnRun = () => Order.Add("RAC");
        }
        public TargetRegistrationObserver Observer(bool enabled = true, int timeout = 10,
            TimeProvider? clock = null, bool synthetic = false)
        {
            var s = Expected.RegistrationScope;
            var rac = new InfobaseRegistrationObserver(Runner, new()
            {
                Enabled = true, TrustedRacToolId = "operator-rac", TrustedRacVersion = "8.3.22.2557",
                Endpoint = "rac01:1545", WorkingDirectory = Path.GetTempPath(), Timeout = TimeSpan.FromSeconds(30),
                CanonicalClusterId = s.CanonicalClusterId, CanonicalSqlServerId = s.CanonicalSqlServerId,
                RacEndpointMappingId = s.RacEndpointMappingId, SqlEndpointMappingId = s.SqlEndpointMappingId,
                PhysicalClusterId = Guid.NewGuid(), ExpectedSqlServer = s.ExpectedSqlServer,
                OutputProfileId = synthetic ? InfobaseRegistrationReadback.ProfileId : InfobaseRegistrationReadback.ObservedProfileId
            });
            var sql = new SqlDatabaseObserver(() =>
            {
                SqlCalls++; Order.Add("SQL");
                return Connection = new(Table(["operator-host", "MSSQLSERVER", 1]), SqlState is null ? Table() :
                    Table([WrongName ? s.ExpectedDatabaseName.ToUpperInvariant() : s.ExpectedDatabaseName,
                        7, new DateTime(2025, 1, 2), SqlState]))
                { Failure = SqlFailure, OnPresence = OnSql, OnDispose = OnDispose };
            }, new() { EndpointMappingId = s.SqlEndpointMappingId, CanonicalSqlServerId = s.CanonicalSqlServerId,
                ExpectedMachineName = "operator-host", TimeoutSeconds = 10 });
            return new(rac, sql, new() { Enabled = enabled, TotalTimeoutSeconds = timeout }, clock);
        }
    }
    private static string Core(InfobaseRegistrationReadbackScope s) =>
        $"infobase : {s.InfobaseId:D}\nname : {s.ExpectedInfobaseName}\ndbms : MSSQLServer\n" +
        $"db-server : {s.ExpectedSqlServer}\ndb-name : {s.ExpectedDatabaseName}\nscheduled-jobs-deny : on\n";
    // Reconstructed 21-key observed shape, never a live-output fixture.
    private static string Output(InfobaseRegistrationReadbackScope s) => Core(s) +
        "db-user : \nsecurity-level : 0\nlicense-distribution : deny\nsessions-deny : on\n" +
        "denied-from : \ndenied-message : \ndenied-parameter : \ndenied-to : \npermission-code : private\n" +
        "external-session-manager-connection-string : private\nexternal-session-manager-required : no\n" +
        "security-profile-name : \nsafe-mode-security-profile-name : \nreserve-working-processes : no\ndescr : private\n\n";
    private static async Task Error(Func<Task<TargetRegistrationAssessmentResult>> action,
        string code = "TargetRegistrationObserverUnavailable")
    {
        var error = await Assert.ThrowsAsync<TargetRegistrationObserverException>(action);
        Assert.Equal(code, error.Code); Assert.Equal(code, error.Message); Assert.Null(error.InnerException);
        Assert.DoesNotContain(Secret, error.ToString()); Assert.DoesNotContain(Credentials.ClusterUser, error.ToString());
    }

    [Fact]
    public async Task DefaultOffIgnoresInvalidTimeoutAndScopeWithoutIo()
    {
        var f = new Fixture();
        await Error(() => f.Observer(false, -5).ObserveExpectedScopeAsync(null!, null!), "TargetRegistrationObserverDisabled");
        Assert.False(new TargetRegistrationObserverOptions().Enabled);
        Assert.Equal(0, f.Runner.Calls); Assert.Equal(0, f.SqlCalls);
    }
    [Theory]
    [InlineData(0)] [InlineData(121)] [InlineData(-1)]
    public void EnabledInvalidOptionsHaveFixedDiagnostic(int timeout)
    {
        var f = new Fixture();
        var error = Assert.Throws<TargetRegistrationObserverException>(() => f.Observer(timeout: timeout));
        Assert.Equal("TargetRegistrationObserverInvalidOptions", error.Code); Assert.Null(error.InnerException);
        Assert.Equal(0, f.Runner.Calls); Assert.Equal(0, f.SqlCalls);
    }
    [Fact]
    public void NullAdaptersEvenDisabledHaveFixedDiagnostic()
    {
        var error = Assert.Throws<TargetRegistrationObserverException>(() => new TargetRegistrationObserver(null!, null!, new()));
        Assert.Equal("TargetRegistrationObserverInvalidOptions", error.Code);
    }
    [Fact]
    public async Task ActualAdaptersRunSequentiallyAndReturnOnlyInertObservedMetadata()
    {
        var f = new Fixture();
        var result = await f.Observer().ObserveExpectedScopeAsync(f.Expected, Credentials);
        Assert.Equal(new[] { "RAC", "SQL" }, f.Order);
        Assert.Equal(f.Expected, result.Scope); Assert.Equal(f.Expected.RegistrationScope.InfobaseId, result.InfobaseId);
        Assert.Equal(InfobaseRegistrationReadback.ObservedProfileId, result.RegistrationProfileId);
        Assert.Equal(7, result.Database.DatabaseId); Assert.Equal(new DateTime(2025, 1, 2), result.Database.CreateDate);
        Assert.False(result.Executable); Assert.False(result.OwnershipEstablished); Assert.False(result.RestoreAllowed);
        Assert.False(result.FreshAuthority); Assert.True(f.Connection!.WasDisposed);
        Assert.Equal(SqlDatabaseObserver.IdentitySql, f.Connection.Commands[0].CommandText);
        Assert.Equal(SqlDatabaseObserver.PresenceSql, f.Connection.Commands[1].CommandText);
        Assert.Equal(f.Expected.RegistrationScope.ExpectedDatabaseName, f.Connection.Commands[1].Parameters[0].Value);
    }
    [Fact]
    public async Task EveryInvalidCorrelationIsRejectedBeforeIo()
    {
        var f = new Fixture(); var e = f.Expected; var s = e.RegistrationScope;
        var invalid = new List<TargetRegistrationAssessmentScope> { null!, e with { RegistrationScope = null! },
            e with { SqlObservationId = Guid.Empty }, e with { SqlObservationId = s.ObservationId },
            e with { ExpectedProfileId = InfobaseRegistrationReadback.ProfileId }, e with { ExpectedProfileId = "unknown" } };
        InfobaseRegistrationReadbackScope[] scopes = [s with { OperationId = Guid.Empty }, s with { FencingToken = 0 },
            s with { TargetIncarnationId = Guid.Empty }, s with { ReservationId = Guid.Empty },
            s with { CanonicalClusterId = Guid.Empty }, s with { CanonicalSqlServerId = Guid.Empty },
            s with { RacEndpointMappingId = Guid.Empty }, s with { SqlEndpointMappingId = Guid.Empty },
            s with { AttemptId = Guid.Empty }, s with { ClaimId = Guid.Empty }, s with { InfobaseId = Guid.Empty },
            s with { ObservationId = Guid.Empty }, s with { ExpectedInfobaseName = "wrong" },
            s with { ExpectedDatabaseName = s.ExpectedDatabaseName.ToUpperInvariant() },
            s with { ExpectedSqlServer = "sql host" }, s with { ExpectedSqlServer = "-sql" },
            s with { ExpectedSqlServer = "сервер" }, s with { ExpectedSqlServer = new string('s', 256) }];
        invalid.AddRange(scopes.Select(scope => e with { RegistrationScope = scope }));
        var observer = f.Observer();
        foreach (var value in invalid)
            await Error(() => observer.ObserveExpectedScopeAsync(value, Credentials), "TargetRegistrationObserverInvalidScope");
        Assert.Equal(0, f.Runner.Calls); Assert.Equal(0, f.SqlCalls);
    }
    [Theory]
    [InlineData("throw")] [InlineData("malformed")] [InlineData("jobs")] [InlineData("synthetic")]
    public async Task BadRacNeverFallsBackToSql(string failure)
    {
        var f = new Fixture(); f.Runner.Failure = failure == "throw";
        if (failure == "malformed") f.Runner.Output = Secret;
        if (failure == "jobs") f.Runner.Output = f.Runner.Output.Replace("scheduled-jobs-deny : on", "scheduled-jobs-deny : off");
        if (failure == "synthetic") f.Runner.Output = Core(f.Expected.RegistrationScope);
        await Error(() => f.Observer(synthetic: failure == "synthetic").ObserveExpectedScopeAsync(f.Expected, Credentials));
        Assert.Equal(1, f.Runner.Calls); Assert.Equal(0, f.SqlCalls);
    }
    [Theory]
    [InlineData(null, false)] [InlineData("OFFLINE", false)] [InlineData("ONLINE", true)]
    public async Task SqlAbsentOfflineOrCaseMismatchCannotProduceAssessment(string? state, bool wrongName)
    {
        var f = new Fixture { SqlState = state, WrongName = wrongName };
        await Error(() => f.Observer().ObserveExpectedScopeAsync(f.Expected, Credentials));
        Assert.Equal(1, f.SqlCalls); Assert.True(f.Connection!.WasDisposed);
    }
    [Theory]
    [InlineData("before")] [InlineData("rac")] [InlineData("sql")] [InlineData("dispose")]
    public async Task CancellationAtEveryBoundaryIsFixedAndCleanupCompletes(string boundary)
    {
        using var cts = new CancellationTokenSource(); var f = new Fixture();
        if (boundary == "before") cts.Cancel();
        if (boundary == "rac") f.Runner.OnRun = cts.Cancel;
        if (boundary == "sql") f.OnSql = cts.Cancel;
        if (boundary == "dispose") f.OnDispose = cts.Cancel;
        await Error(() => f.Observer().ObserveExpectedScopeAsync(f.Expected, Credentials, cts.Token));
        Assert.Equal(boundary == "before" ? 0 : 1, f.Runner.Calls);
        Assert.Equal(boundary is "sql" or "dispose" ? 1 : 0, f.SqlCalls);
        if (f.Connection is not null) Assert.True(f.Connection.WasDisposed);
    }
    [Theory]
    [InlineData(1, "throw", 0)] [InlineData(2, "throw", 0)] [InlineData(2, "backward", 0)]
    [InlineData(3, "throw", 1)] [InlineData(3, "deadline", 1)] [InlineData(4, "deadline", 1)]
    [InlineData(5, "deadline", 1)] [InlineData(6, "deadline", 1)]
    public async Task BrokenOrExpiredClockNeverReturnsPartialResult(int call, string mode, int racCalls)
    {
        var f = new Fixture();
        await Error(() => f.Observer(clock: new ScriptedClock(call, mode)).ObserveExpectedScopeAsync(f.Expected, Credentials));
        Assert.Equal(racCalls, f.Runner.Calls);
        Assert.Equal(call >= 5 ? 1 : 0, f.SqlCalls);
        if (f.Connection is not null) Assert.True(f.Connection.WasDisposed);
    }
    [Fact]
    public async Task ProviderSecretsAreSanitizedAfterRacAndConnectionDisposed()
    {
        var f = new Fixture { SqlFailure = true };
        await Error(() => f.Observer().ObserveExpectedScopeAsync(f.Expected, Credentials));
        Assert.Equal(1, f.Runner.Calls); Assert.Equal(1, f.SqlCalls); Assert.True(f.Connection!.WasDisposed);
    }
    private sealed class ScriptedClock(int failCall, string mode) : TimeProvider
    {
        private int calls;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp()
        {
            if (++calls != failCall) return 0;
            if (mode == "throw") throw new InvalidOperationException(Secret);
            return mode == "backward" ? -1 : 120;
        }
    }
    private sealed class FakeRunner : IProcessRunner
    {
        public int Calls { get; private set; }
        public string Output { get; set; } = "";
        public bool Failure { get; set; }
        public Action? OnRun { get; set; }
        public async Task<ProcessResult> RunAsync(ProcessCommand command, Func<ProcessOutput, Task>? onOutput = null,
            CancellationToken cancellationToken = default)
        {
            Calls++; Assert.True(cancellationToken.CanBeCanceled); OnRun?.Invoke();
            if (Failure) throw new InvalidOperationException(Secret);
            await onOutput!(new(ProcessOutputStream.StandardOutput, Output));
            return new(123, 0, ProcessCompletion.Exited, false);
        }
    }
    private static DataTable Table(params object[][] rows)
    {
        var table = new DataTable();
        for (var i = 0; i < (rows.Length == 0 ? 4 : rows[0].Length); i++) table.Columns.Add("c" + i, typeof(object));
        foreach (var row in rows) table.Rows.Add(row);
        return table;
    }
    private sealed class FakeConnection(DataTable identity, DataTable presence) : DbConnection
    {
        public Action? OnPresence { get; init; }
        public Action? OnDispose { get; init; }
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
            var command = new FakeCommand(this, Commands.Count == 0); Commands.Add(command); return command;
        }
        public DbDataReader Execute(bool first)
        {
            if (Failure) throw new InvalidOperationException(Secret);
            if (!first) OnPresence?.Invoke();
            return (first ? identity : presence).CreateDataReader();
        }
        public override ValueTask DisposeAsync() { WasDisposed = true; OnDispose?.Invoke(); return ValueTask.CompletedTask; }
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
