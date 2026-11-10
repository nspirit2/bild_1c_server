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
using Microsoft.Data.Sqlite;
using Xunit;

namespace BuildSystem.AgentTests;

// Trusted fake ports only: no process, SQL server, network, CLI or worker registration.
public sealed class TargetCreationOneShotCoordinatorTests
{
    private const string Canary = "secret-canary";
    private static readonly DateTimeOffset Issued = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private sealed class Clock : TimeProvider
    {
        public long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => send(r, ct); }
    private static HttpResponseMessage Reply(HttpRequestMessage r, object body) => new(HttpStatusCode.OK)
    { RequestMessage = r, Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } } };
    private sealed class Fixture : IDisposable, ITargetCreationPermitStartTransport, ITargetCreationCredentialTransport,
        ITargetCreationNamespacePort, ITargetCreationNamespaceHold, ITargetCreationAbsencePort, ITargetCreationExecutionClaimTransport, ITargetCreationSingleInvocation
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "one-shot-tests", Guid.NewGuid().ToString("N"));
        public readonly AgentJournal Journal;
        public readonly TargetCreationIntent Intent;
        public readonly TargetCreationPermitBinding Binding;
        public readonly Clock Clock = new();
        public readonly List<string> Order = [];
        public readonly CancellationTokenSource Cancellation = new();
        public TargetCreationDeliveredCredentials? Holder;
        private TargetCreationPermitBinding? otherBinding;
        public TargetCreationOneShotOptions Options;
        public SqlTargetNamespaceScope Scope { get; private set; } = null!;
        public string? Failure;
        public bool Acknowledged = true, Disposed;
        public int Checks, Invocations, Claims, PhysicalInvocations;
        public Action? AfterClaim, LastCheck, OnDispose, BeforePhysicalInvocation;
        public ProcessLaunchDeadline? PortDeadline;
        private readonly TargetCreationCredentialHttpClient credentials;
        private readonly TargetCreationExecutionClaimHttpClient claims;
        public Fixture()
        {
            var r = Guid.NewGuid(); var name = "bs_test_" + r.ToString("N");
            Intent = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 17, r, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), name, name);
            Binding = new(Guid.NewGuid(), Intent.OperationId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Issued, Issued.AddMinutes(5))
                { CredentialId = Guid.NewGuid(), EndpointMappingId = Guid.NewGuid() };
            Options = new() { Enabled = true, TrustedAgentId = Binding.AgentId, CanonicalClusterId = Intent.CanonicalClusterId,
                CanonicalSqlServerId = Intent.CanonicalSqlServerId, EndpointMappingId = Binding.EndpointMappingId.Value,
                RacObservationMappingId = Guid.NewGuid(), SqlObservationMappingId = Guid.NewGuid() };
            Journal = new(directory); Journal.SaveTargetCreationIntent(Intent); Journal.SaveTargetCreationPermitBinding(Intent, Binding);
            credentials = new(new Handler(async (request, ct) =>
            {
                var p = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct));
                return Reply(request, new { permitId = p.GetProperty("permitId").GetGuid(), expectedPermitRevision = 2,
                    attemptId = p.GetProperty("attemptId").GetGuid(), credentialId = p.GetProperty("credentialId").GetGuid(), endpointMappingId = p.GetProperty("endpointMappingId").GetGuid(),
                    clusterUser = Canary, clusterPassword = Canary, sqlUser = Canary, sqlPassword = Canary,
                    executable = false, scheduledJobsMustRemainBlocked = true, retryAllowed = false });
            }), new() { Enabled = true, BaseUri = new("https://credentials.example/") }, () => "token");
            claims = new(new Handler((request, _) => Task.FromResult(Reply(request, new {
                claimId = Guid.NewGuid(), permitId = (otherBinding ?? Binding).PermitId, operationId = Intent.OperationId,
                attemptId = (otherBinding ?? Binding).AttemptId, agentId = (otherBinding ?? Binding).AgentId,
                credentialId = (otherBinding ?? Binding).CredentialId, endpointMappingId = (otherBinding ?? Binding).EndpointMappingId,
                issuedAt = Binding.StartBefore.AddSeconds(-10), expiresAt = Binding.StartBefore,
                newlyIssued = true, executable = true, scheduledJobsMustRemainBlocked = true, retryAllowed = false }))),
                new() { Enabled = true, BaseUri = new("https://claims.example/") }, () => "token");
        }
        public Task<TargetCreationOneShotReceipt> Run() => new TargetCreationOneShotCoordinator(Journal,
            new(Journal, this, this, new() { Enabled = true }), this, this, this, this, Options, Clock).RunAsync(Intent, Binding, Cancellation.Token);
        public Task<TargetCreationPermitStartReceipt> RecordStartAsync(TargetCreationIntent i, TargetCreationPermitBinding b, CancellationToken cancellationToken = default)
        {
            Assert.Equal(Intent, i); Assert.Equal(Binding, b); Order.Add("start");
            Assert.Equal(2, Journal.GetTargetCreationIntent(i.OperationId)!.Revision);
            using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT state || ':' || revision FROM target_creation_intents";
            Assert.Equal("CreationInFlight:2", command.ExecuteScalar());
            return Task.FromResult(new TargetCreationPermitStartReceipt(i, b, true, Issued.AddSeconds(1)));
        }
        public async Task<TargetCreationDeliveredCredentials> ResolveAsync(TargetCreationCredentialRequest request, CancellationToken cancellationToken = default)
        { Order.Add("credentials"); Holder = await credentials.ResolveAsync(request, cancellationToken); return Holder; }
        public Task<ITargetCreationNamespaceHold> AcquireAsync(SqlTargetNamespaceScope scope, CancellationToken ct)
        { Order.Add("acquire"); Scope = Failure == "hold-scope" ? scope with { AttemptId = Guid.NewGuid() } : scope; return Task.FromResult<ITargetCreationNamespaceHold>(this); }
        public Task CheckHeldAsync(CancellationToken ct)
        { Order.Add("held"); Checks++; if (Failure == "held") throw new InvalidOperationException(Canary); if (Checks == 3) LastCheck?.Invoke(); return Task.CompletedTask; }
        public async Task<TargetPresenceAssessmentResult> ObserveAsync(TargetPresenceAssessmentScope scope, TargetCreationDeliveredSecretValues secrets, CancellationToken ct)
        {
            Order.Add("observe"); Assert.Equal(Canary, secrets.SqlPassword);
            Assert.NotEqual(Guid.Empty, scope.RacObservationId); Assert.NotEqual(scope.RacObservationId, scope.SqlObservationId);
            if (Failure == "observe") throw new InvalidOperationException(Canary);
            if (Failure == "observation-scope") scope = scope with { OperationId = Guid.NewGuid() };
            if (Failure == "observation-id") scope = scope with { RacObservationId = Guid.NewGuid() };
            var name = "bs_test_" + scope.ReservationId.ToString("N");
            var rac = InfobaseInventoryReadback.Parse(new(scope.OperationId, scope.FencingToken, scope.TargetIncarnationId, scope.CanonicalClusterId,
                scope.RacEndpointMappingId, scope.ReservationId, scope.RacObservationId, name), 0, false, Failure == "present" ? $"infobase: {Guid.NewGuid():D}\nname: {name}\n" : "");
            var sql = await new SqlDatabaseObserver(() => new Connection(), new() { CanonicalSqlServerId = scope.CanonicalSqlServerId,
                EndpointMappingId = scope.SqlEndpointMappingId, ExpectedMachineName = "fake", TimeoutSeconds = 30 }).ObserveAsync(
                new(scope.OperationId, scope.FencingToken, scope.TargetIncarnationId, scope.CanonicalSqlServerId, scope.SqlEndpointMappingId, scope.ReservationId, scope.SqlObservationId, name), ct);
            return TargetPresenceAssessment.Assess(scope, rac, sql);
        }
        public async Task<TargetCreationExecutionClaimGrant> ClaimAsync(TargetCreationIntent i, TargetCreationPermitBinding b, CancellationToken cancellationToken = default)
        {
            Order.Add("claim"); Claims++; if (Failure == "claim") throw new InvalidOperationException(Canary);
            if (Failure == "null-claim") return null!;
            if (Failure == "mismatched-claim") otherBinding = b with { AttemptId = Guid.NewGuid() };
            var grant = await claims.ClaimAsync(i, otherBinding ?? b, cancellationToken); AfterClaim?.Invoke(); return grant;
        }
        public Task<RacInfobaseCreationOutcome> InvokeAsync(RacInfobaseCreationScope scope, TargetCreationDeliveredSecretValues secrets, ProcessLaunchDeadline launchDeadline, CancellationToken ct)
        {
            Order.Add("invoke"); Invocations++; Assert.Equal(Canary, secrets.ClusterPassword);
            PortDeadline = launchDeadline; launchDeadline.DemandNotExpired(); BeforePhysicalInvocation?.Invoke(); launchDeadline.DemandNotExpired(); PhysicalInvocations++;
            Assert.Equal(2, Journal.GetTargetCreationIntent(Intent.OperationId)!.Revision);
            if (Failure == "invoke") throw new InvalidOperationException(Canary);
            return Task.FromResult(RacInfobaseCreationOutcomes.Classify(scope, new(123, 0, ProcessCompletion.Exited, false), Acknowledged ? $"infobase: {Guid.NewGuid():D}\n" : "", ""));
        }
        public async ValueTask DisposeAsync()
        { await Task.Yield(); Disposed = true; Order.Add("dispose"); OnDispose?.Invoke(); if (Failure == "dispose") throw new InvalidOperationException(Canary); }
        public SqliteConnection Open() { var db = new SqliteConnection($"Data Source={Path.Combine(directory, "agent-journal.sqlite")};Pooling=False"); db.Open(); return db; }
        public void AssertQuarantined(TargetCreationOneShotReceipt result, bool invoked)
        {
            Assert.Equal("ReconciliationRequired", result.Outcome.ToString()); Assert.Equal(Binding, result.Binding);
            Assert.Equal(3, result.Record.Revision); Assert.Equal(TargetCreationState.ReconciliationRequired, result.Record.State);
            Assert.Equal(Journal.GetTargetCreationIntent(Intent.OperationId), result.Record); Assert.Equal(invoked, result.PhysicalInvocationAttempted);
            Assert.False(result.Executable); Assert.False(result.FreshAuthority); Assert.False(result.RetryAllowed); Assert.True(result.ScheduledJobsMustRemainBlocked);
            Assert.Throws<ObjectDisposedException>(() => Holder!.ReadSecrets()); Assert.DoesNotContain(Canary, result.ToString());
            Assert.Throws<InvalidOperationException>(() => Journal.SaveTargetCreationIntent(Intent with { OperationId = Guid.NewGuid(), CanonicalClusterId = Guid.NewGuid() }));
            Assert.Throws<InvalidOperationException>(() => Journal.SaveTargetCreationIntent(Intent with { OperationId = Guid.NewGuid(), CanonicalSqlServerId = Guid.NewGuid() }));
        }
        public void Dispose() { Holder?.Dispose(); credentials.Dispose(); claims.Dispose(); Cancellation.Dispose(); Journal.Dispose(); Directory.Delete(directory, true); }
    }
    // Real SQL observer with deterministic in-memory provider reads; never opens a SQL connection.
    private sealed class Connection : DbConnection
    {
        [AllowNull] public override string ConnectionString { get; set; } = "";
        public override string Database => "master"; public override string DataSource => "fake"; public override string ServerVersion => "1";
        public override ConnectionState State => ConnectionState.Open;
        public override void Open() { } public override void Close() { } public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new Command();
    }
    private sealed class Command : DbCommand
    {
        private readonly Microsoft.Data.SqlClient.SqlCommand parameters = new();
        [AllowNull] public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; } public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; } public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => parameters.Parameters;
        protected override DbParameter CreateDbParameter() => parameters.CreateParameter();
        public override void Cancel() { } public override void Prepare() => throw new NotSupportedException();
        public override int ExecuteNonQuery() => throw new NotSupportedException(); public override object ExecuteScalar() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            var table = new DataTable();
            if (CommandText == SqlDatabaseObserver.IdentitySql) { table.Columns.Add("machine", typeof(string)); table.Columns.Add("instance", typeof(string)); table.Columns.Add("visibility", typeof(int)); table.Rows.Add("fake", "MSSQLSERVER", 1); }
            else { for (var n = 0; n < 4; n++) table.Columns.Add("column" + n); }
            return table.CreateDataReader();
        }
        protected override void Dispose(bool disposing) { if (disposing) parameters.Dispose(); base.Dispose(disposing); }
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task One_invocation_even_acknowledged_always_quarantines_and_cannot_replay(bool ack)
    {
        using var f = new Fixture { Acknowledged = ack }; var result = await f.Run(); f.AssertQuarantined(result, true);
        Assert.Equal(ack, result.CreationAcknowledged); Assert.Equal(1, f.Invocations); Assert.True(f.Disposed);
        Assert.Equal(new[] { "start", "credentials", "acquire", "held", "observe", "held", "claim", "held", "invoke", "dispose" }, f.Order);
        var order = f.Order.ToArray(); var replay = await f.Run(); Assert.Equal("AlreadyAttempted", replay.Outcome.ToString()); Assert.Equal(order, f.Order);
    }
    [Fact]
    public async Task Default_off_precedes_cancelled_token_and_disposed_journal()
    {
        using var f = new Fixture(); f.Options = new(); f.Cancellation.Cancel(); f.Journal.Dispose();
        Assert.False(f.Options.Enabled); var e = await Assert.ThrowsAsync<TargetCreationOneShotException>(() => f.Run());
        Assert.Equal("TargetCreationOneShotUnavailable", e.Message); Assert.Equal(e.Message, e.ToString()); Assert.Null(e.InnerException); Assert.Empty(f.Order);
    }
    [Fact]
    public async Task Trusted_configuration_mismatch_has_no_mutation_or_ports()
    {
        using var f = new Fixture(); f.Options = new() { Enabled = true, TrustedAgentId = Guid.NewGuid(),
            CanonicalClusterId = f.Intent.CanonicalClusterId, CanonicalSqlServerId = f.Intent.CanonicalSqlServerId,
            EndpointMappingId = f.Binding.EndpointMappingId!.Value, RacObservationMappingId = Guid.NewGuid(), SqlObservationMappingId = Guid.NewGuid() };
        var e = await Assert.ThrowsAsync<TargetCreationOneShotException>(() => f.Run()); Assert.Equal("TargetCreationOneShotUnavailable", e.Message);
        Assert.Equal(1, f.Journal.GetTargetCreationIntent(f.Intent.OperationId)!.Revision); Assert.Empty(f.Order);
    }
    [Fact]
    public async Task Precancel_has_no_mutation_or_ports()
    {
        using var f = new Fixture(); f.Cancellation.Cancel(); var e = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Run());
        Assert.Equal(f.Cancellation.Token, e.CancellationToken); Assert.Equal(1, f.Journal.GetTargetCreationIntent(f.Intent.OperationId)!.Revision); Assert.Empty(f.Order);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Existing_attempt_never_prepares_acquires_or_claims(bool quarantine)
    {
        using var f = new Fixture(); using var prepared = await new TargetCreationCredentialPreparation(f.Journal, f, f, new() { Enabled = true }).PrepareAsync(f.Intent, f.Binding);
        if (quarantine) f.Journal.QuarantineTargetCreation(f.Intent.OperationId, 2, "OutcomeUnknown");
        f.Order.Clear(); var result = await f.Run(); Assert.Equal("AlreadyAttempted", result.Outcome.ToString()); Assert.Empty(f.Order); Assert.Equal(0, f.Invocations);
    }
    [Theory] [InlineData("observe")] [InlineData("present")] [InlineData("hold-scope")] [InlineData("held")]
    [InlineData("observation-scope")] [InlineData("observation-id")]
    public async Task Invalid_hold_or_observation_never_claims_or_invokes(string failure)
    {
        using var f = new Fixture { Failure = failure }; var result = await f.Run(); f.AssertQuarantined(result, false);
        Assert.Equal(0, f.Claims); Assert.Equal(0, f.Invocations); Assert.True(f.Disposed);
    }
    [Theory] [InlineData("claim")] [InlineData("null-claim")] [InlineData("mismatched-claim")]
    public async Task Failed_claim_never_invokes_and_disposes(string failure)
    {
        using var f = new Fixture { Failure = failure }; f.AssertQuarantined(await f.Run(), false); Assert.Equal(1, f.Claims); Assert.True(f.Disposed);
    }
    [Theory] [InlineData(100000000L, false)] [InlineData(99999999L, true)]
    public async Task Monotonic_budget_uses_exclusive_edge_not_old_utc(long ticks, bool invoke)
    {
        using var f = new Fixture(); f.AfterClaim = () => f.Clock.Ticks += ticks;
        f.AssertQuarantined(await f.Run(), invoke); Assert.Equal(invoke ? 1 : 0, f.Invocations);
    }
    [Fact]
    public async Task Port_delay_keeps_preclaim_deadline_and_prevents_physical_invocation()
    {
        using var f = new Fixture(); f.AfterClaim = () => f.Clock.Ticks = TimeSpan.FromSeconds(9).Ticks;
        f.BeforePhysicalInvocation = () => f.Clock.Ticks = TimeSpan.FromSeconds(10).Ticks;
        f.AssertQuarantined(await f.Run(), true); Assert.Equal(1, f.Invocations); Assert.Equal(0, f.PhysicalInvocations);
        Assert.NotNull(f.PortDeadline); Assert.Throws<ProcessRunnerException>(() => f.PortDeadline.DemandNotExpired());
    }
    [Fact]
    public async Task Budget_expiring_during_final_hold_check_prevents_invocation()
    {
        using var f = new Fixture(); f.LastCheck = () => f.Clock.Ticks += TimeSpan.FromSeconds(10).Ticks;
        f.AssertQuarantined(await f.Run(), false); Assert.Equal(3, f.Checks); Assert.Equal(0, f.Invocations);
    }
    [Fact]
    public async Task Journal_change_during_last_held_await_blocks_invocation_without_resetting_other_state()
    {
        using var f = new Fixture(); f.LastCheck = () => f.Journal.QuarantineTargetCreation(f.Intent.OperationId, 2, "OutcomeUnknown");
        var e = await Assert.ThrowsAsync<TargetCreationOneShotException>(() => f.Run());
        Assert.Equal("TargetCreationOneShotReconciliationUnconfirmed", e.Message); Assert.Equal(0, f.Invocations);
        Assert.Equal(3, f.Journal.GetTargetCreationIntent(f.Intent.OperationId)!.Revision); Assert.True(f.Disposed);
        Assert.Throws<ObjectDisposedException>(() => f.Holder!.ReadSecrets());
    }
    [Fact]
    public async Task Postclaim_cancellation_disposes_holder_and_awaits_hold_cleanup()
    {
        using var f = new Fixture(); f.AfterClaim = f.Cancellation.Cancel;
        f.AssertQuarantined(await f.Run(), false); Assert.True(f.Disposed); Assert.Equal(0, f.Invocations);
    }
    [Fact]
    public async Task Invocation_throw_is_uncertain_and_never_retried()
    {
        using var f = new Fixture { Failure = "invoke" }; f.AssertQuarantined(await f.Run(), true);
        Assert.False((await f.Run()).CreationAcknowledged); Assert.Equal(1, f.Invocations); Assert.True(f.Disposed);
    }
    [Fact]
    public async Task Cancellation_during_awaited_cleanup_clears_acknowledgement_and_quarantines()
    {
        using var f = new Fixture(); f.OnDispose = f.Cancellation.Cancel;
        var result = await f.Run(); f.AssertQuarantined(result, true);
        Assert.False(result.CreationAcknowledged); Assert.Equal("Cancelled", result.Record.QuarantineReason); Assert.Equal(1, f.Invocations);
    }
    [Fact]
    public async Task Cleanup_failure_still_quarantines_and_does_not_report_acknowledgement()
    {
        using var f = new Fixture { Failure = "dispose" }; var result = await f.Run(); f.AssertQuarantined(result, true);
        Assert.False(result.CreationAcknowledged); Assert.True(f.Disposed); Assert.Equal(1, f.Invocations);
        Assert.Equal("AlreadyAttempted", (await f.Run()).Outcome.ToString()); Assert.Equal(1, f.Invocations);
    }
    [Fact]
    public async Task Quarantine_failure_is_fixed_unconfirmed_without_reinvocation()
    {
        using var f = new Fixture(); using var db = f.Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = $"CREATE TRIGGER fail_quarantine BEFORE UPDATE ON target_creation_intents WHEN NEW.state='ReconciliationRequired' BEGIN SELECT RAISE(ABORT, '{Canary}'); END"; cmd.ExecuteNonQuery();
        var e = await Assert.ThrowsAsync<TargetCreationOneShotException>(() => f.Run());
        Assert.Equal("TargetCreationOneShotReconciliationUnconfirmed", e.Message); Assert.Equal(e.Message, e.ToString()); Assert.Null(e.InnerException);
        Assert.Equal(2, f.Journal.GetTargetCreationIntent(f.Intent.OperationId)!.Revision); Assert.Throws<ObjectDisposedException>(() => f.Holder!.ReadSecrets());
        Assert.True(f.Disposed); Assert.Equal("AlreadyAttempted", (await f.Run()).Outcome.ToString()); Assert.Equal(1, f.Invocations);
    }
}
