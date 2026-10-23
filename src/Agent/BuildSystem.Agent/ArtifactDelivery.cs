using System.Net;
using BuildSystem.Contracts;

namespace BuildSystem.Agent;

public interface IArtifactTransport
{
    Task<StagedArtifactReceipt> ReportArtifactAsync(ReportStagedArtifact report, CancellationToken ct);
}

/// <summary>Receipt failure has a stable code and never includes server response contents.</summary>
public sealed class ArtifactReceiptException : Exception
{
    public string Code { get; }
    public ArtifactReceiptException(string code) : base("Artifact receipt rejected.") => Code = code;
}

public enum ArtifactDeliveryResult { Empty, Acknowledged, RetryLater, Blocked }

/// <summary>Dispatches one already-durable artifact; neither creates reports nor settles leases.</summary>
public sealed class ArtifactDelivery(AgentJournal journal, IArtifactTransport transport, TimeProvider? clock = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    public async Task<ArtifactDeliveryResult> FlushAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var item = journal.PendingArtifacts().FirstOrDefault();
        if (item is null) return ArtifactDeliveryResult.Empty;
        if (item.State == "Blocked") return ArtifactDeliveryResult.Blocked;
        var report = item.Report;
        if (clock.GetUtcNow() >= item.ExpiresAt)
        {
            journal.BlockArtifact(report.ArtifactId, "LeaseExpired");
            return ArtifactDeliveryResult.Blocked;
        }
        if (journal.IsSettled(report.JobId, report.FencingToken))
        {
            journal.BlockArtifact(report.ArtifactId, "LeaseRejected");
            return ArtifactDeliveryResult.Blocked;
        }
        try
        {
            var receipt = await transport.ReportArtifactAsync(report, ct);
            ct.ThrowIfCancellationRequested();
            if (receipt?.ArtifactId != report.ArtifactId || receipt.State != "ContentVerified")
            {
                journal.BlockArtifact(report.ArtifactId, "ReceiptMismatch");
                return ArtifactDeliveryResult.Blocked;
            }
            // The server's accepted receipt is authoritative even if local time crosses expiry in flight.
            journal.AcknowledgeArtifact(report.ArtifactId);
            return ArtifactDeliveryResult.Acknowledged;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ArtifactReceiptException e) when (e.Code == "ReceiptMismatch")
        {
            journal.BlockArtifact(report.ArtifactId, "ReceiptMismatch");
            return ArtifactDeliveryResult.Blocked;
        }
        catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            journal.BlockArtifact(report.ArtifactId, "AuthRejected");
            return ArtifactDeliveryResult.Blocked;
        }
        catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity or HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.Gone)
        {
            journal.BlockArtifact(report.ArtifactId, "LeaseRejected");
            return ArtifactDeliveryResult.Blocked;
        }
        catch (HttpRequestException) { return ArtifactDeliveryResult.RetryLater; }
        catch (IOException) { return ArtifactDeliveryResult.RetryLater; }
        catch (OperationCanceledException) { return ArtifactDeliveryResult.RetryLater; } // HttpClient timeout.
    }
}
// end
