using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BuildSystem.Agent;
using BuildSystem.Agent.Execution;
using BuildSystem.Agent.Tools;
using Xunit;

namespace BuildSystem.AgentTests;

// The delegate is explicitly trusted: a fake can verify settings, not prove a provider honors them.
public sealed class TargetCreationTrustedAdaptersTests
{
    private static readonly Guid Agent = Guid.NewGuid(), Cluster = Guid.NewGuid(), RawCluster = Guid.NewGuid(),
        Server = Guid.NewGuid(), Mapping = Guid.NewGuid(), RacMapping = Guid.NewGuid(), SqlMapping = Guid.NewGuid();
    private const string Source = "fake-sql.example", Canary = "trusted-adapter-secret-canary";
    private static readonly TargetCreationDeliveredSecretValues Secrets = new(Canary, Canary, Canary, Canary);
    private static TargetCreationTrustedAdapterOptions Options(bool enabled = true, string source = Source, Guid? sqlMapping = null) => new()
    {
        Enabled = enabled, ExclusiveNamespaceConfirmed = true, TrustedAgentId = Agent,
        CanonicalClusterId = Cluster, RacClusterId = RawCluster, CanonicalSqlServerId = Server,
        EndpointMappingId = Mapping, RacObservationMappingId = RacMapping, SqlObservationMappingId = sqlMapping ?? SqlMapping,
        TrustedRacToolId = "trusted-rac", RacEndpoint = "fake-ras.example:1545", SqlServer = source,
        WorkingDirectory = Path.GetFullPath(Path.GetTempPath()), ExpectedSqlMachineName = "fake"
    };
    private static TargetPresenceAssessmentScope Expected() => new(Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(),
        Cluster, Server, RacMapping, SqlMapping, Guid.NewGuid(), Guid.NewGuid());
    private static void Fixed(TargetCreationTrustedAdapterException error)
    {
        Assert.Equal("TargetCreationTrustedAdapterUnavailable", error.Code);
        Assert.Equal(error.Code, error.Message); Assert.Equal(error.Code, error.ToString()); Assert.Null(error.InnerException);
        Assert.DoesNotContain(Canary, error.ToString());
    }

    [Fact]
    public void Default_off_is_first_before_invalid_configuration_or_callbacks()
    {
        var runner = new Runner(); var calls = 0;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.False(new TargetCreationTrustedAdapterOptions().Enabled);
        Assert.False(new TargetCreationTrustedAdapterOptions().ExclusiveNamespaceConfirmed);
        Fixed(Assert.Throws<TargetCreationTrustedAdapterException>(() => TargetCreationTrustedAdapters.Create(
            new TargetCreationTrustedAdapterOptions(), runner, _ => { calls++; throw new Exception(Canary); })));
        Fixed(Assert.Throws<TargetCreationTrustedAdapterException>(() => TargetCreationTrustedAdapters.Create(
            new TargetCreationTrustedAdapterOptions(), null!, null!)));
        Assert.True(cancelled.IsCancellationRequested); Assert.Equal(0, calls); Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task Valid_composition_is_inert_and_observes_exact_scope_fresh_each_time()
    {
        var runner = new Runner(); var provider = new Provider(); var bundle = TargetCreationTrustedAdapters.Create(Options(), runner, provider.Create);
        Assert.Empty(provider.Connections); Assert.Empty(runner.Commands);
        var options = bundle.OneShotOptions;
        Assert.True(options.Enabled); Assert.Equal(Agent, options.TrustedAgentId); Assert.Equal(Cluster, options.CanonicalClusterId);
        Assert.Equal(Server, options.CanonicalSqlServerId); Assert.Equal(Mapping, options.EndpointMappingId);
        Assert.Equal(RacMapping, options.RacObservationMappingId); Assert.Equal(SqlMapping, options.SqlObservationMappingId);
        Assert.NotEqual(Cluster, RawCluster);
        var expected = Expected();
        for (var i = 0; i < 2; i++)
        {
            var result = await bundle.AbsencePort.ObserveAsync(expected, Secrets);
            Assert.Equal(expected, result.Scope); Assert.Equal(expected.RacObservationId, result.Scope.RacObservationId);
            Assert.Equal(expected.SqlObservationId, result.Scope.SqlObservationId);
            Assert.Equal(TargetPresenceObservation.BothObservedAbsent, result.Observation);
            Assert.False(result.Executable); Assert.True(result.ScheduledJobsMustRemainBlocked);
        }
        Assert.Equal(2, runner.Commands.Count); Assert.Equal(2, provider.Connections.Count);
        Assert.All(provider.Connections, c => { Assert.Equal(1, c.Opens); Assert.Equal(1, c.Disposals); });
        Assert.All(runner.Commands, c => Assert.Contains(c.Arguments, a => a.Value == "summary"));
    }

    [Fact]
    public async Task Every_expected_pin_is_rejected_before_either_physical_adapter()
    {
        var runner = new Runner(); var provider = new Provider(); var bundle = TargetCreationTrustedAdapters.Create(Options(), runner, provider.Create);
        var expected = Expected();
        var bad = new[] { expected with { OperationId = Guid.Empty }, expected with { FencingToken = 0 },
            expected with { TargetIncarnationId = Guid.Empty }, expected with { ReservationId = Guid.Empty },
            expected with { CanonicalClusterId = Guid.NewGuid() }, expected with { CanonicalSqlServerId = Guid.NewGuid() },
            expected with { RacEndpointMappingId = Guid.NewGuid() }, expected with { SqlEndpointMappingId = Guid.NewGuid() },
            expected with { RacObservationId = Guid.Empty }, expected with { SqlObservationId = Guid.Empty },
            expected with { SqlObservationId = expected.RacObservationId } };
        foreach (var scope in bad)
            Fixed(await Assert.ThrowsAsync<TargetCreationTrustedAdapterException>(() => bundle.AbsencePort.ObserveAsync(scope, Secrets)));
        Assert.Empty(runner.Commands); Assert.Empty(provider.Connections);
    }

    [Fact]
    public async Task Namespace_port_wraps_actual_guard_and_preserves_scope_until_awaited_disposal()
    {
        var runner = new Runner(); var provider = new Provider(); var bundle = TargetCreationTrustedAdapters.Create(Options(), runner, provider.Create);
        var scope = new SqlTargetNamespaceScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Server, Mapping);
        var hold = await bundle.NamespacePort.AcquireAsync(scope);
        Assert.Equal(scope, hold.Scope); await hold.CheckHeldAsync();
        var connection = Assert.Single(provider.Connections);
        Assert.Equal(1, connection.Acquires); Assert.True(connection.Held >= 2); Assert.Equal(0, connection.Disposals);
        await hold.DisposeAsync();
        Assert.Equal(1, connection.Releases); Assert.Equal(1, connection.Disposals); Assert.Equal(1, connection.Opens);
        Assert.Empty(runner.Commands); Settings(connection.ConnectionString);
    }

    [Fact]
    public async Task Observation_namespace_and_creation_share_physical_pins_and_original_launch_guard()
    {
        var runner = new Runner(); var provider = new Provider(); var bundle = TargetCreationTrustedAdapters.Create(Options(), runner, provider.Create);
        var expected = Expected(); await bundle.AbsencePort.ObserveAsync(expected, Secrets);
        await using var hold = await bundle.NamespacePort.AcquireAsync(new(expected.OperationId, Guid.NewGuid(), expected.ReservationId, Server, Mapping));
        var scope = new RacInfobaseCreationScope(expected.OperationId, Guid.NewGuid(), Guid.NewGuid(), expected.ReservationId,
            expected.TargetIncarnationId, Cluster, Server, Mapping);
        var deadline = new ProcessLaunchDeadline(TimeProvider.System, TimeProvider.System.GetTimestamp(), TimeSpan.FromSeconds(30));
        runner.ExpectedDeadline = deadline;
        var result = await bundle.InvocationPort.InvokeAsync(scope, Secrets, deadline);
        Assert.Equal(RacInfobaseCreationOutcomeKind.AcknowledgementObserved, result.Kind); Assert.Equal(scope, result.Scope);
        Assert.Equal(2, runner.Commands.Count); var create = runner.Commands[1];
        Assert.Same(deadline, create.LaunchDeadline); Assert.Contains(create.Arguments, a => a.Value == "create");
        Assert.All(runner.Commands, c => Assert.Contains(c.Arguments, a => a.Value == $"--cluster={RawCluster:D}"));
        Assert.Contains(create.Arguments, a => a.Value == "--db-server=" + Source);
        Assert.Equal(2, provider.Connections.Count);
        Assert.All(provider.Connections, c => { Settings(c.ConnectionString); Assert.Equal(Source, c.DataSource); });
    }

    [Fact]
    public void Invalid_snapshot_ids_and_connection_string_injection_fail_during_inert_factory()
    {
        var runner = new Runner(); var provider = new Provider();
        foreach (var options in new[] { Options(source: Source + ";TrustServerCertificate=true"), Options(sqlMapping: Guid.Empty) })
            Fixed(Assert.Throws<TargetCreationTrustedAdapterException>(() => TargetCreationTrustedAdapters.Create(options, runner, provider.Create)));
        Assert.Empty(runner.Commands); Assert.Empty(provider.Connections);
    }

    [Fact]
    public async Task Real_one_shot_composition_holds_guard_through_creation_and_quarantines_without_replay()
    {
        using var local = new LocalTransports(); var runner = new Runner(); var provider = new Provider();
        var bundle = TargetCreationTrustedAdapters.Create(Options(), runner, provider.Create);
        var coordinator = new TargetCreationOneShotCoordinator(local.Journal,
            new TargetCreationCredentialPreparation(local.Journal, local, local.Credentials, new() { Enabled = true }),
            bundle.NamespacePort, bundle.AbsencePort, local.Claims, bundle.InvocationPort, bundle.OneShotOptions, new Clock());
        runner.OnCreate = () =>
        {
            Assert.Equal(ConnectionState.Open, provider.Connections[0].State);
            Assert.True(provider.Connections[0].Held >= 3); Assert.Equal(0, provider.Connections[0].Releases);
            Assert.Equal(2, local.Journal.GetTargetCreationIntent(local.Intent.OperationId)!.Revision);
        };
        var result = await coordinator.RunAsync(local.Intent, local.Binding);
        Assert.True(result.CreationAcknowledged); Assert.True(result.PhysicalInvocationAttempted);
        Assert.Equal(TargetCreationState.ReconciliationRequired, result.Record.State); Assert.Equal(3, result.Record.Revision);
        Assert.False(result.Executable); Assert.False(result.FreshAuthority); Assert.False(result.RetryAllowed);
        Assert.True(result.ScheduledJobsMustRemainBlocked); Assert.DoesNotContain(Canary, result.ToString());
        Assert.Equal(2, runner.Commands.Count); Assert.Contains(runner.Commands[0].Arguments, a => a.Value == "summary");
        Assert.Contains(runner.Commands[1].Arguments, a => a.Value == "create"); Assert.Equal(2, provider.Connections.Count);
        Assert.Equal(1, provider.Connections[0].Releases); Assert.Equal(ConnectionState.Closed, provider.Connections[0].State);
        Assert.All(provider.Connections, c => Assert.Equal(1, c.Disposals));
        Assert.Throws<InvalidOperationException>(() => local.Journal.SaveTargetCreationIntent(local.Intent with { OperationId = Guid.NewGuid(), CanonicalClusterId = Guid.NewGuid() }));
        Assert.Throws<InvalidOperationException>(() => local.Journal.SaveTargetCreationIntent(local.Intent with { OperationId = Guid.NewGuid(), CanonicalSqlServerId = Guid.NewGuid() }));
        var replay = await coordinator.RunAsync(local.Intent, local.Binding);
        Assert.Equal("AlreadyAttempted", replay.Outcome.ToString()); Assert.Equal(2, runner.Commands.Count);
        Assert.Equal(2, provider.Connections.Count); Assert.Equal(1, local.Starts); Assert.Equal(1, local.CredentialRequests); Assert.Equal(1, local.ClaimRequests);
    }
    private sealed class Clock : TimeProvider
    { public override long TimestampFrequency => TimeSpan.TicksPerSecond; public override long GetTimestamp() => 0; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }
    private sealed class LocalTransports : IDisposable, ITargetCreationPermitStartTransport
    {
        private readonly string directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "trusted-adapter-tests", Guid.NewGuid().ToString("N")));
        public readonly AgentJournal Journal;
        public readonly TargetCreationIntent Intent;
        public readonly TargetCreationPermitBinding Binding;
        public readonly TargetCreationCredentialHttpClient Credentials;
        public readonly TargetCreationExecutionClaimHttpClient Claims;
        public int Starts, CredentialRequests, ClaimRequests;
        public LocalTransports()
        {
            var issued = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero); var reservation = Guid.NewGuid(); var name = "bs_test_" + reservation.ToString("N");
            Intent = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 17, reservation, Guid.NewGuid(), Cluster, Server, name, name);
            Binding = new(Guid.NewGuid(), Intent.OperationId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Agent, issued, issued.AddMinutes(5))
                { CredentialId = Guid.NewGuid(), EndpointMappingId = Mapping };
            Journal = new(directory); Journal.SaveTargetCreationIntent(Intent); Journal.SaveTargetCreationPermitBinding(Intent, Binding);
            Credentials = new(new Handler(async (request, ct) =>
            {
                CredentialRequests++; var p = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct));
                return Reply(request, new { permitId = p.GetProperty("permitId").GetGuid(), expectedPermitRevision = 2,
                    attemptId = p.GetProperty("attemptId").GetGuid(), credentialId = p.GetProperty("credentialId").GetGuid(), endpointMappingId = p.GetProperty("endpointMappingId").GetGuid(),
                    clusterUser = Canary, clusterPassword = Canary, sqlUser = Canary, sqlPassword = Canary,
                    executable = false, scheduledJobsMustRemainBlocked = true, retryAllowed = false });
            }), new() { Enabled = true, BaseUri = new("https://credentials.example/") }, () => "token");
            Claims = new(new Handler((request, _) =>
            {
                ClaimRequests++; return Task.FromResult(Reply(request, new { claimId = Guid.NewGuid(), permitId = Binding.PermitId, operationId = Intent.OperationId,
                    attemptId = Binding.AttemptId, agentId = Binding.AgentId, credentialId = Binding.CredentialId, endpointMappingId = Binding.EndpointMappingId,
                    issuedAt = Binding.StartBefore.AddSeconds(-10), expiresAt = Binding.StartBefore,
                    newlyIssued = true, executable = true, scheduledJobsMustRemainBlocked = true, retryAllowed = false }));
            }), new() { Enabled = true, BaseUri = new("https://claims.example/") }, () => "token");
        }
        public Task<TargetCreationPermitStartReceipt> RecordStartAsync(TargetCreationIntent intent, TargetCreationPermitBinding binding, CancellationToken cancellationToken = default)
        {
            Assert.Equal(Intent, intent); Assert.Equal(Binding, binding); Starts++;
            Assert.Equal(2, Journal.GetTargetCreationIntent(intent.OperationId)!.Revision);
            return Task.FromResult(new TargetCreationPermitStartReceipt(intent, binding, true, Binding.IssuedAt.AddSeconds(1)));
        }
        private static HttpResponseMessage Reply(HttpRequestMessage request, object body) => new(HttpStatusCode.OK)
        { RequestMessage = request, Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } } };
        public void Dispose()
        {
            Credentials.Dispose(); Claims.Dispose(); Journal.Dispose();
            Assert.True(Path.IsPathFullyQualified(directory));
            Assert.Equal(Path.Combine(Path.GetFullPath(Path.GetTempPath()), "trusted-adapter-tests"), Path.GetDirectoryName(directory));
            Assert.True(Guid.TryParseExact(Path.GetFileName(directory), "N", out _)); Directory.Delete(directory, true);
        }
    }

    private static void Settings(string value)
    {
        var b = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(value);
        Assert.Equal(Source, b.DataSource); Assert.Equal("master", b.InitialCatalog);
        Assert.True(b.IntegratedSecurity); Assert.Equal(Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Mandatory, b.Encrypt);
        Assert.False(b.TrustServerCertificate); Assert.False(b.Pooling);
        Assert.Equal(0, b.ConnectRetryCount); Assert.True(b.ShouldSerialize("ConnectRetryCount"));
    }
    private sealed class Runner : IProcessRunner
    {
        public List<ProcessCommand> Commands { get; } = [];
        public ProcessLaunchDeadline? ExpectedDeadline;
        public Action? OnCreate;
        public async Task<ProcessResult> RunAsync(ProcessCommand command, Func<ProcessOutput, Task>? onOutput = null, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            if (command.Arguments.Any(a => a.Value == "create"))
            {
                Assert.NotNull(command.LaunchDeadline);
                if (ExpectedDeadline is not null) Assert.Same(ExpectedDeadline, command.LaunchDeadline);
                command.LaunchDeadline.DemandNotExpired(); OnCreate?.Invoke();
                await onOutput!(new(ProcessOutputStream.StandardOutput, $"infobase: {Guid.NewGuid():D}\n"));
            }
            else Assert.Contains(command.Arguments, a => a.Value == "summary");
            return new(123, 0, ProcessCompletion.Exited, false);
        }
    }
    private sealed class Provider
    {
        public List<Connection> Connections { get; } = [];
        public DbConnection Create(string value) { Settings(value); var c = new Connection { ConnectionString = value }; Connections.Add(c); return c; }
    }
    private sealed class Connection : DbConnection
    {
        public int Opens, Acquires, Held, Releases, Disposals;
        private ConnectionState state;
        [AllowNull] public override string ConnectionString { get; set; } = "";
        public override string Database => "master";
        public override string DataSource => new DbConnectionStringBuilder { ConnectionString = ConnectionString }["Data Source"].ToString()!;
        public override string ServerVersion => "1";
        public override ConnectionState State => state;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() => state = ConnectionState.Closed;
        public override void Open() { Opens++; state = ConnectionState.Open; }
        public override Task OpenAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); Open(); return Task.CompletedTask; }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new Command(this);
        public override ValueTask DisposeAsync() { Disposals++; Close(); return ValueTask.CompletedTask; }
        public DbDataReader Read(string sql)
        {
            object[]? row = sql switch
            {
                SqlTargetNamespaceGuard.IdentitySql => ["fake", "MSSQLSERVER", 1, 21],
                SqlDatabaseObserver.IdentitySql => ["fake", "MSSQLSERVER", 1],
                SqlTargetNamespaceGuard.AcquireSql => Acquire(), SqlTargetNamespaceGuard.HeldSql => Check(),
                SqlTargetNamespaceGuard.ReleaseSql => Release(), SqlDatabaseObserver.PresenceSql => null,
                _ => throw new InvalidOperationException("Unexpected SQL")
            };
            var table = new DataTable(); for (var i = 0; i < (row?.Length ?? 4); i++) table.Columns.Add("c" + i, typeof(object));
            if (row is not null) table.Rows.Add(row); return table.CreateDataReader();
        }
        private object[] Acquire() { Acquires++; return [0, 21]; }
        private object[] Check() { Held++; return [21, "Exclusive"]; }
        private object[] Release() { Releases++; return [21, 0]; }
    }
    private sealed class Command(Connection owner) : DbCommand
    {
        private readonly Parameters parameters = new();
        [AllowNull] public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = owner;
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => parameters;
        public override void Cancel() { }
        public override int ExecuteNonQuery() => throw new NotSupportedException();
        public override object ExecuteScalar() => throw new NotSupportedException();
        public override void Prepare() => throw new NotSupportedException();
        protected override DbParameter CreateDbParameter() => new Parameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => owner.Read(CommandText);
        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken token) => Task.FromResult(owner.Read(CommandText));
    }
    private sealed class Parameter : DbParameter
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
    private sealed class Parameters : DbParameterCollection
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
