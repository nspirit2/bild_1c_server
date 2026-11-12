using System.Reflection;
using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using BuildSystem.Agent.Execution;
using BuildSystem.Agent.Tools;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class TargetRegistrationAssessmentTests
{
    private static TargetRegistrationAssessmentScope Scope()
    {
        var reservation = Guid.NewGuid();
        var name = $"bs_test_{reservation:N}";
        return new(new(Guid.NewGuid(), 7, Guid.NewGuid(), reservation, Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            name, "sql-host\\instance", name), Guid.NewGuid(), InfobaseRegistrationReadback.ObservedProfileId);
    }

    // Authorized RAC sample's 21-key *shape*, reconstructed with synthetic values: not raw captured bytes.
    private static InfobaseRegistrationReadbackResult Rac(TargetRegistrationAssessmentScope expected)
    {
        var s = expected.RegistrationScope;
        var output = $"infobase: {s.InfobaseId:D}\nname: {s.ExpectedInfobaseName}\ndbms: MSSQLServer\n" +
            $"db-server: {s.ExpectedSqlServer}\ndb-name: {s.ExpectedDatabaseName}\nscheduled-jobs-deny: on\n" +
            "db-user: synthetic\nsecurity-level: 0\nlicense-distribution: allow\nsessions-deny: off\n" +
            "denied-from: synthetic\ndenied-message: synthetic\ndenied-parameter: synthetic\n" +
            "denied-to: synthetic\npermission-code: synthetic\n" +
            "external-session-manager-connection-string: synthetic\nexternal-session-manager-required: no\n" +
            "security-profile-name: synthetic\nsafe-mode-security-profile-name: synthetic\n" +
            "reserve-working-processes: no\ndescr: synthetic\n\n";
        return InfobaseRegistrationReadback.Parse(s, 0, false, output, expected.ExpectedProfileId);
    }

    private static SqlDatabaseObservationScope SqlScope(TargetRegistrationAssessmentScope expected)
    {
        var s = expected.RegistrationScope;
        return new(s.OperationId, s.FencingToken, s.TargetIncarnationId, s.CanonicalSqlServerId,
            s.SqlEndpointMappingId, s.ReservationId, expected.SqlObservationId, s.ExpectedDatabaseName);
    }

    // Exercise the real observer against an in-memory provider, never live SQL or internal constructors.
    private static SqlDatabaseObservationResult Sql(TargetRegistrationAssessmentScope expected,
        SqlDatabaseObservedIdentity? identity = null, bool absent = false) =>
        Sql(SqlScope(expected), absent ? null : identity ?? new(expected.RegistrationScope.ExpectedDatabaseName,
            12, new DateTime(2025, 1, 2), "ONLINE"));
    private static SqlDatabaseObservationResult Sql(SqlDatabaseObservationScope scope, SqlDatabaseObservedIdentity? identity)
    {
        var observer = new SqlDatabaseObserver(() => new FakeConnection(identity), new()
        {
            EndpointMappingId = scope.EndpointMappingId, CanonicalSqlServerId = scope.SqlServerId,
            ExpectedMachineName = "test-host", TimeoutSeconds = 10
        });
        return observer.ObserveAsync(scope).GetAwaiter().GetResult();
    }

    private static void Reject(string code, Action action)
    {
        var error = Assert.Throws<TargetRegistrationAssessmentException>(action);
        Assert.Equal(code, error.Code);
        Assert.Equal(code, error.Message);
        Assert.Equal($"TargetRegistrationAssessmentException: {code}", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void ValidObservedShapeAndOnlineSqlRemainStrictlyInert()
    {
        var expected = Scope();
        var result = TargetRegistrationAssessment.Assess(expected, Rac(expected), Sql(expected));
        Assert.Same(expected, result.Scope);
        Assert.Equal(expected.RegistrationScope.InfobaseId, result.InfobaseId);
        Assert.Equal(InfobaseRegistrationReadback.ObservedProfileId, result.RegistrationProfileId);
        Assert.Equal(expected.RegistrationScope.ExpectedDatabaseName, result.Database.Name);
        Assert.Equal(12, result.Database.DatabaseId);
        Assert.Equal(new DateTime(2025, 1, 2), result.Database.CreateDate);
        Assert.Equal("ONLINE", result.Database.State);
        Assert.False(result.Executable);
        Assert.False(result.OwnershipEstablished);
        Assert.False(result.RestoreAllowed);
        Assert.False(result.RetryAllowed);
        Assert.False(result.FreshAuthority);
        Assert.True(result.RequiresIndependentVerification);
        Assert.True(result.ScheduledJobsMustRemainBlocked);
        Assert.DoesNotContain(expected.RegistrationScope.ExpectedDatabaseName, result.ToString());
        Assert.DoesNotContain(expected.RegistrationScope.ExpectedSqlServer, result.ToString());
        Assert.DoesNotContain(expected.RegistrationScope.ExpectedDatabaseName, result.Database.ToString());
        Assert.DoesNotContain(expected.RegistrationScope.ExpectedDatabaseName, expected.ToString());
        Assert.True(typeof(TargetRegistrationAssessmentResult).IsSealed);
        Assert.Empty(typeof(TargetRegistrationAssessmentResult).GetConstructors());
        Assert.All(typeof(TargetRegistrationAssessmentResult).GetProperties(), p => Assert.Null(p.SetMethod));
    }

    [Fact]
    public void AllSavedAckPinsAndProfileAreValidatedBeforeAvailability()
    {
        var expected = Scope();
        var s = expected.RegistrationScope;
        InfobaseRegistrationReadbackScope[] invalid =
        [
            s with { OperationId = Guid.Empty }, s with { FencingToken = 0 }, s with { FencingToken = -1 },
            s with { TargetIncarnationId = Guid.Empty }, s with { ReservationId = Guid.Empty },
            s with { CanonicalClusterId = Guid.Empty }, s with { CanonicalSqlServerId = Guid.Empty },
            s with { RacEndpointMappingId = Guid.Empty }, s with { SqlEndpointMappingId = Guid.Empty },
            s with { AttemptId = Guid.Empty }, s with { ClaimId = Guid.Empty },
            s with { InfobaseId = Guid.Empty }, s with { ObservationId = Guid.Empty },
            s with { ExpectedInfobaseName = "custom" }, s with { ExpectedDatabaseName = "custom" },
            s with { ExpectedInfobaseName = s.ExpectedInfobaseName.ToUpperInvariant() },
            s with { ExpectedDatabaseName = s.ExpectedDatabaseName.ToUpperInvariant() },
            s with { ExpectedSqlServer = "" }, s with { ExpectedSqlServer = "-host" },
            s with { ExpectedSqlServer = "host name" }, s with { ExpectedSqlServer = new string('x', 256) }
        ];
        foreach (var invalidScope in invalid)
            Reject("TargetRegistrationInvalidScope", () => TargetRegistrationAssessment.ValidateScope(expected with { RegistrationScope = invalidScope }));
        Reject("TargetRegistrationInvalidScope", () => TargetRegistrationAssessment.ValidateScope(null!));
        Reject("TargetRegistrationInvalidScope", () => TargetRegistrationAssessment.ValidateScope(expected with { RegistrationScope = null! }));
        Reject("TargetRegistrationInvalidScope", () => TargetRegistrationAssessment.ValidateScope(expected with { SqlObservationId = Guid.Empty }));
        Reject("TargetRegistrationInvalidScope", () => TargetRegistrationAssessment.ValidateScope(expected with { SqlObservationId = s.ObservationId }));
        foreach (var profile in new[] { "", "unknown", InfobaseRegistrationReadback.ProfileId, null! })
            Reject("TargetRegistrationInvalidScope", () => TargetRegistrationAssessment.ValidateScope(expected with { ExpectedProfileId = profile }));
        Reject("TargetRegistrationInvalidScope", () => TargetRegistrationAssessment.Assess(null!, null!, null!));
    }

    [Fact]
    public void SyntheticContractCannotBePresentedAsObservedProfile()
    {
        var expected = Scope();
        var s = expected.RegistrationScope;
        var synthetic = InfobaseRegistrationReadback.Parse(s, 0, false,
            $"infobase: {s.InfobaseId:D}\nname: {s.ExpectedInfobaseName}\ndbms: MSSQLServer\n" +
            $"db-server: {s.ExpectedSqlServer}\ndb-name: {s.ExpectedDatabaseName}\nscheduled-jobs-deny: on\n");
        Reject("TargetRegistrationScopeMismatch", () => TargetRegistrationAssessment.Assess(expected, synthetic, Sql(expected)));
    }

    [Fact]
    public void EachRacScopePinMustMatchIndividually()
    {
        var expected = Scope();
        var s = expected.RegistrationScope;
        var rac = Rac(expected);
        var sql = Sql(expected);
        InfobaseRegistrationReadbackScope[] mismatches =
        [
            s with { OperationId = Guid.NewGuid() }, s with { FencingToken = 8 },
            s with { TargetIncarnationId = Guid.NewGuid() },
            s with { CanonicalClusterId = Guid.NewGuid() }, s with { CanonicalSqlServerId = Guid.NewGuid() },
            s with { RacEndpointMappingId = Guid.NewGuid() }, s with { SqlEndpointMappingId = Guid.NewGuid() },
            s with { AttemptId = Guid.NewGuid() }, s with { ClaimId = Guid.NewGuid() },
            s with { InfobaseId = Guid.NewGuid() }, s with { ObservationId = Guid.NewGuid() },
            s with { ExpectedSqlServer = "other-host" }
        ];
        foreach (var mismatch in mismatches)
            Reject("TargetRegistrationScopeMismatch", () => TargetRegistrationAssessment.Assess(
                expected with { RegistrationScope = mismatch }, rac, sql));
    }

    [Fact]
    public void EachSqlProjectionPinMustMatchIndividually()
    {
        var expected = Scope();
        var scope = SqlScope(expected);
        SqlDatabaseObservationScope[] mismatches =
        [
            scope with { OperationId = Guid.NewGuid() }, scope with { FencingToken = 8 },
            scope with { TargetIncarnationId = Guid.NewGuid() }, scope with { SqlServerId = Guid.NewGuid() },
            scope with { EndpointMappingId = Guid.NewGuid() },
            scope with { ObservationId = Guid.NewGuid() },
            scope with { ObservationId = expected.RegistrationScope.ObservationId }
        ];
        foreach (var mismatch in mismatches)
            Reject("TargetRegistrationScopeMismatch", () => TargetRegistrationAssessment.Assess(expected,
                Rac(expected), Sql(mismatch, new(scope.ExpectedDatabaseName, 12, new DateTime(2025, 1, 2), "ONLINE"))));
    }

    [Fact]
    public void MissingAbsentAndNonOnlineSqlNeverPass()
    {
        var expected = Scope();
        var rac = Rac(expected);
        Reject("TargetRegistrationUnavailable", () => TargetRegistrationAssessment.Assess(expected, null!, Sql(expected)));
        Reject("TargetRegistrationUnavailable", () => TargetRegistrationAssessment.Assess(expected, rac, null!));
        Reject("TargetRegistrationUnavailable", () => TargetRegistrationAssessment.Assess(expected, rac, Sql(expected, absent: true)));
        foreach (var state in new[] { "OFFLINE", "RESTORING", "RECOVERING", "SUSPECT" })
            Reject("TargetRegistrationSqlNotOnline", () => TargetRegistrationAssessment.Assess(expected, rac,
                Sql(expected, new(expected.RegistrationScope.ExpectedDatabaseName, 12, new DateTime(2025, 1, 2), state))));
    }

    [Fact]
    public void ExactSqlNameAndFingerprintMustMatchEvenIfObserverCollationMatched()
    {
        var expected = Scope();
        var rac = Rac(expected);
        var name = expected.RegistrationScope.ExpectedDatabaseName;
        foreach (var identity in new[]
        {
            new SqlDatabaseObservedIdentity(name.ToUpperInvariant(), 12, new DateTime(2025, 1, 2), "ONLINE"),
            new SqlDatabaseObservedIdentity("other-database", 12, new DateTime(2025, 1, 2), "ONLINE")
        })
            Reject("TargetRegistrationSqlTargetMismatch", () => TargetRegistrationAssessment.Assess(expected, rac, Sql(expected, identity)));
    }

    [Fact]
    public void SameScopeReplayIsNotFreshnessAndDoesNotGrantAuthority()
    {
        var expected = Scope();
        var rac = Rac(expected);
        var sql = Sql(expected);
        var first = TargetRegistrationAssessment.Assess(expected, rac, sql);
        var replay = TargetRegistrationAssessment.Assess(expected, rac, sql);
        Assert.Equal(first.Database, replay.Database);
        Assert.False(replay.FreshAuthority);
        Assert.False(replay.OwnershipEstablished);
        Assert.True(replay.ScheduledJobsMustRemainBlocked);
    }
    [Fact]
    public void DifferentValidReservationAndGeneratedNamesCannotMatchEitherObservation()
    {
        var expected = Scope();
        var reservation = Guid.NewGuid();
        var name = $"bs_test_{reservation:N}";
        var changed = expected.RegistrationScope with
        {
            ReservationId = reservation, ExpectedInfobaseName = name, ExpectedDatabaseName = name
        };
        Reject("TargetRegistrationScopeMismatch", () => TargetRegistrationAssessment.Assess(expected,
            Rac(expected with { RegistrationScope = changed }), Sql(expected)));
        var changedSql = SqlScope(expected) with { ReservationId = reservation, ExpectedDatabaseName = name };
        Reject("TargetRegistrationScopeMismatch", () => TargetRegistrationAssessment.Assess(expected,
            Rac(expected), Sql(changedSql, new(name, 12, new DateTime(2025, 1, 2), "ONLINE"))));
    }

    [Fact]
    public void InvalidFingerprintsFailInTrustedSqlAdapterBeforeAssessment()
    {
        var expected = Scope();
        var name = expected.RegistrationScope.ExpectedDatabaseName;
        foreach (var database in new[]
        {
            new SqlDatabaseObservedIdentity(name + " ", 12, new DateTime(2025, 1, 2), "ONLINE"),
            new SqlDatabaseObservedIdentity(name, 0, new DateTime(2025, 1, 2), "ONLINE"),
            new SqlDatabaseObservedIdentity(name, 12, default, "ONLINE"),
            new SqlDatabaseObservedIdentity(name, 12, new DateTime(2025, 1, 2), "online")
        })
        {
            var error = Assert.Throws<SqlDatabaseObservationException>(() => Sql(expected, database));
            Assert.Equal("SqlDatabaseObservationUnavailable", error.Code);
            Assert.Null(error.InnerException);
        }
    }

    private const string Secret = "private-assessment-provider-canary";
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
