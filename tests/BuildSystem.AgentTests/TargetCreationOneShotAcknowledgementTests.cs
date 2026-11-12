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

// Synthetic trusted ports only. No network, process, live SQL, or automatic ownership promotion.
public sealed class TargetCreationOneShotAcknowledgementTests
{
    private static readonly DateTimeOffset Issued = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private sealed class Clock : TimeProvider
    {
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => 0;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }
    private static HttpResponseMessage Reply(HttpRequestMessage request, object body) => new(HttpStatusCode.OK)
    {
        RequestMessage = request,
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } }
    };
    private sealed class Fixture : IDisposable, ITargetCreationPermitStartTransport, ITargetCreationCredentialTransport,
        ITargetCreationNamespacePort, ITargetCreationNamespaceHold, ITargetCreationAbsencePort,
        ITargetCreationExecutionClaimTransport, ITargetCreationSingleInvocation
    {
        public readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "one-shot-ack-tests", Guid.NewGuid().ToString("N"));
        public readonly AgentJournal Journal;
        public readonly TargetCreationIntent Intent;
        public readonly TargetCreationPermitBinding Binding;
        public readonly Guid ClaimId = Guid.NewGuid(), ReportedId = Guid.NewGuid();
        public readonly CancellationTokenSource Cancellation = new();
        public readonly TargetCreationOneShotOptions Options;
        public SqlTargetNamespaceScope Scope { get; private set; } = null!;
        public string? Failure;
        public int Invocations, Checks;
        public bool Disposed;
        private readonly TargetCreationCredentialHttpClient credentials;
        private readonly TargetCreationExecutionClaimHttpClient claims;
        public Fixture()
        {
            var reservation = Guid.NewGuid(); var name = "bs_test_" + reservation.ToString("N");
            Intent = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 17, reservation, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), name, name);
            Binding = new(Guid.NewGuid(), Intent.OperationId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Issued, Issued.AddMinutes(5))
                { CredentialId = Guid.NewGuid(), EndpointMappingId = Guid.NewGuid() };
            Options = new() { Enabled = true, TrustedAgentId = Binding.AgentId, CanonicalClusterId = Intent.CanonicalClusterId,
                CanonicalSqlServerId = Intent.CanonicalSqlServerId, EndpointMappingId = Binding.EndpointMappingId.Value,
                RacObservationMappingId = Guid.NewGuid(), SqlObservationMappingId = Guid.NewGuid() };
            Journal = new(DirectoryPath); Journal.SaveTargetCreationIntent(Intent); Journal.SaveTargetCreationPermitBinding(Intent, Binding);
            credentials = new(new Handler(async (request, ct) =>
            {
                var p = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct));
                return Reply(request, new { permitId = p.GetProperty("permitId").GetGuid(), expectedPermitRevision = 2,
                    attemptId = p.GetProperty("attemptId").GetGuid(), credentialId = p.GetProperty("credentialId").GetGuid(),
                    endpointMappingId = p.GetProperty("endpointMappingId").GetGuid(), clusterUser = "synthetic", clusterPassword = "synthetic",
                    sqlUser = "synthetic", sqlPassword = "synthetic", executable = false, scheduledJobsMustRemainBlocked = true, retryAllowed = false });
            }), new() { Enabled = true, BaseUri = new("https://credentials.example/") }, () => "token");
            claims = new(new Handler((request, _) => Task.FromResult(Reply(request, new {
                claimId = ClaimId, permitId = Binding.PermitId, operationId = Intent.OperationId, attemptId = Binding.AttemptId,
                agentId = Binding.AgentId, credentialId = Binding.CredentialId, endpointMappingId = Binding.EndpointMappingId,
                issuedAt = Binding.StartBefore.AddSeconds(-10), expiresAt = Binding.StartBefore,
                newlyIssued = true, executable = true, scheduledJobsMustRemainBlocked = true, retryAllowed = false }))),
                new() { Enabled = true, BaseUri = new("https://claims.example/") }, () => "token");
        }
        public Task<TargetCreationOneShotReceipt> Run(AgentJournal? journal = null)
        {
            var active = journal ?? Journal;
            return new TargetCreationOneShotCoordinator(active, new(active, this, this, new() { Enabled = true }),
                this, this, this, this, Options, new Clock()).RunAsync(Intent, Binding, Cancellation.Token);
        }
        public Task<TargetCreationPermitStartReceipt> RecordStartAsync(TargetCreationIntent intent, TargetCreationPermitBinding binding, CancellationToken cancellationToken = default)
            => Task.FromResult(new TargetCreationPermitStartReceipt(intent, binding, true, Issued.AddSeconds(1)));
        public Task<TargetCreationDeliveredCredentials> ResolveAsync(TargetCreationCredentialRequest request, CancellationToken cancellationToken = default)
            => credentials.ResolveAsync(request, cancellationToken);
        public Task<ITargetCreationNamespaceHold> AcquireAsync(SqlTargetNamespaceScope scope, CancellationToken cancellationToken = default)
        { Scope = scope; return Task.FromResult<ITargetCreationNamespaceHold>(this); }
        public Task CheckHeldAsync(CancellationToken cancellationToken = default)
        { Checks++; return Task.CompletedTask; }
        public async Task<TargetPresenceAssessmentResult> ObserveAsync(TargetPresenceAssessmentScope scope, TargetCreationDeliveredSecretValues secrets, CancellationToken cancellationToken = default)
        {
            var name = "bs_test_" + scope.ReservationId.ToString("N");
            var rac = InfobaseInventoryReadback.Parse(new(scope.OperationId, scope.FencingToken, scope.TargetIncarnationId,
                scope.CanonicalClusterId, scope.RacEndpointMappingId, scope.ReservationId, scope.RacObservationId, name), 0, false, "");
            var sql = await new SqlDatabaseObserver(() => new Connection(), new() { CanonicalSqlServerId = scope.CanonicalSqlServerId,
                EndpointMappingId = scope.SqlEndpointMappingId, ExpectedMachineName = "fake", TimeoutSeconds = 30 }).ObserveAsync(
                new(scope.OperationId, scope.FencingToken, scope.TargetIncarnationId, scope.CanonicalSqlServerId,
                    scope.SqlEndpointMappingId, scope.ReservationId, scope.SqlObservationId, name), cancellationToken);
            return TargetPresenceAssessment.Assess(scope, rac, sql);
        }
        public Task<TargetCreationExecutionClaimGrant> ClaimAsync(TargetCreationIntent intent, TargetCreationPermitBinding binding, CancellationToken cancellationToken = default)
            => claims.ClaimAsync(intent, binding, cancellationToken);
        public Task<RacInfobaseCreationOutcome> InvokeAsync(RacInfobaseCreationScope scope, TargetCreationDeliveredSecretValues secrets,
            ProcessLaunchDeadline launchDeadline, CancellationToken cancellationToken = default)
        {
            Invocations++; launchDeadline.DemandNotExpired();
            Assert.Equal(2, Journal.GetTargetCreationIntent(Intent.OperationId)!.Revision);
            if (Failure == "scope") scope = scope with { OperationId = Guid.NewGuid() };
            var output = Failure switch {
                "invalid-uuid" => "infobase: not-a-uuid\n",
                "ambiguous" => $"infobase: {ReportedId:D}\ninfobase: {Guid.NewGuid():D}\n",
                _ => $"infobase: {ReportedId:D}\n" };
            var result = RacInfobaseCreationOutcomes.Classify(scope, new(123, 0, ProcessCompletion.Exited, false), output, "");
            if (Failure == "cancel-invoke") Cancellation.Cancel();
            return Task.FromResult(result);
        }
        public async ValueTask DisposeAsync()
        {
            await Task.Yield(); Disposed = true;
            if (Failure == "cancel-cleanup") Cancellation.Cancel();
            if (Failure == "cleanup") throw new InvalidOperationException("synthetic-cleanup");
        }
        public SqliteConnection Open()
        { var connection = new SqliteConnection($"Data Source={Path.Combine(DirectoryPath, "agent-journal.sqlite")};Pooling=False"); connection.Open(); return connection; }
        public void Dispose()
        { credentials.Dispose(); claims.Dispose(); Cancellation.Dispose(); Journal.Dispose(); Directory.Delete(DirectoryPath, true); }
    }
    private sealed class Connection : DbConnection
    {
        [AllowNull] public override string ConnectionString { get; set; } = "";
        public override string Database => "master"; public override string DataSource => "fake"; public override string ServerVersion => "1";
        public override ConnectionState State => ConnectionState.Open;
        public override void Open() { } public override void Close() { }
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
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
            if (CommandText == SqlDatabaseObserver.IdentitySql) {
                table.Columns.Add("machine", typeof(string)); table.Columns.Add("instance", typeof(string)); table.Columns.Add("visibility", typeof(int));
                table.Rows.Add("fake", "MSSQLSERVER", 1);
            } else { for (var n = 0; n < 4; n++) table.Columns.Add("column" + n); }
            return table.CreateDataReader();
        }
        protected override void Dispose(bool disposing) { if (disposing) parameters.Dispose(); base.Dispose(disposing); }
    }

    [Fact]
    public async Task Valid_acknowledgement_survives_reopen_but_never_replays_authority()
    {
        using var f = new Fixture(); var receipt = await f.Run();
        Assert.True(receipt.CreationAcknowledged); Assert.Equal(3, f.Checks); Assert.True(f.Disposed);
        Assert.Equal(3, receipt.Record.Revision); Assert.Equal(TargetCreationState.ReconciliationRequired, receipt.Record.State);
        Assert.Equal("OutcomeUnknown", receipt.Record.QuarantineReason);
        f.Journal.Dispose();
        using var reopened = new AgentJournal(f.DirectoryPath);
        var acknowledgement = reopened.GetTargetCreationAcknowledgement(f.Intent.OperationId);
        Assert.NotNull(acknowledgement);
        Assert.Equal(new TargetCreationAcknowledgement(f.Intent, f.Binding, f.ClaimId, f.ReportedId,
            f.Binding.StartBefore.AddSeconds(-10), f.Binding.StartBefore), acknowledgement);
        Assert.False(acknowledgement.Executable); Assert.False(acknowledgement.FreshAuthority); Assert.False(acknowledgement.RetryAllowed);
        Assert.False(acknowledgement.OwnershipEstablished); Assert.True(acknowledgement.RequiresIndependentVerification);
        Assert.True(acknowledgement.ScheduledJobsMustRemainBlocked);
        Assert.Equal(receipt.Record, reopened.GetTargetCreationIntent(f.Intent.OperationId));
        var replay = await f.Run(reopened);
        Assert.Equal(TargetCreationOneShotOutcome.AlreadyAttempted, replay.Outcome);
        Assert.False(replay.CreationAcknowledged); Assert.False(replay.PhysicalInvocationAttempted);
        Assert.False(replay.Executable); Assert.False(replay.FreshAuthority); Assert.False(replay.RetryAllowed);
        Assert.Equal(1, f.Invocations);
        Assert.Equal(acknowledgement, reopened.GetTargetCreationAcknowledgement(f.Intent.OperationId));
    }

    [Theory]
    [InlineData("cancel-invoke")]
    [InlineData("cancel-cleanup")]
    [InlineData("cleanup")]
    [InlineData("invalid-uuid")]
    [InlineData("ambiguous")]
    [InlineData("scope")]
    public async Task Unusable_outcome_quarantines_without_any_durable_acknowledgement(string failure)
    {
        using var f = new Fixture { Failure = failure }; var receipt = await f.Run();
        Assert.False(receipt.CreationAcknowledged); Assert.True(receipt.PhysicalInvocationAttempted);
        Assert.Equal(3, receipt.Record.Revision); Assert.Equal(TargetCreationState.ReconciliationRequired, receipt.Record.State);
        Assert.Equal(failure.StartsWith("cancel", StringComparison.Ordinal) ? "Cancelled" : "OutcomeUnknown", receipt.Record.QuarantineReason);
        Assert.Null(f.Journal.GetTargetCreationAcknowledgement(f.Intent.OperationId));
        Assert.True(f.Disposed); Assert.Equal(1, f.Invocations);
    }

    [Fact]
    public async Task Atomic_acknowledgement_insert_failure_keeps_inflight_barrier_without_fallback_or_retry()
    {
        using var f = new Fixture();
        using (var connection = f.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER fail_ack BEFORE INSERT ON target_creation_acknowledgements BEGIN SELECT RAISE(ABORT, 'synthetic-ack-storage-failure'); END";
            command.ExecuteNonQuery();
        }
        var exception = await Assert.ThrowsAsync<TargetCreationOneShotException>(() => f.Run());
        Assert.Equal("TargetCreationOneShotReconciliationUnconfirmed", exception.Code);
        Assert.Equal(exception.Code, exception.ToString()); Assert.Null(exception.InnerException);
        var record = f.Journal.GetTargetCreationIntent(f.Intent.OperationId)!;
        Assert.Equal(2, record.Revision); Assert.Equal(TargetCreationState.CreationInFlight, record.State);
        Assert.Null(f.Journal.GetTargetCreationAcknowledgement(f.Intent.OperationId));
        Assert.True(f.Disposed);
        var replay = await f.Run();
        Assert.Equal(TargetCreationOneShotOutcome.AlreadyAttempted, replay.Outcome);
        Assert.False(replay.CreationAcknowledged); Assert.False(replay.RetryAllowed); Assert.Equal(1, f.Invocations);
        Assert.Equal(record, f.Journal.GetTargetCreationIntent(f.Intent.OperationId));
    }
}
