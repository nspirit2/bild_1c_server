using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using BuildSystem.Agent;
using BuildSystem.Agent.Execution;
using BuildSystem.Agent.Tools;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class TargetRegistrationEvidenceCoordinatorTests : IDisposable
{
    private const string Secret = "private-coordinator-canary";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "coordinated-evidence-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Issued = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly InfobaseRegistrationObserverCredentials Credentials = new("private-user", Secret);
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    private static TargetCreationAcknowledgement Ack()
    {
        var reservation = Guid.NewGuid(); var name = $"bs_test_{reservation:N}";
        var intent = new TargetCreationIntent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 7, reservation,
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), name, name);
        var binding = new TargetCreationPermitBinding(Guid.NewGuid(), intent.OperationId, Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Issued, Issued.AddMinutes(5))
        { CredentialId = Guid.NewGuid(), EndpointMappingId = Guid.NewGuid() };
        return new(intent, binding, Guid.NewGuid(), Guid.NewGuid(), Issued.AddSeconds(1), binding.StartBefore);
    }
    private static void Seed(AgentJournal journal, TargetCreationAcknowledgement ack, bool saveAck = true)
    {
        journal.SaveTargetCreationIntent(ack.Intent);
        journal.SaveTargetCreationPermitBinding(ack.Intent, ack.Binding);
        journal.TryBeginTargetCreation(ack.Intent.OperationId, 1, ack.Binding.AttemptId);
        if (saveAck) journal.QuarantineTargetCreationWithAcknowledgement(ack.Intent.OperationId, 2, ack);
        else journal.QuarantineTargetCreation(ack.Intent.OperationId, 2, "OutcomeUnknown");
    }

    [Fact]
    public async Task SavedAcknowledgementProducesHistoricalEvidenceUsingRealPairedObservers()
    {
        var ack = Ack(); using var journal = new AgentJournal(directory); Seed(journal, ack);
        var fixture = new Fixture(ack);
        var before = journal.GetTargetCreationIntent(ack.Intent.OperationId);
        var result = await fixture.Coordinator(journal).ObserveAndRecordAsync(ack.Intent.OperationId, Credentials);
        Assert.Equal(new[] { "RAC", "SQL" }, fixture.Order);
        Assert.Equal(1, fixture.Runner.Calls); Assert.Equal(1, fixture.SqlCalls);
        Assert.True(fixture.Connection!.WasDisposed);
        Assert.Equal(ack, result.Acknowledgement);
        Assert.Equal(before, journal.GetTargetCreationIntent(ack.Intent.OperationId));
        Assert.Equal(ack, journal.GetTargetCreationAcknowledgement(ack.Intent.OperationId));
        Assert.Equal(result.Scope, journal.GetTargetRegistrationEvidence(ack.Intent.OperationId)!.Scope);
        Assert.Equal(ack.Intent.OperationId, result.Scope.RegistrationScope.OperationId);
        Assert.Equal(ack.ReportedInfobaseId, result.Scope.RegistrationScope.InfobaseId);
        Assert.Equal(InfobaseRegistrationReadback.ObservedProfileId, result.Scope.ExpectedProfileId);
        Assert.NotEqual(Guid.Empty, result.Scope.RegistrationScope.ObservationId);
        Assert.NotEqual(Guid.Empty, result.Scope.SqlObservationId);
        Assert.NotEqual(result.Scope.RegistrationScope.ObservationId, result.Scope.SqlObservationId);
        Assert.False(result.Executable); Assert.False(result.OwnershipEstablished); Assert.False(result.RestoreAllowed);
        Assert.False(result.RetryAllowed); Assert.False(result.FreshAuthority);
        Assert.True(result.RequiresIndependentVerification); Assert.True(result.ScheduledJobsMustRemainBlocked);
        Assert.DoesNotContain(Secret, System.Text.Json.JsonSerializer.Serialize(result));
    }

    private static async Task Error(Func<Task<TargetRegistrationEvidence>> action, string suffix = "Unavailable", bool uncertain = false)
    {
        var e = await Assert.ThrowsAsync<TargetRegistrationEvidenceCoordinatorException>(action);
        var code = suffix == "PersistenceUnconfirmed" ? "TargetRegistrationEvidencePersistenceUnconfirmed" : "TargetRegistrationEvidenceCoordinator" + suffix;
        Assert.Equal(code, e.Code); Assert.Equal(code, e.Message); Assert.Equal(code, e.ToString());
        Assert.Equal(uncertain, e.PersistenceMayHaveCompleted); Assert.Null(e.InnerException);
        Assert.DoesNotContain(Secret, e.ToString()); Assert.DoesNotContain(Credentials.ClusterUser, e.ToString());
    }
    [Fact]
    public async Task DefaultOffNeverTouchesDisposedJournalOrValidatesRequest()
    {
        var ack = Ack(); var journal = new AgentJournal(directory); journal.Dispose(); var f = new Fixture(ack);
        Assert.False(new TargetRegistrationEvidenceCoordinatorOptions().Enabled);
        var coordinator = new TargetRegistrationEvidenceCoordinator(journal, f.Observer(), new() { TotalTimeoutSeconds = -1 });
        await Error(() => coordinator.ObserveAndRecordAsync(Guid.Empty, null!), "Disabled"); Assert.Empty(f.Order);
    }
    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(121)]
    public void InvalidEnabledTimeoutHasFixedDiagnostic(int timeout)
    {
        using var journal = new AgentJournal(directory); var f = new Fixture(Ack(), timeout: timeout);
        var e = Assert.Throws<TargetRegistrationEvidenceCoordinatorException>(() => f.Coordinator(journal));
        Assert.Equal("TargetRegistrationEvidenceCoordinatorInvalidOptions", e.Code); Assert.False(e.PersistenceMayHaveCompleted);
        Assert.Empty(f.Order);
    }
    [Theory]
    [InlineData("agent")] [InlineData("cluster")] [InlineData("sql")] [InlineData("pair")]
    public async Task WrongSavedPinsRejectBeforeExternalIo(string pin)
    {
        var ack = Ack(); using var journal = new AgentJournal(directory); Seed(journal, ack); var f = new Fixture(ack, pin);
        await Error(() => f.Coordinator(journal).ObserveAndRecordAsync(ack.Intent.OperationId, Credentials));
        Assert.Empty(f.Order); Assert.Null(journal.GetTargetRegistrationEvidence(ack.Intent.OperationId));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MissingOrPlainQuarantineAckDoesNotStartObservation(bool plain)
    {
        var ack = Ack(); using var journal = new AgentJournal(directory); if (plain) Seed(journal, ack, false);
        var f = new Fixture(ack); await Error(() => f.Coordinator(journal).ObserveAndRecordAsync(ack.Intent.OperationId, Credentials));
        Assert.Empty(f.Order); Assert.Null(journal.GetTargetRegistrationEvidence(ack.Intent.OperationId));
    }
    [Fact]
    public async Task RestartRejectsHistoricalEvidenceBeforeRacOrSql()
    {
        var ack = Ack();
        using (var journal = new AgentJournal(directory))
        { Seed(journal, ack); await new Fixture(ack).Coordinator(journal).ObserveAndRecordAsync(ack.Intent.OperationId, Credentials); }
        using var reopened = new AgentJournal(directory); var f = new Fixture(ack);
        await Error(() => f.Coordinator(reopened).ObserveAndRecordAsync(ack.Intent.OperationId, Credentials), "AlreadyRecorded");
        Assert.Empty(f.Order); Assert.NotNull(reopened.GetTargetRegistrationEvidence(ack.Intent.OperationId));
    }
    [Theory]
    [InlineData("rac")] [InlineData("malformed")] [InlineData("sql")] [InlineData("absent")] [InlineData("offline")]
    public async Task FailedObservationNeverAppends(string failure)
    {
        var ack = Ack(); using var journal = new AgentJournal(directory); Seed(journal, ack); var f = new Fixture(ack);
        f.Runner.Failure = failure == "rac"; if (failure == "malformed") f.Runner.Output = Secret;
        f.SqlFailure = failure == "sql"; if (failure == "absent") f.SqlState = null; if (failure == "offline") f.SqlState = "OFFLINE";
        await Error(() => f.Coordinator(journal).ObserveAndRecordAsync(ack.Intent.OperationId, Credentials));
        Assert.Null(journal.GetTargetRegistrationEvidence(ack.Intent.OperationId));
        Assert.Equal(failure is "rac" or "malformed" ? 0 : 1, f.SqlCalls);
        if (f.Connection is not null) Assert.True(f.Connection.WasDisposed);
    }
    [Theory]
    [InlineData("before")] [InlineData("rac")] [InlineData("sql")] [InlineData("dispose")]
    public async Task CancellationAtEveryBoundaryDoesNotAppend(string stage)
    {
        var ack = Ack(); using var journal = new AgentJournal(directory); Seed(journal, ack); var f = new Fixture(ack);
        using var cts = new CancellationTokenSource(); if (stage == "before") cts.Cancel();
        if (stage == "rac") f.Runner.OnRun = cts.Cancel; if (stage == "sql") f.OnSql = cts.Cancel;
        if (stage == "dispose") f.OnDispose = cts.Cancel;
        await Error(() => f.Coordinator(journal).ObserveAndRecordAsync(ack.Intent.OperationId, Credentials, cts.Token));
        Assert.Equal(stage == "before" ? 0 : 1, f.Runner.Calls); Assert.Null(journal.GetTargetRegistrationEvidence(ack.Intent.OperationId));
        if (f.Connection is not null) Assert.True(f.Connection.WasDisposed);
    }
    [Fact]
    public async Task SameInstanceConcurrentCallIsBusyWithoutExtraExternalReads()
    {
        var ack = Ack(); using var journal = new AgentJournal(directory); Seed(journal, ack); var f = new Fixture(ack);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Runner.Pause = async () => { entered.SetResult(); await release.Task; };
        var coordinator = f.Coordinator(journal); var first = coordinator.ObserveAndRecordAsync(ack.Intent.OperationId, Credentials);
        await entered.Task;
        try { await Error(() => coordinator.ObserveAndRecordAsync(ack.Intent.OperationId, Credentials), "Busy"); Assert.Equal(1, f.Runner.Calls); Assert.Equal(0, f.SqlCalls); }
        finally { release.SetResult(); }
        await first; Assert.Equal(1, f.SqlCalls);
    }
    private object? Raw(string sql, bool scalar = false)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "agent-journal.sqlite")};Pooling=False");
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql;
        return scalar ? command.ExecuteScalar() : command.ExecuteNonQuery();
    }
    [Theory]
    [InlineData("ABORT,'private-coordinator-canary'")] [InlineData("IGNORE")]
    public async Task FailedStorageEntryIsUnconfirmedEvenWithNoSavedRow(string failure)
    {
        var ack = Ack(); using var journal = new AgentJournal(directory); Seed(journal, ack); var f = new Fixture(ack);
        var before = journal.GetTargetCreationIntent(ack.Intent.OperationId);
        Raw($"CREATE TRIGGER coordinator_failure BEFORE INSERT ON target_registration_evidence BEGIN SELECT RAISE({failure}); END;");
        await Error(() => f.Coordinator(journal).ObserveAndRecordAsync(ack.Intent.OperationId, Credentials), "PersistenceUnconfirmed", true);
        Assert.Null(journal.GetTargetRegistrationEvidence(ack.Intent.OperationId)); Assert.Equal(before, journal.GetTargetCreationIntent(ack.Intent.OperationId));
        Assert.Equal(ack, journal.GetTargetCreationAcknowledgement(ack.Intent.OperationId));
    }

    private sealed class Clock : TimeProvider
    {
        public int ReadCount { get; private set; }
        public Func<int, long> Read { get; set; } = _ => 0;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Read(++ReadCount);
    }
    [Theory]
    [InlineData("start")] [InlineData("backward")] [InlineData("after-observer")] [InlineData("budget")]
    public async Task BrokenOrExpiredOuterClockBeforePersistenceNeverAppends(string stage)
    {
        var ack = Ack(); using var journal = new AgentJournal(directory); Seed(journal, ack); var f = new Fixture(ack);
        var clock = new Clock();
        clock.Read = n => stage switch { "start" => throw new InvalidOperationException(Secret), "backward" => n == 1 ? 0 : -1,
            "budget" => n == 1 ? 0 : 120, _ => f.Connection?.WasDisposed == true ? 120 : 0 };
        await Error(() => f.Coordinator(journal, clock).ObserveAndRecordAsync(ack.Intent.OperationId, Credentials));
        Assert.Null(journal.GetTargetRegistrationEvidence(ack.Intent.OperationId));
        Assert.Equal(stage == "after-observer" ? 1 : 0, f.Runner.Calls);
        if (f.Connection is not null) Assert.True(f.Connection.WasDisposed);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DeadlineOrCancellationAfterCommitIsUnconfirmedAndHistoricalRowSurvives(bool cancel)
    {
        var ack = Ack(); using var journal = new AgentJournal(directory); Seed(journal, ack); var f = new Fixture(ack);
        using var cts = new CancellationTokenSource(); var clock = new Clock();
        clock.Read = _ =>
        {
            if ((long)Raw("SELECT COUNT(*) FROM target_registration_evidence", true)! == 0) return 0;
            if (cancel) { cts.Cancel(); return 0; }
            return 120;
        };
        await Error(() => f.Coordinator(journal, clock).ObserveAndRecordAsync(ack.Intent.OperationId, Credentials, cts.Token), "PersistenceUnconfirmed", true);
        Assert.NotNull(journal.GetTargetRegistrationEvidence(ack.Intent.OperationId));
        Assert.Equal(ack, journal.GetTargetCreationAcknowledgement(ack.Intent.OperationId)); Assert.Equal(1, f.Runner.Calls);
    }

    private sealed class Fixture
    {
        public TargetCreationAcknowledgement Ack { get; }
        public FakeRunner Runner { get; } = new();
        public List<string> Order { get; } = [];
        public int SqlCalls { get; private set; }
        public FakeConnection? Connection { get; private set; }
        public string? SqlState { get; set; } = "ONLINE";
        public bool SqlFailure { get; set; }
        public Action? OnSql { get; set; }
        public Action? OnDispose { get; set; }
        public TargetRegistrationEvidenceCoordinatorOptions Options { get; }
        public Fixture(TargetCreationAcknowledgement ack, string? wrongPin = null, int timeout = 10)
        {
            Ack = ack; var i = ack.Intent;
            Options = new() { Enabled = true, TotalTimeoutSeconds = timeout, ExpectedAgentId = wrongPin == "agent" ? Guid.NewGuid() : ack.Binding.AgentId,
                ExpectedCanonicalClusterId = wrongPin == "cluster" ? Guid.NewGuid() : i.CanonicalClusterId,
                ExpectedCanonicalSqlServerId = wrongPin == "sql" ? Guid.NewGuid() : i.CanonicalSqlServerId,
                ExpectedEndpointMappingId = wrongPin == "pair" ? Guid.NewGuid() : ack.Binding.EndpointMappingId!.Value, RacObservationMappingId = Guid.NewGuid(),
                SqlObservationMappingId = Guid.NewGuid(), ExpectedSqlServer = "sql01\\INSTANCE" };
            Runner.Output = $"infobase : {ack.ReportedInfobaseId:D}\nname : {i.InfobaseName}\ndbms : MSSQLServer\n" +
                $"db-server : {Options.ExpectedSqlServer}\ndb-name : {i.SqlDatabaseName}\nscheduled-jobs-deny : on\n" +
                "db-user : \nsecurity-level : 0\nlicense-distribution : deny\nsessions-deny : on\n" +
                "denied-from : \ndenied-message : \ndenied-parameter : \ndenied-to : \npermission-code : private-coordinator-canary\n" +
                "external-session-manager-connection-string : private-coordinator-canary\nexternal-session-manager-required : no\n" +
                "security-profile-name : \nsafe-mode-security-profile-name : \nreserve-working-processes : no\ndescr : private\n\n";
            Runner.OnRun = () => Order.Add("RAC");
        }
        public TargetRegistrationObserver Observer()
        {
            var i = Ack.Intent;
            var rac = new InfobaseRegistrationObserver(Runner, new() { Enabled = true, TrustedRacToolId = "operator-rac",
                TrustedRacVersion = "8.3.22.2557", Endpoint = "rac01:1545", WorkingDirectory = Path.GetTempPath(),
                Timeout = TimeSpan.FromSeconds(30), CanonicalClusterId = i.CanonicalClusterId,
                CanonicalSqlServerId = i.CanonicalSqlServerId, RacEndpointMappingId = Options.RacObservationMappingId,
                SqlEndpointMappingId = Options.SqlObservationMappingId, PhysicalClusterId = Guid.NewGuid(),
                ExpectedSqlServer = Options.ExpectedSqlServer, OutputProfileId = InfobaseRegistrationReadback.ObservedProfileId });
            var sql = new SqlDatabaseObserver(() =>
            {
                SqlCalls++; Order.Add("SQL");
                return Connection = new(Table(["operator-host", "MSSQLSERVER", 1]), SqlState is null ? Table() :
                    Table([i.SqlDatabaseName, 7, new DateTime(2025, 1, 2), SqlState]))
                    { Failure = SqlFailure, OnPresence = OnSql, OnDispose = OnDispose };
            }, new() { EndpointMappingId = Options.SqlObservationMappingId, CanonicalSqlServerId = i.CanonicalSqlServerId,
                ExpectedMachineName = "operator-host", TimeoutSeconds = 10 });
            return new(rac, sql, new() { Enabled = true, TotalTimeoutSeconds = 30 });
        }
        public TargetRegistrationEvidenceCoordinator Coordinator(AgentJournal journal, TimeProvider? clock = null) =>
            new(journal, Observer(), Options, clock);
    }
    private sealed class FakeRunner : IProcessRunner
    {
        public int Calls { get; private set; }
        public string Output { get; set; } = "";
        public bool Failure { get; set; }
        public Action? OnRun { get; set; }
        public Func<Task>? Pause { get; set; }
        public async Task<ProcessResult> RunAsync(ProcessCommand command, Func<ProcessOutput, Task>? onOutput = null,
            CancellationToken cancellationToken = default)
        {
            Calls++; Assert.True(cancellationToken.CanBeCanceled); OnRun?.Invoke();
            if (Pause is not null) await Pause();
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
