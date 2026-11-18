using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using BuildSystem.Agent;
using BuildSystem.Agent.Execution;
using BuildSystem.Agent.Tools;
using Xunit;

namespace BuildSystem.TestFixtures;

// Shared creation/delivery seed: real journal, parser, and assessment with canned RAC text and
// SQL readback rows; never invokes a RAC executable or connects to a SQL Server.
internal static class TargetRegistrationEvidenceDeliverySeed
{
    private const string Secret = "private-evidence-canary";
    internal static readonly DateTimeOffset Issued = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    internal static (TargetCreationAcknowledgement Ack, TargetRegistrationAssessmentScope Scope, DateTime Created)
        SeedEvidence(AgentJournal journal, DateTimeKind kind = DateTimeKind.Unspecified)
    {
        var ack = Ack(); Seed(journal, ack); var scope = Scope(ack); var created = new DateTime(2025, 1, 2, 3, 4, 5, kind);
        journal.RecordTargetRegistrationEvidence(ack.Intent.OperationId, Assessment(scope, created)); return (ack, scope, created);
    }
    internal static TargetCreationAcknowledgement Ack()
    {
        var reservation = Guid.NewGuid(); var name = $"bs_test_{reservation:N}";
        var intent = new TargetCreationIntent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 7, reservation,
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), name, name);
        var binding = new TargetCreationPermitBinding(Guid.NewGuid(), intent.OperationId, Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Issued, Issued.AddMinutes(5))
        { CredentialId = Guid.NewGuid(), EndpointMappingId = Guid.NewGuid() };
        return new(intent, binding, Guid.NewGuid(), Guid.NewGuid(), Issued.AddSeconds(1), binding.StartBefore);
    }
    internal static void Seed(AgentJournal journal, TargetCreationAcknowledgement ack)
    {
        journal.SaveTargetCreationIntent(ack.Intent);
        journal.SaveTargetCreationPermitBinding(ack.Intent, ack.Binding);
        journal.TryBeginTargetCreation(ack.Intent.OperationId, 1, ack.Binding.AttemptId);
        journal.QuarantineTargetCreationWithAcknowledgement(ack.Intent.OperationId, 2, ack);
    }
    private static TargetRegistrationAssessmentScope Scope(TargetCreationAcknowledgement ack)
    {
        var i = ack.Intent;
        return new(new(i.OperationId, i.FencingToken, i.TargetIncarnationId, i.ReservationId,
            i.CanonicalClusterId, i.CanonicalSqlServerId, Guid.NewGuid(), Guid.NewGuid(),
            ack.Binding.AttemptId, ack.ClaimId, ack.ReportedInfobaseId, Guid.NewGuid(),
            i.InfobaseName, "sql-host\\instance", i.SqlDatabaseName), Guid.NewGuid(), InfobaseRegistrationReadback.ObservedProfileId);
    }
    private static TargetRegistrationAssessmentResult Assessment(TargetRegistrationAssessmentScope expected, DateTime created)
    {
        var s = expected.RegistrationScope;
        var output = $"infobase: {s.InfobaseId:D}\nname: {s.ExpectedInfobaseName}\ndbms: MSSQLServer\n" +
            $"db-server: {s.ExpectedSqlServer}\ndb-name: {s.ExpectedDatabaseName}\nscheduled-jobs-deny: on\n" +
            "db-user: \nsecurity-level: 0\nlicense-distribution: deny\nsessions-deny: on\n" +
            "denied-from: \ndenied-message: \ndenied-parameter: \ndenied-to: \npermission-code: private-evidence-canary\n" +
            "external-session-manager-connection-string: private-evidence-canary\nexternal-session-manager-required: no\n" +
            "security-profile-name: \nsafe-mode-security-profile-name: \nreserve-working-processes: no\ndescr: \n\n";
        var rac = InfobaseRegistrationReadback.Parse(s, 0, false, output, expected.ExpectedProfileId);
        var db = new SqlDatabaseObservedIdentity(s.ExpectedDatabaseName, 12, created, "ONLINE");
        var observer = new SqlDatabaseObserver(() => new FakeConnection(db), new()
        { CanonicalSqlServerId = s.CanonicalSqlServerId, EndpointMappingId = s.SqlEndpointMappingId,
            ExpectedMachineName = "test-host", TimeoutSeconds = 10 });
        var sql = observer.ObserveAsync(new(s.OperationId, s.FencingToken, s.TargetIncarnationId,
            s.CanonicalSqlServerId, s.SqlEndpointMappingId, s.ReservationId, expected.SqlObservationId,
            s.ExpectedDatabaseName)).GetAwaiter().GetResult();
        return TargetRegistrationAssessment.Assess(expected, rac, sql);
    }
    private static DataTable Table(params object[] row)
    {
        var table = new DataTable();
        for (var i = 0; i < row.Length; i++) table.Columns.Add("c" + i, typeof(object));
        table.Rows.Add(row);
        return table;
    }
    private sealed class FakeConnection : DbConnection
    {
        private readonly DataTable identity = Table(["test-host", "MSSQLSERVER", 1]);
        private readonly DataTable presence;
        private bool identityRead;
        public FakeConnection(SqlDatabaseObservedIdentity database) => presence =
            Table([database.Name, database.DatabaseId, database.CreateDate, database.State]);
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
            var data = identityRead ? presence : identity;
            identityRead = true;
            return new FakeCommand(this, data);
        }
    }
    private sealed class FakeCommand(FakeConnection owner, DataTable data) : DbCommand
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
            => Task.FromResult<DbDataReader>(data.CreateDataReader());
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
