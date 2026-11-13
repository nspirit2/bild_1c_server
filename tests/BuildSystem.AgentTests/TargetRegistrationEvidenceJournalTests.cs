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

public sealed class TargetRegistrationEvidenceJournalTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "paired-evidence-" + Guid.NewGuid().ToString("N"));
    private const string Secret = "private-evidence-canary";
    private static readonly DateTimeOffset Issued = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
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
    private static TargetRegistrationAssessmentScope Scope(TargetCreationAcknowledgement ack)
    {
        var i = ack.Intent;
        return new(new(i.OperationId, i.FencingToken, i.TargetIncarnationId, i.ReservationId,
            i.CanonicalClusterId, i.CanonicalSqlServerId, Guid.NewGuid(), Guid.NewGuid(),
            ack.Binding.AttemptId, ack.ClaimId, ack.ReportedInfobaseId, Guid.NewGuid(),
            i.InfobaseName, "sql-host\\instance", i.SqlDatabaseName), Guid.NewGuid(), InfobaseRegistrationReadback.ObservedProfileId);
    }
    private static TargetRegistrationAssessmentResult Assessment(TargetRegistrationAssessmentScope expected, DateTime? created = null)
    {
        var s = expected.RegistrationScope;
        var output = $"infobase: {s.InfobaseId:D}\nname: {s.ExpectedInfobaseName}\ndbms: MSSQLServer\n" +
            $"db-server: {s.ExpectedSqlServer}\ndb-name: {s.ExpectedDatabaseName}\nscheduled-jobs-deny: on\n" +
            "db-user: \nsecurity-level: 0\nlicense-distribution: deny\nsessions-deny: on\n" +
            "denied-from: \ndenied-message: \ndenied-parameter: \ndenied-to: \npermission-code: private-evidence-canary\n" +
            "external-session-manager-connection-string: private-evidence-canary\nexternal-session-manager-required: no\n" +
            "security-profile-name: \nsafe-mode-security-profile-name: \nreserve-working-processes: no\ndescr: \n\n";
        var rac = InfobaseRegistrationReadback.Parse(s, 0, false, output, expected.ExpectedProfileId);
        var db = new SqlDatabaseObservedIdentity(s.ExpectedDatabaseName, 12, created ?? new DateTime(2025, 1, 2), "ONLINE");
        var observer = new SqlDatabaseObserver(() => new FakeConnection(db), new()
        { CanonicalSqlServerId = s.CanonicalSqlServerId, EndpointMappingId = s.SqlEndpointMappingId,
            ExpectedMachineName = "test-host", TimeoutSeconds = 10 });
        var sql = observer.ObserveAsync(new(s.OperationId, s.FencingToken, s.TargetIncarnationId,
            s.CanonicalSqlServerId, s.SqlEndpointMappingId, s.ReservationId, expected.SqlObservationId,
            s.ExpectedDatabaseName)).GetAwaiter().GetResult();
        return TargetRegistrationAssessment.Assess(expected, rac, sql);
    }
    private static void Reject(Action action)
    {
        var error = Assert.Throws<TargetRegistrationEvidenceUnavailableException>(action);
        Assert.Equal("TargetRegistrationEvidenceUnavailable", error.Code);
        Assert.Equal(error.Code, error.Message); Assert.Equal(error.Code, error.ToString()); Assert.Null(error.InnerException);
        Assert.DoesNotContain(Secret, error.ToString());
    }
    private SqliteConnection Raw()
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "agent-journal.sqlite")};Pooling=False");
        connection.Open();
        using var policy = connection.CreateCommand(); policy.CommandText = "PRAGMA foreign_keys=OFF"; policy.ExecuteNonQuery();
        return connection;
    }
    private void Sql(string sql)
    {
        using var connection = Raw(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)] [InlineData(DateTimeKind.Utc)] [InlineData(DateTimeKind.Local)]
    public void AppendReopenPreservesMetadataAndNeverChangesQuarantine(DateTimeKind kind)
    {
        var ack = Ack(); var scope = Scope(ack); var created = new DateTime(2025, 1, 2, 3, 4, 5, kind);
        var assessment = Assessment(scope, created);
        using (var journal = new AgentJournal(directory))
        {
            Seed(journal, ack); var before = journal.GetTargetCreationIntent(ack.Intent.OperationId);
            var evidence = journal.RecordTargetRegistrationEvidence(ack.Intent.OperationId, assessment);
            Assert.Equal(ack, evidence.Acknowledgement); Assert.Equal(scope, evidence.Scope);
            Assert.Equal(assessment.Database, evidence.Database);
            Assert.Equal(before, journal.GetTargetCreationIntent(ack.Intent.OperationId));
            Assert.Equal(ack, journal.GetTargetCreationAcknowledgement(ack.Intent.OperationId));
            Reject(() => journal.RecordTargetRegistrationEvidence(ack.Intent.OperationId, assessment));
            Reject(() => journal.RecordTargetRegistrationEvidence(ack.Intent.OperationId, Assessment(Scope(ack))));
        }
        using var reopened = new AgentJournal(directory);
        var saved = Assert.IsType<TargetRegistrationEvidence>(reopened.GetTargetRegistrationEvidence(ack.Intent.OperationId));
        Assert.Equal(ack, saved.Acknowledgement); Assert.Equal(scope, saved.Scope);
        Assert.Equal(created, saved.Database.CreateDate); Assert.Equal(kind, saved.Database.CreateDate.Kind);
        Assert.False(saved.Executable); Assert.False(saved.OwnershipEstablished); Assert.False(saved.RestoreAllowed);
        Assert.False(saved.RetryAllowed); Assert.False(saved.FreshAuthority);
        Assert.True(saved.RequiresIndependentVerification); Assert.True(saved.ScheduledJobsMustRemainBlocked);
        Assert.DoesNotContain(Secret, System.Text.Json.JsonSerializer.Serialize(saved));
        Assert.DoesNotContain(scope.RegistrationScope.ExpectedSqlServer, saved.ToString());
        Assert.False(reopened.TryBeginTargetCreation(ack.Intent.OperationId, 1, ack.Binding.AttemptId).NewlyRecorded);
    }

    [Fact]
    public void MissingOrPlainQuarantineAckNeverPermitsEvidence()
    {
        var ack = Ack(); using var journal = new AgentJournal(directory);
        Assert.Null(journal.GetTargetRegistrationEvidence(ack.Intent.OperationId));
        Reject(() => journal.RecordTargetRegistrationEvidence(ack.Intent.OperationId, Assessment(Scope(ack))));
        Seed(journal, ack, false);
        Reject(() => journal.RecordTargetRegistrationEvidence(ack.Intent.OperationId, Assessment(Scope(ack))));
        Assert.Null(journal.GetTargetRegistrationEvidence(ack.Intent.OperationId));
    }

    [Fact]
    public void EveryAckPinnedIdentityMustMatchWithoutMutatingState()
    {
        var ack = Ack(); using var journal = new AgentJournal(directory); Seed(journal, ack);
        var expected = Scope(ack); var s = expected.RegistrationScope;
        var reservation = Guid.NewGuid(); var name = $"bs_test_{reservation:N}";
        InfobaseRegistrationReadbackScope[] mismatches =
        [
            s with { OperationId = Guid.NewGuid() }, s with { FencingToken = 8 },
            s with { TargetIncarnationId = Guid.NewGuid() },
            s with { ReservationId = reservation, ExpectedInfobaseName = name, ExpectedDatabaseName = name },
            s with { CanonicalClusterId = Guid.NewGuid() }, s with { CanonicalSqlServerId = Guid.NewGuid() },
            s with { AttemptId = Guid.NewGuid() }, s with { ClaimId = Guid.NewGuid() }, s with { InfobaseId = Guid.NewGuid() }
        ];
        var before = journal.GetTargetCreationIntent(ack.Intent.OperationId);
        foreach (var mismatch in mismatches)
            Reject(() => journal.RecordTargetRegistrationEvidence(ack.Intent.OperationId,
                Assessment(expected with { RegistrationScope = mismatch })));
        Reject(() => journal.RecordTargetRegistrationEvidence(Guid.Empty, Assessment(expected)));
        Reject(() => journal.RecordTargetRegistrationEvidence(ack.Intent.OperationId, null!));
        Assert.Equal(before, journal.GetTargetCreationIntent(ack.Intent.OperationId));
        Assert.Null(journal.GetTargetRegistrationEvidence(ack.Intent.OperationId));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void GlobalSameAndCrossRoleObservationReuseRejectsReplacement(int mode)
    {
        var first = Ack(); var second = Ack(); var firstScope = Scope(first); var secondScope = Scope(second);
        using (var journal = new AgentJournal(directory))
        {
            Seed(journal, first); journal.RecordTargetRegistrationEvidence(first.Intent.OperationId, Assessment(firstScope));
        }
        // A different-operation raw candidate isolates observation collisions from the operation PK.
        // Foreign keys are off on this test-only connection, so only the cross-role trigger blocks it.
        using var reopened = new AgentJournal(directory);
        var old = reopened.GetTargetRegistrationEvidence(first.Intent.OperationId)!;
        var source = mode < 2 ? "rac_observation" : "sql_observation";
        var destination = mode % 2 == 0 ? "rac_observation" : "sql_observation";
        using var connection = Raw(); using var command = connection.CreateCommand();
        command.CommandText = $"INSERT OR REPLACE INTO target_registration_evidence SELECT $operation,attempt,claim,reported_infobase,fencing_token,incarnation,reservation,canonical_cluster,canonical_sql,rac_mapping,sql_mapping," +
            (destination == "rac_observation" ? source : "$fresh") + "," +
            (destination == "sql_observation" ? source : "$fresh") +
            ",profile,infobase_name,sql_server,database_name,database_id,create_date,state FROM target_registration_evidence";
        command.Parameters.AddWithValue("$operation", second.Intent.OperationId.ToString());
        command.Parameters.AddWithValue("$fresh", Guid.NewGuid().ToString());
        var error = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        Assert.Equal(1811, error.SqliteExtendedErrorCode);
        Assert.Contains("Target registration evidence is immutable.", error.Message);
        Assert.Equal(old.Scope, reopened.GetTargetRegistrationEvidence(first.Intent.OperationId)!.Scope);
        using var count = connection.CreateCommand(); count.CommandText = "SELECT COUNT(*) FROM target_registration_evidence";
        Assert.Equal(1L, count.ExecuteScalar());
        Assert.NotEqual(first.ClaimId, second.ClaimId); Assert.NotEqual(firstScope, secondScope);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ApiRejectsObservationReuseAcrossTwoSavedAcknowledgements(int mode)
    {
        var first = Ack(); var second = Ack(); var firstScope = Scope(first); var secondScope = Scope(second);
        var reused = mode < 2 ? firstScope.RegistrationScope.ObservationId : firstScope.SqlObservationId;
        secondScope = mode % 2 == 0
            ? secondScope with { RegistrationScope = secondScope.RegistrationScope with { ObservationId = reused } }
            : secondScope with { SqlObservationId = reused };
        using var journal = new AgentJournal(directory); Seed(journal, first); Seed(journal, second);
        journal.RecordTargetRegistrationEvidence(first.Intent.OperationId, Assessment(firstScope));
        var before = journal.GetTargetCreationIntent(second.Intent.OperationId);
        Reject(() => journal.RecordTargetRegistrationEvidence(second.Intent.OperationId, Assessment(secondScope)));
        Assert.Equal(firstScope, journal.GetTargetRegistrationEvidence(first.Intent.OperationId)!.Scope);
        Assert.Null(journal.GetTargetRegistrationEvidence(second.Intent.OperationId));
        Assert.Equal(before, journal.GetTargetCreationIntent(second.Intent.OperationId));
        Assert.Equal(second, journal.GetTargetCreationAcknowledgement(second.Intent.OperationId));
    }

    [Theory]
    [InlineData("ack")] [InlineData("revision")] [InlineData("reason")]
    public void NestedAckOrQuarantineCorruptionIsSanitizedOnReadAndAppend(string target)
    {
        var ack = Ack(); using var journal = new AgentJournal(directory); Seed(journal, ack);
        journal.RecordTargetRegistrationEvidence(ack.Intent.OperationId, Assessment(Scope(ack)));
        if (target == "ack")
            Sql("DROP TRIGGER target_creation_acknowledgements_no_update; UPDATE target_creation_acknowledgements SET claim='private-evidence-canary'");
        else
            Sql(target == "revision" ? "UPDATE target_creation_intents SET revision=4" : "UPDATE target_creation_intents SET reason='TargetConflict'");
        Reject(() => journal.GetTargetRegistrationEvidence(ack.Intent.OperationId));
        Reject(() => journal.RecordTargetRegistrationEvidence(ack.Intent.OperationId, Assessment(Scope(ack))));
    }

    [Theory]
    [InlineData("UPDATE target_registration_evidence SET sql_server='private-evidence-canary'")]
    [InlineData("DELETE FROM target_registration_evidence")]
    [InlineData("INSERT OR REPLACE INTO target_registration_evidence SELECT * FROM target_registration_evidence")]
    public void OrdinarySqlCannotRewriteHistoricalEvidence(string sql)
    {
        var ack = Ack(); using var journal = new AgentJournal(directory); Seed(journal, ack);
        var saved = journal.RecordTargetRegistrationEvidence(ack.Intent.OperationId, Assessment(Scope(ack)));
        Assert.Throws<SqliteException>(() => Sql(sql));
        Assert.Equal(saved.Scope, journal.GetTargetRegistrationEvidence(ack.Intent.OperationId)!.Scope);
    }

    [Theory]
    [InlineData("ABORT,'private-evidence-canary'")] [InlineData("IGNORE")]
    public void FailedOrIgnoredInsertReturnsNoEvidenceAndLeavesQuarantineUnchanged(string failure)
    {
        var ack = Ack(); using var journal = new AgentJournal(directory); Seed(journal, ack);
        var before = journal.GetTargetCreationIntent(ack.Intent.OperationId);
        Sql($"CREATE TRIGGER test_evidence_failure BEFORE INSERT ON target_registration_evidence BEGIN SELECT RAISE({failure}); END;");
        Reject(() => journal.RecordTargetRegistrationEvidence(ack.Intent.OperationId, Assessment(Scope(ack))));
        Assert.Null(journal.GetTargetRegistrationEvidence(ack.Intent.OperationId));
        Assert.Equal(before, journal.GetTargetCreationIntent(ack.Intent.OperationId));
        Assert.Equal(ack, journal.GetTargetCreationAcknowledgement(ack.Intent.OperationId));
    }

    [Theory]
    [InlineData("claim", "'private-evidence-canary'")]
    [InlineData("reported_infobase", "'00000000-0000-0000-0000-000000000000'")]
    [InlineData("profile", "'rac-registration-contract-v1'")]
    [InlineData("fencing_token", "'07'")] [InlineData("database_id", "'12.0'")]
    [InlineData("create_date", "'private-evidence-canary'")]
    [InlineData("state", "'OFFLINE'")] [InlineData("sql_server", "NULL")]
    public void StrictGetterRejectsCorruptPersistedMetadataWithoutEcho(string column, string value)
    {
        var ack = Ack(); using var journal = new AgentJournal(directory); Seed(journal, ack);
        journal.RecordTargetRegistrationEvidence(ack.Intent.OperationId, Assessment(Scope(ack)));
        Sql("DROP TRIGGER target_registration_evidence_no_update;");
        // NULL violates storage constraints before getter; exercise a blob storage-class mismatch instead.
        var replacement = value == "NULL" ? "X'010203'" : value;
        Sql($"UPDATE target_registration_evidence SET {column}={replacement}");
        Reject(() => journal.GetTargetRegistrationEvidence(ack.Intent.OperationId));
    }

    [Fact]
    public void DisposedJournalAndInvalidReadFailWithFixedDiagnostics()
    {
        var ack = Ack(); var journal = new AgentJournal(directory); journal.Dispose();
        Reject(() => journal.GetTargetRegistrationEvidence(ack.Intent.OperationId));
        Reject(() => journal.RecordTargetRegistrationEvidence(ack.Intent.OperationId, Assessment(Scope(ack))));
    }
    private static DataTable Table(params object[][] rows)
    {
        var table = new DataTable();
        for (var i = 0; i < (rows.Length == 0 ? 4 : rows[0].Length); i++) table.Columns.Add("c" + i, typeof(object));
        foreach (var row in rows) table.Rows.Add(row);
        return table;
    }
    private sealed class FakeConnection : DbConnection
    {
        private readonly DataTable identity = Table(["test-host", "MSSQLSERVER", 1]);
        private readonly DataTable presence;
        public FakeConnection(SqlDatabaseObservedIdentity? database) => presence = database is null ? Table() :
            Table([database.Name, database.DatabaseId, database.CreateDate, database.State]);
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
