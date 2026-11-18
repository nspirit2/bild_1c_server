using System.Globalization;
using System.Net;
using BuildSystem.Agent;
using BuildSystem.Application;
using BuildSystem.TestFixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BuildSystem.ApiTests;

// Real SQLite journal, delivery coordinator, HTTP transport, and in-process ASP.NET pipeline.
// The shared seed uses canned RAC text and SQL observation rows. The receipt port stands in for
// SQL persistence and agent/claim lookup; this test does not exercise SQL idempotency or auth there.
public sealed class TargetRegistrationEvidenceDeliveryInteropTests : IDisposable
{
    private const string Token = "registration-evidence-interop-token";
    private static readonly Uri BaseUri = new("https://localhost/");
    private static readonly Uri ReportUri = new(BaseUri, "/api/agent/target-creation/registration-evidence");
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("registration-evidence-interop-");

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Utc)]
    public async Task LostAcceptedResponseReplaysIdenticalSavedEvidenceAfterReopenThenDurableAckSkipsHttp(DateTimeKind kind)
    {
        TargetCreationAcknowledgement creationAck;
        TargetCreationRecord quarantine;
        TargetRegistrationEvidenceReport savedReport;
        using (var journal = new AgentJournal(directory.FullName))
        {
            var (ack, _, created) = TargetRegistrationEvidenceDeliverySeed.SeedEvidence(journal, kind);
            creationAck = ack;
            var op = ack.Intent.OperationId;
            quarantine = Assert.IsType<TargetCreationRecord>(journal.GetTargetCreationIntent(op));
            savedReport = journal.PrepareTargetRegistrationEvidenceReport(op);
            Assert.Equal(created.ToString("O", CultureInfo.InvariantCulture), savedReport.SqlCreateDate);
        }

        var operationId = creationAck.Intent.OperationId;
        var receipts = new ReceiptPort(creationAck);
        using var factory = new Factory(receipts);
        var bridge = new LoseFirstResponse(factory.Server.CreateHandler());
        using var transport = new TargetRegistrationEvidenceHttpClient(bridge,
            new() { Enabled = true, BaseUri = BaseUri }, () => Token);

        using (var journal = new AgentJournal(directory.FullName))
        {
            var failure = await Assert.ThrowsAsync<TargetRegistrationEvidenceDeliveryException>(() =>
                Deliver(journal, transport, operationId));
            Assert.Equal("TargetRegistrationEvidenceDeliveryUnavailable", failure.Code);
            Assert.False(failure.AcknowledgementMayHaveCompleted);
            Assert.Equal(HttpStatusCode.OK, Assert.Single(bridge.StatusCodes));
            Assert.Single(bridge.Bodies);
            Assert.Equal(1, receipts.Calls);
            Assert.Single(receipts.Accepted);
            Assert.Null(journal.GetTargetRegistrationEvidenceDeliveryAcknowledgement(operationId));
            AssertQuarantineUnchanged(journal, creationAck, quarantine);
        }

        var accepted = Assert.Single(receipts.Accepted).Value;
        var receipt = new TargetRegistrationEvidenceDeliveryReceipt(accepted.Receipt.ClaimId,
            accepted.Receipt.OperationId, accepted.Receipt.Status, accepted.Receipt.AcceptedAt);
        Assert.Equal(new ReportTargetRegistrationEvidence(savedReport.ClaimId, savedReport.ReportedInfobaseId,
            savedReport.RacObservationId, savedReport.SqlObservationId, savedReport.RacEndpointMappingId,
            savedReport.SqlEndpointMappingId, savedReport.RegistrationProfileId, savedReport.InfobaseName,
            savedReport.SqlServer, savedReport.DatabaseName, savedReport.SqlDatabaseId, savedReport.SqlCreateDate,
            savedReport.SqlState), accepted.Report);

        using (var journal = new AgentJournal(directory.FullName))
        {
            Assert.Null(journal.GetTargetRegistrationEvidenceDeliveryAcknowledgement(operationId));
            var replay = await Deliver(journal, transport, operationId);
            Assert.True(replay.DeliveredThisCall);
            Assert.Equal(operationId, replay.OperationId);
            Assert.Equal(savedReport, replay.Acknowledgement.Report);
            Assert.Equal(receipt, replay.Acknowledgement.Receipt);
            Assert.Equal(2, bridge.Bodies.Count);
            Assert.Equal(bridge.Bodies[0], bridge.Bodies[1]);
            Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.OK }, bridge.StatusCodes);
            Assert.Equal(2, receipts.Calls);
            Assert.Equal(accepted, Assert.Single(receipts.Accepted).Value);
            AssertQuarantineUnchanged(journal, creationAck, quarantine);
        }

        using (var journal = new AgentJournal(directory.FullName))
        {
            var durable = Assert.IsType<TargetRegistrationEvidenceDeliveryAcknowledgement>(
                journal.GetTargetRegistrationEvidenceDeliveryAcknowledgement(operationId));
            Assert.Equal(savedReport, durable.Report);
            Assert.Equal(receipt, durable.Receipt);
            var alreadyDelivered = await Deliver(journal, transport, operationId);
            Assert.False(alreadyDelivered.DeliveredThisCall);
            Assert.Equal(durable.Report, alreadyDelivered.Acknowledgement.Report);
            Assert.Equal(durable.Receipt, alreadyDelivered.Acknowledgement.Receipt);
            Assert.Equal(2, bridge.Bodies.Count);
            Assert.Equal(2, receipts.Calls);
            AssertQuarantineUnchanged(journal, creationAck, quarantine);
        }
    }

    private static Task<TargetRegistrationEvidenceDeliveryResult> Deliver(AgentJournal journal,
        TargetRegistrationEvidenceHttpClient transport, Guid operationId) =>
        new TargetRegistrationEvidenceDelivery(journal, transport, new() { Enabled = true })
            .DeliverOperationAsync(operationId);

    private static void AssertQuarantineUnchanged(AgentJournal journal, TargetCreationAcknowledgement ack,
        TargetCreationRecord quarantine)
    {
        var op = ack.Intent.OperationId;
        Assert.Equal(TargetCreationState.ReconciliationRequired, quarantine.State);
        Assert.Equal(quarantine, journal.GetTargetCreationIntent(op));
        Assert.Equal(ack.Binding, journal.GetTargetCreationPermitBinding(op));
        Assert.Equal(ack, journal.GetTargetCreationAcknowledgement(op));
        var attempt = journal.TryBeginTargetCreation(op, 1, ack.Binding.AttemptId);
        Assert.False(attempt.NewlyRecorded);
        Assert.Equal(quarantine, attempt.Record);
    }

    private sealed class Factory(ReceiptPort port) : ApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("TargetRegistrationEvidenceEndpoint:Enabled", "true");
            builder.UseSetting("TargetRegistrationEvidenceReceipts:Enabled", "true");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITargetRegistrationEvidenceReceipts>();
                services.AddSingleton<ITargetRegistrationEvidenceReceipts>(port);
            });
        }
    }

    private sealed class ReceiptPort(TargetCreationAcknowledgement knownClaim) : ITargetRegistrationEvidenceReceipts
    {
        public int Calls { get; private set; }
        public Dictionary<Guid, (ReportTargetRegistrationEvidence Report, TargetRegistrationEvidenceReceipt Receipt)> Accepted { get; } = [];

        public Task<TargetRegistrationEvidenceReceipt> ReportAsync(string agentToken, ReportTargetRegistrationEvidence report,
            CancellationToken ct)
        {
            Calls++;
            Assert.Equal(Token, agentToken);
            Assert.Equal(knownClaim.ClaimId, report.ClaimId);
            if (!Accepted.TryGetValue(report.ClaimId, out var prior))
            {
                prior = (report, new(report.ClaimId, knownClaim.Intent.OperationId, "Recorded",
                    TargetRegistrationEvidenceDeliverySeed.Issued.AddSeconds(2)));
                Accepted.Add(report.ClaimId, prior);
            }
            Assert.Equal(prior.Report, report);
            return Task.FromResult(prior.Receipt);
        }
    }

    private sealed class LoseFirstResponse(HttpMessageHandler server) : DelegatingHandler(server)
    {
        public List<byte[]> Bodies { get; } = [];
        public List<HttpStatusCode> StatusCodes { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(ReportUri, request.RequestUri);
            // ReadAsByteArrayAsync returns a copy; the real transport clears its own request buffer.
            Bodies.Add(await request.Content!.ReadAsByteArrayAsync(ct));
            var response = await base.SendAsync(request, ct);
            StatusCodes.Add(response.StatusCode);
            if (StatusCodes.Count == 1)
            {
                using (response)
                    await response.Content.ReadAsByteArrayAsync(ct); // Let the real response finish before dropping it.
                throw new HttpRequestException("Simulated loss after the server returned the receipt.");
            }

            // Preserve provenance for TestServer versions without RequestMessage. No response headers/body are rewritten.
            response.RequestMessage ??= request;
            return response;
        }
    }

    public void Dispose() => directory.Delete(recursive: true);
}
