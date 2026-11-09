using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BuildSystem.Agent;
using BuildSystem.Application;
using BuildSystem.Domain;
using Xunit;

namespace BuildSystem.IntegrationTests;

// Offline DTO/transport/journal interop only: no hosted server, SQL Server, network or target process.
public sealed class TargetCreationStartInteropTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "creation-start-interop", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Issued = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Started = Issued.AddSeconds(150);
    // Matches TargetCreationPermitEndpoints.JsonOptions exactly; enums must remain numeric.
    private static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web);

    private static TargetCreationIntent Intent()
    {
        var reservation = Guid.NewGuid(); var name = "bs_test_" + reservation.ToString("N");
        return new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 17, reservation,
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), name, name);
    }

    private static TargetCreationPermitBinding Binding(TargetCreationIntent i) =>
        new(Guid.NewGuid(), i.OperationId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Issued, Issued.AddMinutes(5))
        { CredentialId = Guid.NewGuid(), EndpointMappingId = Guid.NewGuid() };

    private static TargetCreationPermitReceipt ServerReceipt(TargetCreationIntent i, TargetCreationPermitBinding b, bool newlyRecorded) =>
        new(b.PermitId, i.OperationId, b.AttemptId, 1, b.IssuedAt, b.StartBefore,
            TargetCreationPermitState.StartRecorded, 2, Started, 1, newlyRecorded,
            new TargetCreationOperationReceipt(i.OperationId, b.RestoreIntentId, i.EnvironmentId,
                i.JobId, i.ReservationId, b.ArtifactId, b.AgentId, i.TargetIncarnationId,
                i.CanonicalClusterId, i.CanonicalSqlServerId, i.InfobaseName, i.SqlDatabaseName,
                TargetCreationOperationState.AttemptRecorded, 2, b.AttemptId, 1, null, false))
        { CredentialId = b.CredentialId, EndpointMappingId = b.EndpointMappingId };

    private sealed class Handler(AgentJournal journal, TargetCreationIntent intent, TargetCreationPermitBinding binding,
        Func<TargetCreationPermitReceipt> receipt) : HttpMessageHandler
    {
        private readonly int ownerThread = Environment.CurrentManagedThreadId;
        public int Calls { get; private set; }
        public int ResponsesProduced { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            // Synchronous handler runs after TryBegin commits, without concurrent journal access.
            Assert.Equal(ownerThread, Environment.CurrentManagedThreadId);
            var durable = journal.GetTargetCreationIntent(intent.OperationId)!;
            Assert.Equal(TargetCreationState.CreationInFlight, durable.State); Assert.Equal(2, durable.Revision);
            Assert.Equal(intent, durable.Intent); Assert.Equal(binding.AttemptId, durable.AttemptId);
            Assert.Equal(1, durable.AttemptExpectedRevision); Assert.Null(durable.QuarantineReason);
            Assert.Equal(binding, journal.GetTargetCreationPermitBinding(intent.OperationId));
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://interop.example.invalid/api/agents/target-creation-permit-starts", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("fresh-token-1", request.Headers.Authorization?.Parameter);
            // Only actual Application DTOs are serialized, and only after the durable-state assertions.
            var server = receipt();
            Assert.False(server.Executable); Assert.True(server.ScheduledJobsMustRemainBlocked);
            Assert.False(server.Operation.Executable); Assert.True(server.Operation.ScheduledJobsMustRemainBlocked);
            var json = JsonSerializer.Serialize(server, ApiJson);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            ResponsesProduced++;
            return Task.FromResult(response);
        }
    }

    private static void Inert(TargetCreationStartCoordinationReceipt r)
    {
        Assert.False(r.Executable); Assert.False(r.FreshAuthority); Assert.False(r.RetryAllowed);
        Assert.True(r.ScheduledJobsMustRemainBlocked);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Real_server_start_DTO_is_inert_and_local_attempt_stays_consumed(bool newlyRecorded)
    {
        var i = Intent(); var b = Binding(i); using var journal = new AgentJournal(directory);
        journal.SaveTargetCreationIntent(i); journal.SaveTargetCreationPermitBinding(i, b);
        var handler = new Handler(journal, i, b, () => ServerReceipt(i, b, newlyRecorded)); var tokens = 0;
        using var client = new TargetCreationPermitHttpClient(handler,
            new() { Enabled = true, BaseUri = new Uri("https://interop.example.invalid/") }, () => "fresh-token-" + ++tokens);
        var coordinator = new TargetCreationStartCoordinator(journal, client, new() { Enabled = true });
        var result = await coordinator.RecordStartAsync(i, b);
        Assert.Equal(TargetCreationStartCoordinationOutcome.StartMetadataRecorded, result.Outcome); Inert(result);
        Assert.Equal(TargetCreationState.CreationInFlight, result.Record.State); Assert.Equal(2, result.Record.Revision);
        Assert.Equal(result.Record, journal.GetTargetCreationIntent(i.OperationId)); Assert.Equal(b, result.Binding);
        var start = Assert.IsType<BuildSystem.Agent.TargetCreationPermitStartReceipt>(result.StartReceipt);
        Assert.Equal(newlyRecorded, start.NewlyRecorded); Assert.Equal(Started, start.StartedAt);
        Assert.Equal(i, start.TargetIntent); Assert.Equal(b, start.ScopeBinding); Assert.Equal(2, start.Revision);
        Assert.False(start.Executable); Assert.False(start.FreshAuthority); Assert.False(start.RetryAllowed);
        Assert.True(start.ScheduledJobsMustRemainBlocked);
        var replay = await coordinator.RecordStartAsync(i, b);
        Assert.Equal(TargetCreationStartCoordinationOutcome.AlreadyAttempted, replay.Outcome); Inert(replay);
        Assert.Equal(result.Record, replay.Record); Assert.Null(replay.StartReceipt);
        Assert.Equal(b, journal.GetTargetCreationPermitBinding(i.OperationId)); Assert.Equal(1, handler.Calls); Assert.Equal(1, handler.ResponsesProduced); Assert.Equal(1, tokens);
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("mapping")]
    [InlineData("canonical-sql")]
    public async Task Historical_revocation_or_changed_real_DTO_pin_quarantines_without_retry(string change)
    {
        var i = Intent(); var b = Binding(i); using var journal = new AgentJournal(directory);
        journal.SaveTargetCreationIntent(i); journal.SaveTargetCreationPermitBinding(i, b);
        var handler = new Handler(journal, i, b, () =>
        {
            var server = ServerReceipt(i, b, false);
            return change switch
            {
                "revoked" => server with { State = TargetCreationPermitState.Revoked, Revision = 3 },
                "mapping" => server with { EndpointMappingId = Guid.NewGuid() },
                _ => server with { Operation = server.Operation with { CanonicalSqlServerId = Guid.NewGuid() } }
            };
        });
        var tokens = 0;
        using var client = new TargetCreationPermitHttpClient(handler,
            new() { Enabled = true, BaseUri = new Uri("https://interop.example.invalid/") }, () => "fresh-token-" + ++tokens);
        var coordinator = new TargetCreationStartCoordinator(journal, client, new() { Enabled = true });
        var result = await coordinator.RecordStartAsync(i, b);
        Assert.Equal(TargetCreationStartCoordinationOutcome.ReconciliationRequired, result.Outcome); Inert(result);
        Assert.Equal(TargetCreationState.ReconciliationRequired, result.Record.State); Assert.Equal(3, result.Record.Revision);
        Assert.Equal("OutcomeUnknown", result.Record.QuarantineReason); Assert.Null(result.StartReceipt);
        Assert.Equal(b.AttemptId, result.Record.AttemptId); Assert.Equal(1, result.Record.AttemptExpectedRevision);
        Assert.Equal(i, result.Record.Intent); Assert.Equal(result.Record, journal.GetTargetCreationIntent(i.OperationId));
        Assert.Equal(b, journal.GetTargetCreationPermitBinding(i.OperationId));
        var replay = await coordinator.RecordStartAsync(i, b);
        Assert.Equal(TargetCreationStartCoordinationOutcome.AlreadyAttempted, replay.Outcome); Inert(replay);
        Assert.Equal(result.Record, replay.Record); Assert.Null(replay.StartReceipt);
        Assert.Equal(1, handler.Calls); Assert.Equal(1, handler.ResponsesProduced); Assert.Equal(1, tokens);
    }

    public void Dispose()
    {
        var absolute = Path.GetFullPath(directory);
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "creation-start-interop")) + Path.DirectorySeparatorChar;
        if (!Path.IsPathFullyQualified(absolute) || !absolute.StartsWith(root, StringComparison.Ordinal) ||
            Path.GetFileName(absolute).Length != 32) throw new InvalidOperationException("Unexpected test cleanup path.");
        try { if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true); }
        catch (IOException) { /* Local SQLite cleanup must not obscure test assertions. */ }
    }
}
